using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Ume.LlmGateway.AdminApi.Security;
using Ume.LlmGateway.AdminApi.Settings;
using Ume.LlmGateway.Domain.Entities;
using Ume.LlmGateway.Infrastructure.Persistence;
using Ume.LlmGateway.Infrastructure.Security;

namespace Ume.LlmGateway.AdminApi.Setup;

/// <summary>Where a test's client secret came from; completing stores it only when it was typed.</summary>
public static class SetupSecretSources
{
    /// <summary>The deployment sets it; a typed one is ignored and never stored.</summary>
    public const string Deployment = "deployment";

    public const string Typed = "typed";

    /// <summary>None typed: the stored one was used, and stays.</summary>
    public const string Stored = "stored";

    /// <summary>An empty one typed: the test ran without a secret (a public client), and completing removes the stored one.</summary>
    public const string Removed = "removed";

    /// <summary>No secret anywhere: a public client.</summary>
    public const string None = "none";
}

/// <summary>
/// What the test row keeps protected (never answered, logged or audited). <see cref="Authority"/> and <see cref="ClientId"/> are the
/// effective ones the test started with; <see cref="Secret"/> is set only for a typed or stored secret, never a deployment one.
/// </summary>
public sealed record SetupTestPayload(
    SignInWizardValues Values, string Authority, string ClientId, string SecretSource, string? Secret, ProviderMetadata Provider, string RedirectUri,
    string CodeVerifier, string Nonce);

