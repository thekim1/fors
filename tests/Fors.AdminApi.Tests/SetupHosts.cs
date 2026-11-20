using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Fors.AdminApi.Setup;
using Fors.Domain.Entities;
using Fors.Infrastructure.Persistence;
using Fors.Infrastructure.Security;
using Fors.TestKit;

namespace Fors.AdminApi.Tests;

/// <summary>What a host wrote as its setup code output (<see cref="SetupConsole"/>), instead of the process's standard output.</summary>
public sealed class SetupOutput
{
    private readonly StringWriter _inner = new();

    public SetupOutput() => Writer = TextWriter.Synchronized(_inner);

    public TextWriter Writer { get; }

    public string Text
    {
        get
        {
            lock (Writer)
            {
                return _inner.ToString();
            }
        }
    }
}

/// <summary>Hosts and helpers for the setup mode tests (thekim1/fors#100).</summary>
internal static class SetupHosts
{
    /// <summary>The setup secret the test hosts share, as a deployment's generated one (64 hex characters).</summary>
    public static readonly string Secret = TestSecrets.RandomHex();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// An admin API on the fixture's database without a deployment authority or client secret (so the deployment does not declare
    /// the sign-in), with <see cref="Secret"/>, <paramref name="settings"/> after them, and the given output, log capture, clock and
    /// back channel to providers.
    /// </summary>
    public static WebApplicationFactory<Program> Host(AdminFixture fixture, SetupOutput? output = null, CapturingLoggerProvider? logs = null,
        TimeProvider? time = null, string? environment = null, HttpMessageHandler? provider = null, params (string Key, string Value)[] settings) =>
        fixture.Derive(web =>
        {
            if (environment is not null)
            {
                web.UseEnvironment(environment);
            }

            web.UseSetting("Oidc:Authority", "");
            web.UseSetting("Oidc:ClientSecret", "");
            web.UseSetting("Setup:Secret", Secret);
            foreach (var (key, value) in settings)
            {
                web.UseSetting(key, value);
            }

            web.ConfigureTestServices(services =>
            {
                services.AddSingleton(new SetupConsole((output ?? new SetupOutput()).Writer));
                if (logs is not null)
                {
                    services.AddLogging(b => b.AddProvider(logs).SetMinimumLevel(LogLevel.Trace));
                }

                if (time is not null)
                {
                    services.AddSingleton(time);
                }

                if (provider is not null)
                {
                    services.AddSingleton(new SetupProviderClient(provider));
                }
            });
        });

    /// <summary>Runs <paramref name="action"/> on a context of the fixture's database (the owner's rights).</summary>
    public static async Task<T> DbAsync<T>(AdminFixture fixture, Func<GatewayDbContext, Task<T>> action)
    {
        await using var scope = fixture.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<GatewayDbContext>());
    }

    /// <summary>
    /// Puts setup back to a known state: never completed (a first run) or completed and closed, no opening, no sessions or tests, no
    /// re-open consumed, and no stored sign-in values. A missing row is created as the migration creates it.
    /// </summary>
    public static async Task ResetAsync(AdminFixture fixture, bool completed)
    {
        await DbAsync(fixture, async db =>
        {
            await db.SetupSessions.ExecuteDeleteAsync(Ct);
            if (!await db.SignInSettings.AnyAsync(Ct))
            {
                db.SignInSettings.Add(new SignInSettings());
                await db.SaveChangesAsync(Ct);
            }

            return 0;
        });
        await SignInSettingsTests.ClearAsync(fixture.Services);
        var now = DateTimeOffset.UtcNow;
        await DbAsync(fixture, db => db.SignInSettings.ExecuteUpdateAsync(s => s
            .SetProperty(x => x.SetupCompletedAt, completed ? now : null)
            .SetProperty(x => x.SetupNonce, (string?)null)
            .SetProperty(x => x.SetupOpenedAt, (DateTimeOffset?)null)
            .SetProperty(x => x.SetupReopenConsumed, (string?)null), Ct));
    }

    public static Task<SignInSettings?> RowAsync(AdminFixture fixture) => DbAsync(fixture, db => db.SignInSettings.AsNoTracking().SingleOrDefaultAsync(Ct));

    /// <summary>The current opening's code, from the row's nonce.</summary>
    public static async Task<string> CodeAsync(AdminFixture fixture) =>
        SetupCode.Compute(Secret, (await RowAsync(fixture))?.SetupNonce ?? throw new InvalidOperationException("Setup is not open."));

    /// <summary>A browser-like client: https, cookies kept, no redirects followed, holding the XSRF token from <c>/bff/user</c>.</summary>
    public static async Task<HttpClient> ClientAsync(WebApplicationFactory<Program> factory) => await factory.CreateHttpsClient().WithXsrfTokenAsync();

    public static Task<HttpResponseMessage> PostAsync(HttpClient client, string path, object body) => client.PostAsJsonAsync(path, body, Ct);

    /// <summary>A client with a setup session for the current opening's code.</summary>
    public static async Task<HttpClient> SessionAsync(WebApplicationFactory<Program> factory, AdminFixture fixture)
    {
        var client = await ClientAsync(factory);
        using var response = await PostAsync(client, "/bff/setup/session", new { code = await CodeAsync(fixture) });
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return client;
    }

    /// <summary><c>setup.open</c> of <c>/bff/user</c>.</summary>
    public static async Task<bool> SetupOpenAsync(HttpClient client)
    {
        using var json = JsonDocument.Parse(await client.GetStringAsync("/bff/user", Ct));
        return json.RootElement.GetProperty("setup").GetProperty("open").GetBoolean();
    }

    /// <summary>The <c>code</c> member of a problem response.</summary>
    public static async Task<string?> CodeOf(HttpResponseMessage response)
    {
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        return json.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null;
    }

    /// <summary>The <c>reason</c> member of a problem response.</summary>
    public static async Task<string?> ReasonOf(HttpResponseMessage response)
    {
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        return json.RootElement.TryGetProperty("reason", out var reason) ? reason.GetString() : null;
    }

    /// <summary>The security events of <paramref name="logs"/> with <paramref name="id"/>.</summary>
    public static IReadOnlyList<LogEntry> Events(CapturingLoggerProvider logs, int id) =>
        [.. logs.Entries.Where(e => e.Category == SecurityEvents.Category && e.EventId.Id == id)];

    /// <summary>Every form of <paramref name="code"/> that could leak: grouped, plain, lower case.</summary>
    public static IEnumerable<string> Forms(string code) => [code, code.Replace("-", "", StringComparison.Ordinal), code.ToLowerInvariant()];
}
