using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Coverage for the flush-delegation paths of <see cref="StreamAffinityWorkCoordinatorStrategy"/>
/// that <see cref="StreamAffinityWorkCoordinatorStrategyTests"/> does not exercise: the plain
/// <see cref="IWorkCoordinatorStrategy.FlushAsync"/> / <see cref="IWorkCoordinatorStrategy.FlushAndGetBatchAsync"/>
/// pass-throughs, and both branches of the explicit <see cref="IWorkFlusher"/> forwarding — inner
/// strategy implements <see cref="IWorkFlusher"/> itself vs. falls back to the standard flush.
/// </summary>
/// <remarks>
/// Only outbox writes are stream-affinity-batched; every other member — including both flush entry
/// points — must reach the inner strategy completely unchanged. A break here would not corrupt
/// ordering directly, but it would silently strand queued inbox/outbox state: a caller (shutdown
/// drain, end-of-request middleware) that believes it just persisted everything via this wrapper
/// would instead have flushed nothing, because the call never reached the inner strategy that
/// actually owns persistence.
/// </remarks>
public class StreamAffinityWorkCoordinatorStrategyCoverageTests {

  // If FlushAsync(WorkBatchOptions, CancellationToken) stopped forwarding to the inner strategy,
  // a fire-and-forget flush issued through this wrapper would silently do nothing — the caller
  // gets a completed Task back with no exception, but nothing was ever asked to persist.
  [Test]
  public async Task FlushAsync_DelegatesToInnerStrategyWithSameFlagsAndTokenAsync() {
    await using var batch = new SlidingWindowOutboxBatchStrategy(flush: (_, _) => Task.CompletedTask);
    var inner = new RecordingFlushInner();
    var sut = new StreamAffinityWorkCoordinatorStrategy(inner, batch);
    using var cts = new CancellationTokenSource();

    await sut.FlushAsync(WorkBatchOptions.SkipInboxClaiming, cts.Token);

    await Assert.That(inner.FlushAsyncCallCount).IsEqualTo(1)
      .Because("FlushAsync must forward straight to the inner strategy — the decorator only intercepts outbox queuing, never the flush signal itself.");
    await Assert.That(inner.LastFlushFlags).IsEqualTo(WorkBatchOptions.SkipInboxClaiming)
      .Because("the caller's flags must reach the inner strategy verbatim; silently normalizing them could disable a caller's SkipInboxClaiming intent.");
    await Assert.That(inner.LastFlushCt).IsEqualTo(cts.Token);
  }

  // If FlushAndGetBatchAsync substituted its own batch instead of returning the inner strategy's,
  // a dedup consumer (e.g. an inbox handler filtering by MessageId) would process against the wrong
  // — or an always-empty — WorkBatch, silently dropping the very work it just flushed.
  [Test]
  public async Task FlushAndGetBatchAsync_DelegatesToInnerStrategyAndReturnsItsBatchAsync() {
    await using var batch = new SlidingWindowOutboxBatchStrategy(flush: (_, _) => Task.CompletedTask);
    var expectedBatch = new WorkBatch { OutboxWork = [], InboxWork = [], PerspectiveWork = [] };
    var inner = new RecordingFlushInner { BatchToReturn = expectedBatch };
    var sut = new StreamAffinityWorkCoordinatorStrategy(inner, batch);
    using var cts = new CancellationTokenSource();

    var actualBatch = await sut.FlushAndGetBatchAsync(WorkBatchOptions.None, cts.Token);

    await Assert.That(inner.FlushAndGetBatchCallCount).IsEqualTo(1)
      .Because("callers that consume the returned WorkBatch must reach the inner strategy that actually claims/persists work.");
    await Assert.That(actualBatch).IsSameReferenceAs(expectedBatch)
      .Because("the wrapper must hand back exactly what the inner strategy produced, not a batch of its own.");
    await Assert.That(inner.LastFlushAndGetBatchCt).IsEqualTo(cts.Token);
  }