/// <summary>
/// The test sign-in: a hand-written authorization-code sign-in with candidate values and the admin API's own redirect URI
/// (<c>/signin-oidc</c>, so nothing extra is registered at the provider). <c>state</c> is <see cref="StatePrefix"/> and a 256-bit
/// random marker (the OpenID Connect handler's own state is base64url and never holds a dot); the test row, single use and
/// <see cref="Lifetime"/>, keeps the PKCE verifier, nonce and candidate values protected, so the callback may land on another
/// instance. The callback exchanges the code as the handler does (secret in the form, PKCE S256), validates the id token against
/// the candidate's metadata (issuer, audience = client id, signature, lifetime, nonce), and runs the same claim mapping as a real
/// sign-in (<see cref="AdminAuthentication.ApplyClaimMapping"/>) on the candidate claim names with the deployment's role groups and
/// the departments' member groups. No admin session is created and no token is kept; the only cookie is the test cookie, which
/// binds the callback to the browser that started the test.
/// </summary>
public sealed class SetupTestSignIn(GatewayDbContext db, IConfiguration configuration, IHostEnvironment environment, TimeProvider time, SetupMode setup,
    SetupProviderClient provider, IDataProtectionProvider protection, SignInSettingsService signIn, SetupEventSampler sampler, SetupCleanup cleanup,
    IOptions<OidcOptions> deployment, IOptionsMonitor<OpenIdConnectOptions> oidc, ILoggerFactory loggers)
{
    public const string StatePrefix = "ume-setup-test.";

    /// <summary>The test cookie, set by <c>POST /bff/setup/test</c> on the callback path and required at the callback.</summary>
    public const string TestCookieName = "__Secure-ume-setup-test";

    /// <summary>Where every callback ends, whatever happened: the result is never in the address.</summary>
    public const string DonePath = "/setup?test=done";

    /// <summary>The Data Protection purpose of a test's payload; no other ciphertext unprotects as it.</summary>
    public const string Purpose = "Ume.LlmGateway.AdminApi.SetupTest.v1";

    /// <summary>The event id sampled for callbacks of an unknown test (security event <c>admin.setup.test</c>).</summary>
    public const int UnknownTestEvent = 2011;

    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private const int MarkerLength = 43; // 32 random bytes, base64url

    /// <summary>The handler's scopes: its defaults openid and profile, and email (<see cref="AdminAuthentication.AddAdminAuthentication"/>).</summary>
    private static readonly string[] BaseScopes = ["openid", "profile", "email"];

    private const int MaxListed = 100;
    private const int MaxValue = 200;

    private readonly IDataProtector _protector = protection.CreateProtector(Purpose);

    private bool RequireHttps => !environment.IsDevelopment();

    /// <summary>
    /// Starts a test for <paramref name="session"/>: resolves the candidate through the same precedence as the live values (a
    /// deployment value wins), applies the secret rules (<see cref="SecretRefusal"/>), fetches and checks the discovery document
    /// and keys, stores the test and sets the test cookie that binds the callback to this browser. Throws <c>setup_test_refused</c>
    /// with a <see cref="SetupTestReasons"/> code.
    /// </summary>
    public async Task<SetupTestStart> StartAsync(HttpContext http, SetupSession session, SetupValuesRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(request);
        var values = request.ToWizardValues().Normalized();
        var typed = request.ClientSecret;
        var live = await signIn.ReadAsync(ct);
        var effective = SignInSettingsService.Resolve(configuration, values,
            () => typed is null ? StoredSecretOf(live.ClientSecret)
                : string.IsNullOrWhiteSpace(typed) ? StoredSecretValue.NotSet : StoredSecretValue.Readable(typed),
            null, null);
        var snapshot = SignInSnapshot.Of(effective, RequireHttps);
        if (snapshot.NotConfiguredReason == SignInNotConfiguredReasons.UnreadableClientSecret)
        {
            throw SetupErrors.Refused(SetupTestReasons.StoredSecretUnreadable);
        }

        if (snapshot.NotConfiguredReason is not null || !Uri.TryCreate(effective.Authority.Value, UriKind.Absolute, out var authorityUri) ||
            !SetupDiscovery.Allowed(authorityUri, RequireHttps) || authorityUri.Query.Length > 0 || authorityUri.Fragment.Length > 0)
        {
            throw SetupErrors.Refused(SetupTestReasons.AuthorityInvalid);
        }

        var secretSource = effective.ClientSecret.Source == SignInValueSources.Deployment ? SetupSecretSources.Deployment
            : typed is null ? effective.ClientSecret.Value is null ? SetupSecretSources.None : SetupSecretSources.Stored
            : string.IsNullOrWhiteSpace(typed) ? SetupSecretSources.Removed : SetupSecretSources.Typed;
        var authority = effective.Authority.Value!;
        if (SecretRefusal(secretSource, effective, live, authority, effective.ClientId.Value) is { } secretRefusal)
        {
            throw SetupErrors.Refused(secretRefusal);
        }

        var (metadata, refusal) = await SetupDiscovery.FetchAsync(provider, authority, RequireHttps, ct);
        if (refusal is not null)
        {
            throw SetupErrors.Refused(refusal);
        }

        if (await SetupDiscovery.KeysAsync(provider, metadata!, ct) is null)
        {
            throw SetupErrors.Refused(SetupTestReasons.KeysUnavailable);
        }

        var marker = SetupMode.Base64Url(RandomNumberGenerator.GetBytes(32));
        var browser = SetupMode.Base64Url(RandomNumberGenerator.GetBytes(32));
        var verifier = SetupMode.Base64Url(RandomNumberGenerator.GetBytes(32));
        var nonce = SetupMode.Base64Url(RandomNumberGenerator.GetBytes(32));
        var options = oidc.Get(OpenIdConnectDefaults.AuthenticationScheme);
        var redirectUri = RedirectUri(http, options.CallbackPath);
        // A deployment secret is never copied into the row: the callback reads it from the deployment again.
        var payload = new SetupTestPayload(values, authority, effective.ClientId.Value, secretSource,
            secretSource is SetupSecretSources.Typed or SetupSecretSources.Stored ? effective.ClientSecret.Value : null, metadata!, redirectUri, verifier, nonce);

        var now = time.GetUtcNow();
        var test = new SetupTest
        {
            Id = Guid.NewGuid(), SessionId = session.Id, MarkerHash = SetupSessions.Hash(marker), BrowserHash = SetupSessions.Hash(browser),
            Protected = _protector.Protect(JsonSerializer.Serialize(payload, Json)), ValuesFingerprint = Fingerprint(values), CreatedAt = now,
            ExpiresAt = now + Lifetime,
        };
        db.SetupTests.Add(test);
        await db.SaveChangesAsync(ct);
        http.Response.Cookies.Append(TestCookieName, browser, TestCookie(options.CallbackPath, test.ExpiresAt));

        // As the handler's challenge (scopes openid, profile, email and the extra ones; code with PKCE S256), with form_post as the
        // handler's default response mode.
        var message = new OpenIdConnectMessage
        {
            IssuerAddress = metadata!.AuthorizationEndpoint, ClientId = effective.ClientId.Value, RedirectUri = redirectUri,
            ResponseType = OpenIdConnectResponseType.Code, ResponseMode = OpenIdConnectResponseMode.FormPost,
            Scope = string.Join(' ', BaseScopes.Concat(effective.ExtraScopes.Value).Distinct(StringComparer.Ordinal)),
            State = StatePrefix + marker, Nonce = nonce,
        };
        message.Parameters.Add("code_challenge", SetupMode.Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))));
        message.Parameters.Add("code_challenge_method", "S256");
        return new SetupTestStart(test.Id, message.CreateAuthenticationRequestUrl(), test.ExpiresAt);
    }

    /// <summary>
    /// The callback of a test (<see cref="SetupCallbackMiddleware"/>): claims the test once, requires the test cookie of the browser
    /// that started it, applies the secret rules again, runs the exchange and the checks, stores the result and sends the browser to
    /// <see cref="DonePath"/>. A replayed, forged or expired marker gets the same redirect and a sampled <c>unknown_test</c> event.
    /// While setup is closed it answers 404.
    /// </summary>
    public async Task HandleCallbackAsync(HttpContext http, string state, string? code, bool providerError)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(state);
        var ct = http.RequestAborted;
        var options = oidc.Get(OpenIdConnectDefaults.AuthenticationScheme);
        var browser = http.Request.Cookies[TestCookieName];
        http.Response.Cookies.Delete(TestCookieName, TestCookie(options.CallbackPath, null));
        var status = await setup.ReadAsync(ct);
        if (!status.Open)
        {
            await cleanup.AfterClosedAsync(status, setup, ct);
            http.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var security = SecurityEvents.CreateLogger(loggers);
        var address = http.Items[AdminClientAddresses.ItemKey] as string;
        var marker = state[StatePrefix.Length..];
        var now = time.GetUtcNow();
        SetupTest? test = null;
        if (marker.Length == MarkerLength && marker.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
        {
            var hash = SetupSessions.Hash(marker);
            // Single use across instances: only one callback moves CallbackAt from null.
            if (await db.SetupTests.Where(t => t.MarkerHash == hash && t.CallbackAt == null && t.ExpiresAt > now)
                    .ExecuteUpdateAsync(s => s.SetProperty(t => t.CallbackAt, now), ct) == 1)
            {
                test = await db.SetupTests.SingleAsync(t => t.MarkerHash == hash, ct);
            }
        }

        if (test is null)
        {
            if (sampler.ShouldLog(UnknownTestEvent, out var count))
            {
                SecurityEvents.AdminSetupTest(security, "failed", SetupTestReasons.UnknownTest, null, false, null, address, count);
            }

            http.Response.Redirect(DonePath);
            return;
        }

        var session = await db.SetupSessions.AsNoTracking().SingleOrDefaultAsync(s => s.Id == test.SessionId && s.OpeningNonce == status.Nonce && s.ExpiresAt > now, ct);
        var sameBrowser = browser is { Length: > 0 and <= 100 } && CryptographicOperations.FixedTimeEquals(SetupSessions.Hash(browser), test.BrowserHash);
        (string Reason, SetupTestResult? Result) outcome = session is null ? (SetupTestReasons.SessionExpired, null)
            : !sameBrowser ? (SetupTestReasons.BrowserMismatch, null)
            : providerError || string.IsNullOrEmpty(code) ? (SetupTestReasons.ProviderError, null)
            : await RunAsync(test, code, ct);
        var (reason, result) = outcome;
        test.Passed = reason == SetupTestReasons.Passed;
        test.Reason = test.Passed == true ? null : reason;
        test.Result = result is null ? null : JsonSerializer.Serialize(result, Json);
        await db.SaveChangesAsync(CancellationToken.None);
        SecurityEvents.AdminSetupTest(security, test.Passed == true ? "passed" : "failed", reason, result?.Subject, result?.IsGatewayAdmin == true,
            session is null ? null : SetupSessions.EventId(session), address, 1);
        http.Response.Redirect(DonePath);
    }

    /// <summary>The protected part of <paramref name="test"/>, or null when the key ring cannot read it.</summary>
    public SetupTestPayload? PayloadOf(SetupTest test)
    {
        ArgumentNullException.ThrowIfNull(test);
        try
        {
            return JsonSerializer.Deserialize<SetupTestPayload>(_protector.Unprotect(test.Protected), Json);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    /// <summary>
    /// SHA-256 of the tested values without the secret (the secret is taken from the test row, never sent again). Completing saves
    /// only values with the same fingerprint.
    /// </summary>
    public static byte[] Fingerprint(SignInWizardValues values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(values.Normalized(), Json)));
    }

    /// <summary>The redirect URI as the OpenID Connect handler builds it: the request's scheme and host (behind the proxy, its Host header), path base and <paramref name="path"/>.</summary>
    public static string RedirectUri(HttpContext http, PathString path)
    {
        ArgumentNullException.ThrowIfNull(http);
        return http.Request.Scheme + Uri.SchemeDelimiter + http.Request.Host.ToUriComponent() + http.Request.PathBase.ToUriComponent() + path.ToUriComponent();
    }

    /// <summary>The stored secret as the live values read it: readable, unreadable, or none (a deployment secret is not a stored one).</summary>
    private static StoredSecretValue StoredSecretOf(SignInClientSecret live) =>
        live.Source != SignInValueSources.Wizard ? StoredSecretValue.NotSet
        : live.Unreadable ? StoredSecretValue.Unreadable
        : live.Value is { } value ? StoredSecretValue.Readable(value)
        : StoredSecretValue.NotSet;

    /// <summary>
    /// The secret rules: the stored secret goes only to the provider and client it was stored for (the candidate's effective authority
    /// and client id equal the ones in force, <paramref name="live"/>), and a deployment secret only to the deployment's own authority.
    /// Checked when a test starts and again at its callback, before any secret is sent.
    /// </summary>
    private static string? SecretRefusal(string secretSource, EffectiveSignIn candidate, EffectiveSignIn live, string authority, string clientId) => secretSource switch
    {
        SetupSecretSources.Deployment => candidate.ClientSecret.Source == SignInValueSources.Deployment && candidate.Authority.Source == SignInValueSources.Deployment &&
                                         Same(candidate.Authority.Value, authority) ? null : SetupTestReasons.DeploymentSecretWithoutAuthority,
        SetupSecretSources.Stored => Same(live.Authority.Value, authority) && Same(live.ClientId.Value, clientId) ? null : SetupTestReasons.SecretRequired,
        _ => null,
    };

    private static bool Same(string? a, string? b) => string.Equals(a?.Trim(), b?.Trim(), StringComparison.Ordinal);

    /// <summary>
    /// The test cookie: <c>SameSite=None</c>, since the provider's form_post back is a cross-site POST, scoped to the callback path and
    /// expiring with the test.
    /// </summary>
    private static CookieOptions TestCookie(PathString callbackPath, DateTimeOffset? expiresAt) => new()
    {
        HttpOnly = true, Secure = true, SameSite = SameSiteMode.None, Path = callbackPath.Value, Expires = expiresAt, IsEssential = true,
    };

    /// <summary>The candidate's effective values at the callback, through the same precedence (the deployment may have changed since).</summary>
    private EffectiveSignIn Effective(SetupTestPayload payload) => SignInSettingsService.Resolve(configuration, payload.Values,
        () => payload.Secret is null ? StoredSecretValue.NotSet : StoredSecretValue.Readable(payload.Secret), null, null);

    private async Task<(string Reason, SetupTestResult? Result)> RunAsync(SetupTest test, string code, CancellationToken ct)
    {
        if (PayloadOf(test) is not { } payload)
        {
            return (SetupTestReasons.UnknownTest, null);
        }

        var effective = Effective(payload);
        // Again before anything is sent: the deployment or the stored values may have changed since the test started.
        if (SecretRefusal(payload.SecretSource, effective, await signIn.ReadAsync(ct), payload.Authority, payload.ClientId) is { } secretRefusal)
        {
            return (secretRefusal, null);
        }

        var secret = payload.SecretSource == SetupSecretSources.Deployment ? effective.ClientSecret.Value : payload.Secret;
        List<KeyValuePair<string, string>> form =
        [
            new("grant_type", "authorization_code"), new("code", code), new("redirect_uri", payload.RedirectUri), new("client_id", payload.ClientId),
            new("code_verifier", payload.CodeVerifier),
        ];
        if (!string.IsNullOrEmpty(secret))
        {
            form.Add(new("client_secret", secret));
        }

        var tokenEndpoint = new Uri(payload.Provider.TokenEndpoint);
        if (!SetupDiscovery.Allowed(tokenEndpoint, RequireHttps))
        {
            return (SetupTestReasons.InsecureEndpoint, null);
        }

        var token = await provider.PostFormAsync(tokenEndpoint, form, ct);
        if (SetupDiscovery.Refusal(token.Outcome, SetupTestReasons.TokenEndpointUnreachable, SetupTestReasons.TokenRefused) is { } failed)
        {
            return (failed, null);
        }

        string? idToken;
        try
        {
            using var json = JsonDocument.Parse(token.Body!);
            idToken = json.RootElement.ValueKind == JsonValueKind.Object && json.RootElement.TryGetProperty("id_token", out var value) &&
                      value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        }
        catch (JsonException)
        {
            return (SetupTestReasons.TokenRefused, null);
        }

        if (string.IsNullOrEmpty(idToken))
        {
            return (SetupTestReasons.IdTokenMissing, null);
        }

        if (!SetupDiscovery.Allowed(new Uri(payload.Provider.JwksUri), RequireHttps) || await SetupDiscovery.KeysAsync(provider, payload.Provider, ct) is not { } keys)
        {
            return (SetupTestReasons.KeysUnavailable, null);
        }

        // As the handler's TokenValidationParameters: issuer from the metadata, audience the client id, the metadata's keys, the
        // default lifetime check and clock skew; MapInboundClaims off; name and role claim types as AddAdminAuthentication sets them.
        var validated = await new JsonWebTokenHandler { MapInboundClaims = false }.ValidateTokenAsync(idToken, new TokenValidationParameters
        {
            ValidIssuer = payload.Provider.Issuer, ValidAudience = payload.ClientId, IssuerSigningKeys = keys, RequireSignedTokens = true,
            RequireExpirationTime = true, NameClaimType = "name", RoleClaimType = AdminClaims.Roles, AuthenticationType = OpenIdConnectDefaults.AuthenticationScheme,
        });
        if (!validated.IsValid || validated.ClaimsIdentity.FindFirst(AdminClaims.Subject) is null)
        {
            return (SetupTestReasons.IdTokenInvalid, null);
        }

        var identity = validated.ClaimsIdentity;
        if (identity.FindFirst(OpenIdConnectParameterNames.Nonce)?.Value is not { } nonce ||
            !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(nonce), Encoding.UTF8.GetBytes(payload.Nonce)))
        {
            return (SetupTestReasons.NonceMismatch, null);
        }

        // As OnTokenValidated: the candidate's claim names with the deployment's role groups and the departments' member groups.
        var principal = new ClaimsPrincipal(identity);
        var options = SignInSnapshot.Of(effective, RequireHttps).ToOidcOptions(deployment.Value.RoleGroups);
        var groups = principal.FindAll(options.GroupClaim).Select(c => c.Value).Distinct(StringComparer.Ordinal).ToList();
        var departments = await db.Departments.AsNoTracking()
            .Where(d => d.IsActive && d.MemberGroups.Count > 0)
            .Select(d => new DepartmentGroups(d.CostCenterCode, d.MemberGroups))
            .ToListAsync(ct);
        AdminAuthentication.ApplyClaimMapping(principal, options, departments);
        return (SetupTestReasons.Passed, ResultOf(principal, groups, options));
    }

    private static SetupTestResult ResultOf(ClaimsPrincipal principal, List<string> groups, OidcOptions options)
    {
        static string Shown(string value) => AdminActor.Printable(value.Length > MaxValue ? value[..MaxValue] : value)!;
        static IReadOnlyList<string> Listed(IEnumerable<string> values) => [.. values.Distinct(StringComparer.Ordinal).Take(MaxListed).Select(Shown)];
        return new SetupTestResult(
            Shown(principal.FindFirstValue(AdminClaims.Subject)!), principal.Identity?.Name is { } name ? Shown(name) : null,
            Listed(principal.FindAll(AdminClaims.Roles).Select(c => c.Value)), Listed(groups), groups.Count,
            Listed(principal.FindAll(AdminClaims.DepartmentCodes).Select(c => c.Value)), Listed(principal.FindAll(AdminClaims.TeamCodes).Select(c => c.Value)),
            principal.HasClaim(AdminClaims.Roles, AdminRoles.GatewayAdmin),
            new SetupClaimsRead(options.RoleClaim, options.GroupClaim, options.DepartmentClaim, options.TeamClaim));
    }
}

