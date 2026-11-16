using System.Net;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Ume.LlmGateway.AdminApi.Security;
using Ume.LlmGateway.AdminApi.Settings;
using Ume.LlmGateway.Infrastructure.Security;
using Ume.LlmGateway.TestKit;

namespace Ume.LlmGateway.AdminApi.Tests;

/// <summary>
/// The admin API's OpenID Connect options come from the effective sign-in values and are rebuilt at the next sign-in when they
/// change, in the database or in the deployment (thekim1/llmgateway#100); it starts without an authority and refuses a sign-in
/// then. The placeholder provider's metadata follows the authority (<see cref="AdminFixture.StaticConfiguration"/>), so a
/// challenge's address names the authority it was built with.
/// </summary>
public sealed class SignInReloadTests(AdminFixture fixture)
{
    private const string Deployed = "https://deployed.invalid/realms/ume";
    private const string StoredA = "https://stored-a.invalid/realms/ume";
    private const string StoredB = "https://stored-b.invalid/realms/ume";
    private const string StoredSecret = "stored-secret-0c4d7e";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The hosts' clock: a sign-in reads the values at most once per <see cref="AdminSignIn.SignInRefreshInterval"/>, so <see cref="ChallengeAsync"/> moves it on.</summary>
    private readonly FakeTimeProvider _time = new(DateTimeOffset.UtcNow);

    /// <summary>An admin API instance on the fixture's database with no deployment authority or secret, plus <paramref name="settings"/>.</summary>
    private WebApplicationFactory<Program> Host(CapturingLoggerProvider? logs = null, params (string Key, string Value)[] settings) =>
        fixture.Derive(web =>
        {
            web.UseSetting("Oidc:Authority", "");
            web.UseSetting("Oidc:ClientSecret", "");
            foreach (var (key, value) in settings)
            {
                web.UseSetting(key, value);
            }

            web.ConfigureTestServices(services => services.AddSingleton<TimeProvider>(_time));
            if (logs is not null)
            {
                web.ConfigureTestServices(services => services.AddLogging(b => b.AddProvider(logs).SetMinimumLevel(LogLevel.Trace)));
            }
        });

    /// <summary>Starts a sign-in at <c>/bff/login</c>, later than the values last read stay in use; returns where the browser is sent.</summary>
    private async Task<Uri> ChallengeAsync(WebApplicationFactory<Program> factory, bool atOnce = false)
    {
        if (!atOnce)
        {
            _time.Advance(AdminSignIn.SignInRefreshInterval);
        }

        using var client = factory.CreateHttpsClient();
        using var response = await client.GetAsync("/bff/login?returnUrl=/keys", Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Redirect, await response.Content.ReadAsStringAsync(Ct));
        return response.Headers.Location.ShouldNotBeNull();
    }

    private static void ShouldGoTo(Uri location, string authority, string clientId)
    {
        location.GetLeftPart(UriPartial.Path).ShouldBe(authority + "/authorize");
        QueryHelpers.ParseQuery(location.Query)["client_id"].ToString().ShouldBe(clientId);
    }

    private static async Task<bool> ConfiguredAsync(WebApplicationFactory<Program> factory)
    {
        using var client = factory.CreateHttpsClient();
        using var json = JsonDocument.Parse(await client.GetStringAsync("/bff/user", Ct));
        return json.RootElement.GetProperty("signIn").GetProperty("configured").GetBoolean();
    }

    private static OpenIdConnectOptions LiveOptions(WebApplicationFactory<Program> factory) =>
        factory.Services.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>().Get(OpenIdConnectDefaults.AuthenticationScheme);

    private static string DefaultClientId => new OidcOptions().ClientId;

