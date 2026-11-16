using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;

namespace Ume.LlmGateway.AdminApi.Security;

/// <summary>What unprotecting the stored client secret gave.</summary>
public enum StoredSecretState
{
    /// <summary>Nothing is stored.</summary>
    NotSet = 0,
    Readable = 1,

    /// <summary>Stored, but the key ring cannot unprotect it (a missing key, a replaced certificate, a damaged value).</summary>
    Unreadable = 2,
}

/// <summary>The stored client secret after unprotecting: its state and, when readable, its value (never in <see cref="ToString"/>).</summary>
public sealed class StoredSecretValue
{
    private StoredSecretValue(StoredSecretState state, string? value)
    {
        State = state;
        Value = value;
    }

    public static StoredSecretValue NotSet { get; } = new(StoredSecretState.NotSet, null);
    public static StoredSecretValue Unreadable { get; } = new(StoredSecretState.Unreadable, null);

    public StoredSecretState State { get; }

    /// <summary>The plaintext when <see cref="State"/> is <see cref="StoredSecretState.Readable"/>.</summary>
    public string? Value { get; }

    public static StoredSecretValue Readable(string value) => new(StoredSecretState.Readable, value);

    public override string ToString() => State.ToString();
}

/// <summary>
/// Protects the admin console's stored client secret (<see cref="Domain.Entities.SignInSettings.EncryptedClientSecret"/>) with
/// the shared key ring, under a purpose of its own so no other ciphertext unprotects as it. An unprotect failure is
/// <see cref="StoredSecretState.Unreadable"/> and a warning without the value, never an exception: a replaced certificate must
/// not take the admin API down, and the sign-in then reports itself not configured instead of signing in as a public client.
/// </summary>
public sealed partial class SignInSecretProtector(IDataProtectionProvider protection, ILogger<SignInSecretProtector> logger)
{
    public const string Purpose = "Ume.LlmGateway.AdminApi.SignInClientSecret.v1";

    private readonly IDataProtector _protector = protection.CreateProtector(Purpose);

    public string Protect(string plaintext)
    {
        ArgumentException.ThrowIfNullOrEmpty(plaintext);
        return _protector.Protect(plaintext);
    }

    public StoredSecretValue Unprotect(string? ciphertext)
    {
        if (string.IsNullOrEmpty(ciphertext))
        {
            return StoredSecretValue.NotSet;
        }

        try
        {
            return StoredSecretValue.Readable(_protector.Unprotect(ciphertext));
        }
        catch (CryptographicException ex)
        {
            // Data Protection reports every unprotect failure (bad payload, missing key, undecryptable ring) as this type; its
            // message names the key or the payload problem, never the plaintext.
            LogUnreadable(logger, ex.Message);
            return StoredSecretValue.Unreadable;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The stored sign-in client secret cannot be unprotected and is treated as unreadable: {Reason}")]
    private static partial void LogUnreadable(ILogger logger, string reason);
}
