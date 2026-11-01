using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;

namespace YARG.Relay.Tests
{
    /// <summary>
    /// The real relay on Kestrel, on a random local port.
    /// </summary>
    public sealed class RelayTestServer : IAsyncDisposable
    {
        private readonly WebApplication _app;

        public Uri WebSocketUri { get; }

        private RelayTestServer(WebApplication app, Uri uri)
        {
            _app = app;
            WebSocketUri = uri;
        }

        public static async Task<RelayTestServer> StartAsync(Action<RelayOptions>? configure = null)
        {
            var app = RelayApp.Build(new[] { "--urls", "http://127.0.0.1:0", "--Logging:LogLevel:Default=Warning" },
                options =>
                {
                    // Fast hashing; the tests don't care about brute-force cost
                    options.PasswordIterations = 10;
                    options.TrustForwardedFor = true;
                    configure?.Invoke(options);
                });
            await app.StartAsync();

            string url = app.Urls.First();
            return new RelayTestServer(app, new Uri("ws" + url.Substring("http".Length) + "/ws"));
        }

        public async ValueTask DisposeAsync()
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }
}
