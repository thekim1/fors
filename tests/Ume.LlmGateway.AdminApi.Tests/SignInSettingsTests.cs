using System.Net;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Ume.LlmGateway.AdminApi.Security;
using Ume.LlmGateway.AdminApi.Settings;
using Ume.LlmGateway.Infrastructure.Auditing;
using Ume.LlmGateway.Infrastructure.Persistence;
using Ume.LlmGateway.Infrastructure.Stores;
using Ume.LlmGateway.TestKit;

namespace Ume.LlmGateway.AdminApi.Tests;

/// <summary>
/// The admin console's sign-in values (thekim1/llmgateway#100): from the deployment, else the database, else the code's
/// default, each with its source; the stored client secret encrypted with Data Protection and never in a response, the audit
/// log or a log line. The fixture's deployment sets <c>Oidc:Authority</c> and no client secret.
/// </summary>
public sealed class SignInSettingsTests(AdminFixture fixture)
{
    private const string StoredSecret = "stored-secret-5f1c9a";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static IConfiguration Configuration(params (string Key, string? Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value))).Build();

    private static EffectiveSignIn Resolve(IConfiguration configuration, SignInWizardValues stored, Func<StoredSecretValue>? secret = null) =>
        SignInSettingsService.Resolve(configuration, stored, secret ?? (() => StoredSecretValue.NotSet), null, null);


    [Fact]
    public void A_deployment_value_wins_over_the_stored_one_and_a_blank_one_counts_as_unset()
    {
        var stored = new SignInWizardValues
        {
            Authority = "https://stored.example/realms/ume", ClientId = "stored-client", RoleClaim = "stored-roles", TeamClaim = "stored-teams", ExtraScopes = ["groups"],
        };
        var effective = Resolve(Configuration(
            ("Oidc:Authority", "https://deploy.example/realms/ume"), ("Oidc:ClientId", "  "), ("Oidc:RoleClaim", ""), ("Oidc:TeamClaim", "deploy-teams"),
            ("Oidc:ExtraScopes:0", " ")), stored);

        effective.Authority.ShouldBe(new SignInValue<string?>("https://deploy.example/realms/ume", SignInValueSources.Deployment));
        effective.TeamClaim.ShouldBe(new SignInValue<string>("deploy-teams", SignInValueSources.Deployment));
        // Blank deployment values (Compose passes an unset variable as "") are unset, so the stored ones apply.
        effective.ClientId.ShouldBe(new SignInValue<string>("stored-client", SignInValueSources.Wizard));
        effective.RoleClaim.ShouldBe(new SignInValue<string>("stored-roles", SignInValueSources.Wizard));
        effective.ExtraScopes.Value.ShouldBe(["groups"]);
        effective.ExtraScopes.Source.ShouldBe(SignInValueSources.Wizard);

        var scopes = Resolve(Configuration(("Oidc:ExtraScopes:0", "groups"), ("Oidc:ExtraScopes:1", "offline_access")), stored).ExtraScopes;
        scopes.Value.ShouldBe(["groups", "offline_access"]);
        scopes.Source.ShouldBe(SignInValueSources.Deployment);
    }

    [Fact]
    public void The_code_default_applies_when_neither_sets_a_value()
    {
        var defaults = new OidcOptions();
        var effective = Resolve(Configuration(("Oidc:Authority", ""), ("Oidc:ClientSecret", " ")), new SignInWizardValues { ClientId = " ", ExtraScopes = [] });

        effective.Authority.ShouldBe(new SignInValue<string?>(null, SignInValueSources.Default));
        effective.ClientId.ShouldBe(new SignInValue<string>(defaults.ClientId, SignInValueSources.Default));
        effective.RoleClaim.ShouldBe(new SignInValue<string>(defaults.RoleClaim, SignInValueSources.Default));
        effective.DepartmentClaim.ShouldBe(new SignInValue<string>(defaults.DepartmentClaim, SignInValueSources.Default));
        effective.TeamClaim.ShouldBe(new SignInValue<string>(defaults.TeamClaim, SignInValueSources.Default));
        effective.GroupClaim.ShouldBe(new SignInValue<string>(defaults.GroupClaim, SignInValueSources.Default));
        effective.ExtraScopes.Value.ShouldBeEmpty();
        effective.ExtraScopes.Source.ShouldBe(SignInValueSources.Default);
        (effective.ClientSecret.Source, effective.ClientSecret.IsSet, effective.ClientSecret.Value).ShouldBe((SignInValueSources.Default, false, null));
    }

    [Fact]
    public void The_client_secret_follows_the_same_rule_and_a_deployment_secret_never_touches_the_key_ring()
    {
        var deployed = Resolve(Configuration(("Oidc:ClientSecret", "deploy-secret")), new SignInWizardValues(),
            () => throw new InvalidOperationException("The stored secret is not read when the deployment sets one."));
        (deployed.ClientSecret.Source, deployed.ClientSecret.Value, deployed.ClientSecret.IsSet).ShouldBe((SignInValueSources.Deployment, "deploy-secret", true));
        deployed.ClientSecret.ToString().ShouldNotContain("deploy-secret");
        deployed.ToString().ShouldNotContain("deploy-secret");

        var stored = Resolve(Configuration(("Oidc:ClientSecret", "")), new SignInWizardValues(), () => StoredSecretValue.Readable(StoredSecret));
        (stored.ClientSecret.Source, stored.ClientSecret.Value, stored.ClientSecret.Unreadable).ShouldBe((SignInValueSources.Wizard, StoredSecret, false));
        stored.ToString().ShouldNotContain(StoredSecret);

        var unreadable = Resolve(Configuration(), new SignInWizardValues(), () => StoredSecretValue.Unreadable);
        (unreadable.ClientSecret.Source, unreadable.ClientSecret.Value, unreadable.ClientSecret.IsSet, unreadable.ClientSecret.Unreadable)
            .ShouldBe((SignInValueSources.Wizard, null, true, true));
    }

    [Fact]
    public void A_secret_round_trips_as_ciphertext_and_one_the_key_ring_cannot_read_is_unreadable_without_an_exception()
    {
        var provider = new EphemeralDataProtectionProvider();
        var logger = new ListLogger();
        var protector = new SignInSecretProtector(provider, logger);

        var ciphertext = protector.Protect(StoredSecret);
        ciphertext.ShouldNotContain(StoredSecret);
        var read = protector.Unprotect(ciphertext);
        (read.State, read.Value).ShouldBe((StoredSecretState.Readable, StoredSecret));
        read.ToString().ShouldNotContain(StoredSecret);
        protector.Unprotect(null).State.ShouldBe(StoredSecretState.NotSet);

        // Another key ring (a replaced certificate, a lost key), a damaged value, and a payload of another purpose.
        var other = new SignInSecretProtector(new EphemeralDataProtectionProvider(), new ListLogger());
        var otherPurpose = provider.CreateProtector("Ume.LlmGateway.ProviderCredential.v1").Protect(StoredSecret);
        foreach (var bad in new[] { other.Protect(StoredSecret), "not-a-payload", otherPurpose })
        {
            var result = protector.Unprotect(bad);
            (result.State, result.Value).ShouldBe((StoredSecretState.Unreadable, null));
        }

        logger.Messages.Count.ShouldBe(3);
        logger.Messages.ShouldAllBe(m => m.Level == LogLevel.Warning && !m.Text.Contains(StoredSecret) && !m.HadException);
    }

    [Fact]
    public async Task Saving_stores_ciphertext_publishes_the_change_and_audits_without_the_secret()
    {
        var changes = new List<InvalidationKind>();
        using var subscription = fixture.Services.GetRequiredService<IInvalidationBus>().Subscribe(kind => { lock (changes) { changes.Add(kind); } });
        try
        {
            var saved = await SaveAsync(fixture.Services, new SignInWizardValues
            {
                Authority = " https://stored.example/realms/ume ", ClientId = "stored-client", ExtraScopes = ["groups", " "], ClientSecretExpiresOn = new DateOnly(2027, 6, 30),
            }, StoredSecret);

            // The deployment's authority wins; the stored client id and secret apply, the latter unprotected for server-side callers.
            saved.Authority.ShouldBe(new SignInValue<string?>("https://identity.invalid", SignInValueSources.Deployment));
            saved.ClientId.ShouldBe(new SignInValue<string>("stored-client", SignInValueSources.Wizard));
            saved.ExtraScopes.Value.ShouldBe(["groups"]);
            (saved.ClientSecret.Source, saved.ClientSecret.Value, saved.ClientSecret.Unreadable).ShouldBe((SignInValueSources.Wizard, StoredSecret, false));
            saved.ClientSecret.SetAt.ShouldNotBeNull();
            saved.ClientSecretExpiresOn.ShouldBe(new DateOnly(2027, 6, 30));
            // The gateways hand the authority out on /v1/ume/context, so they reload it with the catalogue.
            lock (changes) { changes.ShouldContain(InvalidationKind.Config); }

            using var scope = fixture.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
            var row = await db.SignInSettings.AsNoTracking().SingleAsync(Ct);
            row.Authority.ShouldBe("https://stored.example/realms/ume");
            row.EncryptedClientSecret.ShouldNotBeNullOrEmpty();
            row.EncryptedClientSecret.ShouldNotContain(StoredSecret);

            var audit = await db.AuditLog.AsNoTracking()
                .Where(a => a.EntityType == AuditEntities.Settings && a.EntityId == SignInSettingsService.AuditEntityId)
                .OrderByDescending(a => a.Id).FirstAsync(Ct);
            audit.Action.ShouldBe(AuditActions.Update);
            audit.Actor.ShouldBe("setup-person");
            audit.Details.ShouldNotBeNull();
            audit.Details.ShouldContain("stored-client");
            audit.Details.ShouldNotContain(StoredSecret);
            audit.Details.ShouldNotContain(row.EncryptedClientSecret!);
            using (var details = JsonDocument.Parse(audit.Details))
            {
                var secret = details.RootElement.GetProperty("after").GetProperty("clientSecret");
                secret.GetProperty("set").GetBoolean().ShouldBeTrue();
                secret.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ShouldBe(["expiresOn", "set", "setAt"]);
            }

            // Saving again without a secret keeps it; an empty one removes it.
            var kept = await SaveAsync(fixture.Services, new SignInWizardValues { ClientId = "stored-client-2" }, null);
            (kept.ClientSecret.Value, kept.ClientId.Value).ShouldBe((StoredSecret, "stored-client-2"));
            var removed = await SaveAsync(fixture.Services, new SignInWizardValues(), "");
            (removed.ClientSecret.Source, removed.ClientSecret.IsSet).ShouldBe((SignInValueSources.Default, false));
            removed.ClientId.Source.ShouldBe(SignInValueSources.Default);
        }
        finally
        {
            await ClearAsync(fixture.Services);
        }
    }

    [Fact]
    public async Task The_endpoint_answers_values_with_their_sources_and_never_the_secret()
    {
        try
        {
            await SaveAsync(fixture.Services, new SignInWizardValues { ClientId = "stored-client", GroupClaim = "stored-groups", ClientSecretExpiresOn = new DateOnly(2027, 1, 31) },
                StoredSecret);
            using var admin = await fixture.ClientAsync();
            using var response = await admin.GetAsync("/api/settings/sign-in", Ct);
            var text = await response.Content.ReadAsStringAsync(Ct);
            response.StatusCode.ShouldBe(HttpStatusCode.OK, text);
            text.ShouldNotContain(StoredSecret);
            using (var scope = fixture.Services.CreateScope())
            {
                var ciphertext = await scope.ServiceProvider.GetRequiredService<GatewayDbContext>().SignInSettings.AsNoTracking()
                    .Select(s => s.EncryptedClientSecret).SingleAsync(Ct);
                text.ShouldNotContain(ciphertext!);
            }

            var json = JsonDocument.Parse(text).RootElement;
            Value(json, "authority").ShouldBe(("https://identity.invalid", SignInValueSources.Deployment));
            Value(json, "clientId").ShouldBe(("stored-client", SignInValueSources.Wizard));
            Value(json, "groupClaim").ShouldBe(("stored-groups", SignInValueSources.Wizard));
            Value(json, "roleClaim").ShouldBe((new OidcOptions().RoleClaim, SignInValueSources.Default));
            json.GetProperty("extraScopes").GetProperty("source").GetString().ShouldBe(SignInValueSources.Default);
            json.GetProperty("updatedAt").ValueKind.ShouldBe(JsonValueKind.String);
            var secret = json.GetProperty("clientSecret");
            secret.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ShouldBe(["expiresOn", "locked", "set", "setAt", "source", "unreadable"]);
            secret.GetProperty("locked").GetBoolean().ShouldBeFalse();
            json.GetProperty("authority").GetProperty("locked").GetBoolean().ShouldBeTrue();
            json.GetProperty("clientId").GetProperty("locked").GetBoolean().ShouldBeFalse();
            secret.GetProperty("set").GetBoolean().ShouldBeTrue();
            secret.GetProperty("source").GetString().ShouldBe(SignInValueSources.Wizard);
            secret.GetProperty("expiresOn").GetString().ShouldBe("2027-01-31");
            secret.GetProperty("unreadable").GetBoolean().ShouldBeFalse();

            // A deployment secret: answered as set by the deployment, never shown, and the stored one's expiry does not apply.
            await using var deployed = fixture.Derive(web => web.UseSetting("Oidc:ClientSecret", "deploy-secret-7d2e"));
            using var deployedAdmin = deployed.CreateHttpsClient();
            deployedAdmin.DefaultRequestHeaders.Add(HeaderAuthenticationHandler.RoleHeader, AdminRoles.GatewayAdmin);
            var deployedText = await deployedAdmin.GetStringAsync("/api/settings/sign-in", Ct);
            deployedText.ShouldNotContain("deploy-secret-7d2e");
            deployedText.ShouldNotContain(StoredSecret);
            var deployedSecret = JsonDocument.Parse(deployedText).RootElement.GetProperty("clientSecret");
            (deployedSecret.GetProperty("source").GetString(), deployedSecret.GetProperty("set").GetBoolean(), deployedSecret.GetProperty("locked").GetBoolean())
                .ShouldBe((SignInValueSources.Deployment, true, true));
            deployedSecret.GetProperty("expiresOn").ValueKind.ShouldBe(JsonValueKind.Null);
            deployedSecret.GetProperty("setAt").ValueKind.ShouldBe(JsonValueKind.Null);
        }
        finally
        {
            await ClearAsync(fixture.Services);
        }
    }

    [Fact]
    public async Task A_ciphertext_the_key_ring_cannot_read_is_reported_unreadable_not_a_failure()
    {
        try
        {
            await SaveAsync(fixture.Services, new SignInWizardValues(), StoredSecret);
            await ReplaceCiphertextAsync(fixture.Services);

            using var admin = await fixture.ClientAsync();
            using var response = await admin.GetAsync("/api/settings/sign-in", Ct);
            var text = await response.Content.ReadAsStringAsync(Ct);
            response.StatusCode.ShouldBe(HttpStatusCode.OK, text);
            var secret = JsonDocument.Parse(text).RootElement.GetProperty("clientSecret");
            (secret.GetProperty("set").GetBoolean(), secret.GetProperty("source").GetString(), secret.GetProperty("unreadable").GetBoolean())
                .ShouldBe((true, SignInValueSources.Wizard, true));

            using var scope = fixture.Services.CreateScope();
            var effective = await scope.ServiceProvider.GetRequiredService<SignInSettingsService>().ReadAsync(Ct);
            (effective.ClientSecret.Unreadable, effective.ClientSecret.Value).ShouldBe((true, null));
        }
        finally
        {
            await ClearAsync(fixture.Services);
        }
    }

    [Theory]
    [InlineData(null, 401)]
    [InlineData("viewer", 403)]
    [InlineData("department-admin", 403)]
    [InlineData("team-admin", 403)]
    public async Task Only_gateway_admins_read_the_sign_in_settings(string? role, int status)
    {
        using var client = await fixture.ClientAsync(role);
        using var response = await client.GetAsync("/api/settings/sign-in", Ct);
        ((int)response.StatusCode).ShouldBe(status);
    }

    private static (string?, string?) Value(JsonElement json, string name) =>
        (json.GetProperty(name).GetProperty("value").GetString(), json.GetProperty(name).GetProperty("source").GetString());

    /// <summary>The service's write as setup mode will call it, in a request of a gateway admin (<c>setup-person</c>).</summary>
    internal static async Task<EffectiveSignIn> SaveAsync(IServiceProvider services, SignInWizardValues values, string? clientSecret)
    {
        await using var scope = services.CreateAsyncScope();
        var accessor = scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>();
        accessor.HttpContext = new DefaultHttpContext
        {
            RequestServices = scope.ServiceProvider,
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(AdminClaims.Subject, "setup-person"), new Claim(AdminClaims.Roles, AdminRoles.GatewayAdmin)], "test")),
        };
        try
        {
            return await scope.ServiceProvider.GetRequiredService<SignInSettingsService>().SaveWizardValuesAsync(values, clientSecret, null, null, Ct);
        }
        finally
        {
            accessor.HttpContext = null;
        }
    }

    /// <summary>Removes every stored value and the secret, so the next test starts from the deployment's values alone.</summary>
    internal static Task<EffectiveSignIn> ClearAsync(IServiceProvider services) => SaveAsync(services, new SignInWizardValues(), "");

    /// <summary>Replaces the stored ciphertext with one of another key ring: what a replaced certificate without the old one leaves.</summary>
    internal static async Task ReplaceCiphertextAsync(IServiceProvider services)
    {
        var foreign = new EphemeralDataProtectionProvider().CreateProtector(SignInSecretProtector.Purpose).Protect(StoredSecret);
        using var scope = services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<GatewayDbContext>().SignInSettings
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.EncryptedClientSecret, foreign), Ct);
    }

    private sealed class ListLogger : ILogger<SignInSecretProtector>
    {
        public List<(LogLevel Level, string Text, bool HadException)> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Messages.Add((logLevel, formatter(state, exception), exception is not null));
    }
}