    [Fact]
    public async Task The_challenge_goes_to_the_stored_authority_without_a_deployment_one_and_to_the_deployments_when_both_are_set()
    {
        await using var storedOnly = Host();
        await using var both = Host(settings: ("Oidc:Authority", Deployed));
        try
        {
            await SignInSettingsTests.SaveAsync(fixture.Services, new SignInWizardValues { Authority = StoredA, ClientId = "stored-client", ExtraScopes = ["groups"] }, StoredSecret);

            ShouldGoTo(await ChallengeAsync(storedOnly), StoredA, "stored-client");
            LiveOptions(storedOnly).ClientSecret.ShouldBe(StoredSecret);
            LiveOptions(storedOnly).Scope.ShouldContain("groups");
            // The deployment's authority wins; the client id it leaves unset is the stored one.
            ShouldGoTo(await ChallengeAsync(both), Deployed, "stored-client");
        }
        finally
        {
            await SignInSettingsTests.ClearAsync(fixture.Services);
        }
    }

    [Fact]
    public async Task A_saved_change_applies_to_the_next_sign_in_on_both_of_two_instances_without_a_restart()
    {
        await using var first = Host();
        await using var second = Host();
        try
        {
            await SignInSettingsTests.SaveAsync(first.Services, new SignInWizardValues { Authority = StoredA, ClientId = "client-a" }, null);
            ShouldGoTo(await ChallengeAsync(first), StoredA, "client-a");
            ShouldGoTo(await ChallengeAsync(second), StoredA, "client-a");
            var built = LiveOptions(first);

            // Unchanged values: the options are not rebuilt.
            await ChallengeAsync(first);
            LiveOptions(first).ShouldBeSameAs(built);

            // Saved through the first instance; the second has nothing in memory to tell it, and follows at its next sign-in.
            await SignInSettingsTests.SaveAsync(first.Services, new SignInWizardValues { Authority = StoredB, ClientId = "client-b" }, null);
            ShouldGoTo(await ChallengeAsync(second), StoredB, "client-b");
            ShouldGoTo(await ChallengeAsync(first), StoredB, "client-b");
            LiveOptions(first).ShouldNotBeSameAs(built);
        }
        finally
        {
            await SignInSettingsTests.ClearAsync(fixture.Services);
        }
    }

    [Fact]
    public async Task A_changed_deployment_value_applies_to_the_next_sign_in()
    {
        using var logs = new CapturingLoggerProvider();
        await using var factory = Host(logs, ("Oidc:Authority", Deployed), ("Oidc:ClientSecret", "deploy-secret-1"));
        ShouldGoTo(await ChallengeAsync(factory), Deployed, DefaultClientId);
        LiveOptions(factory).ClientSecret.ShouldBe("deploy-secret-1");

        // As a changed secret file or variable: the configuration is read at each sign-in.
        var configuration = factory.Services.GetRequiredService<IConfiguration>();
        configuration["Oidc:Authority"] = "https://reloaded.invalid/realms/ume";
        configuration["Oidc:ClientId"] = "reloaded-client";
        ShouldGoTo(await ChallengeAsync(factory), "https://reloaded.invalid/realms/ume", "reloaded-client");

        // A changed secret alone rebuilds the options too.
        var built = LiveOptions(factory);
        configuration["Oidc:ClientSecret"] = "deploy-secret-2";
        await ChallengeAsync(factory);
        LiveOptions(factory).ShouldNotBeSameAs(built);
        LiveOptions(factory).ClientSecret.ShouldBe("deploy-secret-2");

        logs.Logged(typeof(AdminSignIn).FullName!, "Sign-in options rebuilt").ShouldBeTrue(logs.Text);
        logs.Text.ShouldNotContain("deploy-secret-1");
        logs.Text.ShouldNotContain("deploy-secret-2");
    }

