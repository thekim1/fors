using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Ume.LlmGateway.AdminApi.Common;
using Ume.LlmGateway.AdminApi.Settings;
using Ume.LlmGateway.Domain.Entities;

namespace Ume.LlmGateway.AdminApi.Setup;

/// <summary>
/// The problem codes of the setup API (<c>code</c> in the problem response; <c>reason</c> beside it for a test). English messages;
/// the admin UI shows its own text per code.
/// </summary>
public static class SetupErrors
{
    public const string CodeInvalid = "setup_code_invalid";
    public const string AttemptsExhausted = "setup_attempts_exhausted";
    public const string SessionRequired = "setup_session_required";
    public const string TestRefused = "setup_test_refused";
    public const string TestRequired = "setup_test_required";

    /// <summary>A wrong setup code. It never voids the code.</summary>
    public static ApiFaultException WrongCode() => Of(StatusCodes.Status400BadRequest, CodeInvalid, "The setup code is not right.");

    /// <summary>A setup route that needs the setup session: none, an expired one, or one of an earlier opening.</summary>
    public static ApiFaultException SessionMissing() => Of(StatusCodes.Status401Unauthorized, SessionRequired, "Enter the setup code first.");

    /// <summary>A test sign-in could not start; <paramref name="reason"/> is a <see cref="SetupTestReasons"/> code.</summary>
    public static ApiFaultException Refused(string reason) =>
        Of(StatusCodes.Status400BadRequest, TestRefused, $"The test sign-in could not start ({reason}).", reason);

    /// <summary>
    /// Saving needs the session's latest test, passed, by a gateway admin, with exactly the submitted values; <paramref name="reason"/>
    /// is <c>unknown_test</c>, <c>not_latest</c>, <c>not_passed</c>, <c>not_gateway_admin</c> or <c>values_changed</c>.
    /// </summary>
    public static ApiFaultException TestMissing(string reason) =>
        Of(StatusCodes.Status409Conflict, TestRequired, $"Saving needs a passed test sign-in by a gateway admin with exactly these values ({reason}).", reason);

    /// <summary>Setup closed meanwhile (a completion that lost the race): a 404 like every request after it.</summary>
    public static ApiFaultException Closed() => new(StatusCodes.Status404NotFound, "Setup mode is closed.");

    private static ApiFaultException Of(int status, string code, string detail, string? reason = null)
    {
        var extensions = new Dictionary<string, object?> { ["code"] = code };
        if (reason is not null)
        {
            extensions["reason"] = reason;
        }

        return new ApiFaultException(status, detail, extensions);
    }
}

/// <summary><c>POST /bff/setup/session</c>.</summary>
public sealed record SetupCodeRequest : AdminRequest
{
    [Required, MaxLength(SetupCode.MaxInputLength)]
    public string? Code { get; init; }
}

/// <summary>
/// Candidate sign-in values for <c>POST /bff/setup/test</c>. <see cref="ClientSecret"/>: null tests with the stored secret (only
/// for the provider and client it was stored for), empty tests without one (a public client), a value tests with it; a deployment
/// secret wins over all and goes only to the deployment's authority. A value the deployment sets wins over the candidate's.
/// </summary>
public record SetupValuesRequest : AdminRequest, IValidatableObject
{
    public const int MaxScopes = 20;

    [MaxLength(SignInSettings.MaxAuthorityLength)]
    public string? Authority { get; init; }

    [MaxLength(SignInSettings.MaxValueLength)]
    public string? ClientId { get; init; }

    [MaxLength(SignInSettings.MaxValueLength)]
    public string? ClientSecret { get; init; }

    [MaxLength(SignInSettings.MaxValueLength)]
    public string? RoleClaim { get; init; }

    [MaxLength(SignInSettings.MaxValueLength)]
    public string? DepartmentClaim { get; init; }

    [MaxLength(SignInSettings.MaxValueLength)]
    public string? TeamClaim { get; init; }

    [MaxLength(SignInSettings.MaxValueLength)]
    public string? GroupClaim { get; init; }

    [MaxLength(MaxScopes)]
    public IReadOnlyList<string>? ExtraScopes { get; init; }

    /// <summary>When a typed client secret expires, as the person enters it: a reminder, nothing enforces it.</summary>
    public DateOnly? ClientSecretExpiresOn { get; init; }

