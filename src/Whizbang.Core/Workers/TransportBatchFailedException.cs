namespace Whizbang.Core.Workers;

/// <summary>
/// The signal a transport receives when the consumer failed to handle a batch: the batch must NOT be
/// settled as consumed. The transport abandons (redelivers) or dead-letters its messages, so the
/// broker's max delivery count bounds the retries and the broker dead-letter queue, which the
/// transport dead-letter drain imports, receives what never succeeds.
/// </summary>
/// <remarks>
/// <para>
/// Transports decide settlement by whether the batch handler threw. A consumer that swallowed a failed
/// batch therefore had every "abandoned" batch completed and lost (#921). The failure is reported as
/// this type rather than rethrown raw because a database statement timeout surfaces as an
/// <see cref="OperationCanceledException"/>: passed on as itself, it reads as a shutdown to every
/// "when not canceled" filter between the handler and the host. This type is never a cancellation.
/// </para>
/// <para>
/// A real host shutdown is never wrapped; it propagates as itself.
/// </para>
/// </remarks>
/// <param name="batchCount">How many messages the failed batch carried.</param>
/// <param name="innerException">What failed.</param>
/// <exception cref="ArgumentNullException"><paramref name="innerException"/> is null.</exception>
/// <docs>messaging/transports/transport-consumer#failed-batches</docs>
/// <tests>tests/Whizbang.Core.Tests/Workers/TransportBatchGuardTests.cs</tests>
/// <tests>tests/Whizbang.Core.Tests/Workers/TransportConsumerWorkerBatchFailureTests.cs</tests>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Roslynator", "RCS1194:Implement exception constructors",
  Justification = "The signal always carries the failed batch's size and its cause; a parameterless or message-only instance would report a failure with nothing to settle on and nothing to diagnose.")]
public sealed class TransportBatchFailedException(int batchCount, Exception innerException)
  : Exception(_format(batchCount, innerException), innerException) {

  private static string _format(int batchCount, Exception innerException) {
    ArgumentNullException.ThrowIfNull(innerException);
    return $"Transport batch of {batchCount} message(s) failed and must not be settled as consumed: {innerException.Message}";
  }

  /// <summary>How many messages the failed batch carried.</summary>
  public int BatchCount { get; } = batchCount;
}