    [Fact]
    public async Task Without_an_authority_the_host_starts_reports_it_and_refuses_a_sign_in_without_a_500()
    {
        using var logs = new CapturingLoggerProvider();
        await using var factory = Host(logs);

        (await ConfiguredAsync(factory)).ShouldBeFalse();
        using var client = factory.CreateHttpsClient();
        using (var login = await client.GetAsync("/bff/login", Ct))
        {
            var text = await login.Content.ReadAsStringAsync(Ct);
            login.StatusCode.ShouldBe(HttpStatusCode.Conflict, text);
            JsonDocument.Parse(text).RootElement.GetProperty("code").GetString().ShouldBe(AdminAuthentication.SignInNotConfiguredCode);
            login.Headers.Location.ShouldBeNull();
        }

        logs.Logged(typeof(AdminAuthentication).FullName!, SignInNotConfiguredReasons.NoAuthority).ShouldBeTrue(logs.Text);
        (await client.GetAsync("/health/live", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await client.GetAsync("/api/settings/sign-in", Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        // A stored authority makes it configured at the next sign-in, without a restart.
        try
        {
            await SignInSettingsTests.SaveAsync(factory.Services, new SignInWizardValues { Authority = StoredA }, null);
            ShouldGoTo(await ChallengeAsync(factory), StoredA, DefaultClientId);
            (await ConfiguredAsync(factory)).ShouldBeTrue();
        }
        finally
        {
            await SignInSettingsTests.ClearAsync(fixture.Services);
        }
    }

    [Fact]
    public async Task The_sign_in_paths_read_the_values_at_most_every_two_seconds_and_requests_arriving_together_read_once()
    {
        await using var factory = Host();
        try
        {
            await SignInSettingsTests.SaveAsync(factory.Services, new SignInWizardValues { Authority = StoredA, ClientId = "client-a" }, null);
            ShouldGoTo(await ChallengeAsync(factory), StoredA, "client-a");

            // Anyone may call the sign-in start, the callback and the sign-out: they go on with the values last read for two seconds.
            await SignInSettingsTests.SaveAsync(factory.Services, new SignInWizardValues { Authority = StoredB, ClientId = "client-b" }, null);
            ShouldGoTo(await ChallengeAsync(factory, atOnce: true), StoredA, "client-a");
            var signIn = factory.Services.GetRequiredService<AdminSignIn>();
            var options = LiveOptions(factory);
            signIn.Refreshes(AdminAuthentication.LoginPath, options).ShouldBeFalse();
            signIn.Refreshes(options.CallbackPath, options).ShouldBeFalse();
            signIn.Refreshes(AdminAuthentication.LogoutPath, options).ShouldBeFalse();
            using (var client = factory.CreateHttpsClient())
            {
                using var callback = await client.GetAsync(options.CallbackPath + "?state=made-up&code=x", Ct);
                signIn.Current.Values.Authority.Value.ShouldBe(StoredA);
            }

            _time.Advance(AdminSignIn.SignInRefreshInterval);
            signIn.Refreshes(options.CallbackPath, options).ShouldBeTrue();

            // Requests that waited for another one's read take that read.
            using var scope = factory.Services.CreateScope();
            var settings = scope.ServiceProvider.GetRequiredService<SignInSettingsService>();
            var monitor = factory.Services.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>();
            var cache = factory.Services.GetRequiredService<IOptionsMonitorCache<OpenIdConnectOptions>>();
            var first = await signIn.RefreshAsync(settings, monitor, cache, Ct, AdminSignIn.SignInRefreshInterval);
            first.Values.Authority.Value.ShouldBe(StoredB);
            await SignInSettingsTests.SaveAsync(factory.Services, new SignInWizardValues { Authority = StoredA, ClientId = "client-a" }, null);
            (await signIn.RefreshAsync(settings, monitor, cache, Ct, AdminSignIn.SignInRefreshInterval)).ShouldBeSameAs(first);
            // Without the interval (completing setup) the values are read at once.
            (await signIn.RefreshAsync(settings, monitor, cache, Ct)).Values.Authority.Value.ShouldBe(StoredA);
        }
        finally
        {
            await SignInSettingsTests.ClearAsync(fixture.Services);
        }
    }

    [Fact]
    public async Task The_user_document_reads_the_values_again_at_most_every_ten_seconds()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var factory = fixture.Derive(web =>
        {
            web.UseSetting("Oidc:Authority", "");
            web.ConfigureTestServices(services => services.AddSingleton<TimeProvider>(time));
        });
        try
        {
            (await ConfiguredAsync(factory)).ShouldBeFalse();
            await SignInSettingsTests.SaveAsync(fixture.Services, new SignInWizardValues { Authority = StoredA }, null);
            // Anyone may call /bff/user, so it answers from the values last read until they are ten seconds old.
            (await ConfiguredAsync(factory)).ShouldBeFalse();
            time.Advance(AdminSignIn.UserRefreshInterval);
            (await ConfiguredAsync(factory)).ShouldBeTrue();
        }
        finally
        {
            await SignInSettingsTests.ClearAsync(fixture.Services);
        }
    }

    [Fact]
    public async Task An_unreadable_stored_secret_refuses_the_sign_in_as_not_configured_with_its_own_log_line()
    {
        using var logs = new CapturingLoggerProvider();
        await using var factory = Host(logs);
        try
        {
            await SignInSettingsTests.SaveAsync(fixture.Services, new SignInWizardValues { Authority = StoredA }, StoredSecret);
            ShouldGoTo(await ChallengeAsync(factory), StoredA, DefaultClientId);
            await SignInSettingsTests.ReplaceCiphertextAsync(fixture.Services);

            // Not signed in as a public client without the secret: refused until the secret is set again.
            _time.Advance(AdminSignIn.SignInRefreshInterval);
            using var client = factory.CreateHttpsClient();
            using var login = await client.GetAsync("/bff/login", Ct);
            login.StatusCode.ShouldBe(HttpStatusCode.Conflict, await login.Content.ReadAsStringAsync(Ct));
            (await ConfiguredAsync(factory)).ShouldBeFalse();
            logs.Logged(typeof(AdminAuthentication).FullName!, SignInNotConfiguredReasons.UnreadableClientSecret).ShouldBeTrue(logs.Text);
            logs.Logged(typeof(AdminSignIn).FullName!, SignInNotConfiguredReasons.UnreadableClientSecret).ShouldBeTrue(logs.Text);
            logs.Logged(typeof(SignInSecretProtector).FullName!, "cannot be unprotected").ShouldBeTrue(logs.Text);
            logs.Text.ShouldNotContain(StoredSecret);
        }
        finally
        {
            await SignInSettingsTests.ClearAsync(fixture.Services);
        }
    }

    [Fact]
    public async Task A_callback_the_handler_cannot_complete_sends_the_person_back_to_sign_in_not_a_500()
    {
        // As a sign-in started before the values changed, or a forged or replayed one: its state does not check out.
        using var logs = new CapturingLoggerProvider();
        await using var factory = fixture.Derive(web =>
            web.ConfigureTestServices(services => services.AddLogging(b => b.AddProvider(logs).SetMinimumLevel(LogLevel.Trace))));
        using var client = factory.CreateHttpsClient();
        using var callback = await client.PostAsync("/signin-oidc", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["code"] = "code-from-before", ["state"] = "state-from-before",
        }), Ct);

        callback.StatusCode.ShouldBe(HttpStatusCode.Redirect, await callback.Content.ReadAsStringAsync(Ct));
        callback.Headers.Location!.OriginalString.ShouldBe("/?signin=failed");
        logs.Entries.ShouldContain(e => e.Category == SecurityEvents.Category && e.EventId.Name == "admin.signin.failed" && e.Message.Contains("callback_failed"),
            logs.Text);
    }

