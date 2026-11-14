using System.ComponentModel.DataAnnotations;
using Ume.LlmGateway.AdminApi.Common;
using Ume.LlmGateway.Domain;
using Ume.LlmGateway.Domain.Entities;
using Ume.LlmGateway.Infrastructure.Provisioning;

namespace Ume.LlmGateway.AdminApi.Integrations;

// Request and response bodies; the admin UI's types (src/admin-ui/src/api/types.ts) mirror the responses.

public sealed record IntegrationRequest(
    [property: Required, StringLength(100, MinimumLength = 1)] string Name,
    Guid OwnerTeamId,
    [property: StringLength(1000)] string? Description = null,
    bool IsActive = true) : AdminRequest;

/// <summary>
/// An integration with its owner team and how many keys (not revoked) it has of each kind. Every key manager can list
/// integrations, to mark a key as assigned to one; only gateway admins change them.
/// </summary>
public sealed record IntegrationDto(
    Guid Id, string Name, string? Description, Guid OwnerTeamId, string? OwnerTeamName, Guid? OwnerDepartmentId, string? OwnerDepartmentName,
    bool IsActive, DateTimeOffset CreatedAt, string? CreatedBy, int CredentialKeys, int AssignedKeys)
{
    public static IntegrationDto Of(Integration i, int credentialKeys, int assignedKeys) => new(
        i.Id, i.Name, i.Description, i.OwnerTeamId, i.OwnerTeam?.Name, i.OwnerTeam?.DepartmentId, i.OwnerTeam?.Department?.Name,
        i.IsActive, i.CreatedAt, i.CreatedBy, credentialKeys, assignedKeys);
}

/// <summary>A manifest (docs/integrations.md, <i>Provisioning from a manifest</i>) to preview or import.</summary>
public sealed record IntegrationImportRequest([property: Required, StringLength(64_000, MinimumLength = 2)] string Manifest) : AdminRequest;

/// <summary>
/// What an import created or would create. <see cref="IntegrationId"/> is null in a preview that would create the
/// integration; a key's secret is set only in the answer to the import that created it.
/// </summary>
public sealed record IntegrationImportDto(
    Guid? IntegrationId, string IntegrationName, bool CreatedIntegration, bool CreatedTeam, bool CreatedDepartment,
    IReadOnlyList<ImportedKeyDto> Keys, IReadOnlyList<string> Warnings)
{
    public static IntegrationImportDto Of(ProvisioningResult r) => new(
        r.IntegrationId, r.IntegrationName, r.CreatedIntegration, r.CreatedTeam, r.CreatedDepartment,
        [.. r.Keys.Select(k => new ImportedKeyDto(k.Name, k.Id, k.Prefix, k.State, k.Secret))], r.Warnings);
}

public sealed record ImportedKeyDto(string Name, Guid? Id, string? Prefix, ProvisionedKeyState State, string? Secret);

/// <summary>A manifest the admin console offers as a starting point; <see cref="Manifest"/> is the file as written.</summary>
public sealed record IntegrationPresetDto(string Id, string Title, string? Description, string Manifest);

/// <summary>What a key can be in an integration, for the key form.</summary>
public sealed record IntegrationOptionsDto(IReadOnlyList<string> Scopes, IReadOnlyList<IntegrationRole> Roles);
