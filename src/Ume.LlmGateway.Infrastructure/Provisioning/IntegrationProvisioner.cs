using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Ume.LlmGateway.Domain;
using Ume.LlmGateway.Domain.Entities;
using Ume.LlmGateway.Domain.Keys;
using Ume.LlmGateway.Domain.Networking;
using Ume.LlmGateway.Infrastructure.Auditing;
using Ume.LlmGateway.Infrastructure.Persistence;
using Ume.LlmGateway.Infrastructure.Security;

namespace Ume.LlmGateway.Infrastructure.Provisioning;

/// <summary>
/// Applies an <see cref="IntegrationManifest"/> (ADR 022): creates the owner team (and its department), the integration and
/// its credential keys where they do not exist, in one transaction with an audit entry for each. It never changes,
/// rotates or re-creates anything that exists: a key is found by its integration and name, a revoked key stays revoked,
/// and a setting that differs from the manifest is reported in <see cref="ProvisioningResult.Warnings"/>, not applied,
/// so whoever can edit the file cannot widen an existing key's rights.
/// </summary>
public sealed class IntegrationProvisioner(VirtualKeyHasher hasher, CredentialProtector protector, TimeProvider time)
{
    /// <summary>
    /// Applies <paramref name="manifest"/> as <paramref name="actor"/> (the audit log's actor). The secrets of keys created
    /// now are in the result and nowhere else in plain text; the caller hands them over once. Nothing is saved when the
    /// manifest conflicts with existing data (<see cref="ManifestException"/>).
    /// </summary>
    public async Task<ProvisioningResult> ApplyAsync(GatewayDbContext db, IntegrationManifest manifest, string actor, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(manifest);
        if (manifest.Validate() is { Count: > 0 } errors)
        {
            throw new ManifestException(errors);
        }

        var now = time.GetUtcNow();
        var declared = manifest.Integration!;
        var warnings = new List<string>();
        var audit = new List<AuditLogEntry>();
        void Audit(string entity, Guid id, object after) => audit.Add(new AuditLogEntry
        {
            Timestamp = now, Actor = actor, Action = AuditActions.Create, EntityType = entity, EntityId = id.ToString(),
            Details = JsonSerializer.Serialize(new AuditDetails(null, after), AuditJson.Options),
        });

        var integration = await db.Integrations.Include(i => i.OwnerTeam).SingleOrDefaultAsync(i => i.Name == declared.Name, ct);
        var createdIntegration = integration is null;
        if (integration is null)
        {
            var owner = await OwnerTeamAsync(db, declared.Owner!, now, Audit, ct);
            integration = new Integration
            {
                Name = declared.Name!, Description = Trimmed(declared.Description), OwnerTeam = owner, OwnerTeamId = owner.Id,
                CreatedAt = now, CreatedBy = actor,
            };
            db.Integrations.Add(integration);
            Audit(AuditEntities.Integration, integration.Id, IntegrationAudit.Of(integration));
        }
        else if (!string.Equals(integration.OwnerTeam?.Code, declared.Owner!.TeamCode!.Trim(), StringComparison.Ordinal))
        {
            warnings.Add($"Integration '{integration.Name}' is owned by team '{integration.OwnerTeam?.Code ?? integration.OwnerTeam?.Name}', not '{declared.Owner.TeamCode.Trim()}' as the manifest says; it was not changed.");
        }

        var existing = createdIntegration ? [] : await db.VirtualKeys.AsNoTracking()
            .Where(k => k.IntegrationId == integration.Id && k.IntegrationRole == IntegrationRole.Credential).ToListAsync(ct);
        var keys = new List<ProvisionedKey>();
        foreach (var wanted in manifest.CredentialKeys)
        {
            var named = existing.Where(k => string.Equals(k.Name, wanted.Name, StringComparison.Ordinal)).ToList();
            if (named.Count == 0)
            {
                var generated = hasher.Generate();
                var key = new VirtualKey
                {
                    Team = integration.OwnerTeam, TeamId = integration.OwnerTeamId, Name = wanted.Name!, Description = Trimmed(wanted.Description),
                    Prefix = generated.Prefix, KeyHash = generated.Hash, EncryptedSecret = protector.Protect(generated.PlainText),
                    CreatedAt = now, CreatedBy = actor, Integration = integration, IntegrationId = integration.Id, IntegrationRole = IntegrationRole.Credential,
                    IntegrationScopes = Scopes(wanted.IntegrationScopes), AllowedResidencies = [.. wanted.AllowedResidencies.Distinct().Order()],
                    RequireMaxResidency = wanted.RequireMaxResidency, PiiPolicy = wanted.PiiPolicy, AllowedIpRanges = Ranges(wanted.AllowedIpRanges),
                };
                db.VirtualKeys.Add(key);
                Audit(AuditEntities.VirtualKey, key.Id, KeyAudit.Of(key));
                keys.Add(new ProvisionedKey(wanted.Name!, key.Id, key.Prefix, ProvisionedKeyState.Created, generated.PlainText));
                continue;
            }

            // The key in use: after a rotation the new key, which carries the name on; the old one is in its grace period.
            if (named.FirstOrDefault(k => k.RevokedAt is null && k.RotatedToKeyId is null) is not { } current)
            {
                warnings.Add($"Credential key '{wanted.Name}' of '{integration.Name}' is revoked; the manifest does not create it again. Rotate it in the admin console or give the manifest key another name.");
                keys.Add(new ProvisionedKey(wanted.Name!, named.MaxBy(k => k.CreatedAt)!.Id, null, ProvisionedKeyState.Revoked, null));
                continue;
            }

            if (Differences(current, wanted) is { Count: > 0 } differences)
            {
                warnings.Add($"Credential key '{wanted.Name}' of '{integration.Name}' differs from the manifest in {string.Join(", ", differences)}; it was not changed (change it in the admin console).");
            }

            keys.Add(new ProvisionedKey(wanted.Name!, current.Id, current.Prefix, ProvisionedKeyState.Existing, null));
        }

        db.AuditLog.AddRange(audit);
        await db.SaveChangesAsync(ct);
        return new ProvisioningResult(integration.Id, integration.Name, createdIntegration, keys, warnings);
    }

