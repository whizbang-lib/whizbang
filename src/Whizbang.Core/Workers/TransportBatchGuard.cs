using Microsoft.Extensions.Logging;

namespace Whizbang.Core.Workers;

/// <summary>
/// Runs a transport batch so that a failure costs one batch, never the host process.
/// </summary>
/// <remarks>
/// <para>
/// Separate from the worker so the containment behavior can be proven on its own. The guard is the
/// part that has to be right, and demonstrating it should not require a transport, a database and a
/// running host — a test that needs all three tends to prove the harness rather than the guard.
/// </para>
/// <para>
/// Without this, an exception escaping the batch handler propagates out of the worker's
/// <c>ExecuteAsync</c>, and the default <c>BackgroundServiceExceptionBehavior.StopHost</c> stops the
/// host. Observed in production: a PostgreSQL statement timeout during the inbox store shut down
/// every worker in an orderly fashion and exited <b>zero</b>, with no Error-level line anywhere in
/// the terminated container — invisible to crash alerting and error-rate alerting alike.
/// </para>
/// </remarks>
/// <docs>operations/workers/transport-consumer</docs>
/// <tests>tests/Whizbang.Core.Tests/Workers/TransportBatchGuardTests.cs</tests>
public static partial class TransportBatchGuard {

  /// <summary>
  /// Invokes <paramref name="body"/>, containing any non-shutdown failure.
  /// </summary>
  /// <param name="body">The batch work to run.</param>
  /// <param name="batchCount">Message count, for the log line.</param>
  /// <param name="logger">Logger for the failure report.</param>
  /// <param name="stoppingToken">The worker's stopping token — the sole shutdown authority.</param>
  public static async Task RunAsync(
      Func<Task> body, int batchCount, ILogger logger, CancellationToken stoppingToken) {
    ArgumentNullException.ThrowIfNull(body);
    ArgumentNullException.ThrowIfNull(logger);

    try {
      await body().ConfigureAwait(false);
    } catch (Exception ex) when (
        TransportBatchFailureClassifier.Classify(ex, stoppingToken) == TransportBatchFailure.Transient) {
      if (TransportBatchFailureClassifier.IsStatementCancellation(ex)) {
        LogBatchStatementCanceled(logger, batchCount, ex);
      } else {
        LogBatchFailed(logger, batchCount, ex);
      }
      // Swallowed deliberately. The alternative is terminating the host over one batch that the
      // broker still holds and will redeliver.
    }
  }

  [LoggerMessage(
    EventId = 90,
    Level = LogLevel.Error,
    Message = "Transport batch of {BatchCount} message(s) failed; the batch is abandoned and the broker "
            + "will redeliver. The host stays up — a failed batch must not stop the process.")]
  static partial void LogBatchFailed(ILogger logger, int batchCount, Exception ex);

  [LoggerMessage(
    EventId = 91,
    Level = LogLevel.Error,
    Message = "Transport batch of {BatchCount} message(s) failed because the DATABASE canceled the "
            + "statement (SQLSTATE 57014) — typically a command timeout, not a shutdown. The batch is "
            + "abandoned and the broker will redeliver. If this repeats, the store statement is "
            + "exceeding its timeout, usually because the table has grown.")]
  static partial void LogBatchStatementCanceled(ILogger logger, int batchCount, Exception ex);
}
