using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Sagas.Helpers;
using Whizbang.Sagas.Models;

namespace Whizbang.Sagas.Tests;

/// <summary>
/// Locks the durable-event-store reconciliation path. When a per-item
/// projection row is stranded in a non-terminal state by a cross-pod
/// lost-update — the terminal event is in <c>wh_event_store</c> but the
/// projection's UPDATE was overwritten by a stale Started — completion
/// detection must consult the event store directly. Without this, the
/// saga would never complete despite all items genuinely being done.
/// </summary>
[Category("Unit")]
[Category("Saga")]
public class SagaItemCompletionReconcilerTests {

  private static readonly Guid _sagaId = Guid.Parse("11111111-1111-1111-1111-111111111111");

  // ── Fast path: aggregate already shows everything terminal ───────────

  [Test]
  public async Task ResolveCompletionCounts_AggregateShowsAllTerminal_UsesAggregateWithoutEventStoreAsync() {
    var reader = new ThrowingReader();
    var agg = new SagaItemAggregate(Total: 10, Completed: 9, Failed: 1, InProgress: 0);

    var result = await SagaItemCompletionReconciler.ResolveCompletionCountsAsync(
      _sagaId, totalItems: 10, agg,
      getItems: _ => throw new InvalidOperationException("should not be called"),
      reader, CancellationToken.None);

    await Assert.That(result).IsNotNull();
    await Assert.That(result!.Value.Completed).IsEqualTo(9);
    await Assert.That(result.Value.Failed).IsEqualTo(1)
      .Because("Fast path avoids the event-store walk when the projection's aggregate already shows the saga is done. The reader being throwing proves no read happened.");
  }

  // ── Still dispatching ────────────────────────────────────────────────

  [Test]
  public async Task ResolveCompletionCounts_AggregateTotalLessThanItems_ReturnsNullAsync() {
    var reader = new ThrowingReader();
    var agg = new SagaItemAggregate(Total: 5, Completed: 5, Failed: 0, InProgress: 0);

    var result = await SagaItemCompletionReconciler.ResolveCompletionCountsAsync(
      _sagaId, totalItems: 10, agg,
      getItems: _ => throw new InvalidOperationException("should not be called"),
      reader, CancellationToken.None);

    await Assert.That(result).IsNull()
      .Because("Mid-dispatch: only 5 of 10 items have projection rows — neither the aggregate nor the event store can determine completion yet. The reader being throwing proves no read happened.");
  }

  // ── Reconciliation path: stranded non-terminal row → store says done ─

  [Test]
  public async Task ResolveCompletionCounts_StrandedItem_ButEventStoreSaysCompleted_ReconcilesAsync() {
    // Three items: 2 projection rows show Completed; 1 is stranded in Running.
    // Event store for the stranded item's per-item stream carries a Completed event.
    // The reconciler must report (3, 0) — all done.
    var items = new List<SagaItemModel> {
      new() { ItemIdentifier = "a", State = SagaItemState.Completed },
      new() { ItemIdentifier = "b", State = SagaItemState.Completed },
      new() { ItemIdentifier = "c", State = SagaItemState.Running },  // stranded
    };
    var agg = new SagaItemAggregate(Total: 3, Completed: 2, Failed: 0, InProgress: 1);

    var reader = new StubReader(_sagaId, new Dictionary<string, SagaItemTerminalOutcome> {
      { "c", SagaItemTerminalOutcome.Completed },
    });

    var result = await SagaItemCompletionReconciler.ResolveCompletionCountsAsync(
      _sagaId, totalItems: 3, agg,
      getItems: _ => Task.FromResult<IReadOnlyList<SagaItemModel>>(items),
      reader, CancellationToken.None);

    await Assert.That(result).IsNotNull();
    await Assert.That(result!.Value.Completed).IsEqualTo(3)
      .Because("Reconciler trusts the event store over the stranded projection row. Without this path the saga's lost-update strand becomes permanent and it never completes.");
    await Assert.That(result.Value.Failed).IsEqualTo(0);
  }

  // ── Reconciliation path: store says failed (mixed terminal) ──────────

  [Test]
  public async Task ResolveCompletionCounts_StrandedItem_StoreSaysFailed_CountsAsFailedAsync() {
    var items = new List<SagaItemModel> {
      new() { ItemIdentifier = "a", State = SagaItemState.Completed },
      new() { ItemIdentifier = "b", State = SagaItemState.Running },
    };
    var agg = new SagaItemAggregate(Total: 2, Completed: 1, Failed: 0, InProgress: 1);

    var reader = new StubReader(_sagaId, new Dictionary<string, SagaItemTerminalOutcome> {
      { "b", SagaItemTerminalOutcome.Failed },
    });

    var result = await SagaItemCompletionReconciler.ResolveCompletionCountsAsync(
      _sagaId, totalItems: 2, agg,
      getItems: _ => Task.FromResult<IReadOnlyList<SagaItemModel>>(items),
      reader, CancellationToken.None);

    await Assert.That(result).IsNotNull();
    await Assert.That(result!.Value.Completed).IsEqualTo(1);
    await Assert.That(result.Value.Failed).IsEqualTo(1)
      .Because("Reconciled outcome of a stranded row is whichever terminal type the event store has — Failed here.");
  }

  // ── Genuinely in progress: store has no terminal event ───────────────

  [Test]
  public async Task ResolveCompletionCounts_StrandedItem_NoTerminalEventInStore_ReturnsNullAsync() {
    var items = new List<SagaItemModel> {
      new() { ItemIdentifier = "a", State = SagaItemState.Completed },
      new() { ItemIdentifier = "b", State = SagaItemState.Running },  // genuinely still running
    };
    var agg = new SagaItemAggregate(Total: 2, Completed: 1, Failed: 0, InProgress: 1);

    var reader = new StubReader(_sagaId, new Dictionary<string, SagaItemTerminalOutcome> {
      { "b", SagaItemTerminalOutcome.NotTerminal },
    });

    var result = await SagaItemCompletionReconciler.ResolveCompletionCountsAsync(
      _sagaId, totalItems: 2, agg,
      getItems: _ => Task.FromResult<IReadOnlyList<SagaItemModel>>(items),
      reader, CancellationToken.None);

    await Assert.That(result).IsNull()
      .Because("If the event store has no terminal event for a non-terminal row, the item is genuinely still in flight — saga must NOT complete.");
  }

  // ── Stubs ────────────────────────────────────────────────────────────

  private sealed class ThrowingReader : ISagaItemTerminalReader {
    public Task<SagaItemTerminalOutcome> CheckAsync(Guid perItemStreamId, CancellationToken cancellationToken) =>
      throw new InvalidOperationException("Reconciler should not have touched the event store on this code path.");
  }

  private sealed class StubReader(Guid expectedSagaId, IDictionary<string, SagaItemTerminalOutcome> outcomesByItemId) : ISagaItemTerminalReader {
    public Task<SagaItemTerminalOutcome> CheckAsync(Guid perItemStreamId, CancellationToken cancellationToken) {
      foreach (var (itemId, outcome) in outcomesByItemId) {
        if (SagaItemStreams.Of(expectedSagaId, itemId) == perItemStreamId) {
          return Task.FromResult(outcome);
        }
      }
      throw new InvalidOperationException($"Reconciler queried unknown per-item stream {perItemStreamId}.");
    }
  }
}
