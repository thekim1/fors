using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Ume.LlmGateway.AdminApi.Security;
using Ume.LlmGateway.AdminApi.Settings;
using Ume.LlmGateway.AdminApi.Setup;
using Ume.LlmGateway.TestKit;
using static Ume.LlmGateway.AdminApi.Tests.SetupHosts;

namespace Ume.LlmGateway.AdminApi.Tests;

/// <summary>
/// Setup mode (thekim1/llmgateway#100): when setup opens and closes, what a declared deployment is, the setup code and where it is
/// written, the wrong-code throttle, the setup session and its cookie. Each test resets setup's state in the shared database and the
/// class leaves it completed and closed, as the fixture's declared deployment has it.
/// </summary>
public sealed partial class SetupModeTests(AdminFixture fixture) : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync() => await ResetAsync(fixture, completed: true);

    private WebApplicationFactory<Program> Host(SetupOutput? output = null, CapturingLoggerProvider? logs = null, TimeProvider? time = null,
        params (string Key, string Value)[] settings) => SetupHosts.Host(fixture, output, logs, time, settings: settings);

    [Fact]
    public void The_code_is_26_Crockford_characters_in_groups_and_reads_case_and_separator_tolerant()
    {
        var code = SetupCode.Compute(Secret, "nonce-1");
        CodeShape().IsMatch(code).ShouldBeTrue(code);
        SetupCode.Compute(Secret, "nonce-1").ShouldBe(code);
        SetupCode.Compute(Secret, "nonce-2").ShouldNotBe(code);
        SetupCode.Compute(TestSecrets.RandomHex(), "nonce-1").ShouldNotBe(code);

        SetupCode.Matches(code, code).ShouldBeTrue();
        SetupCode.Matches(code.ToLowerInvariant(), code).ShouldBeTrue();
        SetupCode.Matches(code.Replace("-", "", StringComparison.Ordinal), code).ShouldBeTrue();
        SetupCode.Matches(code.Replace("-", " ", StringComparison.Ordinal), code).ShouldBeTrue();
        // Crockford's readings: O is 0, I and L are 1.
        SetupCode.Matches(code.Replace('0', 'O').Replace('1', 'l'), code).ShouldBeTrue();

        SetupCode.Matches(code[..^1], code).ShouldBeFalse();
        SetupCode.Matches(code + "0", code).ShouldBeFalse();
        SetupCode.Matches(code[..^1] + (code[^1] == 'A' ? 'B' : 'A'), code).ShouldBeFalse();
        SetupCode.Matches(code[..^1] + "U", code).ShouldBeFalse(); // not in the alphabet
        SetupCode.Matches(new string('0', 65), code).ShouldBeFalse();
        SetupCode.Matches(null, code).ShouldBeFalse();
    }

    [Fact]
    public async Task A_first_run_opens_once_on_two_hosts_started_together_and_both_write_the_same_code_to_standard_output_only()
    {
        await ResetAsync(fixture, completed: false);
        var (outA, outB) = (new SetupOutput(), new SetupOutput());
        using var logsA = new CapturingLoggerProvider();
        using var logsB = new CapturingLoggerProvider();
        await using var a = Host(outA, logsA);
        await using var b = Host(outB, logsB);

        await Task.WhenAll(Task.Run(() => a.Services, Ct), Task.Run(() => b.Services, Ct));

        var row = (await RowAsync(fixture)).ShouldNotBeNull();
        row.SetupNonce.ShouldNotBeNull();
        row.SetupCompletedAt.ShouldBeNull();
        var code = SetupCode.Compute(Secret, row.SetupNonce);
        outA.Text.ShouldContain(code);
        outB.Text.ShouldContain(code);
        outA.Text.ShouldContain("/setup");
        // One opening: one admin.setup.opened between the two hosts, and never the code in any log record.
        (Events(logsA, 2008).Count + Events(logsB, 2008).Count).ShouldBe(1);
        Events(logsA, 2008).Concat(Events(logsB, 2008)).Single().Message.ShouldContain(SetupMode.FirstRun);
        foreach (var form in Forms(code))
        {
            logsA.Text.ShouldNotContain(form);
            logsB.Text.ShouldNotContain(form);
        }

        using var client = await ClientAsync(a);
        (await SetupOpenAsync(client)).ShouldBeTrue();
    }

    [Fact]
    public async Task A_deployment_that_sets_the_authority_counts_as_completed_prints_no_code_and_never_sees_setup()
    {
        // An existing deployment on a database that has never been through setup: the authority from the deployment (here with a
        // setup secret too, which it does not need), no client secret since ume-admin is a public client.
        await ResetAsync(fixture, completed: false);
        var output = new SetupOutput();
        using var logs = new CapturingLoggerProvider();
        await using (var declared = Host(output, logs, settings: ("Oidc:Authority", "https://identity.invalid")))
        {
            using var client = await ClientAsync(declared);
            (await SetupOpenAsync(client)).ShouldBeFalse();
            var row = (await RowAsync(fixture))!;
            row.SetupCompletedAt.ShouldNotBeNull();
            row.SetupNonce.ShouldBeNull();
            output.Text.ShouldBeEmpty();
            Events(logs, 2008).ShouldBeEmpty();
            logs.Logged(typeof(SetupStartup).FullName!, "declares the sign-in").ShouldBeTrue(logs.Text);
            (await PostAsync(client, "/bff/setup/session", new { code = "x" })).StatusCode.ShouldBe(HttpStatusCode.NotFound);
            (await client.GetAsync("/bff/setup/state", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        }

        // Nothing new is required: without a setup secret or a re-open value it starts the same, and stays closed.
        var again = new SetupOutput();
        using var logsAgain = new CapturingLoggerProvider();
        await using var plain = Host(again, logsAgain, settings: [("Oidc:Authority", "https://identity.invalid"), ("Setup:Secret", "")]);
        using var plainClient = await ClientAsync(plain);
        (await SetupOpenAsync(plainClient)).ShouldBeFalse();
        again.Text.ShouldBeEmpty();
        logsAgain.Entries.ShouldNotContain(e => e.Level >= LogLevel.Error && e.Category.Contains("Setup", StringComparison.Ordinal), logsAgain.Text);
    }

    [Fact]
    public async Task An_open_first_run_closes_when_the_deployment_starts_declaring_the_sign_in()
    {
        await ResetAsync(fixture, completed: false);
        await using (var open = Host())
        {
            using var client = await ClientAsync(open);
            (await SetupOpenAsync(client)).ShouldBeTrue();
        }

        await using var declared = Host(settings: ("Oidc:Authority", "https://identity.invalid"));
        using var declaredClient = await ClientAsync(declared);
        (await SetupOpenAsync(declaredClient)).ShouldBeFalse();
        (await RowAsync(fixture))!.SetupCompletedAt.ShouldNotBeNull();
        (await DbAsync(fixture, db => db.SetupSessions.CountAsync(Ct))).ShouldBe(0);
    }

    [Fact]
    public async Task Closed_setup_answers_404_on_every_route_even_with_a_cookie_of_an_earlier_opening()
    {
        await ResetAsync(fixture, completed: false);
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using (var opening = Host(time: time))
        {
            using var session = await SessionAsync(opening, fixture);
            (await session.GetAsync("/bff/setup/state", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

            // Setup closes (as a completion does): the same cookie gets 404 everywhere.
            await ResetAsync(fixture, completed: true);
            (await session.GetAsync("/bff/setup/state", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
            (await PostAsync(session, "/bff/setup/test", new { authority = "https://idp.invalid" })).StatusCode.ShouldBe(HttpStatusCode.NotFound);
            (await PostAsync(session, "/bff/setup/complete", new { testId = Guid.NewGuid() })).StatusCode.ShouldBe(HttpStatusCode.NotFound);
            (await PostAsync(session, "/bff/setup/session", new { code = "anything" })).StatusCode.ShouldBe(HttpStatusCode.NotFound);
            // /bff/user, which anyone may call, reads the state again at most every 10 seconds.
            time.Advance(AdminSignIn.UserRefreshInterval);
            (await SetupOpenAsync(session)).ShouldBeFalse();
        }

        // A closed start writes no code; even without the antiforgery headers the answer is 404.
        var output = new SetupOutput();
        await using var closed = Host(output);
        using var plain = closed.CreateHttpsClient();
        (await plain.PostAsync("/bff/setup/session", JsonContent(new { code = "x" }), Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await plain.GetAsync("/bff/setup/state", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        output.Text.ShouldBeEmpty();
    }

    [Fact]
    public async Task Wrong_codes_are_throttled_per_minute_write_nothing_never_void_the_code_and_are_logged_sampled()
    {
        await ResetAsync(fixture, completed: false);
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        using var logs = new CapturingLoggerProvider();
        await using var host = Host(logs: logs, time: time);
        using var client = await ClientAsync(host);
        var code = await CodeAsync(fixture);
        var wrong = SetupCode.Compute(Secret, "another-nonce");
        var before = await RowAsync(fixture);

        for (var attempt = 1; attempt <= SetupCodeThrottle.Limit; attempt++)
        {
            using var refused = await PostAsync(client, "/bff/setup/session", new { code = wrong });
            refused.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            (await CodeOf(refused)).ShouldBe(SetupErrors.CodeInvalid);
            var text = await refused.Content.ReadAsStringAsync(Ct);
            text.ShouldNotContain("remaining");
            foreach (var form in Forms(code).Concat(Forms(wrong)))
            {
                text.ShouldNotContain(form);
            }
        }

        // A wrong code writes nothing: no session, the row untouched.
        (await DbAsync(fixture, db => db.SetupSessions.CountAsync(Ct))).ShouldBe(0);
        var after = (await RowAsync(fixture))!;
        (after.SetupNonce, after.SetupOpenedAt, after.UpdatedAt).ShouldBe((before!.SetupNonce, before.SetupOpenedAt, before.UpdatedAt));

        // Over 20 wrong codes in the minute: 429 with the seconds to the window's end.
        time.Advance(TimeSpan.FromSeconds(20));
        using (var flooded = await PostAsync(client, "/bff/setup/session", new { code = wrong }))
        {
            flooded.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
            (await CodeOf(flooded)).ShouldBe(SetupErrors.AttemptsExhausted);
            int.Parse(flooded.Headers.RetryAfter!.ToString(), System.Globalization.CultureInfo.InvariantCulture).ShouldBeInRange(39, 40);
        }

        // The right code is never throttled: a flood of wrong codes cannot keep the deployer out (the code is compared first).
        using (var deployer = await ClientAsync(host))
        using (var inTheFlood = await PostAsync(deployer, "/bff/setup/session", new { code }))
        {
            inTheFlood.StatusCode.ShouldBe(HttpStatusCode.OK, await inTheFlood.Content.ReadAsStringAsync(Ct));
        }

        // Sampled: the first wrong code at once, the other 19 in the next event after 10 seconds.
        Events(logs, 2009).Count.ShouldBe(1);
        time.Advance(TimeSpan.FromSeconds(41));
        using (var next = await PostAsync(client, "/bff/setup/session", new { code = wrong }))
        {
            next.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }

        var refusedEvents = Events(logs, 2009);
        refusedEvents.Count.ShouldBe(2);
        refusedEvents[1].Message.ShouldContain("20 wrong codes");

        // The window allows again: the right code works, nothing was voided; its answer does not hold the code either.
        using (var right = await PostAsync(client, "/bff/setup/session", new { code }))
        {
            var text = await right.Content.ReadAsStringAsync(Ct);
            right.StatusCode.ShouldBe(HttpStatusCode.OK, text);
            foreach (var form in Forms(code))
            {
                text.ShouldNotContain(form);
            }
        }

        foreach (var form in Forms(code).Concat(Forms(wrong)))
        {
            logs.Text.ShouldNotContain(form);
        }
    }

    [Fact]
    public async Task The_opening_ends_after_an_hour_and_the_next_start_of_a_never_completed_deployment_opens_anew()
    {
        await ResetAsync(fixture, completed: false);
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        string firstNonce;
        await using (var host = Host(time: time))
        {
            using var session = await SessionAsync(host, fixture);
            firstNonce = (await RowAsync(fixture))!.SetupNonce!;
            time.Advance(TimeSpan.FromMinutes(59));
            (await SetupOpenAsync(session)).ShouldBeTrue();
            time.Advance(TimeSpan.FromMinutes(2));
            (await SetupOpenAsync(session)).ShouldBeFalse();
            (await session.GetAsync("/bff/setup/state", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
            (await PostAsync(session, "/bff/setup/session", new { code = SetupCode.Compute(Secret, firstNonce) })).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        }

        // Nothing renews it by itself; the next start does.
        var output = new SetupOutput();
        await using var restarted = Host(output, time: time);
        _ = restarted.Services;
        var row = (await RowAsync(fixture))!;
        row.SetupNonce.ShouldNotBe(firstNonce);
        output.Text.ShouldContain(SetupCode.Compute(Secret, row.SetupNonce!));
        using var client = await ClientAsync(restarted);
        (await SetupOpenAsync(client)).ShouldBeTrue();
    }

    [Fact]
    public async Task A_reopen_value_opens_setup_once_is_consumed_at_the_opening_and_a_restart_with_it_does_not_open_again()
    {
        await ResetAsync(fixture, completed: true);
        using var logs = new CapturingLoggerProvider();
        var output = new SetupOutput();
        await using (var host = Host(output, logs, settings: ("Setup:Reopen", "2026-11-16")))
        {
            _ = host.Services;
            var row = (await RowAsync(fixture))!;
            row.SetupNonce.ShouldNotBeNull();
            row.SetupReopenConsumed.ShouldBe("2026-11-16");
            row.SetupCompletedAt.ShouldNotBeNull();
            Events(logs, 2008).Single().Message.ShouldContain(SetupMode.Reopen);
            output.Text.ShouldContain(SetupCode.Compute(Secret, row.SetupNonce));
        }

        // The opening ran out; a restart with the same value opens nothing and writes no code.
        await DbAsync(fixture, db => db.SignInSettings.ExecuteUpdateAsync(s => s.SetProperty(x => x.SetupOpenedAt, DateTimeOffset.UtcNow.AddHours(-2)), Ct));
        var again = new SetupOutput();
        using var logsAgain = new CapturingLoggerProvider();
        await using (var sameValue = Host(again, logsAgain, settings: ("Setup:Reopen", "2026-11-16")))
        {
            using var client = await ClientAsync(sameValue);
            (await SetupOpenAsync(client)).ShouldBeFalse();
            again.Text.ShouldBeEmpty();
            Events(logsAgain, 2008).ShouldBeEmpty();
        }

        // A new value opens it again, also on a deployment that declares the sign-in.
        await using var newValue = Host(settings: [("Setup:Reopen", "2026-11-17"), ("Oidc:Authority", "https://identity.invalid")]);
        using var reopened = await ClientAsync(newValue);
        (await SetupOpenAsync(reopened)).ShouldBeTrue();
        (await RowAsync(fixture))!.SetupReopenConsumed.ShouldBe("2026-11-17");
    }

    [Fact]
    public async Task Once_completed_a_missing_value_or_an_unreadable_secret_keeps_setup_closed()
    {
        await ResetAsync(fixture, completed: true);
        await SignInSettingsTests.SaveAsync(fixture.Services, new SignInWizardValues { Authority = "https://stored.invalid/realms/ume" }, "stored-secret-3e1f");
        await SignInSettingsTests.ReplaceCiphertextAsync(fixture.Services);

        // The deployment's authority removed and the stored secret unreadable: sign-in is broken, setup still closed.
        var output = new SetupOutput();
        await using var host = Host(output);
        using var client = await ClientAsync(host);
        using var json = JsonDocument.Parse(await client.GetStringAsync("/bff/user", Ct));
        json.RootElement.GetProperty("signIn").GetProperty("configured").GetBoolean().ShouldBeFalse();
        json.RootElement.GetProperty("setup").GetProperty("open").GetBoolean().ShouldBeFalse();
        (await RowAsync(fixture))!.SetupNonce.ShouldBeNull();
        (await PostAsync(client, "/bff/setup/session", new { code = "x" })).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        output.Text.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_missing_settings_row_keeps_setup_closed_says_so_and_is_not_created()
    {
        // The migration creates the row, so the first run is a row without SetupCompletedAt; a missing row is a fault and stays closed.
        await DbAsync(fixture, db => db.SignInSettings.ExecuteDeleteAsync(Ct));
        try
        {
            using var logs = new CapturingLoggerProvider();
            var output = new SetupOutput();
            await using var host = Host(output, logs, settings: [("Oidc:Authority", "https://identity.invalid"), ("Setup:Reopen", "after-restore")]);
            using var client = await ClientAsync(host);
            (await SetupOpenAsync(client)).ShouldBeFalse();
            (await RowAsync(fixture)).ShouldBeNull(); // not even the declaring deployment's branch creates it
            logs.Entries.ShouldContain(e => e.Level == LogLevel.Error && e.Message.Contains("row is missing", StringComparison.Ordinal), logs.Text);
            (await PostAsync(client, "/bff/setup/session", new { code = "x" })).StatusCode.ShouldBe(HttpStatusCode.NotFound);
            output.Text.ShouldBeEmpty();
        }
        finally
        {
            await ResetAsync(fixture, completed: true);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("too-short")]
    public async Task Without_a_setup_secret_setup_stays_closed_logs_the_file_and_consumes_nothing(string secret)
    {
        await ResetAsync(fixture, completed: true);
        using var logs = new CapturingLoggerProvider();
        var output = new SetupOutput();
        await using var host = Host(output, logs, settings: [("Setup:Secret", secret), ("Setup:Reopen", "r1")]);
        using var client = await ClientAsync(host);

        (await SetupOpenAsync(client)).ShouldBeFalse();
        var row = (await RowAsync(fixture))!;
        row.SetupNonce.ShouldBeNull();
        row.SetupReopenConsumed.ShouldBeNull(); // the value opens setup at the first start that has the secret
        logs.Entries.ShouldContain(e => e.Level == LogLevel.Error && e.Message.Contains(SetupOptions.SecretFileName, StringComparison.Ordinal), logs.Text);
        output.Text.ShouldBeEmpty();
    }

    [Fact]
    public async Task The_setup_posts_need_the_antiforgery_header_like_every_bff_post()
    {
        await ResetAsync(fixture, completed: false);
        await using var host = Host();
        using var plain = host.CreateHttpsClient();
        var code = await CodeAsync(fixture);
        foreach (var path in new[] { "/bff/setup/session", "/bff/setup/test", "/bff/setup/complete" })
        {
            using var response = await plain.PostAsync(path, JsonContent(new { code }), Ct);
            response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, path);
            (await response.Content.ReadAsStringAsync(Ct)).ShouldContain("CSRF");
        }

        // The header present with a token that is not the cookie's: refused as well.
        using var forged = host.CreateHttpsClient();
        forged.DefaultRequestHeaders.Add("X-Requested-With", "XMLHttpRequest");
        forged.DefaultRequestHeaders.Add("X-XSRF-TOKEN", "not-the-token");
        using var bad = await forged.PostAsync("/bff/setup/session", JsonContent(new { code }), Ct);
        bad.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await bad.Content.ReadAsStringAsync(Ct)).ShouldContain("CSRF token is invalid");
        (await DbAsync(fixture, db => db.SetupSessions.CountAsync(Ct))).ShouldBe(0);
    }

    [Fact]
    public async Task Other_spellings_of_the_setup_paths_are_gated_like_the_canonical_one()
    {
        await ResetAsync(fixture, completed: true);
        await using (var closed = Host())
        {
            using var client = await ClientAsync(closed);
            (await client.GetAsync("/BFF/Setup/state", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
            (await client.GetAsync("/bff/setup/state/", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        }

        await ResetAsync(fixture, completed: false);
        await using var open = Host();
        using var session = await SessionAsync(open, fixture);
        // Open: the other spellings route to the same endpoint (the setup cookie's path is matched case-sensitively by clients, so
        // only the canonical spelling carries the session).
        (await session.GetAsync("/bff/setup/state/", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        using var anonymous = await ClientAsync(open);
        (await anonymous.GetAsync("/BFF/Setup/state", Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task The_sessions_of_an_ended_opening_are_deleted_once_setup_is_found_closed()
    {
        await ResetAsync(fixture, completed: false);
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var host = Host(time: time);
        using var session = await SessionAsync(host, fixture);
        (await DbAsync(fixture, db => db.SetupSessions.CountAsync(Ct))).ShouldBe(1);
        time.Advance(TimeSpan.FromMinutes(61));
        (await session.GetAsync("/bff/setup/state", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await DbAsync(fixture, db => db.SetupSessions.CountAsync(Ct))).ShouldBe(0);
    }

    [Fact]
    public async Task The_session_cookie_is_HttpOnly_Secure_SameSite_Strict_for_the_setup_api_and_30_minutes()
    {
        await ResetAsync(fixture, completed: false);
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        using var logs = new CapturingLoggerProvider();
        await using var host = Host(logs: logs, time: time);
        using var client = await ClientAsync(host);
        using var response = await PostAsync(client, "/bff/setup/session", new { code = (await CodeAsync(fixture)).ToLowerInvariant() });
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using (var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)))
        {
            body.RootElement.GetProperty("expiresAt").GetDateTimeOffset().ShouldBe(time.GetUtcNow() + SetupSessions.Lifetime);
            body.RootElement.GetProperty("setupEndsAt").ValueKind.ShouldBe(JsonValueKind.String);
        }

        var cookie = response.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith(SetupSessions.CookieName + "=", StringComparison.Ordinal));
        cookie.ShouldContain("path=/bff/setup", Case.Insensitive);
        cookie.ShouldContain("secure", Case.Insensitive);
        cookie.ShouldContain("httponly", Case.Insensitive);
        cookie.ShouldContain("samesite=strict", Case.Insensitive);
        cookie.ShouldContain("expires=", Case.Insensitive);
        var token = cookie.Split(';')[0][(SetupSessions.CookieName.Length + 1)..];
        var stored = await DbAsync(fixture, db => db.SetupSessions.AsNoTracking().SingleAsync(Ct));
        stored.TokenHash.ShouldBe(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
        (stored.ExpiresAt - stored.CreatedAt).ShouldBe(TimeSpan.FromMinutes(30));
        Events(logs, 2010).Single().Message.ShouldContain(SetupSessions.EventId(stored));

        logs.Text.ShouldNotContain(token);
        (await client.GetAsync("/bff/setup/state", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        time.Advance(TimeSpan.FromMinutes(31));
        using var expired = await client.GetAsync("/bff/setup/state", Ct);
        expired.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await CodeOf(expired)).ShouldBe(SetupErrors.SessionRequired);
    }

    [Fact]
    public async Task A_session_of_an_earlier_opening_is_refused()
    {
        await ResetAsync(fixture, completed: true);
        await using var first = Host(settings: ("Setup:Reopen", "first"));
        using var session = await SessionAsync(first, fixture);
        (await session.GetAsync("/bff/setup/state", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        // Another re-open: a new opening, whose start voids the earlier sessions.
        await using var second = Host(settings: ("Setup:Reopen", "second"));
        _ = second.Services;
        using var refused = await session.GetAsync("/bff/setup/state", Ct);
        refused.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await CodeOf(refused)).ShouldBe(SetupErrors.SessionRequired);
    }

    [Fact]
    public async Task The_state_shows_the_values_with_their_sources_the_redirect_uris_and_never_a_secret()
    {
        await ResetAsync(fixture, completed: false);
        await SignInSettingsTests.SaveAsync(fixture.Services, new SignInWizardValues { ClientId = "stored-client" }, "stored-secret-8a2b");
        await using var host = Host(settings: [("Oidc:RoleClaim", "deploy_roles"), ("Oidc:RoleGroups:gateway-admin", "GG-Admins; GG-Ops")]);
        using var session = await SessionAsync(host, fixture);
        var text = await session.GetStringAsync("/bff/setup/state", Ct);
        text.ShouldNotContain("stored-secret-8a2b");
        var ciphertext = (await RowAsync(fixture))!.EncryptedClientSecret!;
        text.ShouldNotContain(ciphertext);

        using var json = JsonDocument.Parse(text);
        var root = json.RootElement;
        root.GetProperty("kind").GetString().ShouldBe(SetupMode.FirstRun);
        root.GetProperty("redirectUris").GetProperty("signIn").GetString().ShouldBe("https://localhost/signin-oidc");
        root.GetProperty("redirectUris").GetProperty("signOutCallback").GetString().ShouldBe("https://localhost/signout-callback-oidc");
        root.GetProperty("gatewayAdminGroups").EnumerateArray().Select(g => g.GetString()).ShouldBe(["GG-Admins", "GG-Ops"]);
        root.GetProperty("latestTest").ValueKind.ShouldBe(JsonValueKind.Null);
        var signIn = root.GetProperty("signIn");
        (signIn.GetProperty("roleClaim").GetProperty("value").GetString(), signIn.GetProperty("roleClaim").GetProperty("locked").GetBoolean()).ShouldBe(("deploy_roles", true));
        (signIn.GetProperty("clientId").GetProperty("source").GetString(), signIn.GetProperty("clientId").GetProperty("locked").GetBoolean())
            .ShouldBe((SignInValueSources.Wizard, false));
        signIn.GetProperty("authority").GetProperty("value").ValueKind.ShouldBe(JsonValueKind.Null);
        signIn.GetProperty("clientSecret").GetProperty("set").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task No_route_but_setup_complete_writes_the_sign_in_settings()
    {
        await ResetAsync(fixture, completed: true);
        // The only sign-in route of an admin session reads; nothing else is mapped under it.
        var methods = fixture.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText?.StartsWith("/api", StringComparison.OrdinalIgnoreCase) == true &&
                        (e.RoutePattern.RawText.Contains("sign-in", StringComparison.OrdinalIgnoreCase) || e.RoutePattern.RawText.Contains("oidc", StringComparison.OrdinalIgnoreCase)))
            .SelectMany(e => e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["*"])
            .Distinct().ToList();
        methods.ShouldBe(["GET"]);

        using var admin = await fixture.ClientAsync();
        foreach (var method in new[] { HttpMethod.Put, HttpMethod.Post, HttpMethod.Patch, HttpMethod.Delete })
        {
            using var request = new HttpRequestMessage(method, "/api/settings/sign-in") { Content = JsonContent(new { authority = "https://evil.invalid", clientSecret = "evil" }) };
            ((int)(await admin.SendAsync(request, Ct)).StatusCode).ShouldBeOneOf(404, 405);
        }

        var row = (await RowAsync(fixture))!;
        row.Authority.ShouldBeNull();
        row.EncryptedClientSecret.ShouldBeNull();
    }

    private static StringContent JsonContent(object body) => new(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

    [GeneratedRegex("^[0-9A-HJKMNP-TV-Z]{5}(-[0-9A-HJKMNP-TV-Z]{5}){3}-[0-9A-HJKMNP-TV-Z]{6}$")]
    private static partial Regex CodeShape();
}
