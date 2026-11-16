namespace Ume.LlmGateway.Domain.Entities;

/// <summary>
/// A setup session of the admin console's setup mode (thekim1/llmgateway#100): given for a correct setup code, 30 minutes,
/// bound to the opening whose nonce it was given under. The cookie holds a random token; only its SHA-256 is stored. Deleted
/// with its tests when its opening ends.
/// </summary>
public sealed class SetupSession
{
    public Guid Id { get; set; }

    /// <summary>SHA-256 of the cookie's token.</summary>
    public required byte[] TokenHash { get; set; }

    /// <summary>The <see cref="SignInSettings.SetupNonce"/> of the opening it belongs to; a new opening voids it.</summary>
    public required string OpeningNonce { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
}

/// <summary>
/// One test sign-in of a setup session: single use, 10 minutes. <see cref="Protected"/> holds the candidate values, the PKCE
/// verifier, the nonce and the provider's endpoints, protected with Data Protection; <see cref="Result"/> what the callback
/// found. No token is ever stored.
/// </summary>
public sealed class SetupTest
{
    public const int MaxReasonLength = 64;

    public Guid Id { get; set; }
    public Guid SessionId { get; set; }

    /// <summary>SHA-256 of the random marker in the authorization request's <c>state</c>.</summary>
    public required byte[] MarkerHash { get; set; }

    /// <summary>SHA-256 of the test cookie set in the browser that started the test; the callback must carry it.</summary>
    public required byte[] BrowserHash { get; set; }

    public required string Protected { get; set; }

    /// <summary>SHA-256 of the tested values without the secret; completing compares the submitted values with it.</summary>
    public required byte[] ValuesFingerprint { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>When the callback claimed it (single use); null until then.</summary>
    public DateTimeOffset? CallbackAt { get; set; }

    /// <summary>Null until the callback finished.</summary>
    public bool? Passed { get; set; }

    /// <summary>The step that failed when <see cref="Passed"/> is false.</summary>
    public string? Reason { get; set; }

    /// <summary>The signed-in person as the claim mapping saw them (JSON): subject, name, roles, groups, departments, teams, gateway admin.</summary>
    public string? Result { get; set; }
}
