using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Npgsql;
using Ume.LlmGateway.AdminApi.Settings;

namespace Ume.LlmGateway.AdminApi.Security;

/// <summary>Why the admin API cannot start a sign-in (<see cref="SignInSnapshot.NotConfiguredReason"/>); logged, never answered.</summary>
public static class SignInNotConfiguredReasons
{
    /// <summary>Neither the deployment nor the database sets an authority.</summary>
    public const string NoAuthority = "no_authority";

    /// <summary>The authority is not an absolute https URL (http is allowed in Development only, as <c>RequireHttpsMetadata</c>).</summary>
    public const string InvalidAuthority = "invalid_authority";

    /// <summary>A client secret is stored but the key ring cannot unprotect it (<see cref="SignInClientSecret.Unreadable"/>).</summary>
    public const string UnreadableClientSecret = "unreadable_client_secret";
}

/// <summary>
/// The admin console's effective sign-in values at one moment: what the OpenID Connect options are built from and what the
/// sign-in's claim mapping reads. Two snapshots are compared by a SHA-256 fingerprint of every value, the client secret
/// included, so a changed row and a changed deployment value are both seen; the fingerprint never leaves the class.
/// </summary>
public sealed class SignInSnapshot
{
    private readonly byte[] _fingerprint;

    private SignInSnapshot(EffectiveSignIn values, string? notConfiguredReason, byte[] fingerprint)
    {
        Values = values;
        NotConfiguredReason = notConfiguredReason;
        _fingerprint = fingerprint;
    }

    /// <summary>The values with their sources; <see cref="SignInClientSecret.Value"/> is for the options only.</summary>
    public EffectiveSignIn Values { get; }

    /// <summary>A <see cref="SignInNotConfiguredReasons"/> value, or null when a sign-in can start.</summary>
    public string? NotConfiguredReason { get; }

    public bool IsConfigured => NotConfiguredReason is null;

    /// <param name="values">The effective values (<see cref="SignInSettingsService.ReadAsync"/>).</param>
    /// <param name="requireHttps">As <c>RequireHttpsMetadata</c>: true outside Development.</param>
    public static SignInSnapshot Of(EffectiveSignIn values, bool requireHttps)
    {
        ArgumentNullException.ThrowIfNull(values);
        var reason = values.Authority.Value is null ? SignInNotConfiguredReasons.NoAuthority
            : !OidcOptions.IsUsableAuthority(values.Authority.Value, requireHttps) ? SignInNotConfiguredReasons.InvalidAuthority
            : values.ClientSecret.Unreadable ? SignInNotConfiguredReasons.UnreadableClientSecret
            : null;
        return new SignInSnapshot(values, reason, Fingerprint(values));
    }

    /// <summary>Whether both hold the same values (when the row was saved is not compared).</summary>
    public bool HasSameValues(SignInSnapshot other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return CryptographicOperations.FixedTimeEquals(_fingerprint, other._fingerprint);
    }

    /// <summary>The values as the claim mapping takes them (<see cref="AdminAuthentication.ApplyClaimMapping"/>), with the deployment's role groups.</summary>
    public OidcOptions ToOidcOptions(IReadOnlyDictionary<string, string> roleGroups)
    {
        ArgumentNullException.ThrowIfNull(roleGroups);
        return new OidcOptions
        {
            Authority = Values.Authority.Value, ClientId = Values.ClientId.Value, RoleClaim = Values.RoleClaim.Value, DepartmentClaim = Values.DepartmentClaim.Value,
            TeamClaim = Values.TeamClaim.Value, GroupClaim = Values.GroupClaim.Value, ExtraScopes = [.. Values.ExtraScopes.Value],
            RoleGroups = new Dictionary<string, string>(roleGroups),
        };
    }

    public override string ToString() => $"configured: {IsConfigured}";

