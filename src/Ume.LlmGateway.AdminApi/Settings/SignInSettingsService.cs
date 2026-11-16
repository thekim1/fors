using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Ume.LlmGateway.AdminApi.Auditing;
using Ume.LlmGateway.AdminApi.Common;
using Ume.LlmGateway.AdminApi.Security;
using Ume.LlmGateway.Domain.Entities;
using Ume.LlmGateway.Infrastructure.Stores;

namespace Ume.LlmGateway.AdminApi.Settings;

/// <summary>Where an effective sign-in value comes from.</summary>
public static class SignInValueSources
{
    /// <summary>The deployment's <c>Oidc:*</c> in the raw configuration.</summary>
    public const string Deployment = "deployment";

    /// <summary>The database (<see cref="SignInSettings"/>), set in setup mode.</summary>
    public const string Wizard = "wizard";

    /// <summary>The code's default (<see cref="OidcOptions"/>).</summary>
    public const string Default = "default";
}

/// <summary>
/// The sign-in values as stored in the database (<see cref="SignInSettings"/>): null is "not stored". The client secret is not
/// in it: it is stored apart, encrypted.
/// </summary>
public sealed record SignInWizardValues
{
    public string? Authority { get; init; }
    public string? ClientId { get; init; }
    public string? RoleClaim { get; init; }
    public string? DepartmentClaim { get; init; }
    public string? TeamClaim { get; init; }
    public string? GroupClaim { get; init; }
    public IReadOnlyList<string>? ExtraScopes { get; init; }

    /// <summary>When the stored client secret expires, as the person entered it; a reminder, nothing enforces it.</summary>
    public DateOnly? ClientSecretExpiresOn { get; init; }

    public static SignInWizardValues Of(SignInSettings row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return new SignInWizardValues
        {
            Authority = row.Authority, ClientId = row.ClientId, RoleClaim = row.RoleClaim, DepartmentClaim = row.DepartmentClaim, TeamClaim = row.TeamClaim,
            GroupClaim = row.GroupClaim, ExtraScopes = row.ExtraScopes, ClientSecretExpiresOn = row.ClientSecretExpiresOn,
        }.Normalized();
    }

    /// <summary>Trimmed, with empty strings and an empty scope list as not stored, so "stored" always means a value.</summary>
    public SignInWizardValues Normalized()
    {
        static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        List<string> scopes = [.. (ExtraScopes ?? []).Select(Clean).OfType<string>().Distinct(StringComparer.Ordinal)];
        return this with
        {
            Authority = Clean(Authority), ClientId = Clean(ClientId), RoleClaim = Clean(RoleClaim), DepartmentClaim = Clean(DepartmentClaim),
            TeamClaim = Clean(TeamClaim), GroupClaim = Clean(GroupClaim), ExtraScopes = scopes.Count == 0 ? null : scopes,
        };
    }
}

/// <summary>One effective sign-in value and its source (<see cref="SignInValueSources"/>).</summary>
public sealed record SignInValue<T>(T Value, string Source);

/// <summary>
/// The effective client secret, for server-side callers only: <see cref="ToString"/> leaves the value out, and the API answers
/// <see cref="SignInSecretStatus"/> instead. <see cref="Unreadable"/>: one is stored that the key ring cannot unprotect.
/// </summary>
public sealed class SignInClientSecret(string source, string? value, DateTimeOffset? setAt, bool unreadable)
{
    public string Source { get; } = source;

    [JsonIgnore]
    public string? Value { get; } = value;

    /// <summary>When it was stored; null for a deployment value.</summary>
    public DateTimeOffset? SetAt { get; } = setAt;

    public bool Unreadable { get; } = unreadable;

    public bool IsSet => Value is not null || Unreadable;

    public override string ToString() => $"{Source}, set: {IsSet}, unreadable: {Unreadable}";
}

