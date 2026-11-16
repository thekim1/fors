using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Ume.LlmGateway.AdminApi.Common;
using Ume.LlmGateway.AdminApi.Security;
using Ume.LlmGateway.AdminApi.Settings;
using Ume.LlmGateway.Domain.Entities;
using Ume.LlmGateway.Infrastructure.Security;

namespace Ume.LlmGateway.AdminApi.Setup;

/// <summary>
/// The setup API's work (thekim1/llmgateway#100): the setup code for a session, the wizard's state, and completing. Only
/// <see cref="CompleteAsync"/> writes the sign-in values, after a passed test with exactly those values by a person who came out
/// as a gateway admin; no route of an admin session writes them.
/// </summary>
public sealed class SetupService(
    AdminContext admin, SetupMode setup, SetupSessions sessions, SetupTestSignIn tests, SignInSettingsService signIn, AdminSignIn adminSignIn,
    SetupCodeThrottle throttle, SetupEventSampler sampler, IConfiguration configuration, IOptions<OidcOptions> deployment,
    IOptionsMonitor<OpenIdConnectOptions> oidc, IOptionsMonitorCache<OpenIdConnectOptions> oidcCache, TimeProvider time, ILoggerFactory loggers)
{
    /// <summary>Where setup sends the person after completion.</summary>
    public const string SignInUrl = AdminAuthentication.LoginPath;

    /// <summary>The event id sampled for wrong codes (security event <c>admin.setup.code_refused</c>).</summary>
    public const int WrongCodeEvent = 2009;

    private readonly ILogger _security = SecurityEvents.CreateLogger(loggers);

    /// <summary>
    /// <c>POST /bff/setup/session</c>: a session for the right code (its cookie set on <paramref name="http"/>), always: the code is
    /// compared first, so no number of wrong codes keeps the right one out. A wrong code is <c>setup_code_invalid</c>, and after
    /// <see cref="SetupCodeThrottle.Limit"/> wrong codes a minute on this instance <c>setup_attempts_exhausted</c> (429 with
    /// <c>Retry-After</c>).
    /// </summary>
    public async Task<IResult> StartSessionAsync(HttpContext http, SetupStatus status, string? code, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(status);
        var address = http.Items[AdminClientAddresses.ItemKey] as string;
        var result = await sessions.StartAsync(status, code, ct);
        switch (result.Outcome)
        {
            case SetupCodeOutcome.Started:
                SetupSessions.SetCookie(http, result.Token!, result.Session!.ExpiresAt);
                SecurityEvents.AdminSetupSessionStarted(_security, SetupSessions.EventId(result.Session), address, result.Session.ExpiresAt);
                return Results.Ok(new SetupSessionDto(result.Session.ExpiresAt, status.EndsAt!.Value));
            case SetupCodeOutcome.Wrong:
                // Only wrong codes count: the throttle slows a guesser and bounds the log, and can never refuse the right code.
                if (!throttle.TryAcquire(out var retryAfter))
                {
                    http.Response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
                    return Results.Problem(statusCode: StatusCodes.Status429TooManyRequests, detail: "Too many wrong setup codes. Wait a minute and try again.",
                        extensions: new Dictionary<string, object?> { ["code"] = SetupErrors.AttemptsExhausted });
                }

                if (sampler.ShouldLog(WrongCodeEvent, out var count))
                {
                    SecurityEvents.AdminSetupCodeRefused(_security, address, count);
                }

                throw SetupErrors.WrongCode();
            default:
                return Results.NotFound();
        }
    }

    /// <summary><c>GET /bff/setup/state</c>.</summary>
    public async Task<SetupStateDto> StateAsync(HttpContext http, SetupStatus status, SetupSession session, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(session);
        var options = oidc.Get(OpenIdConnectDefaults.AuthenticationScheme);
        var latest = await LatestTestAsync(session, ct);
        string[] adminGroups = deployment.Value.RoleGroups.TryGetValue(AdminRoles.GatewayAdmin, out var groups)
            ? groups.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries) : [];
        return new SetupStateDto(status.Kind, status.EndsAt!.Value, session.ExpiresAt, SignInSettingsDto.Of(await signIn.ReadAsync(ct)),
            new SetupRedirectUris(SetupTestSignIn.RedirectUri(http, options.CallbackPath), SetupTestSignIn.RedirectUri(http, options.SignedOutCallbackPath)),
            adminGroups, latest is null ? null : SetupTestDto.Of(latest, tests.PayloadOf(latest), time.GetUtcNow()));
    }

    /// <summary>
    /// <c>POST /bff/setup/complete</c>: saves the values of the session's latest test when it passed, was made with exactly the submitted
    /// values and its person came out as a gateway admin (else <c>setup_test_required</c> with the reason). Values the deployment sets
    /// are not stored. In the same transaction, under <see cref="SetupLock"/>, setup is marked completed, the nonce cleared and every
    /// session deleted; a completion that finds its opening gone (another completion won the race) answers 404. The audit entry's
    /// actor is the tested person.
    /// </summary>
    public async Task<SetupCompletedDto> CompleteAsync(HttpContext http, SetupSession session, SetupCompleteRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(request);
        var values = request.ToWizardValues().Normalized();
        var latest = await LatestTestAsync(session, ct);
        var test = await admin.Db.SetupTests.AsNoTracking().SingleOrDefaultAsync(t => t.Id == request.TestId && t.SessionId == session.Id, ct);
        var payload = test is null ? null : tests.PayloadOf(test);
        var result = test?.Result is { } json ? JsonSerializer.Deserialize<SetupTestResult>(json, SetupTestSignIn.Json) : null;
        var refusal = test is null || payload is null ? "unknown_test"
            : latest?.Id != test.Id ? "not_latest"
            : test.Passed != true || result is null ? "not_passed"
            : !result.IsGatewayAdmin ? "not_gateway_admin"
            : !CryptographicOperations.FixedTimeEquals(test.ValuesFingerprint, SetupTestSignIn.Fingerprint(values, tests.MappingOf(values))) ? "values_changed"
            // What is about to be in force must be what the test ran against: the deployment's values are read again here, and
            // they may differ from the instance and moment the test started on (a rolling change of Oidc:Authority).
            : !TestedProviderIsInForce(payload, values) ? "values_changed"
            : null;
        if (refusal is not null)
        {
            // A completion that lost the race with another one finds its session and tests deleted: setup has closed, which is a 404
            // like every other request after it, not a missing test.
            if (!(await setup.ReadAsync(ct)).Open)
            {
                throw SetupErrors.Closed();
            }

            throw SetupErrors.TestMissing(refusal);
        }

        // The audit actor is the tested person, whoever the chosen provider says signed in.
        var actor = AdminActor.ForSetup(http, result!.Subject, result.Name, result.Roles, result.DepartmentCodes, SetupSessions.EventId(session));
        var now = time.GetUtcNow();
        await signIn.SaveWizardValuesAsync(SignInSettingsService.WithoutDeployed(configuration, values), StoredSecretOf(payload!.SecretSource, payload.Secret), actor,
            async (row, attempt) =>
            {
                // Under the lock: a second completion of the same opening finds it closed.
                if (row.SetupNonce is null || row.SetupNonce != session.OpeningNonce)
                {
                    throw SetupErrors.Closed();
                }

                row.SetupCompletedAt = now;
                row.SetupNonce = null;
                await admin.Db.SetupSessions.ExecuteDeleteAsync(attempt);
            }, ct);
        SetupSessions.DeleteCookie(http);
        SecurityEvents.AdminSetupCompleted(_security, actor.Subject, actor.SessionId, actor.SourceAddress);
        // This instance's /bff/user reports setup closed at once; the others within AdminSignIn.UserRefreshInterval.
        await adminSignIn.RefreshAsync(signIn, oidc, oidcCache, ct);
        return new SetupCompletedDto(SignInUrl);
    }

    /// <summary>Whether the authority and client the test signed in with are the ones <paramref name="values"/> give with this instance's deployment values.</summary>
    private bool TestedProviderIsInForce(SetupTestPayload payload, SignInWizardValues values)
    {
        var effective = SignInSettingsService.Resolve(configuration, values, () => StoredSecretValue.NotSet, null, null);
        return string.Equals(effective.Authority.Value?.Trim(), payload.Authority.Trim(), StringComparison.Ordinal) &&
               string.Equals(effective.ClientId.Value.Trim(), payload.ClientId.Trim(), StringComparison.Ordinal);
    }

    private Task<SetupTest?> LatestTestAsync(SetupSession session, CancellationToken ct) =>
        admin.Db.SetupTests.AsNoTracking().Where(t => t.SessionId == session.Id)
            .OrderByDescending(t => t.CreatedAt).ThenByDescending(t => t.Id).FirstOrDefaultAsync(ct);

    /// <summary>What completing stores of a test's secret: the typed one, an empty value (remove) for a test without one, else nothing (keep).</summary>
    private static string? StoredSecretOf(string secretSource, string? secret) => secretSource switch
    {
        SetupSecretSources.Typed => secret,
        SetupSecretSources.Removed => "",
        _ => null,
    };
}