    /// <summary>The team with the manifest's code, or a new one in the department with its code (also new if absent).</summary>
    private static async Task<Team> OwnerTeamAsync(GatewayDbContext db, ManifestOwner owner, DateTimeOffset now, Action<string, Guid, object> audit, CancellationToken ct)
    {
        var code = owner.TeamCode!.Trim();
        if (await db.Teams.SingleOrDefaultAsync(t => t.Code == code, ct) is { } team)
        {
            return team;
        }

        if (string.IsNullOrWhiteSpace(owner.TeamName) || string.IsNullOrWhiteSpace(owner.DepartmentCode))
        {
            throw new ManifestException([$"There is no team with the code '{code}'; give 'integration.owner.teamName' and 'departmentCode' to create it."]);
        }

        var departmentCode = owner.DepartmentCode.Trim();
        var department = await db.Departments.SingleOrDefaultAsync(d => d.CostCenterCode == departmentCode, ct);
        if (department is null)
        {
            if (string.IsNullOrWhiteSpace(owner.DepartmentName))
            {
                throw new ManifestException([$"There is no department with the code '{departmentCode}'; give 'integration.owner.departmentName' to create it."]);
            }

            department = new Department { Name = owner.DepartmentName.Trim(), CostCenterCode = departmentCode, CreatedAt = now };
            db.Departments.Add(department);
            audit(AuditEntities.Department, department.Id, DepartmentAudit.Of(department));
        }

        team = new Team { Name = owner.TeamName.Trim(), Code = code, Department = department, DepartmentId = department.Id, CreatedAt = now };
        db.Teams.Add(team);
        audit(AuditEntities.Team, team.Id, TeamAudit.Of(team));
        return team;
    }

    /// <summary>The settings in which an existing key differs from the manifest (names only, never values).</summary>
    private static List<string> Differences(VirtualKey key, ManifestCredentialKey wanted)
    {
        var differences = new List<string>();
        if (!key.IntegrationScopes.Order(StringComparer.Ordinal).SequenceEqual(Scopes(wanted.IntegrationScopes), StringComparer.Ordinal))
        {
            differences.Add("integrationScopes");
        }

        if (!key.AllowedResidencies.Distinct().Order().SequenceEqual(wanted.AllowedResidencies.Distinct().Order()))
        {
            differences.Add("allowedResidencies");
        }

        if (key.RequireMaxResidency != wanted.RequireMaxResidency)
        {
            differences.Add("requireMaxResidency");
        }

        if (key.PiiPolicy != wanted.PiiPolicy)
        {
            differences.Add("piiPolicy");
        }

        if (!key.AllowedIpRanges.Order(StringComparer.Ordinal).SequenceEqual(Ranges(wanted.AllowedIpRanges).Order(StringComparer.Ordinal), StringComparer.Ordinal))
        {
            differences.Add("allowedIpRanges");
        }

        return differences;
    }

    private static List<string> Scopes(IEnumerable<string> scopes) => [.. scopes.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

    private static List<string> Ranges(IEnumerable<string> ranges)
    {
        NetworkRanges.Normalize(ranges, out var canonical);
        return canonical;
    }

    private static string? Trimmed(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>What <see cref="IntegrationProvisioner.ApplyAsync"/> did.</summary>
public sealed record ProvisioningResult(Guid IntegrationId, string IntegrationName, bool CreatedIntegration, IReadOnlyList<ProvisionedKey> Keys, IReadOnlyList<string> Warnings);

/// <summary>A manifest's credential key: its id, prefix and state, and its secret only when it was created now.</summary>
public sealed record ProvisionedKey(string Name, Guid Id, string? Prefix, ProvisionedKeyState State, string? Secret)
{
    /// <summary>Never the secret, so a result can be logged or shown in a list.</summary>
    public override string ToString() => $"{Name} ({Prefix ?? "-"}, {State})";
}

public enum ProvisionedKeyState
{
    /// <summary>Created now; the secret is in the result.</summary>
    Created,

    /// <summary>Found in use (after a rotation, the newest key); left as it was.</summary>
    Existing,

    /// <summary>Found, but revoked; not created again.</summary>
    Revoked,
}
