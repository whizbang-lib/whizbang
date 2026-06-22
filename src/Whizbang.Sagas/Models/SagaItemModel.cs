using Whizbang.Core;

namespace Whizbang.Sagas.Models;

/// <summary>
/// Per-item read-model row: one row per <c>(saga, item)</c>, keyed by
/// the per-item stream id (<see cref="SagaItemStreams.Of(Guid, string)"/>).
/// A tiny, total, deterministic state machine — no growing lists, no
/// counters. "How many items completed?" becomes a lens query over
/// these rows rather than a mutable tally on the saga row, which is
/// what removes the cross-pod lost-update race.
/// </summary>
public class SagaItemModel : ISagaItem {

  /// <summary>Per-item stream id — derived from <c>(SagaId, ItemIdentifier)</c>.</summary>
  [StreamId]
  public Guid Id { get; set; }

  /// <summary>Owning saga's stream id. The link back to the parent aggregate.</summary>
  public Guid SagaId { get; set; }

  /// <summary>Saga name (e.g. <c>"BulkJobImport"</c>); matches the value passed to <c>[Saga("Name")]</c>.</summary>
  public string SagaName { get; set; } = string.Empty;

  /// <summary>The item's identifier within the saga (line number, field name, job id, …).</summary>
  public string ItemIdentifier { get; set; } = string.Empty;

  /// <summary>
  /// Human-readable label carried on the per-item events. Lets per-item
  /// dashboards render a friendly name without the saga row storing a
  /// per-item list. Null until an event supplies it.
  /// </summary>
  public string? DisplayName { get; set; }

  /// <summary>Current state.</summary>
  public SagaItemState State { get; set; } = SagaItemState.Pending;

  /// <summary>Wall-clock timestamp when the item moved to <see cref="SagaItemState.Running"/>.</summary>
  public DateTimeOffset? StartedAt { get; set; }

  /// <summary>Wall-clock timestamp when the item moved to <see cref="SagaItemState.Completed"/>.</summary>
  public DateTimeOffset? CompletedAt { get; set; }

  /// <summary>Wall-clock timestamp when the item moved to <see cref="SagaItemState.Failed"/>.</summary>
  public DateTimeOffset? FailedAt { get; set; }

  /// <summary>Short human-readable failure reason.</summary>
  public string? ErrorMessage { get; set; }

  /// <summary>Optional structured error details (stack trace, inner exception payload).</summary>
  public string? ErrorDetails { get; set; }

  /// <summary>Times a Started event has been observed (retries/rebalances). Audit only.</summary>
  public int AttemptCount { get; set; }

  /// <summary>Wall-clock timestamp when the row was first inserted.</summary>
  public DateTimeOffset CreatedAt { get; set; }

  /// <summary>Wall-clock timestamp of the last applied event.</summary>
  public DateTimeOffset UpdatedAt { get; set; }

  /// <summary>
  /// True once the item is in a terminal state (Completed, Failed, or
  /// Skipped). Skipped is included by design — a skipped item must not
  /// block the saga from completing.
  /// </summary>
  public bool IsTerminal => State is SagaItemState.Completed or SagaItemState.Failed or SagaItemState.Skipped;

  // ── ISagaItem explicit mapping ───────────────────────────────────────

  /// <summary>Maps to <see cref="State"/>; <see cref="ISagaItem"/> consumers see the same value.</summary>
  SagaItemState ISagaItem.Status => State;
}