  // Middleware resolves IWorkCoordinatorStrategy and casts to IWorkFlusher (WhizbangFlushMiddleware).
  // When the inner strategy itself implements IWorkFlusher, the wrapper must call THAT — not
  // synthesize a WorkBatchOptions.None flush that could behave differently (e.g. skip a step the
  // inner IWorkFlusher implementation performs).
  [Test]
  public async Task IWorkFlusher_FlushAsync_InnerImplementsIWorkFlusher_DelegatesToInnersIWorkFlusherAsync() {
    await using var batch = new SlidingWindowOutboxBatchStrategy(flush: (_, _) => Task.CompletedTask);
    var inner = new RecordingFlusherInner();
    var sut = new StreamAffinityWorkCoordinatorStrategy(inner, batch);
    var flusher = (IWorkFlusher)sut;
    using var cts = new CancellationTokenSource();

    await flusher.FlushAsync(cts.Token);

    await Assert.That(inner.WorkFlusherFlushCallCount).IsEqualTo(1)
      .Because("the inner strategy's own IWorkFlusher implementation is the more specific contract and must win over the generic FlushAsync(WorkBatchOptions, ct) fallback.");
    await Assert.That(inner.FlushAsyncCallCount).IsEqualTo(0)
      .Because("routing through the inner IWorkFlusher must not ALSO invoke the plain FlushAsync overload — that would flush the same queued work twice.");
  }

  // When the inner strategy has no IWorkFlusher implementation of its own, the wrapper's explicit
  // IWorkFlusher.FlushAsync must still reach persistence via the standard FlushAsync(None, ct)
  // overload — otherwise a strategy that never opted into IWorkFlusher would silently never flush
  // when middleware calls through this hook, and queued work would sit unflushed indefinitely.
  [Test]
  public async Task IWorkFlusher_FlushAsync_InnerDoesNotImplementIWorkFlusher_FallsBackToFlushAsyncWithNoneFlagsAsync() {
    await using var batch = new SlidingWindowOutboxBatchStrategy(flush: (_, _) => Task.CompletedTask);
    var inner = new RecordingFlushInner();
    var sut = new StreamAffinityWorkCoordinatorStrategy(inner, batch);
    var flusher = (IWorkFlusher)sut;
    using var cts = new CancellationTokenSource();

    await flusher.FlushAsync(cts.Token);

    await Assert.That(inner.FlushAsyncCallCount).IsEqualTo(1)
      .Because("without an inner IWorkFlusher, the wrapper's fallback must still call the standard flush so queued work is not silently stranded.");
    await Assert.That(inner.LastFlushFlags).IsEqualTo(WorkBatchOptions.None)
      .Because("the fallback flush must use WorkBatchOptions.None per the documented contract — not whatever flags happen to be lying around.");
    await Assert.That(inner.LastFlushCt).IsEqualTo(cts.Token);
  }

  // ===== fakes =====

  /// <summary>Minimal <see cref="IWorkCoordinatorStrategy"/> double that records flush calls;
  /// queue-side methods are no-ops since the stream-affinity wrapper never forwards outbox
  /// queuing to the inner strategy and the other queue methods are already locked by
  /// <see cref="StreamAffinityWorkCoordinatorStrategyTests"/>.</summary>
  private class RecordingFlushInner : IWorkCoordinatorStrategy {
    public int FlushAsyncCallCount;
    public WorkBatchOptions LastFlushFlags;
    public CancellationToken LastFlushCt;
    public int FlushAndGetBatchCallCount;
    public CancellationToken LastFlushAndGetBatchCt;
    public WorkBatch BatchToReturn = new() { OutboxWork = [], InboxWork = [], PerspectiveWork = [] };

    public void QueueOutboxMessage(OutboxMessage message) { }
    public void QueueInboxMessage(InboxMessage message) { }
    public void QueueOutboxCompletion(Guid messageId, MessageProcessingStatus completedStatus) { }
    public void QueueInboxCompletion(Guid messageId, MessageProcessingStatus completedStatus) { }
    public void QueueOutboxFailure(Guid messageId, MessageProcessingStatus completedStatus, string errorMessage) { }
    public void QueueInboxFailure(Guid messageId, MessageProcessingStatus completedStatus, string errorMessage) { }

    public Task FlushAsync(WorkBatchOptions flags, CancellationToken ct = default) {
      FlushAsyncCallCount++;
      LastFlushFlags = flags;
      LastFlushCt = ct;
      return Task.CompletedTask;
    }

    public Task<WorkBatch> FlushAndGetBatchAsync(WorkBatchOptions flags, CancellationToken ct = default) {
      FlushAndGetBatchCallCount++;
      LastFlushAndGetBatchCt = ct;
      return Task.FromResult(BatchToReturn);
    }
  }

  /// <summary>Same as <see cref="RecordingFlushInner"/> but also implements <see cref="IWorkFlusher"/>,
  /// for testing the branch where the wrapper forwards to the inner strategy's own implementation.</summary>
  private sealed class RecordingFlusherInner : RecordingFlushInner, IWorkFlusher {
    public int WorkFlusherFlushCallCount;

    Task IWorkFlusher.FlushAsync(CancellationToken ct) {
      WorkFlusherFlushCallCount++;
      return Task.CompletedTask;
    }
  }
}
