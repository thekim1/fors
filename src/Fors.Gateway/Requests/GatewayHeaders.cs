using Fors.Infrastructure.Security;
using Microsoft.Extensions.Primitives;

namespace Fors.Gateway.Requests;

/// <summary>Response headers the gateway adds (documented in docs/admin-api.md and the README).</summary>
public static class GatewayHeaders
{
    /// <summary>Provider account that answered.</summary>
    public const string Provider = "x-fors-provider";

    /// <summary>Model deployment that answered.</summary>
    public const string Model = "x-fors-model";

    /// <summary>Data residency of the provider that answered.</summary>
    public const string Residency = "x-fors-residency";

    /// <summary>Providers tried before the one that answered (or all of them, when every one failed).</summary>
    public const string Fallbacks = "x-fors-fallbacks";

    /// <summary>Cost of the request in USD, the currency it is calculated in (complete JSON answers only).</summary>
    public const string CostUsd = "x-fors-cost-usd";

    /// <summary>Smallest remaining amount over the budgets that apply, in USD.</summary>
    public const string BudgetRemainingUsd = "x-fors-budget-remaining-usd";

    /// <summary>Ids of the routing rules that decided where the request went, comma separated.</summary>
    public const string Rule = "x-fors-rule";

    /// <summary>Response header: <c>flagged</c> when the content guard flagged the request or answer (policy Record).</summary>
    public const string Guard = "x-fors-guard";

    /// <summary>Request header: a session or conversation id that keeps the session on one target (prompt cache reuse).</summary>
    public const string Session = "x-fors-session";

    /// <summary>
    /// Request header: the highest residency the request may go to (<c>OnPrem</c>, <c>Eu</c> or <c>External</c>). It only
    /// narrows the key's residencies; <see cref="Residency"/> reports where the request went.
    /// </summary>
    public const string MaxResidency = "x-fors-max-residency";

    /// <summary>
    /// Request header from an integration's credential key: the id of an assigned key of the same integration, which the
    /// request is then made as (docs/integrations.md). Echoed on the answer with the id that was applied.
    /// </summary>
    public const string OnBehalfOf = "x-fors-on-behalf-of";

    /// <summary>Request header: an opaque id that groups the calls of one answer or job; stored on the usage record.</summary>
    public const string RunId = "x-fors-run-id";

    /// <summary>Request header: an opaque step name (<c>answer</c>, <c>rerank</c>, ...); stored on the usage record.</summary>
    public const string Step = "x-fors-step";

    /// <summary>Request header <c>1</c>: a streamed answer ends with the SSE comment <c>: fors-cost-usd=…</c>.</summary>
    public const string StreamCost = "x-fors-stream-cost";

    /// <summary>What the PII guard did: detected, redacted, blocked or rerouted-onprem.</summary>
    public const string Pii = "x-fors-pii";

    /// <summary>The header's name before the rename to Fors (<c>x-ume-*</c>), still read and written until 0.13; null for other headers.</summary>
    public static string? Legacy(string name) => name switch
    {
        Provider => "x-ume-provider",
        Model => "x-ume-model",
        Residency => "x-ume-residency",
        Fallbacks => "x-ume-fallbacks",
        CostUsd => "x-ume-cost-usd",
        BudgetRemainingUsd => "x-ume-budget-remaining-usd",
        Rule => "x-ume-rule",
        Guard => "x-ume-guard",
        Session => "x-ume-session",
        MaxResidency => "x-ume-max-residency",
        OnBehalfOf => "x-ume-on-behalf-of",
        RunId => "x-ume-run-id",
        Step => "x-ume-step",
        StreamCost => "x-ume-stream-cost",
        Pii => "x-ume-pii",
        _ => null,
    };

    /// <summary>A request header by its name, else by its <see cref="Legacy"/> name.</summary>
    public static StringValues Read(IHeaderDictionary headers, string name)
    {
        ArgumentNullException.ThrowIfNull(headers);
        var value = headers[name];
        return value.Count > 0 || Legacy(name) is not { } legacy ? value : headers[legacy];
    }

    /// <summary>Sets a response header under its name and its <see cref="Legacy"/> name.</summary>
    public static void Write(IHeaderDictionary headers, string name, StringValues value)
    {
        ArgumentNullException.ThrowIfNull(headers);
        headers[name] = value;
        if (Legacy(name) is { } legacy)
        {
            headers[legacy] = value;
        }
    }

    public const string RateLimitLimitRequests = "x-ratelimit-limit-requests";
    public const string RateLimitRemainingRequests = "x-ratelimit-remaining-requests";
}

/// <summary>Model names as recorded and echoed back: clients may send anything, so the length is capped.</summary>
public static class ModelNames
{
    /// <summary>Length of <c>UsageRecord.RequestedModel</c> and of names quoted in error messages.</summary>
    public const int MaxLength = 200;

    /// <summary>The name as logged and stored: at most <see cref="MaxLength"/> characters, control characters replaced.</summary>
    public static string Truncate(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return SecurityEvents.Printable(name.Length > MaxLength ? name[..MaxLength] : name);
    }
}
