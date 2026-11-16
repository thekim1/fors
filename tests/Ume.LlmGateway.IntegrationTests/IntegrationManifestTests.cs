extern alias Migration;

using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Migration::Ume.LlmGateway.MigrationService;
using Testcontainers.PostgreSql;
using Ume.LlmGateway.Domain;
using Ume.LlmGateway.Domain.Entities;
using Ume.LlmGateway.Domain.Keys;
using Ume.LlmGateway.Infrastructure.Auditing;
using Ume.LlmGateway.Infrastructure.Persistence;
using Ume.LlmGateway.Infrastructure.Provisioning;
using Ume.LlmGateway.Infrastructure.Security;
using Ume.LlmGateway.TestKit;

namespace Ume.LlmGateway.IntegrationTests;

public sealed class ManifestDatabase : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = TestPostgres.Builder().Build();

    public VirtualKeyHasher Hasher { get; } = new(RandomNumberGenerator.GetBytes(32));
    public CredentialProtector Protector { get; } = new(new EphemeralDataProtectionProvider());

    public GatewayDbContext Context() => TestPostgres.Context(_postgres.GetConnectionString());

    public IntegrationProvisioner Provisioner() => new(Hasher, Protector, TimeProvider.System);

    public async ValueTask InitializeAsync()
    {
        await _postgres.StartAsync();
        await TestPostgres.MigrateAsync(_postgres.GetConnectionString());
    }

    public async ValueTask DisposeAsync() => await _postgres.DisposeAsync();
}

