namespace Fors.Infrastructure.Security;

/// <summary>
/// The names that are part of what the gateway has encrypted and stored with Data Protection: the application name and
/// the purposes. A value unprotects only under the names it was protected with, so they keep the product's name from
/// before the rename to Fors. They are format constants, never shown to anyone. Renaming one makes every value stored
/// under it unreadable: the provider credentials and the sign-in client secret saved in the setup wizard (#116).
/// </summary>
public static class StoredFormatNames
{
    /// <summary>The Data Protection application name, part of every protector's purpose chain.</summary>
    public const string DataProtectionApplication = "Ume.LlmGateway";

    /// <summary>A provider credential (<see cref="CredentialProtector"/>).</summary>
    public const string ProviderCredentialPurpose = "Ume.LlmGateway.ProviderCredential.v1";

    /// <summary>The admin console's stored sign-in client secret.</summary>
    public const string SignInClientSecretPurpose = "Ume.LlmGateway.AdminApi.SignInClientSecret.v1";

    /// <summary>A setup test sign-in's payload.</summary>
    public const string SetupTestPurpose = "Ume.LlmGateway.AdminApi.SetupTest.v1";

    /// <summary>An admin session's ticket.</summary>
    public const string AdminSessionPurpose = "Ume.LlmGateway.AdminApi.Session.v1";
}
