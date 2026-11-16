using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Ume.LlmGateway.AdminApi.Auditing;
using Ume.LlmGateway.AdminApi.Common;
using Ume.LlmGateway.AdminApi.Security;
using Ume.LlmGateway.AdminApi.Setup;
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

/// <summary>
/// One effective sign-in value and its source (<see cref="SignInValueSources"/>). <see cref="Locked"/>: the deployment sets it, so
/// setup mode shows it and cannot change it.
/// </summary>
public sealed record SignInValue<T>(T Value, string Source)
{
    public bool Locked => Source == SignInValueSources.Deployment;
}

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
    DateOnly? ClientSecretExpiresOn, DateTimeOffset? UpdatedAt)
{
    /// <summary>When setup mode was completed, read with the values; null before the first completion (<see cref="Setup.SetupMode.IsOpen(EffectiveSignIn, DateTimeOffset)"/>).</summary>
    public DateTimeOffset? SetupCompletedAt { get; init; }

    /// <summary>When the running setup opening started, read with the values; null while no opening has a nonce.</summary>
    public DateTimeOffset? SetupOpenedAt { get; init; }

    /// <summary>Whether the deployment declares the sign-in: it sets the authority (<see cref="SignInSettingsService.DeclaresSignIn"/>).</summary>
    public bool DeclaredByDeployment => Authority.Source == SignInValueSources.Deployment;
}

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
            row?.ClientSecretSetAt, row?.UpdatedAt) with
        {
            SetupCompletedAt = row?.SetupCompletedAt, SetupOpenedAt = row?.SetupNonce is null ? null : row.SetupOpenedAt,
        };
    }

    /// <summary>
    /// Stores the sign-in values (the row is created on the first save), with an audit entry that holds whether a client secret
    /// is stored and when, never the secret, and publishes <see cref="InvalidationKind.Config"/>: the gateways hand the
    /// authority out on <c>/v1/ume/context</c>. <paramref name="clientSecret"/>: null keeps the stored one, an empty or blank
    /// value removes it. <paramref name="actor"/> is the audit entry's actor (setup completion's tested person, on an anonymous
    /// request); null takes the request's user. <paramref name="alsoApply"/> changes the same row in the same transaction (setup
    /// completion closes setup). The write holds setup mode's lock (<see cref="SetupLock"/>), so it is serialised with every
    /// opening and completion on every instance. Only setup mode calls it; no route of an admin session does.
    /// </summary>
    internal async Task<EffectiveSignIn> SaveWizardValuesAsync(SignInWizardValues values, string? clientSecret, AdminActor? actor,
        Func<SignInSettings, CancellationToken, Task>? alsoApply, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(values);
        var stored = values.Normalized();
        // Protected once, outside the retried write: the ciphertext does not depend on the row.
        var encrypted = string.IsNullOrWhiteSpace(clientSecret) ? null : secrets.Protect(clientSecret);
        await ctx.Db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            ctx.Db.ChangeTracker.Clear();
            await using var transaction = await ctx.Db.Database.BeginTransactionAsync(ct);
            await SetupLock.AcquireAsync(ctx.Db, ct);
            var row = await ctx.Db.SignInSettings.SingleOrDefaultAsync(ct);
            if (row is null)
            {
                // Created by the SetupMode migration and app-roles.sql (the admin role may only update it); a database without it is repaired here.
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
            if (alsoApply is not null)
            {
                await alsoApply(row, ct);
            }

            var entry = ctx.Audit(AuditActions.Update, AuditEntities.Settings, AuditEntityId, before, SignInSettingsAudit.Of(row));
            if (actor is not null)
            {
                entry.Actor = actor.Subject;
                actor.Stamp(entry);
            }

            ctx.Db.AuditLog.Add(entry);
            await ctx.Db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        });
        // After the commit, so a gateway that reloads reads the new values.
        await ctx.PublishAsync([InvalidationKind.Config], ct);
        return await ReadAsync(ct);
    }

    /// <summary>
    /// Whether the deployment declares the admin console's sign-in: it sets <c>Oidc:Authority</c> (non-blank) in the raw
    /// configuration. The default client <c>ume-admin</c> is public, so a client secret cannot tell; every deployment before setup
    /// mode sets the authority. Such a deployment counts as setup completed and never sees the wizard.
    /// </summary>
    public static bool DeclaresSignIn(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return Deployed(configuration.GetSection(OidcOptions.SectionName), nameof(OidcOptions.Authority)) is not null;
    }

    /// <summary><paramref name="values"/> without what the deployment sets: a deployment value wins, so setup stores none of those.</summary>
    public static SignInWizardValues WithoutDeployed(IConfiguration configuration, SignInWizardValues values)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(values);
        var oidc = configuration.GetSection(OidcOptions.SectionName);
        string? Keep(string key, string? value) => Deployed(oidc, key) is null ? value : null;
        var deployedScopes = oidc.GetSection(nameof(OidcOptions.ExtraScopes)).GetChildren().Any(c => !string.IsNullOrWhiteSpace(c.Value));
        var v = values.Normalized();
        return v with
        {
            Authority = Keep(nameof(OidcOptions.Authority), v.Authority), ClientId = Keep(nameof(OidcOptions.ClientId), v.ClientId),
            RoleClaim = Keep(nameof(OidcOptions.RoleClaim), v.RoleClaim), DepartmentClaim = Keep(nameof(OidcOptions.DepartmentClaim), v.DepartmentClaim),
            TeamClaim = Keep(nameof(OidcOptions.TeamClaim), v.TeamClaim), GroupClaim = Keep(nameof(OidcOptions.GroupClaim), v.GroupClaim),
            ExtraScopes = deployedScopes ? null : v.ExtraScopes,
            // The expiry date belongs to a secret typed in the wizard.
            ClientSecretExpiresOn = Deployed(oidc, nameof(OidcOptions.ClientSecret)) is null ? v.ClientSecretExpiresOn : null,
        };
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