    private static byte[] Fingerprint(EffectiveSignIn v)
    {
        // Length-prefixed, so no two different value sets give the same text.
        var text = new StringBuilder();
        void Add(string? value) => text.Append(value is null ? "-1" : value.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(value).Append('|');
        Add(v.Authority.Value);
        Add(v.ClientId.Value);
        Add(v.ClientSecret.Value);
        Add(v.ClientSecret.Unreadable ? "unreadable" : "readable");
        Add(v.RoleClaim.Value);
        Add(v.DepartmentClaim.Value);
        Add(v.TeamClaim.Value);
        Add(v.GroupClaim.Value);
        Add(v.ExtraScopes.Value.Count.ToString(CultureInfo.InvariantCulture));
        foreach (var scope in v.ExtraScopes.Value)
        {
            Add(scope);
        }

        return SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString()));
    }
}

/// <summary>
/// The sign-in values the admin API's OpenID Connect options are built from (thekim1/llmgateway#100). <see cref="Configure"/>
/// is the options' configure step for the first build and every rebuild; it takes <see cref="Current"/>, which
/// <see cref="RefreshAsync"/> reads from the database before the authentication middleware creates the handler for a sign-in
/// start, a callback, a sign-out or <c>/bff/user</c>. When the values differ from what the live options were built from, the
/// options are evicted from the options cache, so the next use builds them again, with the new authority's metadata. Nothing is
/// shared between instances: each follows a change at its own next sign-in, without a restart.
/// </summary>
public sealed partial class AdminSignIn(IConfiguration configuration, IHostEnvironment environment, TimeProvider time, ILogger<AdminSignIn> logger) : IDisposable
{
    /// <summary>How long <c>/bff/user</c>, which anyone may call, answers from the values last read before it reads them again.</summary>
    public static readonly TimeSpan UserRefreshInterval = TimeSpan.FromSeconds(10);

    private const string Scheme = OpenIdConnectDefaults.AuthenticationScheme;

    /// <summary>Which snapshot each live options instance was built from; weak, so evicted options are collected.</summary>
    private readonly ConditionalWeakTable<OpenIdConnectOptions, SignInSnapshot> _builtFrom = new();

    /// <summary>One refresh at a time, so a slower read never replaces a newer one.</summary>
    private readonly SemaphoreSlim _refresh = new(1, 1);

    private readonly bool _requireHttps = !environment.IsDevelopment();

    /// <summary>Until the first read: the deployment's values alone (nothing is read from the database at start).</summary>
    private volatile SignInSnapshot _current = SignInSnapshot.Of(
        SignInSettingsService.Resolve(configuration, new SignInWizardValues(), () => StoredSecretValue.NotSet, null, null), !environment.IsDevelopment());

    /// <summary>When the values were last read, in UTC ticks; 0 before the first read.</summary>
    private long _readAt;

    /// <summary>The latest values read; the sign-in's own steps take <see cref="For"/> instead.</summary>
    public SignInSnapshot Current => _current;

    /// <summary>
    /// Whether the values are read before handling <paramref name="path"/>: the sign-in start, the callback and the sign-out
    /// always; <c>/bff/user</c> once per <see cref="UserRefreshInterval"/>.
    /// </summary>
    public bool Refreshes(PathString path, OpenIdConnectOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (path.Equals(AdminAuthentication.UserPath, StringComparison.OrdinalIgnoreCase))
        {
            return time.GetUtcNow().UtcTicks - Interlocked.Read(ref _readAt) >= UserRefreshInterval.Ticks;
        }

        return path.Equals(AdminAuthentication.LoginPath, StringComparison.OrdinalIgnoreCase) ||
               path.Equals(AdminAuthentication.LogoutPath, StringComparison.OrdinalIgnoreCase) || path == options.CallbackPath;
    }

    /// <summary>The snapshot <paramref name="options"/> were built from (the handler's own), else <see cref="Current"/>.</summary>
    public SignInSnapshot For(OpenIdConnectOptions options) => _builtFrom.TryGetValue(options, out var snapshot) ? snapshot : _current;

