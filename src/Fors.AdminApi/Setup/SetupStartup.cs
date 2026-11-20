using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using Fors.Infrastructure.Security;

namespace Fors.AdminApi.Setup;

/// <summary>
/// Evaluates setup mode at the admin API's start (<see cref="SetupMode.EvaluateAsync"/>) and, while it is open, writes the setup
/// code to standard output with the wizard's address and when the code expires: never through the logger or a security event, so
/// it is not in the OTLP export. The first try runs within the start, briefly; while the database is away it tries again in the
/// background. A never-completed deployment gets a new opening at its next start after the hour, not by itself: setup is open only
/// while a person with host access has just started or re-opened it.
/// </summary>
public sealed partial class SetupStartup(IServiceScopeFactory scopes, SetupSecret secret, SetupConsole console, ILoggerFactory loggers, ILogger<SetupStartup> logger)
    : BackgroundService
{
    private static readonly TimeSpan FirstTry = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RetryEvery = TimeSpan.FromSeconds(15);
    private bool _evaluated;

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        using (var first = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            first.CancelAfter(FirstTry);
            _evaluated = await TryEvaluateAsync(first.Token);
        }

        await base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!_evaluated && !stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(RetryEvery, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            _evaluated = await TryEvaluateAsync(stoppingToken);
        }
    }

    private async Task<bool> TryEvaluateAsync(CancellationToken ct)
    {
        SetupEvaluation evaluation;
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            evaluation = await scope.ServiceProvider.GetRequiredService<SetupMode>().EvaluateAsync(ct);
        }
        catch (Exception ex) when (ex is NpgsqlException or TimeoutException or OperationCanceledException or DbUpdateException or RetryLimitExceededException
                                       or InvalidOperationException { InnerException: NpgsqlException })
        {
            LogEvaluationFailed(logger, ex.GetType().Name);
            return false;
        }

        switch (evaluation.Outcome)
        {
            case SetupEvaluationOutcome.Opened:
                SecurityEvents.AdminSetupOpened(SecurityEvents.CreateLogger(loggers), evaluation.Kind!, evaluation.EndsAt!.Value);
                Print(evaluation);
                break;
            case SetupEvaluationOutcome.StillOpen:
                Print(evaluation);
                break;
            case SetupEvaluationOutcome.CompletedByDeployment:
                LogCompletedByDeployment(logger);
                break;
            case SetupEvaluationOutcome.SecretMissing:
                LogSecretMissing(logger, SetupOptions.SecretFileName, SetupOptions.MinSecretLength);
                break;
            case SetupEvaluationOutcome.RowMissing:
                LogRowMissing(logger);
                break;
            default:
                break;
        }

        return true;
    }

    private void Print(SetupEvaluation evaluation)
    {
        if (secret.Current is not { } key)
        {
            LogSecretMissing(logger, SetupOptions.SecretFileName, SetupOptions.MinSecretLength);
            return;
        }

        LogOpen(logger, evaluation.Kind!, evaluation.EndsAt!.Value);
        var endsAt = evaluation.EndsAt.Value.UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);
        // Standard output only: the logger's records leave the host through the OTLP export.
        var output = console.Out;
        lock (output)
        {
            output.WriteLine($"adminapi: setup mode is open until {endsAt}. Setup code: {SetupCode.Compute(key, evaluation.Nonce!)}");
            output.WriteLine("adminapi: enter it at /setup on the admin console's address (https://<admin host>/setup).");
            output.Flush();
        }
    }

    [LoggerMessage(LogLevel.Warning, "Setup mode could not be evaluated at start ({ExceptionType}); trying again")]
    private static partial void LogEvaluationFailed(ILogger logger, string exceptionType);

    [LoggerMessage(LogLevel.Information, "The deployment declares the sign-in (Oidc:Authority): setup mode counts as completed")]
    private static partial void LogCompletedByDeployment(ILogger logger);

    [LoggerMessage(LogLevel.Error, "Setup mode would open, but the setup secret ({FileName}, at least {MinLength} characters) is missing: setup stays closed until the deployment provides it and the admin API starts again")]
    private static partial void LogSecretMissing(ILogger logger, string fileName, int minLength);

    [LoggerMessage(LogLevel.Error, "The SignInSettings row is missing (the migration service creates it): setup mode stays closed")]
    private static partial void LogRowMissing(ILogger logger);

    [LoggerMessage(LogLevel.Information, "Setup mode is open ({Kind}) until {EndsAt}; the setup code is on the admin API's standard output")]
    private static partial void LogOpen(ILogger logger, string kind, DateTimeOffset endsAt);
}
