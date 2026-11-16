using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Ume.LlmGateway.AdminApi.Settings;
using Ume.LlmGateway.Infrastructure.Persistence;

namespace Ume.LlmGateway.AdminApi.Setup;

/// <summary>
/// Setup mode's state as one request sees it: <see cref="Nonce"/> is the running opening's (null while closed);
/// <see cref="CompletedAt"/> is null on a first run and set on a re-open.
/// </summary>
public sealed record SetupStatus(bool Open, string? Nonce, DateTimeOffset? OpenedAt, DateTimeOffset? EndsAt, DateTimeOffset? CompletedAt)
{
    /// <summary><c>first_run</c> or <c>reopen</c>.</summary>
    public string Kind => CompletedAt is null ? SetupMode.FirstRun : SetupMode.Reopen;
}

/// <summary>What the evaluation at start found or did.</summary>
public enum SetupEvaluationOutcome
{
    /// <summary>Setup is closed and stays closed.</summary>
    Closed = 0,

    /// <summary>An opening started earlier is still running.</summary>
    StillOpen = 1,

    /// <summary>A new opening started (new nonce).</summary>
    Opened = 2,

    /// <summary>The deployment declares the sign-in: <c>SetupCompletedAt</c> was set.</summary>
    CompletedByDeployment = 3,

    /// <summary>Setup would open, but there is no setup secret: it stays closed and nothing is consumed.</summary>
    SecretMissing = 4,

    /// <summary>There is no <c>SignInSettings</c> row (the migration creates it): setup stays closed.</summary>
    RowMissing = 5,
}

/// <summary>The evaluation's outcome and, while open, the opening.</summary>
public sealed record SetupEvaluation(SetupEvaluationOutcome Outcome, string? Nonce = null, DateTimeOffset? EndsAt = null, string? Kind = null);

/// <summary>
/// Open or closed (thekim1/llmgateway#100): open while an opening (a nonce and its start in the <c>SignInSettings</c> row) is
/// younger than <see cref="OpeningLifetime"/>. An opening starts only at the admin API's start (<see cref="EvaluateAsync"/>): when
/// setup has never been completed and no opening runs, or when <c>Setup:Reopen</c> holds a value other than the last consumed
/// one, which the same transaction consumes. A deployment that declares the sign-in (<see cref="SignInSettingsService.DeclaresSignIn"/>)
/// counts as completed. Nothing else opens setup: a missing row, a missing value or an unreadable secret leaves it closed, and
/// setup never creates the row. Writes hold <see cref="SetupLock"/>, so replicas starting together end with one nonce.
/// </summary>
public sealed class SetupMode(GatewayDbContext db, IConfiguration configuration, IOptions<SetupOptions> options, TimeProvider time, SetupSecret secret)
{
    public const string FirstRun = "first_run";
    public const string Reopen = "reopen";

    public static readonly TimeSpan OpeningLifetime = TimeSpan.FromHours(1);

    /// <summary>
    /// The rule on its own: an opening (<paramref name="openedAt"/>, null while no opening has a nonce) younger than an hour, unless the
    /// deployment now declares a sign-in that was never completed.
    /// </summary>
    public static bool IsOpen(DateTimeOffset? completedAt, DateTimeOffset? openedAt, bool declaredByDeployment, DateTimeOffset now) =>
        openedAt is { } opened && now < opened + OpeningLifetime && !(completedAt is null && declaredByDeployment);

    /// <summary>The rule on the values <c>/bff/user</c> read with the sign-in values (<see cref="SignInSettingsService.ReadAsync"/>).</summary>
    public static bool IsOpen(EffectiveSignIn values, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(values);
        return IsOpen(values.SetupCompletedAt, values.SetupOpenedAt, values.DeclaredByDeployment, now);
    }

    /// <summary>The state now: one read of the row; no row is closed.</summary>
    public async Task<SetupStatus> ReadAsync(CancellationToken ct)
    {
        var row = await db.SignInSettings.AsNoTracking()
            .Select(s => new { s.SetupCompletedAt, s.SetupNonce, s.SetupOpenedAt })
            .SingleOrDefaultAsync(ct);
        var openedAt = row?.SetupNonce is null ? null : row.SetupOpenedAt;
        var open = IsOpen(row?.SetupCompletedAt, openedAt, SignInSettingsService.DeclaresSignIn(configuration), time.GetUtcNow());
        return new SetupStatus(open, open ? row!.SetupNonce : null, openedAt, openedAt + OpeningLifetime, row?.SetupCompletedAt);
    }