    /// <summary>
    /// The configure step of the scheme's options: authority, client and secret, extra scopes. Without a usable authority it
    /// gives an empty static configuration, so the options stay valid, nothing is fetched, and the challenge is refused
    /// (<see cref="AdminAuthentication"/>'s <c>OnRedirectToIdentityProvider</c>).
    /// </summary>
    public void Configure(OpenIdConnectOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var snapshot = _current;
        options.ClientId = snapshot.Values.ClientId.Value;
        if (snapshot.IsConfigured)
        {
            options.Authority = snapshot.Values.Authority.Value;
            options.ClientSecret = snapshot.Values.ClientSecret.Value;
        }
        else
        {
            options.Configuration = new OpenIdConnectConfiguration();
        }

        foreach (var scope in snapshot.Values.ExtraScopes.Value)
        {
            options.Scope.Add(scope);
        }

        _builtFrom.AddOrUpdate(options, snapshot);
    }

    /// <summary>
    /// Reads the effective values (one primary-key read) and, when they differ from what the live options were built from, evicts
    /// the options so they are built again from them. Returns the values read.
    /// </summary>
    public async Task<SignInSnapshot> RefreshAsync(SignInSettingsService settings, IOptionsMonitor<OpenIdConnectOptions> monitor,
        IOptionsMonitorCache<OpenIdConnectOptions> cache, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(monitor);
        ArgumentNullException.ThrowIfNull(cache);
        await _refresh.WaitAsync(ct);
        try
        {
            var fresh = SignInSnapshot.Of(await settings.ReadAsync(ct), _requireHttps);
            _current = fresh;
            Interlocked.Exchange(ref _readAt, time.GetUtcNow().UtcTicks);
            // A build that started before _current changed may land after an eviction; the check after each build catches it.
            for (var attempt = 0; attempt < 3; attempt++)
            {
                if (_builtFrom.TryGetValue(monitor.Get(Scheme), out var built) && built.HasSameValues(fresh))
                {
                    if (attempt > 0)
                    {
                        LogRebuilt(logger, fresh.IsConfigured);
                        if (fresh.NotConfiguredReason is { } reason)
                        {
                            LogNotConfigured(logger, reason);
                        }
                    }

                    return fresh;
                }

                cache.TryRemove(Scheme);
            }

            throw new InvalidOperationException("The sign-in options could not be rebuilt from the current sign-in values.");
        }
        finally
        {
            _refresh.Release();
        }
    }

    public void Dispose() => _refresh.Dispose();

    [LoggerMessage(Level = LogLevel.Information, Message = "Sign-in options rebuilt from changed sign-in values; configured: {Configured}")]
    private static partial void LogRebuilt(ILogger logger, bool configured);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Sign-in is not configured ({Reason}): a sign-in is refused until the values are set")]
    private static partial void LogNotConfigured(ILogger logger, string reason);
}

/// <summary>
/// Runs <see cref="AdminSignIn.RefreshAsync"/> before the authentication middleware on the paths of
/// <see cref="AdminSignIn.Refreshes"/>, so the handler created for the request has the current values. Every other request
/// reads nothing.
/// </summary>
public sealed partial class AdminSignInRefreshMiddleware(RequestDelegate next, ILogger<AdminSignInRefreshMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext http, AdminSignIn signIn, IOptionsMonitor<OpenIdConnectOptions> monitor, IOptionsMonitorCache<OpenIdConnectOptions> cache)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(signIn);
        ArgumentNullException.ThrowIfNull(monitor);
        if (signIn.Refreshes(http.Request.Path, monitor.Get(OpenIdConnectDefaults.AuthenticationScheme)))
        {
            try
            {
                await signIn.RefreshAsync(http.RequestServices.GetRequiredService<SignInSettingsService>(), monitor, cache, http.RequestAborted);
            }
            catch (Exception ex) when (ex is NpgsqlException or TimeoutException or RetryLimitExceededException or InvalidOperationException { InnerException: NpgsqlException })
            {
                // The database is away: go on with the values last read, so the sign-in page still loads. A sign-in itself needs
                // the session store and fails there if that is away too.
                LogRefreshFailed(logger, ex.GetType().Name);
            }
        }

        await next(http);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The sign-in values could not be read ({ExceptionType}); the last values read stay in use")]
    private static partial void LogRefreshFailed(ILogger logger, string exceptionType);
}
