namespace Whizbang.Core.Workers;

/// <summary>
/// How a transport batch failure should be treated.
/// </summary>
public enum TransportBatchFailure {
  /// <summary>The host is stopping; unwind promptly.</summary>
  Shutdown,

  /// <summary>One batch failed. Log it, let the broker redeliver, keep the host alive.</summary>
  Transient,
}

/// <summary>
/// Decides whether a transport batch failure is a shutdown or a transient fault.
/// </summary>
/// <remarks>
/// <para>
/// Exists because the two are indistinguishable by exception TYPE. PostgreSQL cancelling a
/// statement (SQLSTATE <c>57014</c>) surfaces through Npgsql as
/// <see cref="OperationCanceledException"/> — the same type a shutdown produces.
/// </para>
/// <para>
/// Observed in production: a <c>store_inbox_messages</c> timeout escaped the transport worker's
/// <c>ExecuteAsync</c> and, under the default <c>BackgroundServiceExceptionBehavior.StopHost</c>,
/// stopped the host. The shutdown was orderly — exit code 0, every worker logging a clean stop, and
/// not one Error-level line in the terminated container. Crash alerting saw no crash; error-rate
/// alerting saw no error.
/// </para>
/// <para>
/// The rule is deliberately narrow: <b>the stopping token is the only authority on whether a
/// shutdown is underway.</b> Inferring intent from an exception type is exactly what turned a
/// database timeout into a process exit, so this classifier refuses to do it.
/// </para>
/// </remarks>
/// <docs>operations/workers/transport-consumer</docs>
/// <tests>tests/Whizbang.Core.Tests/Workers/TransportBatchFailureIsolationTests.cs</tests>
public static class TransportBatchFailureClassifier {

  /// <summary>PostgreSQL SQLSTATE for a statement canceled by request (typically a timeout).</summary>
#pragma warning disable CA1707
  public const string SQLSTATE_STATEMENT_CANCELED = "57014";
#pragma warning restore CA1707

  /// <summary>
  /// Classifies a batch failure.
  /// </summary>
  /// <param name="exception">The fault raised while handling the batch.</param>
  /// <param name="stoppingToken">The worker's stopping token — the sole shutdown authority.</param>
  /// <returns>
  /// <see cref="TransportBatchFailure.Shutdown"/> only when the token is signalled; otherwise
  /// <see cref="TransportBatchFailure.Transient"/>.
  /// </returns>
  public static TransportBatchFailure Classify(Exception exception, CancellationToken stoppingToken) {
    ArgumentNullException.ThrowIfNull(exception);

    // Deliberately the ONLY condition. An OperationCanceledException with an un-signalled token is
    // something else cancelling something else — most often the database timing out a statement —
    // and treating it as a stop request is the defect this type exists to prevent.
    return stoppingToken.IsCancellationRequested
      ? TransportBatchFailure.Shutdown
      : TransportBatchFailure.Transient;
  }

  /// <summary>
  /// True when the fault is a PostgreSQL statement cancellation.
  /// </summary>
  /// <remarks>
  /// Reported separately from <see cref="Classify"/> so the log line can name the actual database
  /// behavior. "A batch failed" sends an operator looking at the message; "the database canceled
  /// the statement, SQLSTATE 57014" sends them to the query and its timeout, which is where the
  /// problem is.
  /// </remarks>
  /// <param name="exception">The fault to inspect.</param>
  /// <returns>True when this or any inner exception carries SQLSTATE <c>57014</c>.</returns>
  /// <remarks>
  /// Reads <see cref="Exception.Data"/> rather than a <c>SqlState</c> property. Core is
  /// zero-reflection and AOT-compatible, so it can neither reference the provider's exception type
  /// nor look the property up at runtime — and the provider already publishes the SQLSTATE into
  /// <c>Data</c> (it is what renders as "Exception data:" in a logged stack).
  /// </remarks>
  public static bool IsStatementCancellation(Exception? exception) {
    for (var e = exception; e is not null; e = e.InnerException) {
      if (e.Data.Contains("SqlState")
          && string.Equals(e.Data["SqlState"] as string, SQLSTATE_STATEMENT_CANCELED, StringComparison.Ordinal)) {
        return true;
      }
    }
    return false;
  }
}