/// <summary>
/// Takes the callbacks of setup tests before authentication: only requests to the OpenID Connect callback path whose <c>state</c>
/// starts with <see cref="SetupTestSignIn.StatePrefix"/>, from the query (GET) or the form (POST, form_post). Every other request, a
/// real sign-in's callback included, passes on untouched; reading the form leaves it cached for the handler.
/// </summary>
public sealed class SetupCallbackMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext http, IOptionsMonitor<OpenIdConnectOptions> monitor)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(monitor);
        if (http.Request.Path == monitor.Get(OpenIdConnectDefaults.AuthenticationScheme).CallbackPath)
        {
            IEnumerable<KeyValuePair<string, Microsoft.Extensions.Primitives.StringValues>>? parameters = null;
            if (HttpMethods.IsGet(http.Request.Method))
            {
                parameters = http.Request.Query;
            }
            else if (HttpMethods.IsPost(http.Request.Method) &&
                     http.Request.ContentType?.StartsWith("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase) == true)
            {
                parameters = await http.Request.ReadFormAsync(http.RequestAborted);
            }

            var values = parameters?.ToDictionary(p => p.Key, p => p.Value.ToString(), StringComparer.Ordinal);
            if (values is not null && values.TryGetValue(OpenIdConnectParameterNames.State, out var state) &&
                state.StartsWith(SetupTestSignIn.StatePrefix, StringComparison.Ordinal))
            {
                await http.RequestServices.GetRequiredService<SetupTestSignIn>().HandleCallbackAsync(http, state,
                    values.GetValueOrDefault(OpenIdConnectParameterNames.Code), values.ContainsKey(OpenIdConnectParameterNames.Error));
                return;
            }
        }

        await next(http);
    }
}
