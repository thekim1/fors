using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Fors.Domain;
using Fors.Domain.Keys;
using Fors.Domain.Networking;

namespace Fors.Infrastructure.Provisioning;

/// <summary>
/// An integration and its credential keys, declared in a file (docs/integrations.md, <i>Provisioning from a manifest</i>;
/// ADR 022). <see cref="IntegrationProvisioner"/> creates what does not exist yet and never changes what does.
/// </summary>
public sealed record IntegrationManifest
{
    /// <summary>The only version so far; a later format gets a new number and keeps reading this one.</summary>
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; }

    /// <summary>Only in a preset file: how the admin console lists it. Not applied.</summary>
    public ManifestPreset? Preset { get; init; }

    public ManifestIntegration? Integration { get; init; }
    public IReadOnlyList<ManifestCredentialKey> CredentialKeys { get; init; } = [];

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) },
        // A misspelt property must not be ignored silently: it would create a key without the setting it meant.
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    /// <summary>Reads and validates a manifest; throws <see cref="ManifestException"/> listing every problem.</summary>
    public static IntegrationManifest Parse(string json)
    {
        IntegrationManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<IntegrationManifest>(json, Json);
        }
        catch (JsonException ex)
        {
            // The message names the path and position, never values from elsewhere in the file.
            throw new ManifestException([$"The manifest is not valid: {ex.Message}"]);
        }

        if (manifest is null)
        {
            throw new ManifestException(["The manifest is empty."]);
        }

        var errors = manifest.Validate();
        return errors.Count == 0 ? manifest : throw new ManifestException(errors);
    }

    /// <summary>Every problem in the manifest; empty when it can be applied.</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (SchemaVersion != CurrentSchemaVersion)
        {
            errors.Add($"'schemaVersion' must be {CurrentSchemaVersion}.");
        }

        if (Preset is { } preset)
        {
            Required(errors, "preset.title", preset.Title, 100);
            Length(errors, "preset.description", preset.Description, 1000);
        }

        if (Integration is not { } integration)
        {
            errors.Add("'integration' is required.");
        }
        else
        {
            if (!ManifestNames.IsValid(integration.Name, 100))
            {
                errors.Add($"'integration.name' must be 1-100 characters of letters, digits, '.', '_' or '-', starting with a letter or digit (it names a directory of secret files).");
            }

            Length(errors, "integration.description", integration.Description, 1000);
            if (integration.Owner is not { } owner)
            {
                // Without an owner a new integration gets a default one, whose codes are made from the name.
                if (ManifestNames.IsValid(integration.Name, 100) && ManifestOwner.DefaultCode(integration.Name!) is { Length: 0 or > ManifestOwner.MaxDefaultCodeLength } code)
                {
                    errors.Add($"'integration.owner' is required for this name: the default owner's code made from 'integration.name' ('{code}') must be 1-{ManifestOwner.MaxDefaultCodeLength} characters.");
                }
            }
            else
            {
                Required(errors, "integration.owner.teamCode", owner.TeamCode, 100);
                Length(errors, "integration.owner.teamName", owner.TeamName, 200);
                Length(errors, "integration.owner.departmentCode", owner.DepartmentCode, 50);
                Length(errors, "integration.owner.departmentName", owner.DepartmentName, 200);
            }
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < CredentialKeys.Count; i++)
        {
            var key = CredentialKeys[i];
            var at = $"credentialKeys[{i}]";
            if (!ManifestNames.IsValid(key.Name, 100))
            {
                errors.Add($"'{at}.name' must be 1-100 characters of letters, digits, '.', '_' or '-', starting with a letter or digit (it names its secret file).");
            }
            else if (!names.Add(key.Name!))
            {
                errors.Add($"'{at}.name' '{key.Name}' is listed twice.");
            }

            Length(errors, $"{at}.description", key.Description, 1000);
            foreach (var scope in key.IntegrationScopes.Where(s => !IntegrationScopes.IsKnown(s)))
            {
                errors.Add($"'{at}.integrationScopes' has the unknown scope '{scope}'; known: {string.Join(", ", IntegrationScopes.All)}.");
            }

            if (key.AllowedIpRanges.Count > NetworkRanges.MaxEntries)
            {
                errors.Add($"'{at}.allowedIpRanges' takes at most {NetworkRanges.MaxEntries} entries.");
            }
            else if (NetworkRanges.Normalize(key.AllowedIpRanges, out _) is { } error)
            {
                errors.Add($"'{at}.allowedIpRanges': {error}");
            }
        }

        return errors;
    }

    private static void Required(List<string> errors, string field, string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            errors.Add($"'{field}' is required.");
        }
        else
        {
            Length(errors, field, value, max);
        }
    }

    private static void Length(List<string> errors, string field, string? value, int max)
    {
        if (value is not null && (value.Trim().Length > max || value.Any(char.IsControl)))
        {
            errors.Add($"'{field}' must be at most {max} characters without control characters.");
        }
    }
}