    public SignInWizardValues ToWizardValues() => new()
    {
        Authority = Authority, ClientId = ClientId, RoleClaim = RoleClaim, DepartmentClaim = DepartmentClaim, TeamClaim = TeamClaim, GroupClaim = GroupClaim,
        ExtraScopes = ExtraScopes, ClientSecretExpiresOn = ClientSecretExpiresOn,
    };

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        (string Name, string? Value)[] texts =
        [
            (nameof(Authority), Authority), (nameof(ClientId), ClientId), (nameof(ClientSecret), ClientSecret), (nameof(RoleClaim), RoleClaim),
            (nameof(DepartmentClaim), DepartmentClaim), (nameof(TeamClaim), TeamClaim), (nameof(GroupClaim), GroupClaim),
        ];
        foreach (var (name, _) in texts.Where(t => t.Value?.Any(char.IsControl) == true))
        {
            yield return new ValidationResult("The value may not hold control characters.", [name]);
        }

        if (ExtraScopes?.Any(s => s is null || s.Length > SignInSettings.MaxValueLength || s.Any(c => char.IsControl(c) || char.IsWhiteSpace(c))) == true)
        {
            yield return new ValidationResult($"Each scope is one word of at most {SignInSettings.MaxValueLength} characters.", [nameof(ExtraScopes)]);
        }
    }
}

/// <summary><c>POST /bff/setup/complete</c>: the tested values again (the secret is taken from the test, never sent again) and the test's id.</summary>
public sealed record SetupCompleteRequest : SetupValuesRequest
{
    public Guid TestId { get; init; }
}

/// <summary>A started setup session: when it and the opening end.</summary>
public sealed record SetupSessionDto(DateTimeOffset ExpiresAt, DateTimeOffset SetupEndsAt);

/// <summary>Where setup sends the person after completion: the normal sign-in.</summary>
public sealed record SetupCompletedDto(string SignInUrl);

/// <summary>The redirect URIs to register at the provider, as the OpenID Connect handler builds them for this request's address.</summary>
public sealed record SetupRedirectUris(string SignIn, string SignOutCallback);

/// <summary>
/// <c>GET /bff/setup/state</c>: <c>first_run</c> or <c>reopen</c>, when the opening and the session end, the sign-in values with their
/// sources (never a secret), the redirect URIs, the groups the deployment maps to <c>gateway-admin</c>, and the session's latest test.
/// </summary>
public sealed record SetupStateDto(
    string Kind, DateTimeOffset EndsAt, DateTimeOffset SessionExpiresAt, SignInSettingsDto SignIn, SetupRedirectUris RedirectUris,
    IReadOnlyList<string> GatewayAdminGroups, SetupTestDto? LatestTest);

/// <summary>A started test: the provider address the wizard opens, in the same browser.</summary>
public sealed record SetupTestStart(Guid TestId, string AuthorizeUrl, DateTimeOffset ExpiresAt);

/// <summary>The claim names a test read, so the wizard can explain a result.</summary>
public sealed record SetupClaimsRead(string Role, string Group, string Department, string Team);

/// <summary>
/// The tested person as the sign-in's claim mapping saw them; lists are capped at 100 entries, <see cref="GroupCount"/> is the
/// whole count. <see cref="IsGatewayAdmin"/>: the person comes out with <c>gateway-admin</c>.
/// </summary>
public sealed record SetupTestResult(
    string Subject, string? Name, IReadOnlyList<string> Roles, IReadOnlyList<string> Groups, int GroupCount, IReadOnlyList<string> DepartmentCodes,
    IReadOnlyList<string> TeamCodes, bool IsGatewayAdmin, SetupClaimsRead ClaimsRead);

/// <summary>
/// A test as the wizard shows it: <c>pending</c> (waiting for the callback), <c>passed</c>, <c>failed</c> (with its reason) or
/// <c>incomplete</c> (expired without a callback); the tested values without the secret, and where the secret came from
/// (<see cref="SetupSecretSources"/>).
/// </summary>
public sealed record SetupTestDto(
    Guid TestId, string Status, string? Reason, DateTimeOffset StartedAt, DateTimeOffset ExpiresAt, SignInWizardValues? Values, string? ClientSecret,
    SetupTestResult? Result)
{
    public static SetupTestDto Of(SetupTest test, SetupTestPayload? payload, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(test);
        var status = test.Passed switch
        {
            true => "passed",
            false => "failed",
            null => test.CallbackAt is null && test.ExpiresAt > now ? "pending" : "incomplete",
        };
        return new SetupTestDto(test.Id, status, test.Reason, test.CreatedAt, test.ExpiresAt, payload?.Values, payload?.SecretSource,
            test.Result is { } json ? JsonSerializer.Deserialize<SetupTestResult>(json, SetupTestSignIn.Json) : null);
    }
}
