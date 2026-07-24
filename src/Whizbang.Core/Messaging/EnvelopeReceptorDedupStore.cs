using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Messaging;

/// <summary>
/// Default <see cref="IReceptorDedupStore"/>. Reads and writes
/// <see cref="IMessageEnvelope.ReceptorInvocations"/> directly — no external state, no
/// DB traffic, records ride along with the message.
/// </summary>
/// <remarks>
/// <para>
/// Thread-safety: the invocations list is mutable and this store does not synchronize
/// access. That is safe because a single envelope is processed single-threaded
/// end-to-end — the same invariant that lets <see cref="IMessageEnvelope.Hops"/> be a
/// plain <c>List&lt;MessageHop&gt;</c>. Concurrent processing of different messages is
/// fine; concurrent mutation of the SAME envelope is a caller bug.
/// </para>
/// <para>
/// Recovery boundary: records only become durable when the envelope next hits a durable
/// write (outbox row, inbox row, or event-store append). An invocation recorded in-memory
/// on the pre-outbox path is lost on process crash — an explicit, documented tradeoff of
/// the "no hot-path DB writes" design.
/// </para>
/// </remarks>
/// <docs>fundamentals/receptors/exactly-once-firing</docs>
/// <tests>tests/Whizbang.Core.Tests/Messaging/EnvelopeReceptorDedupStoreTests.cs</tests>
public sealed class EnvelopeReceptorDedupStore : IReceptorDedupStore {
  /// <inheritdoc />
  public ValueTask<ReceptorInvocationRecord?> TryGetPriorInvocationAsync(
    IMessageEnvelope envelope,
    string receptorId,
    CancellationToken cancellationToken) {
    var list = envelope.ReceptorInvocations;
    if (list is null || list.Count == 0) {
      return ValueTask.FromResult<ReceptorInvocationRecord?>(null);
    }
    for (int i = 0; i < list.Count; i++) {
      if (string.Equals(list[i].ReceptorId, receptorId, System.StringComparison.Ordinal)) {
        return ValueTask.FromResult<ReceptorInvocationRecord?>(list[i]);
      }
    }
    return ValueTask.FromResult<ReceptorInvocationRecord?>(null);
  }

  /// <inheritdoc />
  public ValueTask RecordInvocationAsync(
    IMessageEnvelope envelope,
    ReceptorInvocationRecord record,
    CancellationToken cancellationToken) {
    envelope.GetOrCreateReceptorInvocations().Add(record);
    return ValueTask.CompletedTask;
  }
}
