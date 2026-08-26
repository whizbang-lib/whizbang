using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// A database statement timeout must cost one batch, not the whole process.
/// </summary>
/// <remarks>
/// <para>
/// Observed in production: PostgreSQL cancelled a <c>store_inbox_messages</c> statement
/// (SQLSTATE <c>57014</c>, "canceling statement due to user request"). Npgsql surfaces that as
/// <see cref="OperationCanceledException"/>. The host's stopping token was NOT cancelled, so the
/// conventional guard — <c>catch (OperationCanceledException) when (token.IsCancellationRequested)</c>
/// — correctly declined to swallow it, and it escaped the worker's <c>ExecuteAsync</c>.
/// </para>
/// <para>
/// Under the default <c>BackgroundServiceExceptionBehavior.StopHost</c>, that stops the host. The
/// resulting shutdown is ORDERLY: exit code 0, every worker logging a clean "stopped", and not one
/// Error- or Critical-level line in the entire terminated container. Crash alerting sees nothing
/// because there was no crash; error-rate alerting sees nothing because nothing logged an error.
/// </para>
/// <para>
/// The shape is self-reinforcing: the statement times out because the table is large, and the table
/// is large because the service is behind — so the service most in need of making progress is the
/// one most likely to terminate itself, deepening the backlog for whoever resumes it.
/// </para>
/// </remarks>
/// <code-under-test>src/Whizbang.Core/Workers/TransportBatchFailureClassifier.cs</code-under-test>
[Category("Workers")]
public class TransportBatchFailureIsolationTests {

  /// <summary>Builds the exception Npgsql actually produces for a cancelled statement.</summary>
  private static OperationCanceledException _statementTimeout()
    => new("Query was cancelled", new PostgresException(
        messageText: "canceling statement due to user request",
        severity: "ERROR",
        invariantSeverity: "ERROR",
        sqlState: "57014"));

  [Test]
  public async Task AStatementTimeoutIsNotAShutdownRequestAsync() {
    var shuttingDown = new CancellationTokenSource();   // NOT cancelled

    var verdict = TransportBatchFailureClassifier.Classify(_statementTimeout(), shuttingDown.Token);

    await Assert.That(verdict).IsEqualTo(TransportBatchFailure.Transient)
      .Because("SQLSTATE 57014 is the DATABASE cancelling a statement, not the host asking to stop "
             + "— treating it as shutdown is what silently terminated a healthy process");
  }

  [Test]
  public async Task ARealShutdownIsStillRecognizedAsShutdownAsync() {
    using var shuttingDown = new CancellationTokenSource();
    await shuttingDown.CancelAsync();

    var verdict = TransportBatchFailureClassifier.Classify(
      new OperationCanceledException(shuttingDown.Token), shuttingDown.Token);

    await Assert.That(verdict).IsEqualTo(TransportBatchFailure.Shutdown)
      .Because("a genuine stop must still unwind promptly; blurring the two directions would trade "
             + "a silent-death bug for a hung-shutdown bug");
  }

  [Test]
  public async Task AnUnrelatedFaultIsTransientNotShutdownAsync() {
    var shuttingDown = new CancellationTokenSource();

    var verdict = TransportBatchFailureClassifier.Classify(
      new InvalidOperationException("connection reset"), shuttingDown.Token);

    await Assert.That(verdict).IsEqualTo(TransportBatchFailure.Transient)
      .Because("one bad batch must never be able to take the process with it — the broker will "
             + "redeliver, and a live host can retry it");
  }

  [Test]
  public async Task CancellationWithoutASignalledTokenIsTransientAsync() {
    var shuttingDown = new CancellationTokenSource();

    // A bare OperationCanceledException with no inner Postgres error and an un-signalled token:
    // still not a stop request, because nothing asked this host to stop.
    var verdict = TransportBatchFailureClassifier.Classify(
      new OperationCanceledException("timed out"), shuttingDown.Token);

    await Assert.That(verdict).IsEqualTo(TransportBatchFailure.Transient)
      .Because("the ONLY authority on whether a shutdown is underway is the stopping token; "
             + "inferring it from an exception type is how a timeout became a process exit");
  }

  [Test]
  public async Task TheClassifierRecognizesTheProductionSqlStateAsync() {
    await Assert.That(TransportBatchFailureClassifier.IsStatementCancellation(_statementTimeout()))
      .IsTrue()
      .Because("57014 is the unambiguous marker, and naming it explicitly is what lets an operator "
             + "connect a log line to the database behavior that caused it");
    await Assert.That(TransportBatchFailureClassifier.IsStatementCancellation(
        new OperationCanceledException("plain"))).IsFalse();
  }
}