/// <summary>The admin console's sign-in values as they apply now, and when the stored ones last changed.</summary>
public sealed record EffectiveSignIn(
    SignInValue<string?> Authority, SignInValue<string> ClientId, SignInClientSecret ClientSecret, SignInValue<string> RoleClaim,
    SignInValue<string> DepartmentClaim, SignInValue<string> TeamClaim, SignInValue<string> GroupClaim, SignInValue<IReadOnlyList<string>> ExtraScopes,
    DateOnly? ClientSecretExpiresOn, DateTimeOffset? UpdatedAt);

/// <summary>
/// The admin console's sign-in values (thekim1/llmgateway#100): each from the deployment (<c>Oidc:*</c> in the raw
/// configuration, where an empty value is unset; the bound options cannot tell a default from a set value), else from the
/// database (<see cref="SignInSettings"/>), else the code's default. Configuration is read at each call, so a changed value
/// applies; the row too, so every instance sees a change at once. <c>Oidc:RoleGroups</c> stays a deployment value outside this.
/// Gateway admins read them; only setup mode writes them.
/// </summary>
public sealed class SignInSettingsService(AdminContext ctx, IConfiguration configuration, SignInSecretProtector secrets)
{
    /// <summary>The audit entry's entity id of a sign-in settings change (entity type <c>Settings</c>).</summary>
    public const string AuditEntityId = "sign-in";

    private static readonly OidcOptions Defaults = new();

    public async Task<SignInSettingsDto> GetAsync(CancellationToken ct) => SignInSettingsDto.Of(await ReadAsync(ct));

    /// <summary>The effective values, the client secret unprotected for server-side use (one primary-key read).</summary>
    public async Task<EffectiveSignIn> ReadAsync(CancellationToken ct)
    {
        var row = await ctx.Db.SignInSettings.AsNoTracking().SingleOrDefaultAsync(ct);
        return Resolve(configuration, row is null ? new SignInWizardValues() : SignInWizardValues.Of(row), () => secrets.Unprotect(row?.EncryptedClientSecret),
            row?.ClientSecretSetAt, row?.UpdatedAt);
    }

    /// <summary>
    /// Stores the sign-in values (the row is created on the first save), with an audit entry that holds whether a client secret
    /// is stored and when, never the secret, and publishes <see cref="InvalidationKind.Config"/>: the gateways hand the
    /// authority out on <c>/v1/ume/context</c>. <paramref name="clientSecret"/>: null keeps the stored one, an empty or blank
    /// value removes it. Only setup mode calls it; no route of an admin session does.
    /// </summary>
    internal async Task<EffectiveSignIn> SaveWizardValuesAsync(SignInWizardValues values, string? clientSecret, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(values);
        var stored = values.Normalized();
        var encrypted = string.IsNullOrWhiteSpace(clientSecret) ? null : secrets.Protect(clientSecret);
        var row = await ctx.Db.SignInSettings.SingleOrDefaultAsync(ct);
        if (row is null)
        {
            // Production creates the row in app-roles.sql (the admin role may only update it); development databases may lack it.
            row = new SignInSettings();
            ctx.Db.SignInSettings.Add(row);
        }

        var before = SignInSettingsAudit.Of(row);
        row.Authority = stored.Authority;
        row.ClientId = stored.ClientId;
        row.RoleClaim = stored.RoleClaim;
        row.DepartmentClaim = stored.DepartmentClaim;
        row.TeamClaim = stored.TeamClaim;
        row.GroupClaim = stored.GroupClaim;
        row.ExtraScopes = [.. stored.ExtraScopes ?? []];
        row.ClientSecretExpiresOn = stored.ClientSecretExpiresOn;
        if (clientSecret is not null)
        {
            row.EncryptedClientSecret = encrypted;
            row.ClientSecretSetAt = encrypted is null ? null : ctx.Now;
        }

        row.UpdatedAt = ctx.Now;
        try
        {
            await ctx.SaveAsync(AuditActions.Update, AuditEntities.Settings, AuditEntityId, before, SignInSettingsAudit.Of(row), [InvalidationKind.Config], ct);
        }
        catch (DbUpdateException)
        {
            // Two saves of the very first values at once: one insert wins.
            throw new ApiFaultException(409, "Someone else changed the sign-in settings at the same time. Reload the page and try again.");
        }

        return await ReadAsync(ct);
    }

