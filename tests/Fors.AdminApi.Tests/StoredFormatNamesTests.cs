using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using Fors.AdminApi.Security;
using Fors.AdminApi.Setup;
using Fors.Infrastructure;
using Fors.Infrastructure.Security;

namespace Fors.AdminApi.Tests;

/// <summary>
/// What a gateway from before the rename to Fors has encrypted stays readable (#116): a value protected under 0.11.0's
/// application name and purposes, written out here, unprotects with the same key ring under the names the services use
/// now.
/// </summary>
public sealed class StoredFormatNamesTests : IDisposable
{
    private readonly DirectoryInfo _ring = Directory.CreateTempSubdirectory("fors-dp-ring-");

    public void Dispose() => _ring.Delete(recursive: true);

    private IDataProtectionProvider Before() => DataProtectionProvider.Create(_ring, b => b.SetApplicationName("Ume.LlmGateway"));

    private IDataProtectionProvider Now() =>
        DataProtectionProvider.Create(_ring, b => b.SetApplicationName(InfrastructureExtensions.DataProtectionApplicationName));

    [Theory]
    [InlineData("Ume.LlmGateway.ProviderCredential.v1", StoredFormatNames.ProviderCredentialPurpose)]
    [InlineData("Ume.LlmGateway.AdminApi.SignInClientSecret.v1", SignInSecretProtector.Purpose)]
    [InlineData("Ume.LlmGateway.AdminApi.SetupTest.v1", SetupTestSignIn.Purpose)]
    [InlineData("Ume.LlmGateway.AdminApi.Session.v1", StoredFormatNames.AdminSessionPurpose)]
    public void A_value_protected_by_0_11_unprotects(string purposeBefore, string purposeNow)
    {
        var stored = Before().CreateProtector(purposeBefore).Protect("a stored secret");

        Now().CreateProtector(purposeNow).Unprotect(stored).ShouldBe("a stored secret");
    }

    [Fact]
    public void A_provider_credential_stored_by_0_11_unprotects_with_the_credential_protector()
    {
        var stored = Before().CreateProtector("Ume.LlmGateway.ProviderCredential.v1").Protect("sk-provider-secret");

        new CredentialProtector(Now()).Unprotect(stored).ShouldBe("sk-provider-secret");
    }

    [Fact]
    public void A_sign_in_client_secret_stored_by_0_11_unprotects_with_the_sign_in_secret_protector()
    {
        var stored = Before().CreateProtector("Ume.LlmGateway.AdminApi.SignInClientSecret.v1").Protect("wizard-client-secret");

        var read = new SignInSecretProtector(Now(), NullLogger<SignInSecretProtector>.Instance).Unprotect(stored);

        (read.State, read.Value).ShouldBe((StoredSecretState.Readable, "wizard-client-secret"));
    }
}
