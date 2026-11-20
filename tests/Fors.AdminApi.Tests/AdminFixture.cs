using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Testcontainers.PostgreSql;
using Fors.AdminApi.Catalog.Pricing;
using Fors.TestKit;

[assembly: AssemblyFixture(typeof(Fors.AdminApi.Tests.AdminFixture))]
// All tests share one database, and some act on all of it (configuration export and import, global routing
// rules). Running test classes in parallel made those tests overwrite or collide with each other.
[assembly: CollectionBehavior(CollectionBehavior.CollectionPerAssembly)]

namespace Fors.AdminApi.Tests;

public sealed class AdminFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = TestPostgres.Builder().Build();
    private WebApplicationFactory<Program> _factory = null!;
    public IServiceProvider Services => _factory.Services;
    public static JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public async ValueTask InitializeAsync()
    {
        await _postgres.StartAsync();
        await TestPostgres.MigrateAsync(_postgres.GetConnectionString());
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("ConnectionStrings:gatewaydb", _postgres.GetConnectionString());
            builder.UseSetting("Security:KeyPepper", TestSecrets.RandomHex());
            builder.UseSetting("Oidc:Authority", "https://identity.invalid");
            builder.UseSetting("Admin:RequestsPerMinute", "10000");
            builder.UseSetting("Admin:CheckForReleases", "false"); // tests never call GitHub
            builder.UseSetting("Admin:PriceListUrl", "https://models.dev.test/api.json"); // nor models.dev: ModelListJson is served instead
            builder.ConfigureTestServices(services =>
            {
                // A static OIDC configuration, so nothing is fetched from the placeholder authority. It follows the effective
                // authority the options were built with, and none is set without one (sign-in is then not configured).
                services.Configure<OpenIdConnectOptions>(OpenIdConnectDefaults.AuthenticationScheme, o =>
                {
                    if (o.Authority is { } authority)
                    {
                        o.Configuration = StaticConfiguration(authority);
                    }
                });
                services.AddHeaderAuthentication();
                services.AddHttpClient(ModelListService.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => new FakeModelList());
            });
        });
    }

    /// <summary>The placeholder provider's metadata under <paramref name="authority"/>: its endpoints are the authority's paths.</summary>
    public static OpenIdConnectConfiguration StaticConfiguration(string authority) => new()
    {
        Issuer = authority, AuthorizationEndpoint = authority + "/authorize", TokenEndpoint = authority + "/token", EndSessionEndpoint = authority + "/logout",
    };

    /// <summary>Another admin API instance on the same database, with <paramref name="configure"/> applied on top. Dispose it.</summary>
    public WebApplicationFactory<Program> Derive(Action<IWebHostBuilder> configure) => _factory.WithWebHostBuilder(configure);

    /// <summary>The models.dev list the admin API downloads (see ModelListTests); empty by default.</summary>
    public static string ModelListJson { get; set; } = "{}";

    private sealed class FakeModelList : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(ModelListJson) });
    }

    /// <summary><paramref name="role"/> may name several roles, comma-separated; <paramref name="teams"/> are team codes.</summary>
    public async Task<HttpClient> ClientAsync(string? role = "gateway-admin", string? code = null, string? teams = null)
    {
        await TestClock.InOneBudgetDayAsync(); // reports and budgets count from midnight in Stockholm
        var client = _factory.CreateHttpsClient();
        if (role is not null)
        {
            client.DefaultRequestHeaders.Add(HeaderAuthenticationHandler.RoleHeader, role);
            if (code is not null) { client.DefaultRequestHeaders.Add(HeaderAuthenticationHandler.DepartmentHeader, code); }
            if (teams is not null) { client.DefaultRequestHeaders.Add(HeaderAuthenticationHandler.TeamHeader, teams); }
        }
        return await client.WithXsrfTokenAsync();
    }

    public async Task<(Guid Department, Guid Team, string Code)> OrganisationAsync(HttpClient client)
    {
        var code = Guid.NewGuid().ToString("N");
        using var dept = await client.PostAsJsonAsync("/api/departments", new { name = "Department " + code, costCenterCode = code });
        var d = await ReadAsync(dept, HttpStatusCode.Created);
        using var team = await client.PostAsJsonAsync("/api/teams", new { departmentId = d["id"]!.GetValue<Guid>(), name = "Team" });
        var t = await ReadAsync(team, HttpStatusCode.Created);
        return (d["id"]!.GetValue<Guid>(), t["id"]!.GetValue<Guid>(), code);
    }

    public static async Task<JsonNode> ReadAsync(HttpResponseMessage response, HttpStatusCode status = HttpStatusCode.OK)
    {
        var text = await response.Content.ReadAsStringAsync();
        response.StatusCode.ShouldBe(status, text);
        return JsonNode.Parse(text)!;
    }

    public async ValueTask DisposeAsync()
    {
        await _factory.DisposeAsync();
        await _postgres.DisposeAsync();
    }
}
