using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace YARG.Relay
{
    /// <summary>
    /// Builds the relay: <c>/ws</c> for game clients and <c>/health</c> for the proxy and the tests.
    /// </summary>
    public static class RelayApp
    {
        public static WebApplication Build(string[] args, Action<RelayOptions>? configure = null)
        {
            var builder = WebApplication.CreateBuilder(args);

            var options = new RelayOptions();
            builder.Configuration.GetSection(RelayOptions.SECTION).Bind(options);
            configure?.Invoke(options);

            builder.Services.AddSingleton(options);
            builder.Services.AddSingleton(services => new RoomRegistry(options, () => DateTime.UtcNow,
                services.GetRequiredService<ILoggerFactory>().CreateLogger("YARG.Relay")));
            builder.Services.AddSingleton<RelayHandler>();
            builder.Services.AddHostedService<IdleRoomSweeper>();

            var app = builder.Build();
            app.UseWebSockets();

            var handler = app.Services.GetRequiredService<RelayHandler>();
            app.Map("/ws", (RequestDelegate) handler.HandleAsync);
            app.Map("/health", (RequestDelegate) (context => context.Response.WriteAsync("ok")));
            return app;
        }
    }

    /// <summary>
    /// Closes idle rooms and forgets expired wrong-password counts.
    /// </summary>
    public sealed class IdleRoomSweeper : BackgroundService
    {
        private readonly RoomRegistry _registry;
        private readonly RelayOptions _options;

        public IdleRoomSweeper(RoomRegistry registry, RelayOptions options)
        {
            _registry = registry;
            _options = options;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            using var timer = new PeriodicTimer(_options.IdleCheckInterval);
            try
            {
                while (await timer.WaitForNextTickAsync(stoppingToken))
                {
                    _registry.SweepIdle();
                    _registry.Limiter.Sweep();
                }
            }
            catch (OperationCanceledException)
            {
                // Shutting down
            }
        }
    }
}