public sealed class IntegrationManifestTests(ManifestDatabase database) : IClassFixture<ManifestDatabase>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A manifest like a RAG platform's: three credential keys with different scopes and residencies.</summary>
    private static string Manifest(string integration, string teamCode, string extraKey = "") => $$"""
        {
          // Comments and trailing commas are allowed.
          "schemaVersion": 1,
          "integration": {
            "name": "{{integration}}",
            "description": "A platform",
            "owner": { "teamCode": "{{teamCode}}", "teamName": "Platform team", "departmentCode": "{{teamCode}}-dep", "departmentName": "IT" }
          },
          "credentialKeys": [
            { "name": "api", "requireMaxResidency": true },
            { "name": "admin", "integrationScopes": ["usage.read", "organisation.read"], "requireMaxResidency": true },
            { "name": "worker", "allowedResidencies": ["OnPrem", "Eu"], "requireMaxResidency": true, "piiPolicy": "Off", "allowedIpRanges": ["10.0.0.0/8"] },
            {{extraKey}}
          ]
        }
        """;

    /// <summary>The shortest manifest that works: no owner, so a new integration gets one made from its name.</summary>
    private static string Ownerless(string integration) => $$"""
        { "schemaVersion": 1, "integration": { "name": "{{integration}}" }, "credentialKeys": [{ "name": "api" }] }
        """;

    private static string Unique(string prefix) => prefix + "-" + Guid.NewGuid().ToString("N")[..8];

    [Theory]
    [InlineData("""{ "schemaVersion": 2, "integration": { "name": "x", "owner": { "teamCode": "t" } } }""", "'schemaVersion' must be 1")]
    [InlineData("""{ "schemaVersion": 1 }""", "'integration' is required")]
    [InlineData("""{ "schemaVersion": 1, "integration": { "name": "../x", "owner": { "teamCode": "t" } } }""", "'integration.name'")]
    [InlineData("""{ "schemaVersion": 1, "integration": { "name": "x", "owner": {} } }""", "'integration.owner.teamCode' is required")]
    [InlineData("""{ "schemaVersion": 1, "integration": { "name": "x", "owner": { "teamCode": "t" } }, "credentialKeys": [{ "name": "a" }, { "name": "A" }] }""", "listed twice")]
    [InlineData("""{ "schemaVersion": 1, "integration": { "name": "x", "owner": { "teamCode": "t" } }, "credentialKeys": [{ "name": "a", "integrationScopes": ["admin"] }] }""", "unknown scope 'admin'")]
    [InlineData("""{ "schemaVersion": 1, "integration": { "name": "x", "owner": { "teamCode": "t" } }, "credentialKeys": [{ "name": "a", "allowedIpRanges": ["nope"] }] }""", "allowedIpRanges")]
    [InlineData("""{ "schemaVersion": 1, "integration": { "name": "x", "owner": { "teamCode": "t" } }, "credentialKeys": [{ "name": "a", "allowedResidencies": ["Mars"] }] }""", "not valid")]
    [InlineData("""{ "schemaVersion": 1, "integration": { "name": "x", "owner": { "teamCode": "t" } }, "credentialKeys": [{ "name": "a", "scopes": [] }] }""", "not valid")]
    // A name whose default owner code would not fit a department's cost centre code (50 characters) needs an owner.
    [InlineData("""{ "schemaVersion": 1, "integration": { "name": "Platform-with-a-name-far-too-long-for-a-department-code" } }""", "'integration.owner' is required for this name")]
    public void A_manifest_that_cannot_be_applied_names_the_problem(string json, string expected) =>
        Should.Throw<ManifestException>(() => IntegrationManifest.Parse(json)).Message.ShouldContain(expected);

    [Theory]
    [InlineData("Ume.Rag", "ume-rag")]
    [InlineData("My__Platform-2", "my-platform-2")]
    [InlineData("A.-_b", "a-b")]
    [InlineData("x-", "x")]
    [InlineData("RAG", "rag")]
    public void The_default_owner_code_is_the_name_in_lower_case_with_one_dash_between_letters_and_digits(string name, string code)
    {
        ManifestOwner.DefaultCode(name).ShouldBe(code);
        var owner = ManifestOwner.Default(name);
        (owner.TeamCode, owner.TeamName, owner.DepartmentCode, owner.DepartmentName).ShouldBe((code, name, code, name));
    }

    [Fact]
    public void A_manifest_without_an_owner_is_valid() =>
        IntegrationManifest.Parse(Ownerless("Ume.Rag")).Integration!.Owner.ShouldBeNull();

    [Fact]
    public async Task The_first_apply_creates_owner_integration_and_keys_with_an_audit_entry_each()
    {
        var name = Unique("Platform");
        var team = Unique("team");
        await using var db = database.Context();

        var result = await database.Provisioner().ApplyAsync(db, IntegrationManifest.Parse(Manifest(name, team)), "manifest:test.json", Ct);

        result.CreatedIntegration.ShouldBeTrue();
        result.Warnings.ShouldBeEmpty();
        result.Keys.Select(k => (k.Name, k.State)).ShouldBe([("api", ProvisionedKeyState.Created), ("admin", ProvisionedKeyState.Created), ("worker", ProvisionedKeyState.Created)]);
        await using var read = database.Context();
        var keys = await read.VirtualKeys.AsNoTracking().Include(k => k.Team!).ThenInclude(t => t.Department).Where(k => k.IntegrationId == result.IntegrationId).ToDictionaryAsync(k => k.Name, Ct);
        keys["admin"].IntegrationScopes.ShouldBe(["organisation.read", "usage.read"]);
        keys["admin"].IntegrationRole.ShouldBe(IntegrationRole.Credential);
        keys["worker"].AllowedResidencies.ShouldBe([DataResidency.OnPrem, DataResidency.Eu], ignoreOrder: true);
        keys["worker"].AllowedIpRanges.ShouldBe(["10.0.0.0/8"]);
        keys.Values.ShouldAllBe(k => k.RequireMaxResidency && k.Team!.Code == team && k.Team.Department!.CostCenterCode == team + "-dep");

        // The secret handed out is the key's, and only its hash and the encrypted secret are stored.
        var api = result.Keys.Single(k => k.Name == "api");
        keys["api"].KeyHash.ShouldBe(database.Hasher.Hash(api.Secret!));
        database.Protector.Unprotect(keys["api"].EncryptedSecret).ShouldBe(api.Secret);

        var audit = await read.AuditLog.AsNoTracking().Where(a => a.Actor == "manifest:test.json" && a.Timestamp > DateTimeOffset.UtcNow.AddMinutes(-5)).ToListAsync(Ct);
        audit.Count(a => a.EntityType == AuditEntities.VirtualKey && keys.Values.Any(k => k.Id.ToString() == a.EntityId)).ShouldBe(3);
        audit.ShouldContain(a => a.EntityType == AuditEntities.Integration && a.EntityId == result.IntegrationId.ToString() && a.Action == AuditActions.Create);
        audit.ShouldContain(a => a.EntityType == AuditEntities.Team && a.EntityId == keys["api"].TeamId.ToString());
        audit.ShouldAllBe(a => a.Details == null || !a.Details.Contains(api.Secret!, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Applying_again_creates_only_what_is_missing_and_reports_changed_settings_without_applying_them()
    {
        var name = Unique("Platform");
        var team = Unique("team");
        await using (var db = database.Context())
        {
            await database.Provisioner().ApplyAsync(db, IntegrationManifest.Parse(Manifest(name, team)), "manifest:a", Ct);
        }

        var changed = Manifest(name, team, """{ "name": "extra" }""").Replace("\"usage.read\", ", "", StringComparison.Ordinal);
        await using var again = database.Context();
        var result = await database.Provisioner().ApplyAsync(again, IntegrationManifest.Parse(changed), "manifest:a", Ct);

        result.CreatedIntegration.ShouldBeFalse();
        result.Keys.Single(k => k.Name == "extra").State.ShouldBe(ProvisionedKeyState.Created);
        result.Keys.Where(k => k.Name != "extra").ShouldAllBe(k => k.State == ProvisionedKeyState.Existing && k.Secret == null);
        result.Warnings.ShouldHaveSingleItem().ShouldContain("'admin'");
        result.Warnings[0].ShouldContain("integrationScopes");
        await using var read = database.Context();
        var admin = await read.VirtualKeys.AsNoTracking().SingleAsync(k => k.IntegrationId == result.IntegrationId && k.Name == "admin", Ct);
        admin.IntegrationScopes.ShouldContain(IntegrationScopes.UsageRead); // not narrowed or widened by the file
    }

    [Fact]
    public async Task A_rotated_key_is_found_by_name_and_a_revoked_one_is_not_created_again()
    {
        var name = Unique("Platform");
        await using (var db = database.Context())
        {
            await database.Provisioner().ApplyAsync(db, IntegrationManifest.Parse(Manifest(name, Unique("team"))), "manifest:a", Ct);
        }

        await using (var db = database.Context())
        {
            var keys = await db.VirtualKeys.Where(k => k.Integration!.Name == name).ToDictionaryAsync(k => k.Name, Ct);
            var replacement = KeyRotation.Rotate(keys["api"], database.Hasher.Generate(), KeyRotationMode.Grace24Hours, DateTimeOffset.UtcNow, "admin");
            db.VirtualKeys.Add(replacement);
            keys["worker"].RevokedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(Ct);
        }

        await using var again = database.Context();
        var result = await database.Provisioner().ApplyAsync(again, IntegrationManifest.Parse(Manifest(name, "ignored")), "manifest:a", Ct);

        var api = result.Keys.Single(k => k.Name == "api");
        api.State.ShouldBe(ProvisionedKeyState.Existing);
        (await again.VirtualKeys.AsNoTracking().SingleAsync(k => k.Id == api.Id, Ct)).RotatedToKeyId.ShouldBeNull(); // the replacement
        result.Keys.Single(k => k.Name == "worker").State.ShouldBe(ProvisionedKeyState.Revoked);
        result.Warnings.ShouldContain(w => w.Contains("'worker'", StringComparison.Ordinal) && w.Contains("revoked", StringComparison.Ordinal));
        result.Warnings.ShouldContain(w => w.Contains("owned by team", StringComparison.Ordinal)); // the owner differs, not changed
        (await again.VirtualKeys.CountAsync(k => k.IntegrationId == result.IntegrationId && k.Name == "worker", Ct)).ShouldBe(1);
    }

    [Fact]
    public async Task An_unknown_owner_without_the_names_to_create_it_saves_nothing()
    {
        var name = Unique("Platform");
        var json = $$"""{ "schemaVersion": 1, "integration": { "name": "{{name}}", "owner": { "teamCode": "{{Unique("none")}}" } }, "credentialKeys": [{ "name": "api" }] }""";
        await using var db = database.Context();

        var error = await Should.ThrowAsync<ManifestException>(() => database.Provisioner().ApplyAsync(db, IntegrationManifest.Parse(json), "manifest:a", Ct));

        error.Message.ShouldContain("teamName");
        await using var read = database.Context();
        (await read.Integrations.AnyAsync(i => i.Name == name, Ct)).ShouldBeFalse();
    }

    [Fact]
    public async Task Without_an_owner_a_new_integration_gets_a_team_and_department_made_from_its_name_and_the_result_says_so()
    {
        var name = Unique("Ume.Rag");
        var code = "ume-rag-" + name[^8..];
        await using var db = database.Context();

        var result = await database.Provisioner().ApplyAsync(db, IntegrationManifest.Parse(Ownerless(name)), "manifest:ownerless.json", Ct);

        result.CreatedIntegration.ShouldBeTrue();
        (result.DefaultOwner, result.CreatedTeam, result.CreatedDepartment).ShouldBe((true, true, true));
        var note = result.Warnings.ShouldHaveSingleItem();
        note.ShouldContain($"'{name}' names no owner in the manifest");
        note.ShouldContain($"the team '{name}' (code '{code}') in the department '{name}' (code '{code}'), both created for it");
        note.ShouldContain("A gateway admin can move it to the organisation's own team under Integrations");
        await using var read = database.Context();
        var integration = await read.Integrations.AsNoTracking().Include(i => i.OwnerTeam!).ThenInclude(t => t.Department).SingleAsync(i => i.Id == result.IntegrationId, Ct);
        (integration.OwnerTeam!.Code, integration.OwnerTeam.Name).ShouldBe((code, name));
        (integration.OwnerTeam.Department!.CostCenterCode, integration.OwnerTeam.Department.Name).ShouldBe((code, name));
        (await read.VirtualKeys.AsNoTracking().SingleAsync(k => k.IntegrationId == integration.Id, Ct)).TeamId.ShouldBe(integration.OwnerTeamId);
        var audit = await read.AuditLog.AsNoTracking().Where(a => a.Actor == "manifest:ownerless.json" && a.Action == AuditActions.Create).ToListAsync(Ct);
        audit.ShouldContain(a => a.EntityType == AuditEntities.Team && a.EntityId == integration.OwnerTeamId.ToString());
        audit.ShouldContain(a => a.EntityType == AuditEntities.Department && a.EntityId == integration.OwnerTeam.DepartmentId.ToString());

        // Applied again, and after an admin moved it: no owner to compare, no warning, nothing changed.
        await using (var again = database.Context())
        {
            var second = await database.Provisioner().ApplyAsync(again, IntegrationManifest.Parse(Ownerless(name)), "manifest:ownerless.json", Ct);
            (second.CreatedIntegration, second.DefaultOwner).ShouldBe((false, false));
            second.Warnings.ShouldBeEmpty();
        }

        var elsewhere = new Team { Name = "Elsewhere", Code = Unique("elsewhere"), DepartmentId = integration.OwnerTeam.DepartmentId, CreatedAt = DateTimeOffset.UtcNow };
        await using (var move = database.Context())
        {
            move.Teams.Add(elsewhere);
            (await move.Integrations.SingleAsync(i => i.Id == integration.Id, Ct)).OwnerTeamId = elsewhere.Id;
            await move.SaveChangesAsync(Ct);
        }

        await using var third = database.Context();
        var moved = await database.Provisioner().ApplyAsync(third, IntegrationManifest.Parse(Ownerless(name)), "manifest:ownerless.json", Ct);
        moved.Warnings.ShouldBeEmpty();
        moved.Keys.ShouldHaveSingleItem().State.ShouldBe(ProvisionedKeyState.Existing);
        (await third.Integrations.AsNoTracking().SingleAsync(i => i.Id == integration.Id, Ct)).OwnerTeamId.ShouldBe(elsewhere.Id);
    }

    [Fact]
    public async Task Without_an_owner_an_existing_team_with_the_default_code_is_used()
    {
        var name = Unique("Platform");
        var code = ManifestOwner.DefaultCode(name);
        Guid teamId;
        await using (var setup = database.Context())
        {
            var department = new Department { Name = "IT", CostCenterCode = Unique("it"), CreatedAt = DateTimeOffset.UtcNow };
            var team = new Team { Name = "Our platform team", Code = code, Department = department, CreatedAt = DateTimeOffset.UtcNow };
            setup.Teams.Add(team);
            await setup.SaveChangesAsync(Ct);
            teamId = team.Id;
        }

        await using var db = database.Context();
        var result = await database.Provisioner().ApplyAsync(db, IntegrationManifest.Parse(Ownerless(name)), "manifest:a", Ct);

        (result.DefaultOwner, result.CreatedTeam, result.CreatedDepartment).ShouldBe((true, false, false));
        result.Warnings.ShouldHaveSingleItem().ShouldContain($"it is placed in the existing team 'Our platform team' (code '{code}')");
        await using var read = database.Context();
        (await read.Integrations.AsNoTracking().SingleAsync(i => i.Id == result.IntegrationId, Ct)).OwnerTeamId.ShouldBe(teamId);
        (await read.Departments.AnyAsync(d => d.CostCenterCode == code, Ct)).ShouldBeFalse();
    }

    [Fact]
    public async Task A_preview_without_an_owner_says_so_and_saves_nothing()
    {
        var name = Unique("Platform");
        var code = ManifestOwner.DefaultCode(name);
        await using var db = database.Context();

        var result = await database.Provisioner().ApplyAsync(db, IntegrationManifest.Parse(Ownerless(name)), "manifest:a", Ct, preview: true);

        (result.IntegrationId, result.DefaultOwner, result.CreatedTeam, result.CreatedDepartment).ShouldBe((null, true, true, true));
        result.Warnings.ShouldHaveSingleItem().ShouldContain("names no owner");
        result.Keys.ShouldHaveSingleItem().Secret.ShouldBeNull();
        await using var read = database.Context();
        (await read.Integrations.AnyAsync(i => i.Name == name, Ct)).ShouldBeFalse();
        (await read.Teams.AnyAsync(t => t.Code == code, Ct)).ShouldBeFalse();
        (await read.Departments.AnyAsync(d => d.CostCenterCode == code, Ct)).ShouldBeFalse();
        (await read.AuditLog.AnyAsync(a => a.Actor == "manifest:a" && a.Details != null && a.Details.Contains(code), Ct)).ShouldBeFalse();
    }

    [Fact]
    public async Task The_migration_service_writes_each_secret_once_and_writes_a_missing_file_again_from_the_stored_secret()
    {
        var directory = Directory.CreateTempSubdirectory("ume-manifest-");
        try
        {
            var name = Unique("Platform");
            var manifests = Directory.CreateDirectory(Path.Combine(directory.FullName, "manifests"));
            await File.WriteAllTextAsync(Path.Combine(manifests.FullName, "platform.json"), Manifest(name, Unique("team")), Ct);
            var secrets = Path.Combine(directory.FullName, "secrets");
            var service = new IntegrationManifests(
                Options.Create(new ProvisioningOptions { Manifests = manifests.FullName, SecretsPath = secrets }),
                database.Provisioner(), database.Protector, TimeProvider.System, NullLogger<IntegrationManifests>.Instance);

            await using (var db = database.Context())
            {
                await service.ApplyAsync(db, Ct);
            }

            var file = Path.Combine(secrets, name, "api");
            var secret = await File.ReadAllTextAsync(file, Ct);
            VirtualKeyHasher.LooksLikeKey(secret).ShouldBeTrue();
            Directory.GetFiles(Path.Combine(secrets, name)).Select(Path.GetFileName).ShouldBe(["admin", "api", "worker"], ignoreOrder: true);
            if (!OperatingSystem.IsWindows())
            {
                File.GetUnixFileMode(file).ShouldBe(UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            // An existing file is left alone; a deleted one comes back with the same secret, audited as a reveal.
            await File.WriteAllTextAsync(Path.Combine(secrets, name, "admin"), "kept", Ct);
            File.Delete(file);
            await using (var db = database.Context())
            {
                await service.ApplyAsync(db, Ct);
            }

            (await File.ReadAllTextAsync(file, Ct)).ShouldBe(secret);
            (await File.ReadAllTextAsync(Path.Combine(secrets, name, "admin"), Ct)).ShouldBe("kept");
            await using var read = database.Context();
            var apiId = (await read.VirtualKeys.AsNoTracking().SingleAsync(k => k.Integration!.Name == name && k.Name == "api", Ct)).Id.ToString();
            (await read.AuditLog.AsNoTracking().CountAsync(a => a.EntityId == apiId && a.Action == AuditActions.Reveal && a.Actor == "manifest:platform.json", Ct)).ShouldBe(1);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void Secrets_need_a_place_when_manifests_are_configured()
    {
        var options = new ProvisioningOptions { Manifests = "/manifests" };
        options.Validate(new System.ComponentModel.DataAnnotations.ValidationContext(options)).ShouldHaveSingleItem()
            .MemberNames.ShouldBe([nameof(ProvisioningOptions.SecretsPath)]);
    }
}
