using Fors.AdminApi.Security;
using Fors.AdminApi.Settings;
using Fors.Domain.Keys;
using Fors.Infrastructure.Security;

namespace Fors.AdminApi.Integrations;

/// <summary>
/// <c>/integration/v1</c> (docs/integrations.md): for an integration's own services, authenticated with one of its credential
/// keys that holds the scope; no session, no CSRF (read-only), its own per-key rate limit. Every read is logged as
/// security event 2006 <c>admin.data.read</c> with the actor <c>integration:&lt;name&gt;</c>.
/// </summary>
public static class IntegrationApiEndpoints
{
    public static void MapIntegrationApi(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var api = app.MapGroup("/integration/v1").RequireRateLimiting(AdminPolicies.PerIntegrationKeyRateLimit).WithTags("Integration API")
            .AddEndpointFilter(async (context, next) =>
            {
                var result = await next(context);
                var http = context.HttpContext;
                var logger = SecurityEvents.CreateLogger(http.RequestServices.GetRequiredService<ILoggerFactory>());
                SecurityEvents.AdminDataRead(logger, http.User.Identity?.Name ?? "integration", http.Request.Path.Value ?? "",
                    SecurityEvents.Printable(http.Request.QueryString.Value ?? ""), http.Items[AdminClientAddresses.ItemKey] as string);
                return result;
            })
            .ProducesProblem(StatusCodes.Status400BadRequest).ProducesProblem(StatusCodes.Status404NotFound);
        var usage = IntegrationKeyHandler.Policy(IntegrationScopes.UsageRead);

        api.MapGet("/usage", async (DateOnly from, DateOnly to, string? granularity, string? groupBy, Guid? keyId, string? runId, HttpContext http,
                IntegrationApiService service, CancellationToken ct) =>
            TypedResults.Ok(await service.UsageAsync(IntegrationKeyHandler.Caller(http.User).IntegrationId, from, to, granularity, groupBy, keyId, runId, ct)))
            .RequireAuthorization(usage).WithName("IntegrationUsage")
            .WithSummary("Requests, tokens and cost of the integration's keys per day or month, by key, model and step (scope usage.read)");
        api.MapGet("/budgets", async (Guid keyId, HttpContext http, IntegrationApiService service, CancellationToken ct) =>
            TypedResults.Ok(await service.BudgetsAsync(IntegrationKeyHandler.Caller(http.User).IntegrationId, keyId, ct)))
            .RequireAuthorization(usage).WithName("IntegrationBudgets")
            .WithSummary("The budgets that apply to one of the integration's keys, with what each used this period (scope usage.read)");
        // The admin console's logo for a platform that shows it beside the organisation name (feature organisation-logo). No scope:
        // /bff/branding/logo serves it to every visitor. From BrandingCache, with the logo's hash as its ETag.
        api.MapGet("/branding/logo", async (HttpContext http, BrandingCache cache, CancellationToken ct) =>
            {
                var branding = await cache.GetAsync(ct);
                if (branding.Logo is not { } logo)
                {
                    return Results.NotFound();
                }

                var etag = $"\"{branding.Version}\"";
                http.Response.Headers.ETag = etag;
                http.Response.Headers.CacheControl = "no-cache";
                // Opened directly, an SVG would be a document on this origin: no scripts, no requests, sandboxed.
                http.Response.Headers.ContentSecurityPolicy = "default-src 'none'; img-src data:; style-src 'unsafe-inline'; sandbox";
                // Weak comparison (RFC 9110 §13.1.2), as the gateway's discovery endpoints: a W/ added by a proxy still matches.
                var matches = http.Request.GetTypedHeaders().IfNoneMatch.Any(t => t.Equals(Microsoft.Net.Http.Headers.EntityTagHeaderValue.Any) || t.Tag.Equals(etag, StringComparison.Ordinal));
                return matches ? Results.StatusCode(StatusCodes.Status304NotModified) : Results.Bytes(logo, branding.LogoContentType);
            })
            .RequireAuthorization(IntegrationKeyHandler.AnyCredentialPolicy).WithName("IntegrationBrandingLogo")
            .WithSummary("The admin console's logo (PNG, JPEG, WebP or SVG), with an ETag; 404 when the built-in icon is shown (no scope)");
    }
}
