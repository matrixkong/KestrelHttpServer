// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

#if NETCOREAPP2_2

using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
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

                var requestTask = Client.GetStringAsync($"https://localhost:{server.Port}/");
                Assert.False(requestTask.IsCompleted);

                Assert.True(requestStarted.Wait(5000), "timeout");

                var stopTask = server.StopAsync();

                // Unblock the request
                requestUnblocked.Set();

                Assert.Equal("hello world HTTP/2", await requestTask);
                Assert.Equal(stopTask, await Task.WhenAny(stopTask, Task.Delay(10000)));
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
                var requestTask = Client.GetStringAsync($"https://localhost:{server.Port}/");
                Assert.False(requestTask.IsCompleted);
                Assert.True(requestStarted.Wait(5000), "timeout");

                var stopTask = server.StopAsync();

                // Keep the request unblocked
                Assert.False(requestUnblocked.Wait(8000), "request unblocked");
                Assert.False(requestTask.IsCompletedSuccessfully, "request completed successfully");
                Assert.Equal(stopTask, await Task.WhenAny(stopTask, Task.Delay(10000)));
            }
        }
    }
}
#elif NET461 // No ALPN support
#else
#error TFMs need updating
#endif