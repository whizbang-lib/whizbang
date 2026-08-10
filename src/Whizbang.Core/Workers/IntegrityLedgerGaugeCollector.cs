using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Workers;

/// <summary>
/// Periodically reads the integrity ledger and publishes it to
/// <see cref="StreamIntegrityMetrics"/>'s gauges.
/// </summary>
/// <remarks>
/// <para>
/// This is the reporting half of stream integrity. It used to be a durable event per divergence
/// sighting, which nothing consumed — and because each report carried its own stream id, every one
/// minted a new event stream that no cursor would ever advance past, so the consumption-gated
/// reaper could never collect it. Unbounded growth in the tables the work pump scans, in exchange
/// for notifications no code read.
/// </para>
/// <para>
/// A gauge is the right shape for the question actually being asked. "How many divergences have we
/// ever noticed" only rises; "how many are broken right now" falls as repair works, because healing
/// deletes the ledger row. The cadence is deliberately independent of the audit's: the audit runs
/// daily by default, and a number that refreshes daily is not something an operator can watch.
/// </para>
/// <para>
/// Inert where the engine has no ledger — the coordinator default returns the empty reading, so
/// this costs one no-op call per interval and publishes zeroes.
/// </para>
/// </remarks>
/// <docs>resilience/stream-integrity</docs>
/// <tests>tests/Whizbang.Core.Tests/Workers/IntegrityLedgerGaugeCollectorTests.cs</tests>
public sealed partial class IntegrityLedgerGaugeCollector(
  IServiceScopeFactory scopeFactory,
  StreamIntegrityMetrics metrics,
  IOptions<StreamIntegrityOptions> options,
  ISchemaReadyGate? schemaReadyGate = null,
  ILogger<IntegrityLedgerGaugeCollector>? logger = null
) : BackgroundService {

  private readonly ILogger<IntegrityLedgerGaugeCollector> _logger =
    logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<IntegrityLedgerGaugeCollector>.Instance;

  /// <summary>
  /// Raised after each cycle publishes its reading, so an observer can tell the gauges have
  /// actually been written rather than that a query was merely started.
  /// </summary>
  internal event Action? CycleCompleted;

  /// <inheritdoc/>
  protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
    if (schemaReadyGate is not null) {
      try {
        await schemaReadyGate.WaitForReadyAsync(stoppingToken);
      } catch (OperationCanceledException) {
        return;
      }
    }

    var interval = TimeSpan.FromSeconds(Math.Max(5, options.Value.LedgerGaugeIntervalSeconds));

    while (!stoppingToken.IsCancellationRequested) {
      try {
        await using var scope = scopeFactory.CreateAsyncScope();
        var coordinator = scope.ServiceProvider.GetService<IWorkCoordinator>();
        if (coordinator is null) {
          return;   // nothing to read; no point waking up again
        }

        var snapshot = await coordinator
          .GetIntegrityLedgerSummaryAsync(options.Value.MaxRepairAttemptsPerBucket, stoppingToken)
          .ConfigureAwait(false);
        metrics.UpdateLedgerGauges(snapshot);

        CycleCompleted?.Invoke();
      } catch (ObjectDisposedException) {
        break;   // host shutting down
      } catch (Exception ex) when (ex is not OperationCanceledException) {
        LogCollectionError(_logger, ex);
      }

      try {
        await Task.Delay(interval, stoppingToken);
      } catch (OperationCanceledException) {
        break;
      }
    }
  }

  [LoggerMessage(EventId = 1, Level = LogLevel.Warning,
    Message = "Failed to refresh stream-integrity ledger gauges — will retry")]
  static partial void LogCollectionError(ILogger logger, Exception exception);
}
