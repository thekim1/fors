namespace Fors.AdminApi.Security;

/// <summary>
/// Bound from the <c>Oidc</c> configuration section. Defaults match the bundled Keycloak realm; other
/// identity providers (Keycloak with different mappers, AD FS, Entra ID) can remap claims or map groups to roles.
/// Bound and validated at start, but only <see cref="RoleGroups"/> is read from the bound options: every other value is an
/// effective sign-in value (<see cref="Settings.SignInSettingsService"/>), from the deployment, else the database, else these
/// defaults, and the sign-in takes an instance built from them (<see cref="SignInSnapshot.ToOidcOptions"/>). The admin API
/// starts without an authority; sign-in is then not configured.
/// </summary>
public sealed class OidcOptions
{
    public const string SectionName = "Oidc";

    public const string AuthorityError = "Oidc:Authority must be empty or an absolute https URL (http only in Development).";

    /// <summary>The identity provider's issuer URL; when the deployment sets it, a usable one (<see cref="IsUsableAuthority"/>).</summary>
    public string? Authority { get; set; }

    /// <summary>The client registered for the admin console; an empty value is unset (the default applies).</summary>
    public string ClientId { get; set; } = "fors-admin";

    public string? ClientSecret { get; set; }

    public string RoleClaim { get; set; } = "roles";
    public string DepartmentClaim { get; set; } = "departmentCodes";

    /// <summary>Claim holding the codes of the teams a team admin manages. Set it to the group claim to use group names as team codes.</summary>
    public string TeamClaim { get; set; } = "teamCodes";

    public string GroupClaim { get; set; } = "groups";
    /// <summary>
    /// Role name (gateway-admin, department-admin, viewer, team-admin) to the group names that grant it, separated by ';'.
    /// Always a deployment value: it decides who becomes a gateway admin, so it is never stored in the database.
    /// </summary>
    public Dictionary<string, string> RoleGroups { get; set; } = [];
    /// <summary>Extra scopes to request, for example "groups".</summary>
    public string[] ExtraScopes { get; set; } = [];

    /// <summary>
    /// Whether <paramref name="authority"/> can be signed in through: an absolute https URL, or http where
    /// <paramref name="requireHttps"/> is false (Development, as the handler's <c>RequireHttpsMetadata</c>).
    /// </summary>
    public static bool IsUsableAuthority(string? authority, bool requireHttps) =>
        Uri.TryCreate(authority, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || (!requireHttps && uri.Scheme == Uri.UriSchemeHttp));
}
