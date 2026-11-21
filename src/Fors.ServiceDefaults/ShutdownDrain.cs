using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Fors.ServiceDefaults;

/// <summary>What a host does between the stop signal and closing its listener (<c>Shutdown:*</c>).</summary>
public sealed class ShutdownOptions
{
    public const string SectionName = "Shutdown";

    /// <summary>
    /// Seconds the host keeps accepting requests after the stop signal while its readiness check already fails, so a load
    /// balancer that polls readiness (Kubernetes, HAProxy, F5) stops sending new requests before the listener closes. Counts
    /// against the shutdown timeout (<c>DOTNET_SHUTDOWNTIMEOUTSECONDS</c>). 0 (the default) closes the listener at once.
    /// </summary>
    [Range(0, 300)]
    public int DrainSeconds { get; set; }
}

/// <summary>
/// Fails the readiness check (<c>/health/ready</c>) from the moment the host is told to stop, and holds back the listener's
/// shutdown for <see cref="ShutdownOptions.DrainSeconds"/>. Meanwhile every HTTP/1.1 response carries <c>Connection: close</c>,
/// so a proxy retires its pooled connections to this instance after their current request instead of sending the next
/// request on a connection the server is about to close (which the proxy cannot safely retry for a POST). When the drain
/// time is over, new connections are closed before the TLS handshake for <see cref="RefuseSeconds"/>, so a proxy sees a
/// failed connection (which it retries on another instance, POST included) instead of a connection that Kestrel's shutdown
/// closes after the proxy has sent its request on it; the connections accepted before then get their request answered.
/// Requests in flight then finish within the shutdown timeout as usual.
/// </summary>
public sealed partial class ShutdownDrain(
    IOptions<ShutdownOptions> options, IHostApplicationLifetime lifetime, TimeProvider time, ILogger<ShutdownDrain> logger)
    : IHostedLifecycleService, IHealthCheck
{
    /// <summary>Seconds between refusing new connections and the web server's shutdown, when there is a drain time.</summary>
    public const int RefuseSeconds = 1;

    private volatile bool _stopping;
    private volatile bool _refusing;

    /// <summary>True from the stop signal on.</summary>
    public bool IsStopping => _stopping || lifetime.ApplicationStopping.IsCancellationRequested;

    /// <summary>True once the drain time is over: new connections are closed at once (only with a drain time).</summary>
    public bool RefusesConnections => _refusing;

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(IsStopping ? HealthCheckResult.Unhealthy("Shutting down") : HealthCheckResult.Healthy());

    /// <summary>Puts the <c>Connection: close</c> middleware first in every web host's pipeline (one flag check per request).</summary>
    internal sealed class StartupFilter(ShutdownDrain drain) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((http, nextMiddleware) =>
            {
                if (drain.IsStopping && HttpProtocol.IsHttp11(http.Request.Protocol))
                {
                    http.Response.Headers.Connection = "close";
                }

                return nextMiddleware(http);
            });
            next(app);
        };
    }

    /// <summary>
    /// Closes new connections before anything else (TLS included) runs on them once <see cref="RefusesConnections"/> is set.
    /// Endpoint defaults apply to the endpoints from configuration (<c>Kestrel:Endpoints</c>, as every deployment sets them)
    /// and to those set in code after this is configured.
    /// </summary>
    internal sealed class KestrelSetup(ShutdownDrain drain) : IConfigureOptions<KestrelServerOptions>
    {
        public void Configure(KestrelServerOptions options) =>
            options.ConfigureEndpointDefaults(listen => listen.Use(next => connection =>
            {
                if (!drain.RefusesConnections)
                {
                    return next(connection);
                }

                connection.Abort();
                return Task.CompletedTask;
            }));
    }

    /// <summary>Runs before any hosted service (the web server included) is stopped.</summary>
    public async Task StoppingAsync(CancellationToken cancellationToken)
    {
        _stopping = true;
        var seconds = options.Value.DrainSeconds;
        if (seconds == 0)
        {
            return;
        }

        LogDraining(logger, seconds);
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(seconds), time, cancellationToken);
            // Kestrel's shutdown closes every connection that is not in a request, also one whose first request is still on
            // its way from the proxy. Refuse new ones first and give those already accepted time to send theirs.
            _refusing = true;
            await Task.Delay(TimeSpan.FromSeconds(RefuseSeconds), time, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // The shutdown timeout ran out first; stop now.
        }
    }

    public Task StartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(Level = LogLevel.Information, Message = "Stopping: readiness now fails; accepting requests for {Seconds} s more before closing the listener")]
    private static partial void LogDraining(ILogger logger, int seconds);
}