    /// <summary>
    /// The precedence rule on its own. <paramref name="storedSecret"/> is asked only when the deployment sets no client secret,
    /// so a deployment secret never needs the key ring.
    /// </summary>
    public static EffectiveSignIn Resolve(IConfiguration configuration, SignInWizardValues stored, Func<StoredSecretValue> storedSecret,
        DateTimeOffset? secretSetAt, DateTimeOffset? updatedAt)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(stored);
        ArgumentNullException.ThrowIfNull(storedSecret);
        var oidc = configuration.GetSection(OidcOptions.SectionName);
        var wizard = stored.Normalized();

        SignInValue<string> Text(string key, string? fromWizard, string fallback) =>
            Deployed(oidc, key) is { } deployed ? new(deployed, SignInValueSources.Deployment)
            : fromWizard is not null ? new(fromWizard, SignInValueSources.Wizard)
            : new(fallback, SignInValueSources.Default);

        SignInValue<string?> authority = Deployed(oidc, nameof(OidcOptions.Authority)) is { } deployedAuthority
            ? new(deployedAuthority, SignInValueSources.Deployment)
            : wizard.Authority is not null ? new(wizard.Authority, SignInValueSources.Wizard)
            : new(Defaults.Authority, SignInValueSources.Default);

        string[] deployedScopes = [.. oidc.GetSection(nameof(OidcOptions.ExtraScopes)).GetChildren().Select(c => c.Value).Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v!)];
        SignInValue<IReadOnlyList<string>> scopes = deployedScopes.Length > 0 ? new(deployedScopes, SignInValueSources.Deployment)
            : wizard.ExtraScopes is { } wizardScopes ? new(wizardScopes, SignInValueSources.Wizard)
            : new(Defaults.ExtraScopes, SignInValueSources.Default);

        return new EffectiveSignIn(
            authority,
            Text(nameof(OidcOptions.ClientId), wizard.ClientId, Defaults.ClientId),
            Secret(oidc, storedSecret, secretSetAt),
            Text(nameof(OidcOptions.RoleClaim), wizard.RoleClaim, Defaults.RoleClaim),
            Text(nameof(OidcOptions.DepartmentClaim), wizard.DepartmentClaim, Defaults.DepartmentClaim),
            Text(nameof(OidcOptions.TeamClaim), wizard.TeamClaim, Defaults.TeamClaim),
            Text(nameof(OidcOptions.GroupClaim), wizard.GroupClaim, Defaults.GroupClaim),
            scopes, wizard.ClientSecretExpiresOn, updatedAt);
    }

    private static SignInClientSecret Secret(IConfigurationSection oidc, Func<StoredSecretValue> storedSecret, DateTimeOffset? setAt)
    {
        if (Deployed(oidc, nameof(OidcOptions.ClientSecret)) is { } deployed)
        {
            return new(SignInValueSources.Deployment, deployed, null, unreadable: false);
        }

        var stored = storedSecret();
        return stored.State switch
        {
            StoredSecretState.Readable => new(SignInValueSources.Wizard, stored.Value, setAt, unreadable: false),
            StoredSecretState.Unreadable => new(SignInValueSources.Wizard, null, setAt, unreadable: true),
            _ => new(SignInValueSources.Default, Defaults.ClientSecret, null, unreadable: false),
        };
    }

    /// <summary>The deployment's value of <c>Oidc:<paramref name="key"/></c>; empty or blank is unset (Compose passes an unset variable as "").</summary>
    private static string? Deployed(IConfigurationSection oidc, string key) => oidc[key] is { } value && !string.IsNullOrWhiteSpace(value) ? value : null;
}
