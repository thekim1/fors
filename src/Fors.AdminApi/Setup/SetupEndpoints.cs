using Fors.AdminApi.Common;
using Fors.AdminApi.Security;
using Fors.Domain.Entities;

namespace Fors.AdminApi.Setup;

/// <summary>
/// The setup API (thekim1/fors#100), anonymous, under <c>/bff/setup</c>: 404 while setup is closed
/// (<see cref="SetupGateMiddleware"/>), the BFF's antiforgery on every POST, the per-address rate limit. <c>POST session</c> takes the
/// setup code; <c>GET state</c>, <c>POST test</c> and <c>POST complete</c> need the setup session it gives. Only <c>complete</c> writes
/// the sign-in values; no route of an admin session does. Not in the API reference.
/// </summary>
public static class SetupEndpoints
{
    public static void MapSetupMode(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var setup = app.MapGroup(SetupSessions.CookiePath).AllowAnonymous().RequireRateLimiting(AdminPolicies.PerUserRateLimit)
            .AddEndpointFilter<AdminValidationFilter>().ExcludeFromDescription();

        setup.MapPost("/session", (SetupCodeRequest request, HttpContext http, SetupService service, CancellationToken ct) =>
            service.StartSessionAsync(http, SetupGateMiddleware.StatusOf(http), request.Code, ct));

        var session = setup.MapGroup("").AddEndpointFilter<SetupSessionFilter>();
        session.MapGet("/state", (HttpContext http, SetupService service, CancellationToken ct) =>
            service.StateAsync(http, SetupGateMiddleware.StatusOf(http), SetupSessionFilter.SessionOf(http), ct));
        session.MapPost("/test", (SetupValuesRequest request, HttpContext http, SetupTestSignIn tests, CancellationToken ct) =>
            tests.StartAsync(http, SetupSessionFilter.SessionOf(http), request, ct));
        session.MapPost("/complete", (SetupCompleteRequest request, HttpContext http, SetupService service, CancellationToken ct) =>
            service.CompleteAsync(http, SetupSessionFilter.SessionOf(http), request, ct));
    }
}

/// <summary>Requires the setup session (<see cref="SetupSessions.FindAsync"/>) and keeps it for the endpoint; else <c>setup_session_required</c>.</summary>
public sealed class SetupSessionFilter : IEndpointFilter
{
    private const string SessionKey = "fors.setupSession";

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);
        var http = context.HttpContext;
        var session = await http.RequestServices.GetRequiredService<SetupSessions>().FindAsync(http, SetupGateMiddleware.StatusOf(http), http.RequestAborted);
        if (session is null)
        {
            // Setup may have closed since the gate read it (a completion just now): then it is 404, as for any closed setup.
            return (await http.RequestServices.GetRequiredService<SetupMode>().ReadAsync(http.RequestAborted)).Open
                ? throw SetupErrors.SessionMissing()
                : Results.NotFound();
        }

        http.Items[SessionKey] = session;
        return await next(context);
    }

    public static SetupSession SessionOf(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);
        return http.Items[SessionKey] as SetupSession ?? throw new InvalidOperationException("The setup session filter did not run for this request.");
    }
}
