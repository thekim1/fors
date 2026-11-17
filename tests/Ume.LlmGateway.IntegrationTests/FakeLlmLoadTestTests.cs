extern alias Fake;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Ume.LlmGateway.IntegrationTests;

/// <summary>
/// What the fake provider offers load tests (benchmarks/knowledge-load): the prompt sizes it received, per model, and
/// answers that don't quote the prompt (FakeLlm:EchoPrompt=false).
/// </summary>
public sealed class FakeLlmLoadTestTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static WebApplicationFactory<Fake::Program> Fake(bool? echo = null) =>
        new WebApplicationFactory<Fake::Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Testing");
            if (echo is not null)
            {
                b.UseSetting("FakeLlm:EchoPrompt", echo.Value ? "true" : "false");
            }
        });

    private static Task<HttpResponseMessage> ChatAsync(HttpClient client, string model, string content) =>
        client.PostAsJsonAsync("/v1/chat/completions",
            new JsonObject { ["model"] = model, ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = content }) }, Ct);

    [Fact]
    public async Task Stats_count_chat_prompts_and_embedding_calls_per_model_until_reset()
    {
        await using var fake = Fake();
        using var client = fake.CreateClient();
        (await ChatAsync(client, "fake-chat", "kort")).EnsureSuccessStatusCode();
        (await ChatAsync(client, "fake-chat", string.Join(' ', Enumerable.Repeat("ord", 400)))).EnsureSuccessStatusCode();
        (await client.PostAsJsonAsync("/v1/embeddings", new JsonObject { ["model"] = "fake-embed", ["input"] = "hej" }, Ct)).EnsureSuccessStatusCode();

        var stats = (await client.GetFromJsonAsync<JsonArray>("/stats", Ct))!;
        var chat = stats.Single(s => (string)s!["kind"]! == "chat")!;
        chat["model"]!.GetValue<string>().ShouldBe("fake-chat");
        chat["calls"]!.GetValue<int>().ShouldBe(2);
        chat["tokens_max"]!.GetValue<long>().ShouldBeGreaterThan(chat["tokens_p50"]!.GetValue<long>() / 2);
        stats.Single(s => (string)s!["kind"]! == "embeddings")!["calls"]!.GetValue<int>().ShouldBe(1);

        (await client.DeleteAsync("/stats", Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await client.GetFromJsonAsync<JsonArray>("/stats", Ct))!.Count.ShouldBe(0);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData(false, false)]
    public async Task Answers_quote_the_prompt_unless_told_not_to(bool? echo, bool quoted)
    {
        await using var fake = Fake(echo);
        using var client = fake.CreateClient();

        var answer = await (await ChatAsync(client, "fake-chat", "<sources><source n=\"1\">VPN</source></sources> Hur gör jag?"))
            .Content.ReadFromJsonAsync<JsonObject>(Ct);

        var text = answer!["choices"]![0]!["message"]!["content"]!.GetValue<string>();
        text.ShouldStartWith("Hej!");
        text.Contains("<sources>", StringComparison.Ordinal).ShouldBe(quoted);
    }
}