    /// <summary>
    /// Deletes the setup sessions (their tests cascade) of every opening that is not running now, in one statement, so a typed
    /// candidate secret does not stay in the table once its opening ended. A session of the running opening is kept, whichever
    /// instance started it.
    /// </summary>
    public Task<int> DeleteEndedSessionsAsync(CancellationToken ct)
    {
        var runningSince = time.GetUtcNow() - OpeningLifetime;
        return db.SetupSessions
            .Where(s => !db.SignInSettings.Any(x => x.SetupNonce == s.OpeningNonce && x.SetupOpenedAt > runningSince))
            .ExecuteDeleteAsync(ct);
    }

    /// <summary>The evaluation at start: completes for a declaring deployment, starts an opening when one is due, else reports the state.</summary>
    public async Task<SetupEvaluation> EvaluateAsync(CancellationToken ct)
    {
        var declared = SignInSettingsService.DeclaresSignIn(configuration);
        var reopen = options.Value.ReopenOrNull;
        var hasSecret = secret.Current is not null;
        return await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            await SetupLock.AcquireAsync(db, ct);
            var evaluation = await EvaluateLockedAsync(declared, reopen, hasSecret, ct);
            await transaction.CommitAsync(ct);
            return evaluation;
        });
    }

    private async Task<SetupEvaluation> EvaluateLockedAsync(bool declared, string? reopen, bool hasSecret, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var row = await db.SignInSettings.SingleOrDefaultAsync(ct);
        if (row is null)
        {
            return new SetupEvaluation(SetupEvaluationOutcome.RowMissing);
        }

        if (declared && row.SetupCompletedAt is null)
        {
            row.SetupCompletedAt = now;
            row.SetupNonce = null;
            await db.SaveChangesAsync(ct);
            await db.SetupSessions.ExecuteDeleteAsync(ct);
            return new SetupEvaluation(SetupEvaluationOutcome.CompletedByDeployment);
        }

        var neverCompleted = row.SetupCompletedAt is null;
        var reopenPending = reopen is not null && !string.Equals(reopen, row.SetupReopenConsumed, StringComparison.Ordinal);
        var running = row.SetupNonce is not null && row.SetupOpenedAt is { } openedAt && now < openedAt + OpeningLifetime;
        if (running && !reopenPending)
        {
            return new SetupEvaluation(SetupEvaluationOutcome.StillOpen, row.SetupNonce, row.SetupOpenedAt + OpeningLifetime, neverCompleted ? FirstRun : Reopen);
        }

        var closed = !reopenPending && !neverCompleted;
        if (closed || !hasSecret)
        {
            // The sessions of ended openings go. Without the secret nothing is consumed, so the re-open value opens setup at the
            // first start that has it.
            await DeleteEndedSessionsAsync(ct);
            return new SetupEvaluation(closed ? SetupEvaluationOutcome.Closed : SetupEvaluationOutcome.SecretMissing);
        }

        row.SetupNonce = Base64Url(RandomNumberGenerator.GetBytes(32));
        row.SetupOpenedAt = now;
        if (reopenPending)
        {
            row.SetupReopenConsumed = reopen;
        }

        await db.SaveChangesAsync(ct);
        // A new opening voids the sessions (and their tests) of every earlier one.
        await db.SetupSessions.ExecuteDeleteAsync(ct);
        return new SetupEvaluation(SetupEvaluationOutcome.Opened, row.SetupNonce, now + OpeningLifetime, neverCompleted ? FirstRun : Reopen);
    }

    internal static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>
/// The throttle on wrong setup codes: at most <see cref="Limit"/> per minute in this process, a fixed window; further wrong
/// codes in the window answer 429. The code is compared before the throttle is asked, so the right code always gives a session
/// and a guesser cannot lock the deployer out (128 bits need no hard stop). Nothing is stored and nothing voids the code. The
/// per-address rate limit stays on the route as well.
/// </summary>
public sealed class SetupCodeThrottle(TimeProvider time)
{
    public const int Limit = 20;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    private readonly Lock _lock = new();
    private DateTimeOffset _windowStart = DateTimeOffset.MinValue;
    private int _count;

