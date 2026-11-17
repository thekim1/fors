using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ume.LlmGateway.Benchmarks;

/// <summary>
/// The load of benchmarks/knowledge-load: <c>VUS</c> support staff chat at once, each asking a question, reading the whole
/// streamed answer and asking the next at once (a stress test: no time to read). Every client is started within the ramp,
/// then the hold window counts: requests started in it are measured to the end of their stream. The client is also the
/// probe, because k6 cannot read a stream as it arrives: time to the response headers, to the first token, the gaps
/// between tokens and the whole stream, per request.
/// <code>knowledge-load &lt;eneo|rag&gt; &lt;base url&gt; &lt;vus&gt; &lt;ramp s&gt; &lt;hold s&gt; &lt;questions.json&gt; &lt;out.json&gt;</code>
/// Environment: <c>LOAD_KEY</c> (eneo: service API key; rag: API key), <c>LOAD_ASSISTANT</c> (eneo: assistant id; rag:
/// the assistant's model name), <c>FOLLOWUP_SHARE</c> (default 0.3), <c>LOAD_SEED</c> (default 1), <c>RAG_PATH</c>
/// (default v1/chat/completions).
/// </summary>
public static class KnowledgeLoad
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromMinutes(5);

    public static async Task<int> RunAsync(string target, string baseUrl, int vus, int rampSeconds, int holdSeconds, string questionsPath, string outPath)
    {
        if (target is not ("eneo" or "rag"))
        {
            Console.Error.WriteLine("target must be eneo or rag");
            return 2;
        }

        var key = Environment.GetEnvironmentVariable("LOAD_KEY") ?? "";
        var assistant = Environment.GetEnvironmentVariable("LOAD_ASSISTANT") ?? "";
        var followUpShare = double.TryParse(Environment.GetEnvironmentVariable("FOLLOWUP_SHARE"), System.Globalization.CultureInfo.InvariantCulture, out var f) ? f : 0.3;
        var seed = int.TryParse(Environment.GetEnvironmentVariable("LOAD_SEED"), out var s) ? s : 1;
        var ragPath = Environment.GetEnvironmentVariable("RAG_PATH") ?? "v1/chat/completions";

        var set = JsonNode.Parse(await File.ReadAllTextAsync(questionsPath))!;
        var questions = set["questions"]!.AsArray().Select(q => q!.GetValue<string>()).ToArray();
        var followUps = set["followUps"]!.AsArray().Select(q => q!.GetValue<string>()).ToArray();

        using var http = new HttpClient(new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            MaxConnectionsPerServer = int.MaxValue,
        })
        {
            BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/"),
            Timeout = RequestTimeout,
        };

        var samples = new List<Sample>();
        var gate = new Lock();
        var start = Stopwatch.GetTimestamp();
        double Now() => Stopwatch.GetElapsedTime(start).TotalSeconds;
        var holdStart = (double)rampSeconds;
        var holdEnd = holdStart + holdSeconds;

        await Task.WhenAll(Enumerable.Range(0, vus).Select(vu => Task.Run(async () =>
        {
            // Clients start evenly over the ramp, so 60 first questions don't arrive in the same millisecond.
            await Task.Delay(TimeSpan.FromSeconds(rampSeconds * (double)vu / Math.Max(1, vus)));
            var random = new Random(seed * 1000 + vu);
            var conversation = new Conversation();
            while (Now() < holdEnd)
            {
                var followUp = conversation.CanContinue && random.NextDouble() < followUpShare;
                if (!followUp)
                {
                    conversation = new Conversation();
                }

                var question = followUp ? followUps[random.Next(followUps.Length)] : questions[random.Next(questions.Length)];
                var startedAt = Now();
                var sample = target == "eneo"
                    ? await EneoAsync(http, key, assistant, conversation, question)
                    : await RagAsync(http, ragPath, key, assistant, conversation, question);
                sample = sample with { StartedAt = startedAt, FollowUp = followUp, InHold = startedAt >= holdStart && startedAt < holdEnd };
                lock (gate)
                {
                    samples.Add(sample);
                }
            }
        })));

        var summary = Summarize(target, vus, rampSeconds, holdSeconds, followUpShare, samples, Now());
        await File.WriteAllTextAsync(outPath, summary.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"{target}: {summary["hold"]!["requests"]} requests in the hold, {summary["hold"]!["errors"]} errors, " +
            $"TTFT p50 {summary["hold"]!["ttft_ms"]!["p50"]} ms p95 {summary["hold"]!["ttft_ms"]!["p95"]} ms, " +
            $"stream p95 {summary["hold"]!["stream_ms"]!["p95"]} ms");
        return 0;
    }

    /// <summary>The conversation a client is in: Eneo continues its session, the RAG platform gets the history.</summary>
    private sealed class Conversation
    {
        public string? SessionId { get; set; }

        public List<(string Role, string Content)> History { get; } = [];

        public bool CanContinue => SessionId is not null || History.Count > 0;
    }

    /// <summary>
    /// POST /conversations/?version=2 with the service key: SSE events first_chunk (session id), text (one per word, with
    /// the answer piece), token_usage; the stream ends when the connection closes. An error event fails the request.
    /// </summary>
    private static async Task<Sample> EneoAsync(HttpClient http, string key, string assistantId, Conversation conversation, string question)
    {
        var body = new JsonObject { ["question"] = question, ["stream"] = true, ["files"] = new JsonArray() };
        if (conversation.SessionId is { } session)
        {
            body["session_id"] = session;
        }
        else
        {
            body["assistant_id"] = assistantId;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, "conversations/?version=2")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("X-API-Key", key);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        return await StreamAsync(http, request, (eventName, data, sample) =>
        {
            switch (eventName)
            {
                case "first_chunk":
                    conversation.SessionId = data["session_id"]?.GetValue<string>();
                    return null;
                case "text":
                    return data["answer"]?.GetValue<string>();
                case "error":
                    sample.Error = "error event: " + (data["error"]?.ToString() ?? data.ToJsonString());
                    return null;
                default:
                    return null;
            }
        }, ended: _ => true);
    }

    /// <summary>
    /// The RAG platform's OpenAI-shaped chat API: the assistant is the model; follow-ups send the history. The stream ends
    /// with <c>data: [DONE]</c>; without it the stream counts as cut off.
    /// </summary>
    private static async Task<Sample> RagAsync(HttpClient http, string path, string key, string model, Conversation conversation, string question)
    {
        var messages = new JsonArray();
        foreach (var (role, content) in conversation.History)
        {
            messages.Add(new JsonObject { ["role"] = role, ["content"] = content });
        }

        messages.Add(new JsonObject { ["role"] = "user", ["content"] = question });
        var body = new JsonObject { ["model"] = model, ["messages"] = messages, ["stream"] = true };
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        var answer = new StringBuilder();
        var sample = await StreamAsync(http, request, (_, data, s) =>
        {
            if (data["error"] is { } error)
            {
                s.Error = "error event: " + error.ToJsonString();
                return null;
            }

            var piece = data["choices"]?.AsArray().FirstOrDefault()?["delta"]?["content"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(piece))
            {
                answer.Append(piece);
            }

            return piece;
        }, ended: sawDone => sawDone);
        if (sample.Ok)
        {
            conversation.History.Add(("user", question));
            conversation.History.Add(("assistant", answer.ToString()));
        }

        return sample;
    }

    /// <summary>
    /// Sends the request and reads the server-sent events as they arrive. <paramref name="piece"/> maps one event to the
    /// answer text it carries (or null); <paramref name="ended"/> says whether the stream ended properly, given whether
    /// <c>data: [DONE]</c> was seen.
    /// </summary>
    private static async Task<Sample> StreamAsync(
        HttpClient http, HttpRequestMessage request, Func<string, JsonNode, MutableSample, string?> piece, Func<bool, bool> ended)
    {
        var s = new MutableSample();
        var sent = Stopwatch.GetTimestamp();
        double Ms() => Stopwatch.GetElapsedTime(sent).TotalMilliseconds;
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            s.HeadersMs = Ms();
            if (!response.IsSuccessStatusCode)
            {
                var text = await response.Content.ReadAsStringAsync();
                s.Error = $"HTTP {(int)response.StatusCode}: {text[..Math.Min(200, text.Length)]}";
                return s.Freeze(Ms());
            }

            await using var stream = await response.Content.ReadAsStreamAsync();
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var eventName = "message";
            var sawDone = false;
            double? last = null;
            while (await reader.ReadLineAsync() is { } line)
            {
                if (line.StartsWith("event:", StringComparison.Ordinal))
                {
                    eventName = line[6..].Trim();
                    continue;
                }

                if (!line.StartsWith("data:", StringComparison.Ordinal))
                {
                    if (line.Length == 0)
                    {
                        eventName = "message";
                    }

                    continue;
                }

                var data = line[5..].Trim();
                if (data == "[DONE]")
                {
                    sawDone = true;
                    continue;
                }

                JsonNode? json;
                try
                {
                    json = JsonNode.Parse(data);
                }
                catch (JsonException)
                {
                    continue;
                }

                if (json is null || piece(eventName, json, s) is not { Length: > 0 })
                {
                    continue;
                }

                var now = Ms();
                s.Tokens++;
                if (last is null)
                {
                    s.FirstTokenMs = now;
                }
                else
                {
                    s.Gaps.Add(now - last.Value);
                }

                last = now;
            }

            if (s.Error is null && s.FirstTokenMs is null)
            {
                s.Error = "no answer text";
            }
            else if (s.Error is null && !ended(sawDone))
            {
                s.Error = "stream cut off";
            }
        }
        catch (TaskCanceledException)
        {
            s.Error = "timeout";
        }
        catch (HttpRequestException ex)
        {
            s.Error = "connection: " + ex.Message;
        }
        catch (IOException ex)
        {
            s.Error = "stream broken: " + ex.Message;
        }

        return s.Freeze(Ms());
    }

    private sealed class MutableSample
    {
        public double? HeadersMs { get; set; }

        public double? FirstTokenMs { get; set; }

        public List<double> Gaps { get; } = [];

        public int Tokens { get; set; }

        public string? Error { get; set; }

        public Sample Freeze(double totalMs) => new(Error is null, Error, HeadersMs, FirstTokenMs, totalMs, Tokens, Gaps);
    }

    private sealed record Sample(
        bool Ok, string? Error, double? HeadersMs, double? FirstTokenMs, double TotalMs, int Tokens, List<double> Gaps)
    {
        public double StartedAt { get; init; }

        public bool FollowUp { get; init; }

        public bool InHold { get; init; }
    }

    private static JsonObject Summarize(
        string target, int vus, int ramp, int hold, double followUpShare, List<Sample> all, double elapsed)
    {
        var inHold = all.Where(x => x.InHold).ToList();
        var ok = inHold.Where(x => x.Ok).ToList();
        JsonObject Part(IReadOnlyList<Sample> part) => new()
        {
            ["requests"] = part.Count,
            ["ok"] = part.Count(x => x.Ok),
            ["ttft_ms"] = Stats(part.Where(x => x.Ok).Select(x => x.FirstTokenMs!.Value)),
            ["stream_ms"] = Stats(part.Where(x => x.Ok).Select(x => x.TotalMs)),
        };

        return new JsonObject
        {
            ["target"] = target,
            ["vus"] = vus,
            ["ramp_s"] = ramp,
            ["hold_s"] = hold,
            ["followup_share"] = followUpShare,
            ["elapsed_s"] = Math.Round(elapsed, 1),
            ["hold"] = new JsonObject
            {
                ["requests"] = inHold.Count,
                ["completed_per_min"] = Math.Round(ok.Count * 60.0 / hold, 1),
                ["errors"] = inHold.Count(x => !x.Ok),
                ["error_kinds"] = new JsonObject(inHold.Where(x => !x.Ok)
                    .GroupBy(x => Kind(x.Error!))
                    .Select(g => KeyValuePair.Create(g.Key, (JsonNode?)g.Count()))),
                ["headers_ms"] = Stats(ok.Where(x => x.HeadersMs is not null).Select(x => x.HeadersMs!.Value)),
                ["ttft_ms"] = Stats(ok.Select(x => x.FirstTokenMs!.Value)),
                ["gap_ms"] = Stats(ok.SelectMany(x => x.Gaps)),
                ["max_gap_ms_per_stream"] = Stats(ok.Where(x => x.Gaps.Count > 0).Select(x => x.Gaps.Max())),
                // Where in the answer each stream's longest gap fell (token number): the same place every time is the
                // platform's doing (e.g. buffering), scattered is load.
                ["max_gap_at_token"] = Stats(ok.Where(x => x.Gaps.Count > 0).Select(x => (double)(x.Gaps.IndexOf(x.Gaps.Max()) + 2))),
                ["stream_ms"] = Stats(ok.Select(x => x.TotalMs)),
                ["tokens_per_answer"] = Stats(ok.Select(x => (double)x.Tokens)),
                ["new_conversations"] = Part(inHold.Where(x => !x.FollowUp).ToList()),
                ["follow_ups"] = Part(inHold.Where(x => x.FollowUp).ToList()),
                ["first_errors"] = new JsonArray(inHold.Where(x => !x.Ok).Select(x => (JsonNode?)x.Error).Distinct().Take(5).ToArray()),
            },
            ["all_requests"] = all.Count,
        };
    }

    /// <summary>The kind of failure, without its details: "HTTP 503", "timeout", "stream cut off".</summary>
    private static string Kind(string error) =>
        error.StartsWith("HTTP ", StringComparison.Ordinal) ? error[..Math.Min(8, error.Length)].TrimEnd(':')
        : error.Split(':')[0];

    private static JsonObject Stats(IEnumerable<double> values)
    {
        var sorted = values.Order().ToList();
        if (sorted.Count == 0)
        {
            return new JsonObject { ["n"] = 0 };
        }

        double At(double q) => Math.Round(sorted[Math.Min(sorted.Count - 1, (int)(q * sorted.Count))], 1);
        return new JsonObject
        {
            ["n"] = sorted.Count,
            ["p50"] = At(0.50),
            ["p90"] = At(0.90),
            ["p95"] = At(0.95),
            ["p99"] = At(0.99),
            ["max"] = Math.Round(sorted[^1], 1),
            ["mean"] = Math.Round(sorted.Average(), 1),
        };
    }
}
