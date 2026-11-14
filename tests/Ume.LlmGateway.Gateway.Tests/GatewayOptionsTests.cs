using System.ComponentModel.DataAnnotations;

namespace Ume.LlmGateway.Gateway.Tests;

public sealed class GatewayOptionsTests
{
    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)] // the compose files pass an unset UME_ADMIN_PUBLIC_URL as ""
    [InlineData("https://admin.example.se", true)]
    [InlineData("https://admin.example.se:9443/", true)]
    [InlineData("admin.example.se", false)]
    [InlineData("ftp://admin.example.se", false)]
    public void The_admin_url_is_empty_or_an_http_url(string? adminUrl, bool valid)
    {
        var options = new GatewayOptions { AdminUrl = adminUrl };
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(options, new ValidationContext(options), results, validateAllProperties: true);
        results.Any(r => r.MemberNames.Contains(nameof(GatewayOptions.AdminUrl))).ShouldBe(!valid);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)] // the compose files pass an unset UME_INTEGRATION_API_URL as ""
    [InlineData("https://adminapi:8443/integration/v1", true)]
    [InlineData("adminapi:8443/integration/v1", false)]
    public void The_integration_api_url_is_empty_or_an_http_url(string? url, bool valid)
    {
        var options = new GatewayOptions { IntegrationApiUrl = url };
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(options, new ValidationContext(options), results, validateAllProperties: true);
        results.Any(r => r.MemberNames.Contains(nameof(GatewayOptions.IntegrationApiUrl))).ShouldBe(!valid);
    }
}
