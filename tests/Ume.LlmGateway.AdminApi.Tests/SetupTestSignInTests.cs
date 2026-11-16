using Microsoft.Extensions.Time.Testing;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Web;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Ume.LlmGateway.AdminApi.Security;
using Ume.LlmGateway.AdminApi.Settings;
using Ume.LlmGateway.AdminApi.Setup;
using Ume.LlmGateway.Infrastructure.Auditing;
using Ume.LlmGateway.TestKit;
using static Ume.LlmGateway.AdminApi.Tests.SetupHosts;

namespace Ume.LlmGateway.AdminApi.Tests;

/// <summary>
/// An identity provider in the test process for the setup test sign-in: discovery, keys and a token endpoint that issues an id token
/// signed with its own key for <see cref="Claims"/> and the nonce of the authorization request. Each answer can be replaced, so the
/// refusals can be tested; every replaced answer carries <see cref="LeakMarker"/>, which must never come back.
/// </summary>
internal sealed class FakeProvider : HttpMessageHandler
{
    public const string Authority = "https://idp.test/realms/ume";
    public const string LeakMarker = "LEAK-MARKER-7f3a";
    private const string KeyId = "k1";

    private readonly RSA _rsa = RSA.Create(2048);
    private int _tokenRequests;

    public List<Uri> Requested { get; } = [];

    /// <summary>Answers by path, before the defaults.</summary>
    public Dictionary<string, Func<HttpResponseMessage>> Overrides { get; } = new(StringComparer.Ordinal);

    /// <summary>The id token's claims besides iss, aud, nonce and the times.</summary>
    public Dictionary<string, object> Claims { get; set; } = Person("person-1", AdminRoles.GatewayAdmin);

    /// <summary>The nonce the next id token carries (the authorization request's, unless a test changes it).</summary>
    public string? Nonce { get; set; }

    public string? Audience { get; set; }

    public string Issuer { get; set; } = Authority;

    /// <summary>When the id token expires, from now; negative for an expired one.</summary>
    public TimeSpan ExpiresIn { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>A raw id token instead of the signed one (unsigned, HS256, …), from the token request's form.</summary>
    public Func<Dictionary<string, string>, string>? IdTokenFactory { get; set; }

    public Dictionary<string, string>? LastTokenForm { get; private set; }

    public int TokenRequests => Volatile.Read(ref _tokenRequests);

    public static Dictionary<string, object> Person(string subject, params string[] roles) => new()
    {
        ["sub"] = subject, ["name"] = "Anna Andersson", ["roles"] = roles, ["departmentCodes"] = new[] { "1000" }, ["groups"] = new[] { "g-1", "g-2" },
    };

    public static HttpResponseMessage Json(object body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };

    public static object Discovery(string issuer = Authority, bool requirePar = false, string endpoints = Authority) => new Dictionary<string, object>
    {
        ["issuer"] = issuer, ["authorization_endpoint"] = endpoints + "/auth", ["token_endpoint"] = endpoints + "/token", ["jwks_uri"] = endpoints + "/certs",
        ["require_pushed_authorization_requests"] = requirePar, ["x_note"] = LeakMarker,
    };

    /// <summary>A token of <paramref name="payload"/> with the header <c>alg: none</c> and no signature.</summary>
    public static string Unsigned(object payload) =>
        Base64UrlEncoder.Encode("{\"alg\":\"none\",\"typ\":\"JWT\"}") + "." + Base64UrlEncoder.Encode(JsonSerializer.Serialize(payload)) + ".";

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        lock (Requested)
        {
            Requested.Add(request.RequestUri!);
        }

        var path = request.RequestUri!.AbsolutePath;
        Dictionary<string, string>? form = null;
        if (path.EndsWith("/token", StringComparison.Ordinal))
        {
            Interlocked.Increment(ref _tokenRequests);
            // Kept before any replaced answer, so a test sees what was sent to a token endpoint it answers itself.
            form = (await request.Content!.ReadAsStringAsync(cancellationToken)).Split('&')
                .Select(p => p.Split('=', 2)).ToDictionary(p => WebUtility.UrlDecode(p[0]), p => WebUtility.UrlDecode(p[1]));
            LastTokenForm = form;
        }

        if (Overrides.TryGetValue(path, out var answer))
        {
            return answer();
        }

        if (path.EndsWith("/.well-known/openid-configuration", StringComparison.Ordinal))
        {
            return Json(Discovery());
        }

        if (path.EndsWith("/certs", StringComparison.Ordinal))
        {
            var key = _rsa.ExportParameters(false);
            return Json(new { keys = new[] { new { kty = "RSA", use = "sig", alg = "RS256", kid = KeyId, n = Base64UrlEncoder.Encode(key.Modulus), e = Base64UrlEncoder.Encode(key.Exponent) } } });
        }

        if (form is not null)
        {
            var now = DateTime.UtcNow;
            var token = IdTokenFactory?.Invoke(form) ?? new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
            {
                Issuer = Issuer, Audience = Audience ?? form["client_id"], IssuedAt = now.Add(ExpiresIn).AddMinutes(-10), NotBefore = now.Add(ExpiresIn).AddMinutes(-10),
                Expires = now.Add(ExpiresIn), Claims = new Dictionary<string, object>(Claims) { ["nonce"] = Nonce! },
                SigningCredentials = new SigningCredentials(new RsaSecurityKey(_rsa) { KeyId = KeyId }, SecurityAlgorithms.RsaSha256),
            });
            return Json(new { access_token = "at-" + LeakMarker, id_token = token, token_type = "Bearer" });
        }

        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _rsa.Dispose();
        }

        base.Dispose(disposing);
    }
}

/// <summary>A body that sends a byte, then never more: a provider that drips past the timeout.</summary>
internal sealed class DrippingHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new DrippingStream()) });

    private sealed class DrippingStream : Stream
    {
        private bool _sent;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!_sent)
            {
                _sent = true;
                buffer.Span[0] = (byte)'{';
                return 1;
            }

            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

