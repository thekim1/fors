namespace Ume.LlmGateway.Domain.Entities;

/// <summary>
/// The admin console's sign-in values as stored in the database (set in setup mode, thekim1/llmgateway#100); null or an empty
/// list is "not stored". A value the deployment's <c>Oidc:*</c> sets wins over the stored one, and the code's default applies
/// when neither sets it. One row (<see cref="SingletonId"/>). Kept apart from <see cref="GatewaySettings"/>, which the gateway
/// reads whole: the gateway's database role may read only <see cref="Authority"/> here, never <see cref="EncryptedClientSecret"/>.
/// </summary>
public sealed class SignInSettings
{
    public const int SingletonId = 1;

    public const int MaxAuthorityLength = 500;
    public const int MaxValueLength = 200;
    public const int MaxEncryptedClientSecretLength = 2000;
    public const int MaxSetupNonceLength = 64;
    public const int MaxSetupReopenLength = 200;

    public int Id { get; set; } = SingletonId;

    /// <summary>The identity provider's issuer URL.</summary>
    public string? Authority { get; set; }

    public string? ClientId { get; set; }
    public string? RoleClaim { get; set; }
    public string? DepartmentClaim { get; set; }
    public string? TeamClaim { get; set; }
    public string? GroupClaim { get; set; }

    /// <summary>Scopes requested besides <c>openid</c>, <c>profile</c> and <c>email</c>; empty is not stored.</summary>
    public List<string> ExtraScopes { get; set; } = [];

    /// <summary>The client secret, protected with Data Protection under a purpose of its own; never in a response, the audit log or a log line.</summary>
    public string? EncryptedClientSecret { get; set; }

    /// <summary>When <see cref="EncryptedClientSecret"/> was stored.</summary>
    public DateTimeOffset? ClientSecretSetAt { get; set; }

    /// <summary>When the stored client secret expires, as the person entered it: a reminder, nothing enforces it.</summary>
    public DateOnly? ClientSecretExpiresOn { get; set; }

    public DateTimeOffset? UpdatedAt { get; set; }

    /// <summary>
    /// When setup mode was completed: by a saved, tested sign-in, or at a start whose deployment declares the sign-in. Null
    /// before the first completion. The gateway's database role cannot read it.
    /// </summary>
    public DateTimeOffset? SetupCompletedAt { get; set; }

    /// <summary>The running opening's random nonce, from which the setup code is derived; null while no opening runs.</summary>
    public string? SetupNonce { get; set; }

    /// <summary>When the opening of <see cref="SetupNonce"/> started; an opening lasts one hour.</summary>
    public DateTimeOffset? SetupOpenedAt { get; set; }

    /// <summary>The last <c>Setup:Reopen</c> value that opened setup; a restart with the same value opens nothing.</summary>
    public string? SetupReopenConsumed { get; set; }
}
