using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Ume.LlmGateway.Domain;
using Ume.LlmGateway.Infrastructure.Auditing;
using Ume.LlmGateway.Infrastructure.Persistence;
using Ume.LlmGateway.Infrastructure.Stores;

namespace Ume.LlmGateway.AdminApi.Tests;

/// <summary>Adding an integration with its credential keys from a manifest or a preset (docs/integrations.md, ADR 022).</summary>
public sealed class IntegrationImportTests(AdminFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Manifest(string name, string teamCode, string departmentCode) => $$"""
        {
          "schemaVersion": 1,
          "integration": { "name": "{{name}}", "owner": { "teamCode": "{{teamCode}}", "teamName": "Platform", "departmentCode": "{{departmentCode}}", "departmentName": "IT" } },
          "credentialKeys": [
            { "name": "api", "requireMaxResidency": true },
            { "name": "admin", "integrationScopes": ["usage.read"], "allowedIpRanges": ["10.1.0.0/16"] }
          ]
        }
        """;

    private static string Unique(string prefix) => prefix + "-" + Guid.NewGuid().ToString("N")[..8];

    [Theory]
    [InlineData("viewer")]
    [InlineData("department-admin")]
    public async Task Only_gateway_admins_list_presets_preview_and_import(string role)
    {
        using var client = await fixture.ClientAsync(role, Guid.NewGuid().ToString("N"));
        var body = new { manifest = Manifest(Unique("Denied"), Unique("t"), Unique("d")) };
        (await client.GetAsync("/api/integrations/presets", Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await client.PostAsJsonAsync("/api/integrations/import/preview", body, Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await client.PostAsJsonAsync("/api/integrations/import", body, Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task The_shipped_presets_are_listed_with_their_manifest()
    {
        using var admin = await fixture.ClientAsync();
        using var response = await admin.GetAsync("/api/integrations/presets", Ct);
        var preset = (await AdminFixture.ReadAsync(response)).AsArray().Single(p => p!["id"]!.GetValue<string>() == "ume-rag")!;

        preset["title"]!.GetValue<string>().ShouldBe("Ume.Rag");
        var manifest = preset["manifest"]!.GetValue<string>();
        manifest.ShouldContain("\"rag-worker\"");
        // The department is left for the admin to fill in, so a preview asks for it instead of creating a placeholder.
        using var preview = await admin.PostAsJsonAsync("/api/integrations/import/preview", new { manifest = manifest.Replace("\"ume-rag\"", $"\"{Unique("rag")}\"", StringComparison.Ordinal) }, Ct);
        var problem = await AdminFixture.ReadAsync(preview, HttpStatusCode.BadRequest);
        problem["errors"]!["manifest"]![0]!.GetValue<string>().ShouldContain("departmentCode");
    }

    [Fact]
    public async Task A_preview_saves_nothing_and_an_import_creates_the_keys_and_shows_their_secrets_once()
    {
        using var admin = await fixture.ClientAsync();
        var name = Unique("Platform");
        var body = new { manifest = Manifest(name, Unique("team"), Unique("dep")) };
        var changes = new List<InvalidationKind>();
        using var subscription = fixture.Services.GetRequiredService<IInvalidationBus>().Subscribe(kind => { lock (changes) { changes.Add(kind); } });

        using (var preview = await admin.PostAsJsonAsync("/api/integrations/import/preview", body, Ct))
        {
            var result = await AdminFixture.ReadAsync(preview);
            result["integrationId"].ShouldBeNull();
            result["createdIntegration"]!.GetValue<bool>().ShouldBeTrue();
            result["createdTeam"]!.GetValue<bool>().ShouldBeTrue();
            result["createdDepartment"]!.GetValue<bool>().ShouldBeTrue();
            Keys(result).ShouldAllBe(k => k["state"]!.GetValue<string>() == "Created" && k["secret"] == null && k["id"] == null);
        }

        using (var scope = fixture.Services.CreateScope())
        {
            (await scope.ServiceProvider.GetRequiredService<GatewayDbContext>().Integrations.AnyAsync(i => i.Name == name, Ct)).ShouldBeFalse();
        }

        lock (changes) { changes.ShouldBeEmpty(); }

        using var import = await admin.PostAsJsonAsync("/api/integrations/import", body, Ct);
        var created = await AdminFixture.ReadAsync(import);
        var integrationId = created["integrationId"]!.GetValue<Guid>();
        var keys = Keys(created).ToDictionary(k => k["name"]!.GetValue<string>());
        keys.Values.ShouldAllBe(k => k["state"]!.GetValue<string>() == "Created" && k["secret"]!.GetValue<string>().StartsWith("ume-sk-", StringComparison.Ordinal));
        lock (changes) { changes.ShouldContain(InvalidationKind.Keys); }

        using (var scope = fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
            var stored = await db.VirtualKeys.AsNoTracking().Where(k => k.IntegrationId == integrationId).ToDictionaryAsync(k => k.Name, Ct);
            stored["admin"].IntegrationRole.ShouldBe(IntegrationRole.Credential);
            stored["admin"].IntegrationScopes.ShouldBe(["usage.read"]);
            stored["admin"].AllowedIpRanges.ShouldBe(["10.1.0.0/16"]);
            stored["api"].RequireMaxResidency.ShouldBeTrue();

            // Audited as the signed-in admin, with the session details every admin change carries.
            var audit = await db.AuditLog.AsNoTracking().Where(a => a.EntityType == AuditEntities.VirtualKey && a.EntityId == stored["api"].Id.ToString()).SingleAsync(Ct);
            audit.Action.ShouldBe(AuditActions.Create);
            audit.ActorName.ShouldNotBeNull();
            audit.RequestId.ShouldNotBeNull();
            audit.Details!.ShouldNotContain(keys["api"]["secret"]!.GetValue<string>());
        }

        // Importing again creates nothing and shows no secret.
        using var again = await admin.PostAsJsonAsync("/api/integrations/import", body, Ct);
        Keys(await AdminFixture.ReadAsync(again)).ShouldAllBe(k => k["state"]!.GetValue<string>() == "Existing" && k["secret"] == null);
    }

    [Fact]
    public async Task A_manifest_that_cannot_be_applied_is_a_validation_problem_on_the_manifest()
    {
        using var admin = await fixture.ClientAsync();
        using var response = await admin.PostAsJsonAsync("/api/integrations/import", new { manifest = """{ "schemaVersion": 1, "integration": { "name": "x", "owner": { "teamCode": "t" } }, "credentialKeys": [{ "name": "a", "integrationScopes": ["everything"] }] }""" }, Ct);
        var problem = await AdminFixture.ReadAsync(response, HttpStatusCode.BadRequest);
        problem["errors"]!["manifest"]!.AsArray().ShouldContain(e => e!.GetValue<string>().Contains("everything", StringComparison.Ordinal));
    }

    private static IEnumerable<JsonNode> Keys(JsonNode result) => result["keys"]!.AsArray().Select(k => k!);
}