/// <summary>How a preset (a manifest the admin console offers) is listed.</summary>
public sealed record ManifestPreset
{
    public string? Title { get; init; }
    public string? Description { get; init; }
}

/// <summary>The integration itself and the team that owns its credential keys.</summary>
public sealed record ManifestIntegration
{
    public string? Name { get; init; }
    public string? Description { get; init; }

    /// <summary>Optional: without it a new integration gets <see cref="ManifestOwner.Default"/>, and an existing one keeps its owner.</summary>
    public ManifestOwner? Owner { get; init; }
}

/// <summary>
/// The owner team, found by its code. When it does not exist it is created with <see cref="TeamName"/> in the department
/// with <see cref="DepartmentCode"/> (its cost centre code), which is created with <see cref="DepartmentName"/> if absent.
/// </summary>
public sealed record ManifestOwner
{
    /// <summary>The longest default code: a department's cost centre code holds 50 characters.</summary>
    public const int MaxDefaultCodeLength = 50;

    public string? TeamCode { get; init; }
    public string? TeamName { get; init; }
    public string? DepartmentCode { get; init; }
    public string? DepartmentName { get; init; }

    /// <summary>
    /// The owner of an integration whose manifest names none: a team and a department of its own, both with the code
    /// <see cref="DefaultCode"/> and the integration's name, which a gateway admin can move to the organisation's own team.
    /// </summary>
    public static ManifestOwner Default(string integrationName) => new()
    {
        TeamCode = DefaultCode(integrationName), TeamName = integrationName,
        DepartmentCode = DefaultCode(integrationName), DepartmentName = integrationName,
    };

    /// <summary>
    /// The name in lower case with its ASCII letters and digits kept (accents dropped first, so å, ä and ö count as a, a and
    /// o) and every other run of characters one <c>-</c>, trimmed of <c>-</c>: "My.Platform" gives "my-platform",
    /// "My__Platform-2" gives "my-platform-2", "Källa" gives "kalla".
    /// </summary>
    public static string DefaultCode(string integrationName)
    {
        ArgumentNullException.ThrowIfNull(integrationName);
        var code = new StringBuilder(integrationName.Length);
        foreach (var c in integrationName.Trim().Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsAsciiLetterOrDigit(c))
            {
                code.Append(char.ToLowerInvariant(c));
            }
            else if (code.Length > 0 && code[^1] != '-')
            {
                code.Append('-');
            }
        }

        return code.ToString().TrimEnd('-');
    }
}

/// <summary>A credential key of the integration and the settings it is created with.</summary>
public sealed record ManifestCredentialKey
{
    public string? Name { get; init; }
    public string? Description { get; init; }
    public IReadOnlyList<string> IntegrationScopes { get; init; } = [];

    /// <summary>The residencies the key may reach, and with it every request made as an assigned key; empty = all.</summary>
    public IReadOnlyList<DataResidency> AllowedResidencies { get; init; } = [];
    public bool RequireMaxResidency { get; init; }
    public PiiPolicy PiiPolicy { get; init; } = PiiPolicy.Off;
    public IReadOnlyList<string> AllowedIpRanges { get; init; } = [];
}

/// <summary>A manifest that cannot be applied, with every reason.</summary>
public sealed class ManifestException : Exception
{
    public ManifestException(IReadOnlyList<string> errors) : base(string.Join(" ", errors)) => Errors = errors;

    public ManifestException() : this([]) { }

    public ManifestException(string message) : this([message]) { }

    public ManifestException(string message, Exception inner) : base(message, inner) => Errors = [message];

    public IReadOnlyList<string> Errors { get; }
}

/// <summary>Names that are also file and directory names of secret files: no path separators, no leading dot.</summary>
public static partial class ManifestNames
{
    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]*$")]
    private static partial Regex Pattern();

    public static bool IsValid(string? name, int maxLength) => name is { Length: > 0 } && name.Length <= maxLength && Pattern().IsMatch(name);
}
