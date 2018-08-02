// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

#if NETCOREAPP2_2

using System.Collections.Generic;
using System.IO.Pipelines;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Runtime.InteropServices;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Core.Internal.Http2;
using Microsoft.AspNetCore.Testing;
using Microsoft.AspNetCore.Testing.xunit;
using Microsoft.Extensions.Logging.Testing;
using Xunit;

namespace Microsoft.AspNetCore.Server.Kestrel.FunctionalTests.Http2
{
    [OSSkipCondition(OperatingSystems.MacOSX, SkipReason = "Missing SslStream ALPN support: https://github.com/dotnet/corefx/issues/30492")]
    [OSSkipCondition(OperatingSystems.Linux, SkipReason = "Curl requires a custom install to support HTTP/2, see https://askubuntu.com/questions/884899/how-do-i-install-curl-with-http2-support")]
    [MinimumOSVersion(OperatingSystems.Windows, WindowsVersions.Win10)]
    public class ShutdownTests : LoggedTest
    {
        private static X509Certificate2 _x509Certificate2 = TestResources.GetTestCertificate();

        private HttpClient Client { get; set; }
        private List<Http2Frame> ReceivedFrames { get; } = new List<Http2Frame>();

        public ShutdownTests()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                // We don't want the default SocketsHttpHandler, it doesn't support HTTP/2 yet.
                Client = new HttpClient(new WinHttpHandler()
                {
                    ServerCertificateValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
                });
            }
        }

        [ConditionalFact]
        public async Task GracefulShutdownWaitsForRequestsToFinish()
        {
            var requestStarted = new ManualResetEventSlim();
            var requestUnblocked = new ManualResetEventSlim();
            using (var server = new TestServer(async context =>
            {
                requestStarted.Set();
                Assert.True(requestUnblocked.Wait(5000), "request timeout");
                await context.Response.WriteAsync("hello world " + context.Request.Protocol);
            }, new TestServiceContext(LoggerFactory),
            kestrelOptions =>
            {
                kestrelOptions.Listen(IPAddress.Loopback, 0, listenOptions =>
                {
                    listenOptions.Protocols = HttpProtocols.Http2;
                    listenOptions.UseHttps(_x509Certificate2);
                });
            }))
            {
                // Setup a listening stream
                var connection = server.CreateConnection();
                var sslStream = new SslStream(connection.Stream);
                await sslStream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions()
                {
                    TargetHost = "localhost",
                    RemoteCertificateValidationCallback = (_, __, ___, ____) => true,
                    ApplicationProtocols = new List<SslApplicationProtocol>() { SslApplicationProtocol.Http2, SslApplicationProtocol.Http11 },
                    EnabledSslProtocols = SslProtocols.Tls12,
                }, CancellationToken.None);
                var reader = PipeReaderFactory.CreateFromStream(PipeOptions.Default, sslStream, CancellationToken.None);

                var requestTask = Client.GetStringAsync($"https://localhost:{server.Port}/");
                Assert.False(requestTask.IsCompleted);

                Assert.True(requestStarted.Wait(5000), "timeout");

                var stopTask = server.StopAsync();

                // Unblock the request
                requestUnblocked.Set();

                Assert.Equal("hello world HTTP/2", await requestTask);
                Assert.Equal(stopTask, await Task.WhenAny(stopTask, Task.Delay(10000)));

                await ReceiveFramesAsync(reader);

                var frame = Assert.Single(ReceivedFrames);
                Assert.Equal(Http2FrameType.GOAWAY, frame.Type);
                Assert.Equal(8, frame.Length);
                Assert.Equal(0, frame.Flags);
                Assert.Equal(0, frame.StreamId);
                Assert.Equal(0, frame.GoAwayLastStreamId);
                Assert.Equal(Http2ErrorCode.NO_ERROR, frame.GoAwayErrorCode);
            }
        }

        [ConditionalFact]
        public async Task GracefulTurnsAbortiveIfRequestsDoNotFinish()
        {
            var requestStarted = new ManualResetEventSlim();
            var requestUnblocked = new ManualResetEventSlim();
            // Abortive shutdown leaves one request hanging
            using (var server = new TestServer(TransportSelector.GetWebHostBuilder(new DiagnosticMemoryPoolFactory(allowLateReturn: true).Create), async context =>
            {
                requestStarted.Set();
                requestUnblocked.Wait();
                await context.Response.WriteAsync("hello world " + context.Request.Protocol);
            }, new TestServiceContext(LoggerFactory),
            kestrelOptions =>
            {
                kestrelOptions.Listen(IPAddress.Loopback, 0, listenOptions =>
                {
                    listenOptions.Protocols = HttpProtocols.Http2;
                    listenOptions.UseHttps(_x509Certificate2);
                });
            },
            _ => { }))
            {
                // Setup a listening stream
                var connection = server.CreateConnection();
                var sslStream = new SslStream(connection.Stream);
                await sslStream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions()
                {
                    TargetHost = "localhost",
                    RemoteCertificateValidationCallback = (_, __, ___, ____) => true,
                    ApplicationProtocols = new List<SslApplicationProtocol>() { SslApplicationProtocol.Http2, SslApplicationProtocol.Http11 },
                    EnabledSslProtocols = SslProtocols.Tls12, // Intentionally less than the required 1.2
                }, CancellationToken.None);
                var reader = PipeReaderFactory.CreateFromStream(PipeOptions.Default, sslStream, CancellationToken.None);

                var requestTask = Client.GetStringAsync($"https://localhost:{server.Port}/");
                Assert.False(requestTask.IsCompleted);
                Assert.True(requestStarted.Wait(5000), "timeout");

                var stopTask = server.StopAsync();

                // Keep the request blocked
                Assert.False(requestUnblocked.Wait(6000), "request unblocked");
                Assert.False(requestTask.IsCompletedSuccessfully, "request completed successfully");
                Assert.Equal(stopTask, await Task.WhenAny(stopTask, Task.Delay(10000)));

                await ReceiveFramesAsync(reader);

                var frame = Assert.Single(ReceivedFrames);
                Assert.Equal(Http2FrameType.GOAWAY, frame.Type);
                Assert.Equal(8, frame.Length);
                Assert.Equal(0, frame.Flags);
                Assert.Equal(0, frame.StreamId);
                Assert.Equal(0, frame.GoAwayLastStreamId);
                Assert.Equal(Http2ErrorCode.NO_ERROR, frame.GoAwayErrorCode);
            }
        }

        private async Task ReceiveFramesAsync(PipeReader reader)
        {
            var frame = new Http2Frame();

            while (true)
            {
                var result = await reader.ReadAsync();
                var buffer = result.Buffer;
                var consumed = buffer.Start;
                var examined = buffer.End;

                try
                {
                    if (Http2FrameReader.ReadFrame(buffer, frame, 16_384, out consumed, out examined))
                    {
                        ReceivedFrames.Add(frame);
                    }

                    if (result.IsCompleted)
                    {
                        return;
                    }
                }
                finally
                {
                    reader.AdvanceTo(consumed, examined);
                }
            }
        }
    }
}
#elif NET461 // No ALPN support
#else
#error TFMs need updating
#endif