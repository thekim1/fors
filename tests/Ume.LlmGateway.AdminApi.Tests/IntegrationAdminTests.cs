using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Ume.LlmGateway.Infrastructure.Auditing;
using Ume.LlmGateway.Infrastructure.Persistence;
using Ume.LlmGateway.Infrastructure.Stores;

namespace Ume.LlmGateway.AdminApi.Tests;

/// <summary>Integrations and their keys on the admin API (docs/integrations.md).</summary>
public sealed class IntegrationAdminTests(AdminFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("viewer", 403)]
    [InlineData("department-admin", 403)]
    public async Task Only_gateway_admins_change_integrations(string role, int status)
    {
        using var admin = await fixture.ClientAsync();
        var org = await fixture.OrganisationAsync(admin);
        using var client = await fixture.ClientAsync(role, org.Code);
        ((int)(await client.PostAsJsonAsync("/api/integrations", new { name = "Denied", ownerTeamId = org.Team }, Ct)).StatusCode).ShouldBe(status);
        var id = await CreateIntegrationAsync(admin, org.Team);
        ((int)(await client.PutAsJsonAsync($"/api/integrations/{id}", new { name = "Denied", ownerTeamId = org.Team }, Ct)).StatusCode).ShouldBe(status);
        ((int)(await client.DeleteAsync($"/api/integrations/{id}", Ct)).StatusCode).ShouldBe(status);
    }

    [Fact]
    public async Task A_department_admin_lists_integrations_and_assigns_a_key_that_has_no_secret()
    {
        using var admin = await fixture.ClientAsync();
        var platform = await fixture.OrganisationAsync(admin);
        var integration = await CreateIntegrationAsync(admin, platform.Team);
        var department = await fixture.OrganisationAsync(admin);
        using var client = await fixture.ClientAsync("department-admin", department.Code);

        using var list = await client.GetAsync("/api/integrations", Ct);
        (await AdminFixture.ReadAsync(list)).AsArray().ShouldContain(i => i!["id"]!.GetValue<Guid>() == integration);

        using var create = await client.PostAsJsonAsync("/api/keys", Key(department.Team, integration), Ct);
        var created = await AdminFixture.ReadAsync(create, HttpStatusCode.Created);
        created["secret"].ShouldBeNull();
        var key = created["key"]!;
        key["integrationId"]!.GetValue<Guid>().ShouldBe(integration);
        key["integrationRole"]!.GetValue<string>().ShouldBe("Assigned");
        key["canReveal"]!.GetValue<bool>().ShouldBeFalse();
        var id = key["id"]!.GetValue<Guid>();

        using (var scope = fixture.Services.CreateScope())
        {
            var stored = await scope.ServiceProvider.GetRequiredService<GatewayDbContext>().VirtualKeys.AsNoTracking().SingleAsync(k => k.Id == id, Ct);
            stored.EncryptedSecret.ShouldBeNull(); // nothing to reveal: the key is only used by reference
        }

        // The key's own settings stay with its managers; it cannot be rotated or revealed, only revoked.
        using (var update = await client.PutAsJsonAsync($"/api/keys/{id}", Key(department.Team, integration, requestsPerMinute: 30), Ct))
        {
            (await AdminFixture.ReadAsync(update))["requestsPerMinute"]!.GetValue<int>().ShouldBe(30);
        }

        (await client.PostAsJsonAsync($"/api/keys/{id}/rotate", new { mode = "RevokeImmediately" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await admin.PostAsJsonAsync($"/api/keys/{id}/reveal", new { purpose = "Reveal" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await client.PostAsync($"/api/keys/{id}/revoke", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        using var byIntegration = await admin.GetAsync($"/api/keys?integrationId={integration}", Ct);
        (await AdminFixture.ReadAsync(byIntegration)).AsArray().Select(k => k!["id"]!.GetValue<Guid>()).ShouldBe([id]);
    }

    [Fact]
    public async Task Credential_keys_are_made_and_changed_by_gateway_admins_in_the_owner_team()
    {
        using var admin = await fixture.ClientAsync();
        var platform = await fixture.OrganisationAsync(admin);
        var integration = await CreateIntegrationAsync(admin, platform.Team);
        var other = await fixture.OrganisationAsync(admin);

        // Not in the owner team.
        using (var wrongTeam = await admin.PostAsJsonAsync("/api/keys", Key(other.Team, integration, role: "Credential"), Ct))
        {
            wrongTeam.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }

        // Not by the platform team's department admin.
        using var departmentAdmin = await fixture.ClientAsync("department-admin", platform.Code);
        (await departmentAdmin.PostAsJsonAsync("/api/keys", Key(platform.Team, integration, role: "Credential"), Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        using var create = await admin.PostAsJsonAsync("/api/keys", Key(platform.Team, integration, role: "Credential", scopes: ["usage.read"]), Ct);
        var created = await AdminFixture.ReadAsync(create, HttpStatusCode.Created);
        created["secret"]!.GetValue<string>().ShouldStartWith("ume-sk-");
        var key = created["key"]!;
        key["integrationRole"]!.GetValue<string>().ShouldBe("Credential");
        key["integrationScopes"]!.AsArray().Select(s => s!.GetValue<string>()).ShouldBe(["usage.read"]);
        var id = key["id"]!.GetValue<Guid>();

        // The department admin may rotate and revoke it, not change it.
        (await departmentAdmin.PutAsJsonAsync($"/api/keys/{id}", Key(platform.Team, integration, requestsPerMinute: 1), Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        using var rotate = await departmentAdmin.PostAsJsonAsync($"/api/keys/{id}/rotate", new { mode = "RevokeImmediately" }, Ct);
        var rotated = await AdminFixture.ReadAsync(rotate);
        rotated["key"]!["integrationId"]!.GetValue<Guid>().ShouldBe(integration);
        rotated["key"]!["integrationRole"]!.GetValue<string>().ShouldBe("Credential");
        rotated["key"]!["integrationScopes"]!.AsArray().Select(s => s!.GetValue<string>()).ShouldBe(["usage.read"]);

        // An unknown scope, scopes on an assigned key, and moving a key to another integration are refused.
        (await admin.PostAsJsonAsync("/api/keys", Key(platform.Team, integration, role: "Credential", scopes: ["everything"]), Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await admin.PostAsJsonAsync("/api/keys", Key(other.Team, integration, scopes: ["usage.read"]), Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var second = await CreateIntegrationAsync(admin, platform.Team);
        var newId = rotated["key"]!["id"]!.GetValue<Guid>();
        (await admin.PutAsJsonAsync($"/api/keys/{newId}", Key(platform.Team, second, role: "Credential"), Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task An_integration_with_live_keys_cannot_be_deleted_and_a_deactivated_one_takes_no_keys()
    {
        using var admin = await fixture.ClientAsync();
        var platform = await fixture.OrganisationAsync(admin);
        var integration = await CreateIntegrationAsync(admin, platform.Team);
        using var create = await admin.PostAsJsonAsync("/api/keys", Key(platform.Team, integration, role: "Credential"), Ct);
        var keyId = (await AdminFixture.ReadAsync(create, HttpStatusCode.Created))["key"]!["id"]!.GetValue<Guid>();

        using (var get = await admin.GetAsync($"/api/integrations/{integration}", Ct))
        {
            (await AdminFixture.ReadAsync(get))["credentialKeys"]!.GetValue<int>().ShouldBe(1);
        }

        (await admin.DeleteAsync($"/api/integrations/{integration}", Ct)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await admin.DeleteAsync($"/api/teams/{platform.Team}", Ct)).StatusCode.ShouldBe(HttpStatusCode.Conflict);

        var name = "Paused " + Guid.NewGuid();
        using (var deactivate = await admin.PutAsJsonAsync($"/api/integrations/{integration}", new { name, ownerTeamId = platform.Team, isActive = false }, Ct))
        {
            (await AdminFixture.ReadAsync(deactivate))["isActive"]!.GetValue<bool>().ShouldBeFalse();
        }

        (await admin.PostAsJsonAsync("/api/keys", Key(platform.Team, integration), Ct)).StatusCode.ShouldBe(HttpStatusCode.Conflict);

        (await admin.PostAsync($"/api/keys/{keyId}/revoke", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await admin.DeleteAsync($"/api/integrations/{integration}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        using var audit = await admin.GetAsync("/api/audit?entityType=Integration&pageSize=200", Ct);
        (await AdminFixture.ReadAsync(audit))["items"]!.AsArray().Where(e => e!["entityId"]!.GetValue<string>() == integration.ToString())
            .Select(e => e!["action"]!.GetValue<string>()).ShouldBe(["delete", "update", "create"], ignoreOrder: true);
    }

    [Fact]
    public async Task Moving_an_integration_moves_its_credential_keys_that_are_not_revoked_and_audits_each()
    {
        using var admin = await fixture.ClientAsync();
        var platform = await fixture.OrganisationAsync(admin);
        var integration = await CreateIntegrationAsync(admin, platform.Team);
        async Task<Guid> CreateKeyAsync(Guid team, string? role)
        {
            using var response = await admin.PostAsJsonAsync("/api/keys", Key(team, integration, role: role), Ct);
            return (await AdminFixture.ReadAsync(response, HttpStatusCode.Created))["key"]!["id"]!.GetValue<Guid>();
        }

        var live = await CreateKeyAsync(platform.Team, "Credential");
        var rotated = await CreateKeyAsync(platform.Team, "Credential");
        Guid replacement;
        using (var rotate = await admin.PostAsJsonAsync($"/api/keys/{rotated}/rotate", new { mode = "Grace24Hours" }, Ct))
        {
            replacement = (await AdminFixture.ReadAsync(rotate))["key"]!["id"]!.GetValue<Guid>(); // the old key is in its grace period
        }

        var revoked = await CreateKeyAsync(platform.Team, "Credential");
        (await admin.PostAsync($"/api/keys/{revoked}/revoke", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var department = await fixture.OrganisationAsync(admin);
        var assigned = await CreateKeyAsync(department.Team, null);
        var other = await fixture.OrganisationAsync(admin);
        var changes = new List<InvalidationKind>();
        using var subscription = fixture.Services.GetRequiredService<IInvalidationBus>().Subscribe(kind => { lock (changes) { changes.Add(kind); } });

        // The new owner team must exist.
        (await admin.PutAsJsonAsync($"/api/integrations/{integration}", new { name = "Moved " + Guid.NewGuid(), ownerTeamId = Guid.NewGuid() }, Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        // ... and be active, in an active department: the gateway refuses the keys of an inactive one.
        var idle = await fixture.OrganisationAsync(admin);
        using (var deactivated = await admin.PutAsJsonAsync($"/api/teams/{idle.Team}", new { departmentId = idle.Department, name = "Idle team", isActive = false }, Ct))
        {
            deactivated.StatusCode.ShouldBe(HttpStatusCode.OK, await deactivated.Content.ReadAsStringAsync(Ct));
        }

        using (var refused = await admin.PutAsJsonAsync($"/api/integrations/{integration}", new { name = "Moved " + Guid.NewGuid(), ownerTeamId = idle.Team }, Ct))
        {
            refused.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            (await refused.Content.ReadAsStringAsync(Ct)).ShouldContain("ownerTeamId");
        }

        var name = "Moved " + Guid.NewGuid();
        using (var move = await admin.PutAsJsonAsync($"/api/integrations/{integration}", new { name, ownerTeamId = other.Team }, Ct))
        {
            var moved = await AdminFixture.ReadAsync(move);
            moved["ownerTeamId"]!.GetValue<Guid>().ShouldBe(other.Team);
            moved["ownerDepartmentId"]!.GetValue<Guid>().ShouldBe(other.Department);
            moved["credentialKeys"]!.GetValue<int>().ShouldBe(3); // live, the rotated one in its grace period and its replacement
            moved["assignedKeys"]!.GetValue<int>().ShouldBe(1);
        }

        lock (changes) { changes.ShouldContain(InvalidationKind.Keys); }
        using (var scope = fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
            var teams = await db.VirtualKeys.AsNoTracking().Where(k => k.IntegrationId == integration).ToDictionaryAsync(k => k.Id, k => k.TeamId, Ct);
            teams[live].ShouldBe(other.Team);
            teams[rotated].ShouldBe(other.Team);
            teams[replacement].ShouldBe(other.Team);
            teams[revoked].ShouldBe(platform.Team); // its history stays where it was
            teams[assigned].ShouldBe(department.Team); // belongs to its own team

            var audit = await db.AuditLog.AsNoTracking().Where(a => a.EntityType == AuditEntities.VirtualKey && a.Action == AuditActions.Update).ToListAsync(Ct);
            foreach (var id in new[] { live, rotated, replacement })
            {
                var details = JsonNode.Parse(audit.Single(a => a.EntityId == id.ToString()).Details!)!;
                details["before"]!["teamId"]!.GetValue<Guid>().ShouldBe(platform.Team);
                details["after"]!["teamId"]!.GetValue<Guid>().ShouldBe(other.Team);
            }

            audit.ShouldNotContain(a => a.EntityId == revoked.ToString() || a.EntityId == assigned.ToString());
        }

        // The keys stay in the owner team: a new credential key goes in the new one, not the old.
        (await admin.PostAsJsonAsync("/api/keys", Key(platform.Team, integration, role: "Credential"), Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await admin.PostAsJsonAsync("/api/keys", Key(other.Team, integration, role: "Credential"), Ct)).StatusCode.ShouldBe(HttpStatusCode.Created);
        using var integrationAudit = await admin.GetAsync("/api/audit?entityType=Integration&pageSize=200", Ct);
        (await AdminFixture.ReadAsync(integrationAudit))["items"]!.AsArray().Where(e => e!["entityId"]!.GetValue<string>() == integration.ToString())
            .Select(e => e!["action"]!.GetValue<string>()).ShouldBe(["update", "create"], ignoreOrder: true);
    }

    [Fact]
    public async Task A_models_output_limit_is_kept_checked_against_the_context_window_and_exported()
    {
        using var client = await fixture.ClientAsync();
        var suffix = Guid.NewGuid().ToString("N");
        using var providerResponse = await client.PostAsJsonAsync("/api/providers", new
        {
            name = "limits-" + suffix, baseUrl = "https://limits.invalid/v1", type = "OpenAICompatible", authMode = "None", residency = "Eu",
            capabilities = new[] { "ChatCompletions" }, timeoutSeconds = 60, isEnabled = true,
        }, Ct);
        var provider = (await AdminFixture.ReadAsync(providerResponse, HttpStatusCode.Created))["id"]!.GetValue<Guid>();
        object Model(int? output) => new
        {
            providerId = provider, name = "limits-" + suffix + "/chat", upstreamModel = "chat", kind = "Chat", parameterProfile = "Standard",
            contextWindow = 8000, maxOutputTokens = output, isEnabled = true,
        };

        (await client.PostAsJsonAsync("/api/models", Model(9000), Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        using var create = await client.PostAsJsonAsync("/api/models", Model(4000), Ct);
        (await AdminFixture.ReadAsync(create, HttpStatusCode.Created))["maxOutputTokens"]!.GetValue<int>().ShouldBe(4000);

        using var export = await client.GetAsync("/api/ops/config/export", Ct);
        var exported = (await AdminFixture.ReadAsync(export))["models"]!.AsArray()
            .Single(m => m!["configuration"]!["name"]!.GetValue<string>() == "limits-" + suffix + "/chat")!;
        exported["configuration"]!["maxOutputTokens"]!.GetValue<int>().ShouldBe(4000);
    }

    private static async Task<Guid> CreateIntegrationAsync(HttpClient admin, Guid ownerTeam)
    {
        using var response = await admin.PostAsJsonAsync("/api/integrations",
            new { name = "Platform " + Guid.NewGuid().ToString("N")[..8], ownerTeamId = ownerTeam, description = "RAG" }, Ct);
        var created = await AdminFixture.ReadAsync(response, HttpStatusCode.Created);
        created["ownerTeamId"]!.GetValue<Guid>().ShouldBe(ownerTeam);
        created["isActive"]!.GetValue<bool>().ShouldBeTrue();
        return created["id"]!.GetValue<Guid>();
    }

    private static object Key(Guid team, Guid integration, string? role = null, string[]? scopes = null, int requestsPerMinute = 60) => new
    {
        teamId = team, name = "Chatbot", allowedModels = Array.Empty<string>(), allowedResidencies = Array.Empty<string>(), piiPolicy = "Off",
        requestsPerMinute, integrationId = integration, integrationRole = role, integrationScopes = scopes,
    };
}