/// <summary>
/// The setup test sign-in (thekim1/llmgateway#100) against a provider in the test process: its refusals, the secret rules, the
/// browser binding, the callback and its checks, single use, and what completing saves.
/// </summary>
public sealed class SetupTestSignInTests(AdminFixture fixture) : IAsyncLifetime
{
    /// <summary>Long enough to be an HS256 key, so the "signed with the client secret" token is well-formed.</summary>
    private const string TypedSecret = "typed-secret-9d1c4e-0123456789abcdef";
    private const string OtherAuthority = "https://idp2.test/realms/ume";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => await ResetAsync(fixture, completed: false);

    public async ValueTask DisposeAsync() => await ResetAsync(fixture, completed: true);

    private WebApplicationFactory<Program> Host(FakeProvider provider, CapturingLoggerProvider? logs = null, string? environment = null,
        params (string Key, string Value)[] settings) =>
        SetupHosts.Host(fixture, logs: logs, environment: environment, provider: provider, settings: settings);

    private static object Values(string? clientSecret = TypedSecret, string? roleClaim = null, string authority = FakeProvider.Authority, string? clientId = null) =>
        new { authority, clientId, clientSecret, roleClaim };

    /// <summary>Starts a test; returns its id, the authorization request's parameters and its state.</summary>
    private static async Task<(Guid TestId, System.Collections.Specialized.NameValueCollection Query, string State)> StartAsync(HttpClient session, FakeProvider provider,
        object? values = null)
    {
        using var response = await PostAsync(session, "/bff/setup/test", values ?? Values());
        var text = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, text);
        using var json = JsonDocument.Parse(text);
        var url = new Uri(json.RootElement.GetProperty("authorizeUrl").GetString()!);
        var query = HttpUtility.ParseQueryString(url.Query);
        provider.Nonce = query["nonce"];
        return (json.RootElement.GetProperty("testId").GetGuid(), query, query["state"]!);
    }

    /// <summary>The provider's form_post back to the callback, from <paramref name="browser"/> (which holds the test cookie when it started the test).</summary>
    private static async Task<HttpResponseMessage> CallbackAsync(HttpClient browser, string state, string? code = "code-1", string? error = null)
    {
        var form = new Dictionary<string, string> { ["state"] = state };
        if (code is not null)
        {
            form["code"] = code;
        }

        if (error is not null)
        {
            form["error"] = error;
        }

        return await browser.PostAsync("/signin-oidc", new FormUrlEncodedContent(form), Ct);
    }

    private static async Task<JsonElement> LatestTestAsync(HttpClient session)
    {
        using var json = JsonDocument.Parse(await session.GetStringAsync("/bff/setup/state", Ct));
        return json.RootElement.GetProperty("latestTest").Clone();
    }

    /// <summary>Starts a test and posts its callback from the same browser; returns the test's reason (null when it passed).</summary>
    private static async Task<string?> RunTestAsync(HttpClient session, FakeProvider provider, Action? arrange = null, object? values = null)
    {
        var (_, _, state) = await StartAsync(session, provider, values);
        arrange?.Invoke();
        using var done = await CallbackAsync(session, state);
        done.Headers.Location!.OriginalString.ShouldBe(SetupTestSignIn.DonePath);
        var latest = await LatestTestAsync(session);
        return latest.GetProperty("reason").GetString();
    }

    [Fact]
    public async Task A_test_starts_a_code_sign_in_with_pkce_form_post_and_the_admin_apis_redirect_uri_and_sets_the_browser_cookie()
    {
        using var provider = new FakeProvider();
        await using var host = Host(provider);
        using var session = await SessionAsync(host, fixture);
        using var response = await PostAsync(session, "/bff/setup/test", Values());
        var text = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, text);
        text.ShouldNotContain(TypedSecret);
        text.ShouldNotContain(FakeProvider.LeakMarker);
        var cookie = response.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith(SetupTestSignIn.TestCookieName + "=", StringComparison.Ordinal));
        cookie.ShouldContain("path=/signin-oidc", Case.Insensitive);
        cookie.ShouldContain("samesite=none", Case.Insensitive);
        cookie.ShouldContain("secure", Case.Insensitive);
        cookie.ShouldContain("httponly", Case.Insensitive);
        cookie.ShouldContain("expires=", Case.Insensitive);
        using var json = JsonDocument.Parse(text);
        var url = new Uri(json.RootElement.GetProperty("authorizeUrl").GetString()!);
        url.GetLeftPart(UriPartial.Path).ShouldBe(FakeProvider.Authority + "/auth");
        var query = HttpUtility.ParseQueryString(url.Query);
        query["client_id"].ShouldBe(new OidcOptions().ClientId);
        query["redirect_uri"].ShouldBe("https://localhost/signin-oidc");
        query["response_type"].ShouldBe("code");
        query["response_mode"].ShouldBe("form_post");
        query["code_challenge_method"].ShouldBe("S256");
        query["code_challenge"].ShouldNotBeNullOrEmpty();
        query["nonce"].ShouldNotBeNullOrEmpty();
        query["scope"]!.Split(' ').ShouldBe(["openid", "profile", "email"], ignoreOrder: true);
        query["state"].ShouldStartWith(SetupTestSignIn.StatePrefix);
        query["state"]![SetupTestSignIn.StatePrefix.Length..].Length.ShouldBe(43);

        var test = await DbAsync(fixture, db => db.SetupTests.AsNoTracking().SingleAsync(Ct));
        test.Protected.ShouldNotContain(TypedSecret);
        test.Protected.ShouldNotContain(query["nonce"]!);
        test.MarkerHash.ShouldBe(SHA256.HashData(Encoding.UTF8.GetBytes(query["state"]![SetupTestSignIn.StatePrefix.Length..])));
        test.BrowserHash.ShouldBe(SHA256.HashData(Encoding.UTF8.GetBytes(cookie.Split(';')[0][(SetupTestSignIn.TestCookieName.Length + 1)..])));
        (test.ExpiresAt - test.CreatedAt).ShouldBe(TimeSpan.FromMinutes(10));
        (await LatestTestAsync(session)).GetProperty("status").GetString().ShouldBe("pending");
    }

    [Fact]
    public async Task A_provider_that_cannot_serve_is_refused_with_its_step_and_nothing_of_its_answer()
    {
        const string discovery = "/realms/ume/.well-known/openid-configuration";
        var cases = new (string Reason, string Path, Func<HttpResponseMessage> Answer)[]
        {
            (SetupTestReasons.Redirected, discovery, () => new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("https://internal.test/" + FakeProvider.LeakMarker) } }),
            (SetupTestReasons.NotDiscovery, discovery, () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>" + FakeProvider.LeakMarker + "</html>") }),
            (SetupTestReasons.NotDiscovery, discovery, () => new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent(FakeProvider.LeakMarker) }),
            (SetupTestReasons.NotDiscovery, discovery, () => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"issuer\":\"" + FakeProvider.Authority + "\",\"x\":\"" + new string('a', SetupProviderClient.MaxResponseBytes) + FakeProvider.LeakMarker + "\"}"),
            }),
            (SetupTestReasons.IssuerMismatch, discovery, () => FakeProvider.Json(FakeProvider.Discovery(issuer: "https://other.test/" + FakeProvider.LeakMarker))),
            (SetupTestReasons.InsecureEndpoint, discovery, () => FakeProvider.Json(FakeProvider.Discovery(endpoints: "http://idp.test/realms/ume"))),
            (SetupTestReasons.ParRequired, discovery, () => FakeProvider.Json(FakeProvider.Discovery(requirePar: true))),
            (SetupTestReasons.KeysUnavailable, "/realms/ume/certs", () => new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent(FakeProvider.LeakMarker) }),
            (SetupTestReasons.KeysUnavailable, "/realms/ume/certs", () => FakeProvider.Json(new { keys = Array.Empty<object>(), note = FakeProvider.LeakMarker })),
            (SetupTestReasons.AuthorityUnreachable, discovery, () => throw new HttpRequestException(HttpRequestError.ConnectionError, FakeProvider.LeakMarker)),
            (SetupTestReasons.Tls, discovery, () => throw new HttpRequestException(HttpRequestError.SecureConnectionError, FakeProvider.LeakMarker)),
        };
        foreach (var (reason, path, answer) in cases)
        {
            using var provider = new FakeProvider();
            provider.Overrides[path] = answer;
            using var logs = new CapturingLoggerProvider();
            await using var host = Host(provider, logs);
            using var session = await SessionAsync(host, fixture);
            using var response = await PostAsync(session, "/bff/setup/test", Values());
            var text = await response.Content.ReadAsStringAsync(Ct);
            response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, $"{reason}: {text}");
            (await CodeOf(response)).ShouldBe(SetupErrors.TestRefused);
            (await ReasonOf(response)).ShouldBe(reason, text);
            text.ShouldNotContain(FakeProvider.LeakMarker);
            text.ShouldNotContain("internal.test");
            logs.Text.ShouldNotContain(FakeProvider.LeakMarker);
            provider.Requested.ShouldNotContain(u => u.Host == "internal.test"); // no redirect followed
        }
    }

    [Fact]
    public async Task Only_a_code_holder_can_make_the_admin_api_call_a_provider()
    {
        using var provider = new FakeProvider();
        await using var host = Host(provider);
        // Without the setup session: no test, so no back-channel call; a callback with a made-up marker makes none either.
        using var anonymous = await ClientAsync(host);
        using var refused = await PostAsync(anonymous, "/bff/setup/test", Values());
        refused.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await CodeOf(refused)).ShouldBe(SetupErrors.SessionRequired);
        using (await CallbackAsync(anonymous, SetupTestSignIn.StatePrefix + Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32))))
        {
        }

        provider.Requested.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_provider_that_drips_its_body_past_the_timeout_counts_as_unreachable()
    {
        using var client = new SetupProviderClient(new DrippingHandler(), TimeSpan.FromMilliseconds(300));
        var response = await client.GetAsync(new Uri(FakeProvider.Authority + "/.well-known/openid-configuration"), Ct);
        response.Outcome.ShouldBe(ProviderOutcome.Unreachable);
    }

    [Fact]
    public void The_production_back_channel_follows_no_redirect_keeps_no_cookies_and_has_a_timeout()
    {
        using var handler = SetupProviderClient.CreateHandler();
        handler.AllowAutoRedirect.ShouldBeFalse();
        handler.UseCookies.ShouldBeFalse();
        handler.AutomaticDecompression.ShouldBe(DecompressionMethods.None);
        using var client = SetupProviderClient.Create();
        client.Timeout.ShouldBe(SetupProviderClient.DefaultTimeout);
    }

    [Fact]
    public async Task Outside_development_an_http_authority_is_refused_before_anything_is_fetched_and_in_development_it_is_allowed()
    {
        using var provider = new FakeProvider();
        await using (var host = Host(provider))
        {
            using var session = await SessionAsync(host, fixture);
            using var response = await PostAsync(session, "/bff/setup/test", new { authority = "http://idp.test/realms/ume", clientSecret = TypedSecret });
            response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            (await ReasonOf(response)).ShouldBe(SetupTestReasons.AuthorityInvalid);
            provider.Requested.ShouldBeEmpty();
            using var control = await PostAsync(session, "/bff/setup/test", new { authority = "https://idp.test/realms/ume\u0007" });
            control.StatusCode.ShouldBe(HttpStatusCode.BadRequest); // a control character fails validation
            using var tooLong = await PostAsync(session, "/bff/setup/test", new { authority = "https://idp.test/" + new string('a', 500) });
            tooLong.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            using var query = await PostAsync(session, "/bff/setup/test", new { authority = "https://idp.test/realms/ume?x=1" });
            (await ReasonOf(query)).ShouldBe(SetupTestReasons.AuthorityInvalid);
            provider.Requested.ShouldBeEmpty();
        }

        using var devProvider = new FakeProvider();
        devProvider.Overrides["/realms/ume/.well-known/openid-configuration"] = () => FakeProvider.Json(FakeProvider.Discovery(issuer: "http://idp.test/realms/ume"));
        await using var development = Host(devProvider, environment: "Development");
        using var devSession = await SessionAsync(development, fixture);
        await StartAsync(devSession, devProvider, new { authority = "http://idp.test/realms/ume", clientSecret = TypedSecret });
        devProvider.Requested.ShouldContain(u => u.Scheme == "http");
    }

    [Fact]
    public async Task A_value_the_deployment_sets_wins_over_the_candidate()
    {
        using var provider = new FakeProvider();
        // A declared deployment opens setup only on a re-open; the candidate's authority is ignored.
        await ResetAsync(fixture, completed: true);
        await using var host = Host(provider, settings: [("Oidc:Authority", FakeProvider.Authority), ("Setup:Reopen", "w1")]);
        using var session = await SessionAsync(host, fixture);
        var (_, query, _) = await StartAsync(session, provider, Values(authority: OtherAuthority));
        provider.Requested.ShouldAllBe(u => u.Host == "idp.test");
        query["redirect_uri"].ShouldBe("https://localhost/signin-oidc");
    }

    [Fact]
    public async Task The_stored_secret_goes_only_to_the_provider_and_client_it_was_stored_for()
    {
        using var provider = new FakeProvider();
        await using var host = Host(provider);
        using var session = await SessionAsync(host, fixture);
        await SignInSettingsTests.SaveAsync(fixture.Services, new SignInWizardValues { Authority = FakeProvider.Authority }, "stored-secret-4b1a");

        // No secret typed and another provider or client: refused before anything is fetched, so the stored secret goes nowhere.
        foreach (var values in new[] { Values(clientSecret: null, authority: OtherAuthority), Values(clientSecret: null, clientId: "another-client") })
        {
            using var refused = await PostAsync(session, "/bff/setup/test", values);
            refused.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            (await ReasonOf(refused)).ShouldBe(SetupTestReasons.SecretRequired);
        }

        provider.Requested.ShouldBeEmpty();

        // Another provider as a public client (an empty secret typed) needs no secret, and none is sent.
        (await RunTestAsync(session, provider, values: Values(clientSecret: "", clientId: "public-client"))).ShouldBeNull();
        provider.LastTokenForm!.ShouldNotContainKey("client_secret");
        (await LatestTestAsync(session)).GetProperty("clientSecret").GetString().ShouldBe(SetupSecretSources.Removed);

        // The same provider and client: the stored secret is used.
        (await RunTestAsync(session, provider, values: Values(clientSecret: null))).ShouldBeNull();
        provider.LastTokenForm!["client_secret"].ShouldBe("stored-secret-4b1a");
        (await LatestTestAsync(session)).GetProperty("clientSecret").GetString().ShouldBe(SetupSecretSources.Stored);

        // The stored values change between the test's start and its callback: checked again, nothing is sent.
        var requests = provider.TokenRequests;
        var (_, _, state) = await StartAsync(session, provider, Values(clientSecret: null));
        await SignInSettingsTests.SaveAsync(fixture.Services, new SignInWizardValues { Authority = OtherAuthority }, null);
        using (await CallbackAsync(session, state))
        {
        }

        (await LatestTestAsync(session)).GetProperty("reason").GetString().ShouldBe(SetupTestReasons.SecretRequired);
        provider.TokenRequests.ShouldBe(requests);
    }

    [Fact]
    public async Task A_deployment_secret_goes_only_to_the_deployments_own_authority()
    {
        using var provider = new FakeProvider();
        await using (var secretOnly = Host(provider, settings: ("Oidc:ClientSecret", "deployed-secret")))
        {
            using var session = await SessionAsync(secretOnly, fixture);
            using var refused = await PostAsync(session, "/bff/setup/test", Values());
            refused.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            (await ReasonOf(refused)).ShouldBe(SetupTestReasons.DeploymentSecretWithoutAuthority);
            provider.Requested.ShouldBeEmpty();
        }

        // Both from the deployment (declared, so setup opens only on a re-open): the test uses the deployment's secret.
        await ResetAsync(fixture, completed: true);
        using var deploymentProvider = new FakeProvider();
        await using var declared = Host(deploymentProvider,
            settings: [("Oidc:Authority", FakeProvider.Authority), ("Oidc:ClientSecret", "deployed-secret"), ("Setup:Reopen", "r1")]);
        using var reopened = await SessionAsync(declared, fixture);
        (await RunTestAsync(reopened, deploymentProvider, values: Values())).ShouldBeNull();
        deploymentProvider.LastTokenForm!["client_secret"].ShouldBe("deployed-secret");
        var latest = await LatestTestAsync(reopened);
        latest.GetProperty("clientSecret").GetString().ShouldBe(SetupSecretSources.Deployment);
        var testId = latest.GetProperty("testId").GetGuid();
        using (var completed = await PostAsync(reopened, "/bff/setup/complete", new { authority = FakeProvider.Authority, clientSecret = TypedSecret, testId }))
        {
            completed.StatusCode.ShouldBe(HttpStatusCode.OK, await completed.Content.ReadAsStringAsync(Ct));
        }

        // Neither the deployment's authority nor its secret (nor the typed one) is stored beside it.
        var row = (await RowAsync(fixture))!;
        row.EncryptedClientSecret.ShouldBeNull();
        row.Authority.ShouldBeNull();
    }

    [Fact]
    public async Task A_deployment_that_drops_its_authority_before_the_callback_gets_no_secret_sent()
    {
        using var provider = new FakeProvider();
        await ResetAsync(fixture, completed: true);
        await using var host = Host(provider, settings: [("Oidc:Authority", FakeProvider.Authority), ("Oidc:ClientSecret", "deployed-secret"), ("Setup:Reopen", "r2")]);
        using var session = await SessionAsync(host, fixture);
        var configuration = host.Services.GetRequiredService<IConfiguration>();
        (await RunTestAsync(session, provider, () => configuration["Oidc:Authority"] = "", Values(authority: FakeProvider.Authority)))
            .ShouldBe(SetupTestReasons.DeploymentSecretWithoutAuthority);
        provider.TokenRequests.ShouldBe(0);
    }

    [Fact]
    public async Task The_callback_runs_the_claim_mapping_stores_the_result_once_and_answers_replays_and_forgeries_alike()
    {
        using var provider = new FakeProvider();
        using var logs = new CapturingLoggerProvider();
        await using var host = Host(provider, logs);
        using var session = await SessionAsync(host, fixture);
        var (testId, query, state) = await StartAsync(session, provider);

        using (var done = await CallbackAsync(session, state))
        {
            done.StatusCode.ShouldBe(HttpStatusCode.Redirect, await done.Content.ReadAsStringAsync(Ct));
            done.Headers.Location!.OriginalString.ShouldBe(SetupTestSignIn.DonePath);
            // No admin session: the only cookie touched is the test cookie, deleted.
            done.Headers.GetValues("Set-Cookie").ShouldAllBe(c => c.StartsWith(SetupTestSignIn.TestCookieName + "=;", StringComparison.Ordinal));
        }

        // As the handler's code exchange: the typed secret in the form, and the PKCE verifier of the challenge.
        provider.LastTokenForm!["client_secret"].ShouldBe(TypedSecret);
        provider.LastTokenForm["grant_type"].ShouldBe("authorization_code");
        provider.LastTokenForm["redirect_uri"].ShouldBe("https://localhost/signin-oidc");
        Base64UrlEncoder.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(provider.LastTokenForm["code_verifier"]))).ShouldBe(query["code_challenge"]);

        var latest = await LatestTestAsync(session);
        latest.GetProperty("testId").GetGuid().ShouldBe(testId);
        latest.GetProperty("status").GetString().ShouldBe("passed");
        latest.GetProperty("clientSecret").GetString().ShouldBe(SetupSecretSources.Typed);
        latest.GetProperty("values").GetProperty("authority").GetString().ShouldBe(FakeProvider.Authority);
        var result = latest.GetProperty("result");
        result.GetProperty("subject").GetString().ShouldBe("person-1");
        result.GetProperty("name").GetString().ShouldBe("Anna Andersson");
        result.GetProperty("isGatewayAdmin").GetBoolean().ShouldBeTrue();
        result.GetProperty("roles").EnumerateArray().Select(r => r.GetString()).ShouldContain(AdminRoles.GatewayAdmin);
        result.GetProperty("groups").EnumerateArray().Select(r => r.GetString()).ShouldBe(["g-1", "g-2"]);
        result.GetProperty("departmentCodes").EnumerateArray().Select(r => r.GetString()).ShouldContain("1000");
        result.GetProperty("claimsRead").GetProperty("role").GetString().ShouldBe("roles");

        // Single use: the same callback again, and one with a forged marker, end alike; their unknown_test event is sampled.
        using (var replay = await CallbackAsync(session, state))
        {
            replay.Headers.Location!.OriginalString.ShouldBe(SetupTestSignIn.DonePath);
        }

        using (var forged = await CallbackAsync(session, SetupTestSignIn.StatePrefix + Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32))))
        {
            forged.Headers.Location!.OriginalString.ShouldBe(SetupTestSignIn.DonePath);
        }

        provider.TokenRequests.ShouldBe(1);
        (await LatestTestAsync(session)).GetProperty("status").GetString().ShouldBe("passed");
        var events = Events(logs, 2011);
        events.Count.ShouldBe(2); // the pass, and the first unknown_test; the second waits for the next sampled event
        events[0].Message.ShouldContain("passed");
        events[1].Message.ShouldContain(SetupTestReasons.UnknownTest);

        var row = await DbAsync(fixture, db => db.SetupTests.AsNoTracking().SingleAsync(Ct));
        row.Result!.ShouldNotContain(FakeProvider.LeakMarker); // no token kept
        logs.Text.ShouldNotContain(TypedSecret);
        logs.Text.ShouldNotContain(FakeProvider.LeakMarker);
        logs.Text.ShouldNotContain(provider.Nonce!);
    }

    [Fact]
    public async Task The_admin_role_comes_from_the_candidate_claim_names_and_the_deployments_role_groups()
    {
        using var provider = new FakeProvider();
        await using var host = Host(provider, settings: ("Oidc:RoleGroups:gateway-admin", "GG-Admins"));
        using var session = await SessionAsync(host, fixture);

        // No role in the token's role claim: not a gateway admin, though the test passes.
        provider.Claims = FakeProvider.Person("person-2", AdminRoles.Viewer);
        (await RunTestAsync(session, provider)).ShouldBeNull();
        (await LatestTestAsync(session)).GetProperty("result").GetProperty("isGatewayAdmin").GetBoolean().ShouldBeFalse();

        // A group the deployment maps to gateway-admin, in a group claim the candidate names.
        provider.Claims = new Dictionary<string, object> { ["sub"] = "person-3", ["memberOf"] = new[] { "GG-Admins" } };
        (await RunTestAsync(session, provider, values: new { authority = FakeProvider.Authority, clientSecret = TypedSecret, groupClaim = "memberOf" })).ShouldBeNull();
        var result = (await LatestTestAsync(session)).GetProperty("result");
        result.GetProperty("isGatewayAdmin").GetBoolean().ShouldBeTrue();
        result.GetProperty("groups").EnumerateArray().Select(g => g.GetString()).ShouldBe(["GG-Admins"]);
        result.GetProperty("claimsRead").GetProperty("group").GetString().ShouldBe("memberOf");

        // A role claim of another name.
        provider.Claims = new Dictionary<string, object> { ["sub"] = "person-4", ["app_roles"] = new[] { AdminRoles.GatewayAdmin } };
        (await RunTestAsync(session, provider, values: Values(roleClaim: "app_roles"))).ShouldBeNull();
        (await LatestTestAsync(session)).GetProperty("result").GetProperty("isGatewayAdmin").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task A_callback_from_another_browser_fails_without_a_token_call()
    {
        using var provider = new FakeProvider();
        await using var host = Host(provider);
        using var session = await SessionAsync(host, fixture);
        using var other = await SessionAsync(host, fixture);

        // No test cookie at all.
        var (_, _, state) = await StartAsync(session, provider);
        using var bare = host.CreateHttpsClient();
        using (var done = await CallbackAsync(bare, state))
        {
            done.Headers.Location!.OriginalString.ShouldBe(SetupTestSignIn.DonePath);
        }

        (await LatestTestAsync(session)).GetProperty("reason").GetString().ShouldBe(SetupTestReasons.BrowserMismatch);

        // Another test's cookie: the other browser started its own test, then posts this one's callback.
        var (_, _, mine) = await StartAsync(session, provider);
        await StartAsync(other, provider);
        using (var done = await CallbackAsync(other, mine))
        {
            done.Headers.Location!.OriginalString.ShouldBe(SetupTestSignIn.DonePath);
        }

        (await LatestTestAsync(session)).GetProperty("reason").GetString().ShouldBe(SetupTestReasons.BrowserMismatch);
        provider.TokenRequests.ShouldBe(0);
    }

    [Fact]
    public async Task A_callback_with_a_made_up_marker_does_not_end_the_test_the_browser_is_running()
    {
        // A page that makes the deployer's browser load the callback address with its own state must not cancel the test: the
        // test cookie is deleted only once a test is claimed.
        using var provider = new FakeProvider();
        await using var host = Host(provider);
        using var session = await SessionAsync(host, fixture);
        var (_, _, state) = await StartAsync(session, provider);
        using (var forged = await CallbackAsync(session, SetupTestSignIn.StatePrefix + new string('x', 43)))
        {
            forged.Headers.Location!.OriginalString.ShouldBe(SetupTestSignIn.DonePath);
        }

        using (var real = await CallbackAsync(session, state))
        {
            real.Headers.Location!.OriginalString.ShouldBe(SetupTestSignIn.DonePath);
        }

        (await LatestTestAsync(session)).GetProperty("status").GetString().ShouldBe("passed");
    }

    [Fact]
    public async Task Setup_callbacks_are_limited_per_address_while_setup_is_open_and_answer_404_once_it_is_closed()
    {
        // Anyone can send a callback with a setup test's state; each one that reaches the lookup costs a statement.
        using var provider = new FakeProvider();
        var forged = SetupTestSignIn.StatePrefix + new string('x', 43);
        await using (var host = Host(provider))
        {
            using var session = await SessionAsync(host, fixture);
            using var stranger = host.CreateHttpsClient();
            for (var i = 0; i < SetupCallbackThrottle.Limit; i++)
            {
                using var answered = await CallbackAsync(stranger, forged);
                answered.Headers.Location!.OriginalString.ShouldBe(SetupTestSignIn.DonePath);
            }

            using var limited = await CallbackAsync(stranger, forged);
            limited.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
            limited.Headers.RetryAfter.ShouldNotBeNull();
        }

        // Closed (every deployment after its setup): 404 from the values last read, whatever the marker.
        await ResetAsync(fixture, completed: true);
        await using var closed = Host(provider);
        using var client = closed.CreateHttpsClient();
        for (var i = 0; i < SetupCallbackThrottle.Limit + 5; i++)
        {
            using var answered = await CallbackAsync(client, forged);
            answered.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        }
    }

    [Fact]
    public void The_callback_limit_counts_each_address_on_its_own_in_one_minute_windows()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var throttle = new SetupCallbackThrottle(time);
        for (var i = 0; i < SetupCallbackThrottle.Limit; i++)
        {
            throttle.TryAcquire("198.51.100.1", out _).ShouldBeTrue();
        }

        throttle.TryAcquire("198.51.100.1", out var retryAfter).ShouldBeFalse();
        retryAfter.ShouldBe(SetupCallbackThrottle.Window);
        // A flood from one address does not keep another one's callback out; no address counts as one.
        throttle.TryAcquire("203.0.113.9", out _).ShouldBeTrue();
        throttle.TryAcquire(null, out _).ShouldBeTrue();
        time.Advance(SetupCallbackThrottle.Window);
        throttle.TryAcquire("198.51.100.1", out _).ShouldBeTrue();
    }

    [Fact]
    public async Task Completing_asks_for_a_new_test_when_the_claim_mapping_changed_since_the_test()
    {
        using var provider = new FakeProvider();
        await using var host = Host(provider);
        using var session = await SessionAsync(host, fixture);
        provider.Claims = FakeProvider.Person("person-1", AdminRoles.GatewayAdmin);
        var (testId, _, state) = await StartAsync(session, provider);
        using (await CallbackAsync(session, state))
        {
        }

        (await LatestTestAsync(session)).GetProperty("result").GetProperty("isGatewayAdmin").GetBoolean().ShouldBeTrue();

        // The deployment now names another role claim (a rolling change): the passed test read the old one.
        var configuration = host.Services.GetRequiredService<IConfiguration>();
        configuration["Oidc:RoleClaim"] = "app_roles";
        using (var refused = await PostAsync(session, "/bff/setup/complete", new { authority = FakeProvider.Authority, testId }))
        {
            refused.StatusCode.ShouldBe(HttpStatusCode.Conflict, await refused.Content.ReadAsStringAsync(Ct));
            (await ReasonOf(refused)).ShouldBe("values_changed");
        }

        configuration["Oidc:RoleClaim"] = null;
        using var completed = await PostAsync(session, "/bff/setup/complete", new { authority = FakeProvider.Authority, testId });
        completed.StatusCode.ShouldBe(HttpStatusCode.OK, await completed.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public void The_tests_fingerprint_covers_the_claim_names_and_the_role_groups()
    {
        var values = new SignInWizardValues { Authority = FakeProvider.Authority };
        OidcOptions Mapping(string roleClaim = "roles", params (string Role, string Group)[] groups) =>
            new() { RoleClaim = roleClaim, RoleGroups = groups.ToDictionary(g => g.Role, g => g.Group) };
        var same = SetupTestSignIn.Fingerprint(values, Mapping(groups: [("gateway-admin", "GG-Admins"), ("viewer", "GG-All")]));
        SetupTestSignIn.Fingerprint(values, Mapping(groups: [("viewer", "GG-All"), ("gateway-admin", "GG-Admins")])).ShouldBe(same);
        SetupTestSignIn.Fingerprint(values, Mapping(groups: [("gateway-admin", "GG-Everyone"), ("viewer", "GG-All")])).ShouldNotBe(same);
        SetupTestSignIn.Fingerprint(values, Mapping("app_roles", ("gateway-admin", "GG-Admins"), ("viewer", "GG-All"))).ShouldNotBe(same);
        SetupTestSignIn.Fingerprint(new SignInWizardValues { Authority = OtherAuthority }, Mapping(groups: [("gateway-admin", "GG-Admins"), ("viewer", "GG-All")])).ShouldNotBe(same);
    }

    [Fact]
    public async Task Two_callbacks_with_one_marker_at_once_make_one_token_call_and_an_expired_test_none()
    {
        using var provider = new FakeProvider();
        await using var host = Host(provider);
        using var session = await SessionAsync(host, fixture);
        var (_, _, state) = await StartAsync(session, provider);
        var both = await Task.WhenAll(CallbackAsync(session, state), CallbackAsync(session, state));
        foreach (var response in both)
        {
            response.Headers.Location!.OriginalString.ShouldBe(SetupTestSignIn.DonePath);
            response.Dispose();
        }

        // Only one claims the test; the other is an unknown test, whether or not it still carried the cookie.
        provider.TokenRequests.ShouldBe(1);
        (await LatestTestAsync(session)).GetProperty("status").GetString().ShouldBe("passed");

        // An expired test: the callback claims nothing.
        var requests = provider.TokenRequests;
        var (testId, _, expired) = await StartAsync(session, provider);
        await DbAsync(fixture, db => db.SetupTests.Where(t => t.Id == testId).ExecuteUpdateAsync(s => s.SetProperty(t => t.ExpiresAt, DateTimeOffset.UtcNow.AddMinutes(-1)), Ct));
        using (var done = await CallbackAsync(session, expired))
        {
            done.Headers.Location!.OriginalString.ShouldBe(SetupTestSignIn.DonePath);
        }

        provider.TokenRequests.ShouldBe(requests);
        (await LatestTestAsync(session)).GetProperty("status").GetString().ShouldBe("incomplete");
    }

    [Fact]
    public async Task A_get_callback_with_the_query_works_too()
    {
        using var provider = new FakeProvider();
        await using var host = Host(provider);
        using var session = await SessionAsync(host, fixture);
        var (_, _, state) = await StartAsync(session, provider);
        using var done = await session.GetAsync("/signin-oidc?code=c1&state=" + Uri.EscapeDataString(state), Ct);
        done.Headers.Location!.OriginalString.ShouldBe(SetupTestSignIn.DonePath);
        (await LatestTestAsync(session)).GetProperty("status").GetString().ShouldBe("passed");
    }

    [Fact]
    public async Task A_failed_callback_says_which_step_failed_and_nothing_of_the_providers_answer()
    {
        using var provider = new FakeProvider();
        await using var host = Host(provider);
        using var session = await SessionAsync(host, fixture);

        var (_, _, state) = await StartAsync(session, provider);
        using (var refused = await CallbackAsync(session, state, code: null, error: "access_denied"))
        {
            refused.Headers.Location!.OriginalString.ShouldBe(SetupTestSignIn.DonePath);
        }

        (await LatestTestAsync(session)).GetProperty("reason").GetString().ShouldBe(SetupTestReasons.ProviderError);
        (await RunTestAsync(session, provider, () => provider.Overrides["/realms/ume/token"] = () =>
            FakeProvider.Json(new { error = "invalid_client", error_description = FakeProvider.LeakMarker }, HttpStatusCode.Unauthorized))).ShouldBe(SetupTestReasons.TokenRefused);
        (await session.GetStringAsync("/bff/setup/state", Ct)).ShouldNotContain(FakeProvider.LeakMarker);
        provider.Overrides.Clear();
        (await RunTestAsync(session, provider, () => provider.Overrides["/realms/ume/token"] = () => FakeProvider.Json(new { access_token = "x" }))).ShouldBe(SetupTestReasons.IdTokenMissing);
        provider.Overrides.Clear();
        (await RunTestAsync(session, provider, () => provider.Overrides["/realms/ume/token"] = () => throw new HttpRequestException(FakeProvider.LeakMarker)))
            .ShouldBe(SetupTestReasons.TokenEndpointUnreachable);
        provider.Overrides.Clear();
        (await RunTestAsync(session, provider, () => provider.Nonce = "another-nonce")).ShouldBe(SetupTestReasons.NonceMismatch);
        (await LatestTestAsync(session)).GetProperty("result").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task Id_tokens_that_are_unsigned_signed_with_the_secret_for_another_audience_or_issuer_or_expired_are_refused()
    {
        using var provider = new FakeProvider();
        await using var host = Host(provider);
        using var session = await SessionAsync(host, fixture);
        object Payload(Dictionary<string, string> form) => new Dictionary<string, object>
        {
            ["iss"] = FakeProvider.Authority, ["aud"] = form["client_id"], ["sub"] = "person-1", ["nonce"] = provider.Nonce!, ["roles"] = new[] { AdminRoles.GatewayAdmin },
            ["iat"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), ["exp"] = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds(),
        };

        (await RunTestAsync(session, provider, () => provider.IdTokenFactory = form => FakeProvider.Unsigned(Payload(form)))).ShouldBe(SetupTestReasons.IdTokenInvalid);
        (await RunTestAsync(session, provider, () => provider.IdTokenFactory = form => new JsonWebTokenHandler().CreateToken(JsonSerializer.Serialize(Payload(form)),
            new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(TypedSecret)), SecurityAlgorithms.HmacSha256)))).ShouldBe(SetupTestReasons.IdTokenInvalid);
        provider.IdTokenFactory = null;
        (await RunTestAsync(session, provider, () => provider.Audience = "another-client")).ShouldBe(SetupTestReasons.IdTokenInvalid);
        provider.Audience = null;
        (await RunTestAsync(session, provider, () => provider.Issuer = "https://other.test/realms/ume")).ShouldBe(SetupTestReasons.IdTokenInvalid);
        provider.Issuer = FakeProvider.Authority;
        (await RunTestAsync(session, provider, () => provider.ExpiresIn = TimeSpan.FromMinutes(-10))).ShouldBe(SetupTestReasons.IdTokenInvalid);
        provider.ExpiresIn = TimeSpan.FromMinutes(5);
        (await RunTestAsync(session, provider)).ShouldBeNull(); // the control: the provider's own signed token passes
    }

    [Fact]
    public async Task A_real_sign_in_callback_passes_the_setup_middleware_untouched()
    {
        using var provider = new FakeProvider();
        await using var host = Host(provider);
        using var browser = host.CreateHttpsClient();
        using var callback = await browser.PostAsync("/signin-oidc", new FormUrlEncodedContent(new Dictionary<string, string> { ["code"] = "c", ["state"] = "handler-state" }), Ct);
        callback.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        callback.Headers.Location!.OriginalString.ShouldBe("/?signin=failed"); // the handler's own answer, not setup's
        provider.Requested.ShouldBeEmpty();
    }

    [Fact]
    public async Task Complete_saves_only_the_sessions_latest_passed_test_of_a_gateway_admin_with_the_same_values()
    {
        using var provider = new FakeProvider();
        using var logs = new CapturingLoggerProvider();
        await using var host = Host(provider, logs);
        using var session = await SessionAsync(host, fixture);
        using var other = await SessionAsync(host, fixture);

        async Task<Guid> PassedTestAsync(HttpClient client, params string[] roles)
        {
            provider.Claims = FakeProvider.Person("person-" + roles.Length, roles);
            var (testId, _, state) = await StartAsync(client, provider);
            using var done = await CallbackAsync(client, state);
            (await LatestTestAsync(client)).GetProperty("status").GetString().ShouldBe("passed");
            return testId;
        }

        async Task<HttpResponseMessage> CompleteAsync(HttpClient client, Guid testId, string? roleClaim = null, string? expiresOn = null) =>
            await PostAsync(client, "/bff/setup/complete", new { authority = FakeProvider.Authority, roleClaim, clientSecretExpiresOn = expiresOn, testId });

        var viewer = await PassedTestAsync(session, AdminRoles.Viewer);
        using (var refused = await CompleteAsync(session, viewer))
        {
            refused.StatusCode.ShouldBe(HttpStatusCode.Conflict);
            (await CodeOf(refused)).ShouldBe(SetupErrors.TestRequired);
            (await ReasonOf(refused)).ShouldBe("not_gateway_admin");
        }

        var admin = await PassedTestAsync(session, AdminRoles.GatewayAdmin);
        (await ReasonOf(await CompleteAsync(session, admin, roleClaim: "other_roles"))).ShouldBe("values_changed");
        (await ReasonOf(await CompleteAsync(session, admin, expiresOn: "2027-01-31"))).ShouldBe("values_changed");
        (await ReasonOf(await CompleteAsync(session, viewer))).ShouldBe("not_latest");
        var othersTest = await PassedTestAsync(other, AdminRoles.GatewayAdmin);
        (await ReasonOf(await CompleteAsync(session, othersTest))).ShouldBe("unknown_test");

        // A newer test that is still pending makes the passed one not the latest; a failed latest one is not passed.
        var pending = await StartAsync(session, provider);
        (await ReasonOf(await CompleteAsync(session, admin))).ShouldBe("not_latest");
        using (await CallbackAsync(session, pending.State, code: null, error: "access_denied"))
        {
        }

        (await ReasonOf(await CompleteAsync(session, pending.TestId))).ShouldBe("not_passed");
        var latestAdmin = await PassedTestAsync(session, AdminRoles.GatewayAdmin);
        (await RowAsync(fixture))!.Authority.ShouldBeNull(); // nothing saved by any refusal

        // Two completes at once: one saves, the other finds setup closed.
        var both = await Task.WhenAll(CompleteAsync(session, latestAdmin), CompleteAsync(session, latestAdmin));
        both.Select(r => r.StatusCode).ShouldBe([HttpStatusCode.OK, HttpStatusCode.NotFound], ignoreOrder: true);
        var completed = both.Single(r => r.StatusCode == HttpStatusCode.OK);
        JsonDocument.Parse(await completed.Content.ReadAsStringAsync(Ct)).RootElement.GetProperty("signInUrl").GetString().ShouldBe("/bff/login");
        completed.Headers.GetValues("Set-Cookie").ShouldContain(c => c.StartsWith(SetupSessions.CookieName + "=;", StringComparison.Ordinal));
        foreach (var response in both)
        {
            response.Dispose();
        }

        var row = (await RowAsync(fixture))!;
        row.SetupCompletedAt.ShouldNotBeNull();
        row.SetupNonce.ShouldBeNull();
        row.Authority.ShouldBe(FakeProvider.Authority);
        row.EncryptedClientSecret.ShouldNotBeNullOrEmpty();
        row.EncryptedClientSecret.ShouldNotContain(TypedSecret);
        (await DbAsync(fixture, db => db.SetupSessions.CountAsync(Ct))).ShouldBe(0);
        (await DbAsync(fixture, db => db.SetupTests.CountAsync(Ct))).ShouldBe(0);
        var audit = await DbAsync(fixture, db => db.AuditLog.AsNoTracking()
            .Where(a => a.EntityType == AuditEntities.Settings && a.EntityId == SignInSettingsService.AuditEntityId).OrderByDescending(a => a.Id).FirstAsync(Ct));
        audit.Actor.ShouldBe("person-1");
        audit.ActorName.ShouldBe("Anna Andersson");
        audit.ActorRoles.ShouldBe(AdminRoles.GatewayAdmin);
        audit.SessionId.ShouldNotBeNull().ShouldStartWith("setup-");
        audit.Details.ShouldNotBeNull().ShouldNotContain(TypedSecret);
        audit.Details.ShouldNotContain(row.EncryptedClientSecret!);

        Events(logs, 2012).Single().Message.ShouldContain("person-1");
        logs.Text.ShouldNotContain(TypedSecret);

        // Closed: every setup route answers 404; the admin API's sign-in now has the saved values.
        (await session.GetAsync("/bff/setup/state", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await CompleteAsync(session, latestAdmin)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        using var user = JsonDocument.Parse(await session.GetStringAsync("/bff/user", Ct));
        user.RootElement.GetProperty("setup").GetProperty("open").GetBoolean().ShouldBeFalse();
        user.RootElement.GetProperty("signIn").GetProperty("configured").GetBoolean().ShouldBeTrue();
    }
}
