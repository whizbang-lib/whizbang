using Whizbang.Sagas.Models;

namespace Whizbang.Sagas.Helpers;

/// <summary>
/// Reconciles a saga's per-item completion against the durable event
/// store when the per-item projection
/// (<see cref="SagaItemModel"/>) cannot be trusted.
/// </summary>
/// <remarks>
/// <para>
/// The per-item-stream design routes each item to its own stream so
/// the aggregate "how many done?" is a race-free GROUP BY over the
/// projection. But the projection row itself can still be left in a
/// non-terminal state by a <strong>cross-pod lost-update</strong>: two
/// pods apply the same per-item stream and a stale
/// <c>SagaItemStartedEvent</c>-only write lands after the
/// <c>SagaItemCompletedEvent</c> write, reverting the row to
/// <see cref="SagaItemState.Running"/> — even though the terminal
/// event is durably committed in <c>wh_event_store</c>.
/// </para>
/// <para>
/// Completion must therefore be authoritative against the durable
/// event store, not the derived projection. This walks the
/// projection's non-terminal items and checks each item's per-item
/// stream for a terminal event via an <see cref="ISagaItemTerminalReader"/>
/// adapter; if every item is terminal in the store it returns the
/// reconciled <c>(completed, failed)</c> split, otherwise <c>null</c>
/// (genuinely still in progress). Invoked only on the slow path —
/// when every item already has a projection row but some are
/// non-terminal — so the common case keeps using the cheap aggregate.
/// </para>
/// </remarks>
public static class SagaItemCompletionReconciler {

  /// <summary>
  /// The completion decision for a per-item saga, robust to stranded
  /// projection rows. Returns the <c>(completed, failed)</c> counts to
  /// stamp on the terminal event when the saga is done, or <c>null</c>
  /// when it is genuinely still in progress.
  /// </summary>
  /// <remarks>
  /// Fast path: when the projection aggregate already shows every item
  /// terminal, its counts are used directly (no event-store reads).
  /// Mid fan-out (<c>agg.Total &lt; totalItems</c>) returns
  /// <c>null</c> without touching the event store. Slow path: when
  /// every item has a projection row but some are non-terminal, the
  /// non-terminal rows are reconciled via
  /// <see cref="ReconcileTerminalCountsAsync"/>.
  /// </remarks>
  public static async Task<(int Completed, int Failed)?> ResolveCompletionCountsAsync(
      Guid sagaId,
      int totalItems,
      SagaItemAggregate agg,
      Func<CancellationToken, Task<IReadOnlyList<SagaItemModel>>> getItems,
      ISagaItemTerminalReader terminalReader,
      CancellationToken cancellationToken) {
    ArgumentNullException.ThrowIfNull(agg);
    ArgumentNullException.ThrowIfNull(getItems);
    ArgumentNullException.ThrowIfNull(terminalReader);

    if (agg.InProgress == 0 && agg.Completed + agg.Failed >= totalItems) {
      return (agg.Completed, agg.Failed);
    }
    if (agg.Total < totalItems) {
      return null;   // still dispatching / processing — not every item has a row yet
    }
    var items = await getItems(cancellationToken).ConfigureAwait(false);
    var reconciled = await ReconcileTerminalCountsAsync(sagaId, items, terminalReader, cancellationToken).ConfigureAwait(false);
    if (reconciled is null || reconciled.Value.Completed + reconciled.Value.Failed < totalItems) {
      return null;
    }
    return reconciled;
  }

  /// <summary>
  /// Returns the event-store-true <c>(completed, failed)</c> counts for
  /// <paramref name="items"/>, treating a non-terminal projection row
  /// as terminal when its per-item stream carries a terminal event.
  /// Returns <c>null</c> if any non-terminal item has no terminal event
  /// in the store (genuinely in progress) — the caller must then NOT
  /// complete.
  /// </summary>
  public static async Task<(int Completed, int Failed)?> ReconcileTerminalCountsAsync(
      Guid sagaId,
      IReadOnlyList<SagaItemModel> items,
      ISagaItemTerminalReader terminalReader,
      CancellationToken cancellationToken) {
    ArgumentNullException.ThrowIfNull(items);
    ArgumentNullException.ThrowIfNull(terminalReader);

    var completed = items.Count(i => i.State == SagaItemState.Completed);
    var failed = items.Count(i => i.State == SagaItemState.Failed);

    foreach (var item in items) {
      if (item.IsTerminal) {
        continue;
      }

      var streamId = SagaItemStreams.Of(sagaId, item.ItemIdentifier);
      var outcome = await terminalReader.CheckAsync(streamId, cancellationToken).ConfigureAwait(false);

      switch (outcome) {
        case SagaItemTerminalOutcome.NotTerminal:
          return null;   // genuinely still in progress
        case SagaItemTerminalOutcome.Completed:
          completed++;
          break;
        case SagaItemTerminalOutcome.Failed:
          failed++;
          break;
      }
    }

    return (completed, failed);
  }
}
