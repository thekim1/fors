using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Ume.LlmGateway.Infrastructure.Persistence;

namespace Ume.LlmGateway.AdminApi.Setup;

/// <summary>
/// Bound from the <c>Setup</c> configuration section; both optional. <see cref="Secret"/> is the setup secret the setup code is
/// derived from: a file (<c>Setup__Secret</c> in <c>Security:SecretsDirectory</c>) or a variable the deployment generates. Without
/// one (or with one shorter than <see cref="MinSecretLength"/>) there is no setup mode. <see cref="Reopen"/> opens setup once
/// more when it holds a value it has not opened it with before.
/// </summary>
public sealed class SetupOptions
{
    public const string SectionName = "Setup";

    /// <summary>The name of the secret file (key per file) and the environment variable.</summary>
    public const string SecretFileName = "Setup__Secret";

    /// <summary>A generated secret is 64 characters; anything under this is treated as missing.</summary>
    public const int MinSecretLength = 32;

    [MaxLength(1000)]
    public string? Secret { get; set; }

    [MaxLength(Domain.Entities.SignInSettings.MaxSetupReopenLength)]
    public string? Reopen { get; set; }

    /// <summary>The secret, or null when none (or one too short) is set.</summary>
    public string? SecretOrNull => Secret?.Trim() is { Length: >= MinSecretLength } secret ? secret : null;

    /// <summary>The re-open value, or null when none is set (an empty value, as Compose passes an unset variable, is none).</summary>
    public string? ReopenOrNull => string.IsNullOrWhiteSpace(Reopen) ? null : Reopen.Trim();
}

/// <summary>The setup secret as the setup code needs it (<see cref="SetupOptions.SecretOrNull"/>).</summary>
public sealed class SetupSecret(IOptions<SetupOptions> options)
{
    /// <summary>The secret, or null while the deployment provides none (or one too short).</summary>
    public string? Current => options.Value.SecretOrNull;
}

/// <summary>Where the setup code is written: standard output, never the logger (so it is not in the OTLP export). Tests give their own writer.</summary>
public sealed class SetupConsole(TextWriter? output = null)
{
    public TextWriter Out => output ?? Console.Out;
}

/// <summary>
/// Setup mode's database lock: a transaction-scoped advisory lock that every write of the setup state and of the stored
/// sign-in values takes, so replicas starting together end with one opening, a session is created only while its opening is
/// current, and two completions of one opening cannot both save.
/// </summary>
internal static class SetupLock
{
    // Any constant shared by every instance ("umesetup").
    private const long Key = 0x756d_6573_6574_7570;

    /// <summary>Waits for the lock; it is released when the current transaction ends.</summary>
    public static Task AcquireAsync(GatewayDbContext db, CancellationToken ct) =>
        db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({Key})", ct);
}
