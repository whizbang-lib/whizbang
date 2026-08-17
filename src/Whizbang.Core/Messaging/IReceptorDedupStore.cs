using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Messaging;

/// <summary>
/// Abstraction for tracking prior receptor invocations so <see cref="ReceptorInvoker"/>
/// can enforce "exactly once per receptor per message" before firing.
/// </summary>
/// <remarks>
/// <para>
/// The default implementation (<see cref="EnvelopeReceptorDedupStore"/>) persists records
/// on the envelope itself via <see cref="IMessageEnvelope.ReceptorInvocations"/> — zero
/// external dependency, zero DB writes, and the records ride along with the message
/// through transport / inbox / outbox naturally.
/// </para>
/// <para>
/// A future database-backed implementation would key on
/// <see cref="IMessageEnvelope.MessageId"/> and receptor id — the interface is designed
/// so that swapping implementations is a DI registration change, not an API change in
/// <see cref="ReceptorInvoker"/>.
/// </para>
/// <para>
/// The interface uses <see cref="ValueTask"/> so synchronous (envelope-backed) and
/// asynchronous (DB-backed) implementations both return without allocating on the hot
/// path when the synchronous path dominates.
/// </para>
/// </remarks>
/// <docs>fundamentals/receptors/exactly-once-firing</docs>
/// <tests>tests/Whizbang.Core.Tests/Messaging/IReceptorDedupStoreContractTests.cs:Contract_RecordInvocationThenTryGet_ReturnsRecordedValueAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Messaging/IReceptorDedupStoreContractTests.cs:Contract_TryGetPriorInvocation_ReturnsNullForUnseenReceptorAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Messaging/IReceptorDedupStoreContractTests.cs:Contract_PriorInvocationIsReturnedRegardlessOfStageAsync</tests>
public interface IReceptorDedupStore {
  /// <summary>
  /// Returns a prior <see cref="ReceptorInvocationRecord"/> for <paramref name="receptorId"/>
  /// on the envelope's message, or null if the receptor has not fired for this message yet.
  /// </summary>
  /// <remarks>
  /// The check is per-receptor, not per-stage: a receptor that fired at
  /// <see cref="LifecycleStage.LocalImmediateInline"/> must be blocked from re-firing at
  /// <see cref="LifecycleStage.PreOutboxInline"/> for the same message unless it declares
  /// itself idempotent.
  /// </remarks>
  ValueTask<ReceptorInvocationRecord?> TryGetPriorInvocationAsync(
    IMessageEnvelope envelope,
    string receptorId,
    CancellationToken cancellationToken);

  /// <summary>
  /// Records that a receptor has successfully fired for this envelope.
  /// </summary>
  /// <remarks>
  /// Called after the receptor delegate returns successfully. On exception the invoker
  /// intentionally does NOT record so a retry can re-fire.
  /// </remarks>
  ValueTask RecordInvocationAsync(
    IMessageEnvelope envelope,
    ReceptorInvocationRecord record,
    CancellationToken cancellationToken);
}