    [Fact]
    public async Task The_claim_mapping_takes_the_effective_claim_names()
    {
        await using var factory = Host(settings: ("Oidc:Authority", Deployed));
        try
        {
            await SignInSettingsTests.SaveAsync(fixture.Services, new SignInWizardValues { RoleClaim = "stored_roles" }, null);
            await ChallengeAsync(factory);
            Roles(await TokenValidatedAsync(factory, User(("stored_roles", AdminRoles.Viewer)))).ShouldBe([AdminRoles.Viewer]);

            // A deployment value wins over the stored one.
            factory.Services.GetRequiredService<IConfiguration>()["Oidc:RoleClaim"] = "deploy_roles";
            await ChallengeAsync(factory);
            Roles(await TokenValidatedAsync(factory, User(("stored_roles", AdminRoles.Viewer), ("deploy_roles", AdminRoles.DepartmentAdmin))))
                .ShouldBe([AdminRoles.DepartmentAdmin]);
        }
        finally
        {
            await SignInSettingsTests.ClearAsync(fixture.Services);
        }
    }

    [Fact]
    public void A_snapshot_is_configured_only_with_a_usable_authority_and_compares_every_value_the_secret_included()
    {
        static SignInSnapshot Snapshot(bool requireHttps, StoredSecretValue? secret = null, params (string Key, string? Value)[] values) => SignInSnapshot.Of(
            SignInSettingsService.Resolve(new ConfigurationBuilder().AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value))).Build(),
                new SignInWizardValues(), () => secret ?? StoredSecretValue.NotSet, null, null), requireHttps);

        Snapshot(true).NotConfiguredReason.ShouldBe(SignInNotConfiguredReasons.NoAuthority);
        Snapshot(true, null, ("Oidc:Authority", "http://idp.example/realms/ume")).NotConfiguredReason.ShouldBe(SignInNotConfiguredReasons.InvalidAuthority);
        Snapshot(false, null, ("Oidc:Authority", "idp.example")).NotConfiguredReason.ShouldBe(SignInNotConfiguredReasons.InvalidAuthority);
        Snapshot(false, null, ("Oidc:Authority", "http://localhost:8080/realms/ume")).IsConfigured.ShouldBeTrue();
        Snapshot(true, StoredSecretValue.Unreadable, ("Oidc:Authority", Deployed)).NotConfiguredReason.ShouldBe(SignInNotConfiguredReasons.UnreadableClientSecret);

        var one = Snapshot(true, null, ("Oidc:Authority", Deployed), ("Oidc:ClientSecret", "secret-1"));
        one.IsConfigured.ShouldBeTrue();
        one.HasSameValues(Snapshot(true, null, ("Oidc:Authority", Deployed), ("Oidc:ClientSecret", "secret-1"))).ShouldBeTrue();
        one.HasSameValues(Snapshot(true, null, ("Oidc:Authority", Deployed), ("Oidc:ClientSecret", "secret-2"))).ShouldBeFalse();
        one.HasSameValues(Snapshot(true, null, ("Oidc:Authority", Deployed), ("Oidc:ClientSecret", "secret-1"), ("Oidc:TeamClaim", "teams"))).ShouldBeFalse();
        one.HasSameValues(Snapshot(true, null, ("Oidc:Authority", Deployed), ("Oidc:ClientSecret", "secret-1"), ("Oidc:ExtraScopes:0", "a b"))).ShouldBeFalse();
        Snapshot(true, null, ("Oidc:Authority", Deployed), ("Oidc:ExtraScopes:0", "a b"))
            .HasSameValues(Snapshot(true, null, ("Oidc:Authority", Deployed), ("Oidc:ExtraScopes:0", "a"), ("Oidc:ExtraScopes:1", "b"))).ShouldBeFalse();
        one.ToString().ShouldNotContain("secret-1");
        one.Values.ToString().ShouldNotContain("secret-1");
    }

    [Theory]
    [InlineData("not-a-url")]
    [InlineData("http://idp.example/realms/ume")]
    public void A_set_but_unusable_authority_still_stops_the_start(string authority)
    {
        using var factory = fixture.Derive(web => web.UseSetting("Oidc:Authority", authority));
        Should.Throw<OptionsValidationException>(() => factory.Services.GetRequiredService<IOptions<OidcOptions>>().Value).Message.ShouldContain("Oidc:Authority");
    }

    private static ClaimsPrincipal User(params (string Type, string Value)[] claims) =>
        new(new ClaimsIdentity(claims.Select(c => new Claim(c.Type, c.Value)), "oidc"));

    private static string[] Roles(ClaimsPrincipal principal) => [.. principal.FindAll(AdminClaims.Roles).Select(c => c.Value).Distinct()];

    /// <summary>Runs the live options' <c>OnTokenValidated</c> for <paramref name="user"/>, as the handler does after the token checks.</summary>
    private static async Task<ClaimsPrincipal> TokenValidatedAsync(WebApplicationFactory<Program> factory, ClaimsPrincipal user)
    {
        var options = LiveOptions(factory);
        await using var scope = factory.Services.CreateAsyncScope();
        var http = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        var context = new TokenValidatedContext(http, new AuthenticationScheme(OpenIdConnectDefaults.AuthenticationScheme, null, typeof(OpenIdConnectHandler)),
            options, user, new AuthenticationProperties());
        await options.Events.OnTokenValidated(context);
        return context.Principal!;
    }
}
