using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;

namespace Fors.Gateway.Tests;

/// <summary>
/// The names from before the rename to Fors (0.11 and earlier), still accepted until 0.13 (docs/upgrade-guide.md): keys
/// <c>ume-sk-…</c>, the request headers <c>x-ume-*</c>, every response header also as <c>x-ume-*</c>,
/// <c>/v1/ume/context</c>, the model facts under <c>ume</c> and the stream cost comment <c>: ume-cost-usd=…</c>.
/// </summary>
public sealed class LegacyNameTests(GatewayFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_key_from_before_the_rename_authenticates_and_the_answer_carries_both_header_names()
    {
        var key = await fixture.CreateKeyAsync(legacyPrefix: true);
        key.Secret.ShouldStartWith("ume-sk-");

        using var response = await fixture.SendAsync(key);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        foreach (var name in new[] { "residency", "provider", "model", "fallbacks" })
        {
            response.Headers.GetValues("x-ume-" + name).Single().ShouldBe(response.Headers.GetValues("x-fors-" + name).Single());
        }
    }

    [Fact]
    public async Task A_request_header_is_read_under_its_old_name_and_the_new_name_wins()
    {
        var route = await fixture.CreateRouteAsync("eu/ok", "onprem/ok");
        var key = await fixture.CreateKeyAsync();

        using (var legacy = await fixture.SendAsync(key, route, headers: new Dictionary<string, string> { ["x-ume-max-residency"] = "OnPrem" }))
        {
            legacy.StatusCode.ShouldBe(HttpStatusCode.OK);
            legacy.Headers.GetValues("x-fors-residency").Single().ShouldBe("OnPrem");
        }

        using var both = await fixture.SendAsync(key, route, headers: new Dictionary<string, string>
        {
            ["x-fors-max-residency"] = "Eu",
            ["x-ume-max-residency"] = "OnPrem",
        });
        both.StatusCode.ShouldBe(HttpStatusCode.OK);
        both.Headers.GetValues("x-fors-residency").Single().ShouldBe("Eu");
    }

    [Fact]
    public async Task The_old_context_path_answers_like_the_new_one()
    {
        var key = await fixture.CreateKeyAsync();

        using var current = await GetAsync(key, "/v1/fors/context");
        using var legacy = await GetAsync(key, "/v1/ume/context");

        current.StatusCode.ShouldBe(HttpStatusCode.OK);
        legacy.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = JsonNode.Parse(await legacy.Content.ReadAsStringAsync(Ct))!;
        body["key"]!["id"]!.GetValue<Guid>().ShouldBe(key.Id);
        JsonNode.DeepEquals(body, JsonNode.Parse(await current.Content.ReadAsStringAsync(Ct))).ShouldBeTrue();
    }

    [Fact]
    public async Task The_model_facts_are_listed_under_both_names()
    {
        var key = await fixture.CreateKeyAsync();

        using var response = await GetAsync(key, "/v1/models");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var data = (JsonArray)JsonNode.Parse(await response.Content.ReadAsStringAsync(Ct))!["data"]!;
        data.ShouldNotBeEmpty();
        foreach (var model in data)
        {
            model!["fors"].ShouldNotBeNull();
            JsonNode.DeepEquals(model["fors"], model["ume"]).ShouldBeTrue();
        }
    }

    [Fact]
    public async Task The_old_stream_cost_header_gets_the_comment_under_its_old_name()
    {
        var key = await fixture.CreateKeyAsync();

        using var response = await fixture.SendAsync(key, stream: true, includeUsage: true, headers: new Dictionary<string, string> { ["x-ume-stream-cost"] = "1" });

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var text = await response.Content.ReadAsStringAsync(Ct);
        text.ShouldContain(": ume-cost-usd=");
        text.ShouldNotContain("fors-cost-usd");
    }

    private async Task<HttpResponseMessage> GetAsync(TestKey key, string path)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key.Secret);
        return await fixture.Client.SendAsync(request, Ct);
    }
}
