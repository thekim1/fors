using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Ume.LlmGateway.Domain.Entities;
using Ume.LlmGateway.Infrastructure.Auditing;
using Ume.LlmGateway.Infrastructure.Persistence;
using Ume.LlmGateway.Infrastructure.Provisioning;
using Ume.LlmGateway.Infrastructure.Security;

namespace Ume.LlmGateway.MigrationService;

/// <summary>Integration manifests applied on every deploy (docs/integrations.md, <i>Provisioning from a manifest</i>; ADR 022).</summary>
public sealed class ProvisioningOptions : IValidatableObject
{
    public const string SectionName = "Provisioning";

    /// <summary>A manifest file, or a directory whose <c>*.json</c> files are manifests (applied in name order). Empty = off.</summary>
    public string? Manifests { get; set; }

    /// <summary>
    /// Where each credential key's secret is written, as <c>&lt;SecretsPath&gt;/&lt;integration&gt;/&lt;key&gt;</c>: a volume
    /// shared only with the platform's bootstrap, never the gateway's certificate or secret volumes.
    /// </summary>
    public string? SecretsPath { get; set; }

    /// <summary>
    /// The secret files' Unix mode: <c>600</c> (default, the migration service's user only) or <c>640</c> (also its group,
    /// for a reader that runs with that group).
    /// </summary>
    [RegularExpression("^(600|640)$", ErrorMessage = "Provisioning:SecretFileMode must be 600 or 640.")]
    public string SecretFileMode { get; set; } = "600";

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (!string.IsNullOrWhiteSpace(Manifests) && string.IsNullOrWhiteSpace(SecretsPath))
        {
            yield return new ValidationResult("Provisioning:SecretsPath is required with Provisioning:Manifests.", [nameof(SecretsPath)]);
        }
    }

    internal UnixFileMode FileMode => SecretFileMode == "640"
        ? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead
        : UnixFileMode.UserRead | UnixFileMode.UserWrite;

    internal UnixFileMode DirectoryMode => SecretFileMode == "640"
        ? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
        : UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
}

/// <summary>
/// Applies the configured manifests after migrating, then hands each credential key's secret over as a file: a key
/// created now gets its file; a key that exists but whose file is missing gets it written again from the stored
/// (encrypted) secret, audited like a reveal in the admin console. An existing file is never touched, so a rotation in
/// the admin console reaches the platform when its file is deleted and the stack deployed again.
/// </summary>
public sealed partial class IntegrationManifests(
    IOptions<ProvisioningOptions> options,
    IntegrationProvisioner provisioner,
    CredentialProtector protector,
    TimeProvider time,
    ILogger<IntegrationManifests> logger)
{
    public async Task ApplyAsync(GatewayDbContext db, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        var o = options.Value;
        if (string.IsNullOrWhiteSpace(o.Manifests))
        {
            return;
        }

        foreach (var path in Files(o.Manifests))
        {
            var name = Path.GetFileName(path);
            IntegrationManifest manifest;
            try
            {
                manifest = IntegrationManifest.Parse(await File.ReadAllTextAsync(path, ct));
            }
            catch (ManifestException ex)
            {
                throw new ManifestException($"Integration manifest {name}: {ex.Message}", ex);
            }

            var result = await provisioner.ApplyAsync(db, manifest, "manifest:" + name, ct);
            foreach (var warning in result.Warnings)
            {
                LogWarning(logger, name, warning);
            }

            foreach (var key in result.Keys)
            {
                await DeliverAsync(db, o, result.IntegrationName, key, name, ct);
            }

            var created = result.Keys.Count(k => k.State == ProvisionedKeyState.Created);
            LogApplied(logger, name, result.IntegrationName, created, result.Keys.Count);
        }
    }

    /// <summary>The manifest file, or the <c>*.json</c> files of the directory in ordinal name order.</summary>
    private static IEnumerable<string> Files(string manifests)
    {
        if (Directory.Exists(manifests))
        {
            return Directory.GetFiles(manifests, "*.json").Order(StringComparer.Ordinal);
        }

        return File.Exists(manifests) ? [manifests] : throw new FileNotFoundException($"Provisioning:Manifests '{manifests}' is neither a file nor a directory.");
    }

    private async Task DeliverAsync(GatewayDbContext db, ProvisioningOptions o, string integration, ProvisionedKey key, string manifest, CancellationToken ct)
    {
        var directory = Path.Combine(o.SecretsPath!, integration);
        var file = Path.Combine(directory, key.Name);
        if (key.State == ProvisionedKeyState.Revoked || (key.State == ProvisionedKeyState.Existing && File.Exists(file)))
        {
            return;
        }

        var secret = key.Secret;
        if (secret is null)
        {
            // The file is gone: write the key's current secret again, and audit it as a reveal.
            var stored = await db.VirtualKeys.AsNoTracking().Where(k => k.Id == key.Id).Select(k => k.EncryptedSecret).SingleAsync(ct);
            secret = protector.Unprotect(stored);
            if (secret is null)
            {
                LogNoStoredSecret(logger, manifest, key.Name, integration);
                return;
            }

            db.AuditLog.Add(new AuditLogEntry
            {
                Timestamp = time.GetUtcNow(), Actor = "manifest:" + manifest, Action = AuditActions.Reveal, EntityType = AuditEntities.VirtualKey,
                EntityId = key.Id.ToString(), Details = JsonSerializer.Serialize(new AuditDetails(null, new { name = key.Name, prefix = key.Prefix, file = "written again" }), AuditJson.Options),
            });
            await db.SaveChangesAsync(ct);
        }

        Write(directory, file, secret, o);
        LogDelivered(logger, key.Name, integration, key.State == ProvisionedKeyState.Created ? "created" : "written again");
    }

    /// <summary>Writes through a temporary file and a rename, so a reader never sees half a secret.</summary>
    private static void Write(string directory, string file, string secret, ProvisioningOptions o)
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(directory);
        }
        else
        {
            Directory.CreateDirectory(directory, o.DirectoryMode);
        }

        var temporary = file + ".tmp";
        var stream = OperatingSystem.IsWindows()
            ? new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None)
            : new FileStream(temporary, new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None, UnixCreateMode = o.FileMode });
        using (stream)
        using (var writer = new StreamWriter(stream))
        {
            writer.Write(secret);
        }

        File.Move(temporary, file, overwrite: true);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Integration manifest {Manifest}: '{Integration}' applied, {Created} of {Keys} credential keys created")]
    private static partial void LogApplied(ILogger logger, string manifest, string integration, int created, int keys);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Integration manifest {Manifest}: {Warning}")]
    private static partial void LogWarning(ILogger logger, string manifest, string warning);

    [LoggerMessage(Level = LogLevel.Information, Message = "Secret file of credential key '{Key}' of '{Integration}' {What}")]
    private static partial void LogDelivered(ILogger logger, string key, string integration, string what);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Integration manifest {Manifest}: credential key '{Key}' of '{Integration}' has no stored secret, so its missing file cannot be written; rotate the key in the admin console")]
    private static partial void LogNoStoredSecret(ILogger logger, string manifest, string key, string integration);
}
