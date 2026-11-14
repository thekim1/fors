using Microsoft.Extensions.Options;
using Ume.LlmGateway.AdminApi.Common;
using Ume.LlmGateway.Infrastructure.Provisioning;
using Ume.LlmGateway.Infrastructure.Stores;

namespace Ume.LlmGateway.AdminApi.Integrations;

/// <summary>
/// Adds an integration with its credential keys in one step from a manifest (docs/integrations.md, the same format the
/// migration service applies; ADR 022): pasted, or started from a preset. Gateway admins only. Like the migration service
/// it only creates what is missing and never changes an existing key; the secrets of the keys it creates are answered
/// once and never again.
/// </summary>
public sealed class IntegrationImportService(AdminContext ctx, IntegrationProvisioner provisioner)
{
    /// <summary>What importing <paramref name="input"/> would create, and what differs; nothing is saved.</summary>
    public async Task<IntegrationImportDto> PreviewAsync(IntegrationImportRequest input, CancellationToken ct) =>
        IntegrationImportDto.Of(await ApplyAsync(input, preview: true, ct));

    /// <summary>Creates what <paramref name="input"/> declares and is missing; the answer holds each new key's secret, once.</summary>
    public async Task<IntegrationImportDto> ImportAsync(IntegrationImportRequest input, CancellationToken ct)
    {
        var result = await ApplyAsync(input, preview: false, ct);
        if (result.CreatedIntegration || result.Keys.Any(k => k.State == ProvisionedKeyState.Created))
        {
            await ctx.PublishAsync([InvalidationKind.Keys], ct);
        }

        return IntegrationImportDto.Of(result);
    }

    private async Task<ProvisioningResult> ApplyAsync(IntegrationImportRequest input, bool preview, CancellationToken ct)
    {
        try
        {
            var manifest = IntegrationManifest.Parse(input.Manifest);
            return await provisioner.ApplyAsync(ctx.Db, manifest, ctx.Actor, ct, preview);
        }
        catch (ManifestException ex)
        {
            throw ApiFaultException.Validation(new Dictionary<string, string[]> { ["manifest"] = [.. ex.Errors] });
        }
    }
}

/// <summary>
/// The presets the admin console offers on <i>Add from manifest</i>: the <c>*.json</c> manifests with a <c>preset</c> title in
/// <see cref="AdminOptions.IntegrationPresetsPath"/>. Read when listed (a few small files, admins only); a file that is not
/// a valid preset is logged and left out, so one broken file does not hide the others.
/// </summary>
public sealed partial class IntegrationPresets(IOptions<AdminOptions> options, ILogger<IntegrationPresets> logger)
{
    public async Task<List<IntegrationPresetDto>> ListAsync(CancellationToken ct)
    {
        var path = options.Value.IntegrationPresetsPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            return [];
        }

        // Relative to the application's directory, where the build copies the shipped presets (also under dotnet run).
        var directory = Path.IsPathRooted(path) ? path : Path.Combine(AppContext.BaseDirectory, path);
        if (!Directory.Exists(directory))
        {
            return [];
        }

        var presets = new List<IntegrationPresetDto>();
        foreach (var file in Directory.GetFiles(directory, "*.json").Order(StringComparer.Ordinal))
        {
            var text = await File.ReadAllTextAsync(file, ct);
            try
            {
                var manifest = IntegrationManifest.Parse(text);
                if (manifest.Preset is not { } preset)
                {
                    throw new ManifestException(["'preset.title' is required in a preset."]);
                }

                presets.Add(new IntegrationPresetDto(Path.GetFileNameWithoutExtension(file), preset.Title!.Trim(), preset.Description?.Trim(), text));
            }
            catch (ManifestException ex)
            {
                LogInvalidPreset(logger, Path.GetFileName(file), ex.Message);
            }
        }

        return presets;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Integration preset {File} is left out: {Reason}")]
    private static partial void LogInvalidPreset(ILogger logger, string file, string reason);
}