    /// <summary>Counts one wrong code: false when the window is full, with <paramref name="retryAfter"/> the time to its end.</summary>
    public bool TryAcquire(out TimeSpan retryAfter)
    {
        var now = time.GetUtcNow();
        lock (_lock)
        {
            if (now - _windowStart >= Window)
            {
                _windowStart = now;
                _count = 0;
            }

            retryAfter = _windowStart + Window - now;
            return ++_count <= Limit;
        }
    }
}

/// <summary>
/// The limit on setup test callbacks (<see cref="SetupCallbackMiddleware"/>), which anyone can send while setup is open and which
/// each cost a database statement: at most <see cref="Limit"/> per minute and client address in this process, a fixed window;
/// further ones answer 429 before anything is read. Per address, so a flood from elsewhere does not keep the deployer's own
/// callback out; past <see cref="MaxAddresses"/> addresses in a window the rest share one count. A deployer runs a few tests an hour.
/// </summary>
public sealed class SetupCallbackThrottle(TimeProvider time)
{
    public const int Limit = 20;
    public const int MaxAddresses = 10_000;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    private readonly Lock _lock = new();
    private readonly Dictionary<string, int> _counts = new(StringComparer.Ordinal);
    private DateTimeOffset _windowStart = DateTimeOffset.MinValue;

    /// <summary>Counts one callback from <paramref name="address"/>: false when its window is full, with <paramref name="retryAfter"/> the time to its end.</summary>
    public bool TryAcquire(string? address, out TimeSpan retryAfter)
    {
        var now = time.GetUtcNow();
        lock (_lock)
        {
            if (now - _windowStart >= Window)
            {
                _windowStart = now;
                _counts.Clear();
            }

            retryAfter = _windowStart + Window - now;
            var key = address ?? "";
            if (!_counts.ContainsKey(key) && _counts.Count >= MaxAddresses)
            {
                key = "\0others";
            }

            var count = _counts.GetValueOrDefault(key) + 1;
            _counts[key] = count;
            return count <= Limit;
        }
    }
}

/// <summary>
/// Samples setup's flood-prone security events (wrong codes, callbacks of an unknown test): at most one per <see cref="Every"/> per
/// event in this process, carrying the number since the last one, so a flood cannot fill the log.
/// </summary>
public sealed class SetupEventSampler(TimeProvider time)
{
    public static readonly TimeSpan Every = TimeSpan.FromSeconds(10);

    private readonly Lock _lock = new();
    private readonly Dictionary<int, (DateTimeOffset LoggedAt, int Pending)> _events = [];

    /// <summary>Whether to log event <paramref name="eventId"/> now; <paramref name="count"/> is the occurrences it stands for.</summary>
    public bool ShouldLog(int eventId, out int count)
    {
        var now = time.GetUtcNow();
        lock (_lock)
        {
            var (loggedAt, pending) = _events.GetValueOrDefault(eventId, (DateTimeOffset.MinValue, 0));
            pending++;
            if (now - loggedAt < Every)
            {
                _events[eventId] = (loggedAt, pending);
                count = 0;
                return false;
            }

            _events[eventId] = (now, 0);
            count = pending;
            return true;
        }
    }
}

/// <summary>
/// Deletes the sessions of an ended opening once per process and opening when a request finds setup closed
/// (<see cref="SetupMode.DeleteEndedSessionsAsync"/>), so a closed setup's 404s do not each run a delete.
/// </summary>
public sealed class SetupCleanup
{
    private readonly Lock _lock = new();
    private DateTimeOffset? _cleanedFor = DateTimeOffset.MaxValue;

    /// <summary>Runs the delete when the ended opening <paramref name="status"/> names has not been cleaned in this process yet.</summary>
    public async Task AfterClosedAsync(SetupStatus status, SetupMode setup, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(setup);
        lock (_lock)
        {
            if (_cleanedFor == status.OpenedAt)
            {
                return;
            }

            _cleanedFor = status.OpenedAt;
        }

        await setup.DeleteEndedSessionsAsync(ct);
    }
}
