// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;

namespace Whizbang.Core.Workers;

/// <summary>
/// Runs a transport batch so that a failure costs one batch, never the host process, and never
/// lets the transport settle the failed batch as consumed.
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
/// the terminated container — invisible to crash alerting and error-rate alerting alike. The
/// timeout surfaces as an <see cref="OperationCanceledException"/>, which is what let it through a
/// "not a shutdown" filter as though it were one.
/// </para>
/// <para>
/// The first answer swallowed the failure, which broke something worse: every transport settles a
/// message by whether its handler returned, so a swallowed batch was completed and lost while the log
/// said the broker would redeliver it (#921). A failed batch is now reported to the transport as a
/// <see cref="TransportBatchFailedException"/>, logged here first. Every transport's receive boundary
/// catches it and abandons (or, past the broker's delivery limit, dead-letters) instead of completing,
/// and the typed signal is never a cancellation, so no filter mistakes it for a shutdown.
/// </para>
/// </remarks>
/// <docs>operations/workers/transport-consumer</docs>
/// <tests>tests/Whizbang.Core.Tests/Workers/TransportBatchGuardTests.cs</tests>
public static partial class TransportBatchGuard {

  /// <summary>
  /// Invokes <paramref name="body"/>. A failure that is not a host shutdown is logged and reported to
  /// the transport as a <see cref="TransportBatchFailedException"/>, so the batch is not settled as
  /// consumed; a host shutdown propagates as itself.
  /// </summary>
  /// <remarks>
  /// <para>
  /// Two tokens, deliberately separate parameters. The transport supplies its own per-batch token
  /// and cancels it for reasons unrelated to host shutdown — a lost session lock, a draining
  /// processor, a message-level timeout. Only <paramref name="hostStoppingToken"/> answers "is this
  /// process stopping".
  /// </para>
  /// <para>
  /// The first version of this guard took a single token and the call site passed the per-batch one.
  /// It shipped, and a host still terminated silently with the guard present in the assembly:
  /// the transport canceled the batch, the guard read that as a shutdown, and the exception
  /// escaped. Splitting the parameters makes that substitution impossible to write by accident.
  /// </para>
  /// </remarks>
  /// <param name="body">The batch work; receives the per-batch token.</param>
  /// <param name="batchCount">Message count, for the log line.</param>
  /// <param name="logger">Logger for the failure report.</param>
  /// <param name="batchToken">The transport's per-batch cancellation — passed to the work.</param>
  /// <param name="hostStoppingToken">The worker's stopping token — the sole shutdown authority.</param>
  /// <exception cref="TransportBatchFailedException">The batch failed while the host is running.</exception>
  public static async Task RunAsync(
      Func<CancellationToken, Task> body, int batchCount, ILogger logger,
      CancellationToken batchToken, CancellationToken hostStoppingToken) {
    ArgumentNullException.ThrowIfNull(body);
    ArgumentNullException.ThrowIfNull(logger);

    try {
      await body(batchToken).ConfigureAwait(false);
    } catch (Exception ex) when (
        TransportBatchFailureClassifier.Classify(ex, hostStoppingToken) == TransportBatchFailure.Transient) {
      if (TransportBatchFailureClassifier.IsStatementCancellation(ex)) {
        LogBatchStatementCanceled(logger, batchCount, ex);
      } else {
        LogBatchFailed(logger, batchCount, ex);
      }
      // Reported, never swallowed: the transport completes whatever its handler returns from (#921).
      throw new TransportBatchFailedException(batchCount, ex);
    }
  }

  [LoggerMessage(
    EventId = 90,
    Level = LogLevel.Error,
    Message = "Transport batch of {BatchCount} message(s) failed. It is reported to the transport as failed "
            + "and is not settled as consumed: the transport abandons it for redelivery, and past the broker's "
            + "max delivery count the broker dead-letters it. The host stays up.")]
  static partial void LogBatchFailed(ILogger logger, int batchCount, Exception ex);

  [LoggerMessage(
    EventId = 91,
    Level = LogLevel.Error,
    Message = "Transport batch of {BatchCount} message(s) failed because the DATABASE canceled the "
            + "statement (SQLSTATE 57014) — typically a command timeout, not a shutdown. It is reported "
            + "to the transport as failed and is not settled as consumed: the transport abandons it for "
            + "redelivery. If this repeats, the store statement is exceeding its timeout, usually because "
            + "the table has grown.")]
  static partial void LogBatchStatementCanceled(ILogger logger, int batchCount, Exception ex);
}
