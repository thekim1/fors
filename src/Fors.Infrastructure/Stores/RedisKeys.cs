using StackExchange.Redis;
using Fors.Domain;

namespace Fors.Infrastructure.Stores;

/// <summary>
/// Every Redis key and channel the gateway uses, with their lifetimes. All live under <c>fors:</c>: the restricted
/// gateway ACL user (<c>deploy/redis/users.acl.example</c>) may only touch <c>~fors:*</c> and publish on <c>&amp;fors:invalidate</c>.
/// </summary>
internal static class RedisKeys
{
    public const string Prefix = "fors:";

    public const string InvalidateChannelName = Prefix + "invalidate";

    /// <summary>Cache invalidation (key revoked, configuration changed), published by the admin API.</summary>
    public static readonly RedisChannel InvalidateChannel = RedisChannel.Literal(InvalidateChannelName);

    /// <summary>A signed-in admin session (the admin API's encrypted cookie ticket), expiring with the session.</summary>
    public static string AdminSession(string sessionId) => $"fors:admin-session:{sessionId}";

    /// <summary>A per-minute rate-limit counter outlives its minute a little, so late token reports still land.</summary>
    public const int RateWindowSeconds = 120;

    public static readonly TimeSpan RateWindow = TimeSpan.FromSeconds(RateWindowSeconds);

    /// <summary>
    /// Usage batches the database could not take yet (sorted set of <c>{batchId}:{records}</c> → last heartbeat in ms). A
    /// batch whose heartbeat stops (its gateway instance died) is taken over by another instance.
    /// </summary>
    public const string UsageJournalIndex = "fors:usage-journal";

    /// <summary>Records currently held in the usage journal, for its cap.</summary>
    public const string UsageJournalRecords = "fors:usage-journal:records";

    /// <summary>One journaled usage batch (JSON).</summary>
    public static string UsageJournalBatch(string batchId) => $"fors:usage-journal:batch:{batchId}";

    /// <summary>Requests of a key in flight (sorted set of lease id → expiry in ms), for its concurrency limit.</summary>
    public static string InFlight(Guid keyId) => $"fors:inflight:{keyId:N}";

    /// <summary>Requests a key made in the minute <paramref name="minute"/> (Unix minutes).</summary>
    public static string RateRequests(Guid keyId, long minute) => $"fors:rl:req:{keyId:N}:{minute}";

    /// <summary>Tokens a key used in the minute <paramref name="minute"/> (Unix minutes).</summary>
    public static string RateTokens(Guid keyId, long minute) => $"fors:rl:tok:{keyId:N}:{minute}";

    /// <summary>Open realtime sessions of a key (sorted set of session id → expiry).</summary>
    public static string RealtimeSessions(Guid keyId) => $"fors:rt:{keyId:N}";

    /// <summary>Set while a provider's circuit is open (<c>auto</c> or <c>forced</c>), with the break duration as TTL.</summary>
    public static string CircuitOpen(Guid providerId) => $"fors:circuit:{providerId:N}";

    /// <summary>Failures of a provider in the current sampling window.</summary>
    public static string CircuitFailures(Guid providerId) => $"fors:circuit-fail:{providerId:N}";

    /// <summary>Set while a model deployment is on cooldown because its provider answered with <c>Retry-After</c>, with that delay as TTL.</summary>
    public static string Cooldown(Guid deploymentId) => $"fors:cooldown:{deploymentId:N}";

    /// <summary>
    /// Spend of one budget scope in one period window, in micro-USD. (Before budgets were counted in USD the counters
    /// were <c>fors:spend:…</c> in micro-SEK; those are never read again and expire with their period.)
    /// </summary>
    public static string Spend(BudgetScope scope, Guid scopeId, BudgetPeriod period, DateTimeOffset periodStart) =>
        $"fors:spend-usd:{scope}:{scopeId:N}:{period}:{periodStart.ToUnixTimeSeconds()}";

    /// <summary>Tokens used against a token budget in one period window, counting only <paramref name="residency"/> (null = all).</summary>
    public static string TokenSpend(BudgetScope scope, Guid scopeId, BudgetPeriod period, DateTimeOffset periodStart, DataResidency? residency) =>
        $"fors:spend-tok:{scope}:{scopeId:N}:{period}:{periodStart.ToUnixTimeSeconds()}:{residency?.ToString() ?? "All"}";
}
