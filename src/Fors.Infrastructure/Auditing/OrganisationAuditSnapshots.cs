using Fors.Domain;
using Fors.Domain.Entities;
using Fors.Domain.Requests;

namespace Fors.Infrastructure.Auditing;

// Audit snapshots of the organisation, keys and integrations: what an entity's stored settings were before and after a
// change. Here rather than in the admin API because provisioning (the migration service) audits the same entities. They
// hold only the entity's own columns, so an entry does not depend on which navigations were loaded, and never a secret.

public sealed record DepartmentAudit(Guid Id, string Name, string CostCenterCode, bool IsActive, IReadOnlyList<string>? MemberGroups = null)
{
    public static DepartmentAudit Of(Department d) => new(d.Id, d.Name, d.CostCenterCode, d.IsActive, [.. d.MemberGroups]);
}

public sealed record TeamAudit(Guid Id, Guid DepartmentId, string Name, string? Description, bool IsActive, RequestPolicy? RequestPolicy = null, string? Code = null)
{
    public static TeamAudit Of(Team t) => new(t.Id, t.DepartmentId, t.Name, t.Description, t.IsActive, t.RequestPolicy, t.Code);
}

public sealed record KeyAudit(
    Guid Id, Guid TeamId, string Name, string? Description, string Prefix, bool IsEnabled, DateTimeOffset? ExpiresAt, DateTimeOffset? RevokedAt,
    DateTimeOffset? GraceUntil, Guid? RotatedToKeyId, IReadOnlyList<string> AllowedModels, IReadOnlyList<DataResidency> AllowedResidencies,
    IReadOnlyList<string> AllowedProviders, PiiPolicy PiiPolicy, AttachmentPolicy AttachmentPolicy, int? RequestsPerMinute, int? TokensPerMinute,
    IReadOnlyList<string> AllowedIpRanges, bool AllowProviderTools, bool AllowProviderStorage, int? MaxConcurrentRequests = null,
    RequestPolicy? RequestPolicy = null, ContentGuardPolicy ContentGuardPolicy = ContentGuardPolicy.Off, bool RequireMaxResidency = false,
    Guid? IntegrationId = null, IntegrationRole IntegrationRole = IntegrationRole.None, IReadOnlyList<string>? IntegrationScopes = null)
{
    public static KeyAudit Of(VirtualKey k) => new(
        k.Id, k.TeamId, k.Name, k.Description, k.Prefix, k.IsEnabled, k.ExpiresAt, k.RevokedAt, k.GraceUntil, k.RotatedToKeyId,
        [.. k.AllowedModels], [.. k.AllowedResidencies], [.. k.AllowedProviders], k.PiiPolicy, k.AttachmentPolicy, k.RequestsPerMinute, k.TokensPerMinute,
        [.. k.AllowedIpRanges], k.AllowProviderTools, k.AllowProviderStorage, k.MaxConcurrentRequests, k.RequestPolicy, k.ContentGuardPolicy,
        k.RequireMaxResidency, k.IntegrationId, k.IntegrationRole, [.. k.IntegrationScopes]);
}

public sealed record IntegrationAudit(Guid Id, string Name, string? Description, Guid OwnerTeamId, bool IsActive)
{
    public static IntegrationAudit Of(Integration i) => new(i.Id, i.Name, i.Description, i.OwnerTeamId, i.IsActive);
}
