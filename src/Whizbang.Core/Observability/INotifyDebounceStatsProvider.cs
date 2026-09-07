using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Whizbang.Core.Observability;

/// <summary>
/// Provides a snapshot of the adaptive doorbell-debounce controller's state per payload kind, read
/// from <c>wh_notify_state</c> (migration 137). Implementations use a database-specific aggregate
/// query. Feeds <see cref="NotifyDebounceMetrics"/> through <see cref="NotifyDebounceStatsCollector"/>.
/// </summary>
/// <docs>operations/observability/metrics#notify-debounce</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PostgresNotifyDebounceStatsProviderTests.cs</tests>
public interface INotifyDebounceStatsProvider {
  /// <summary>
  /// Returns one reading per payload kind currently tracked in the debounce-state table: the
  /// cumulative fired/suppressed doorbell counts (summed across live target rows) and the current
  /// worst-case regime (the largest effective window and the deepest rapid-run depth).
  /// </summary>
  /// <param name="ct">Cancellation token.</param>
  /// <returns>One reading per payload kind; empty when the table is empty.</returns>
  Task<IReadOnlyList<NotifyDebounceKindStats>> GetStatsAsync(CancellationToken ct = default);
}

/// <summary>One payload kind's adaptive-debounce reading.</summary>
/// <param name="PayloadKind">The work kind the doorbell targets (<c>inbox</c>, <c>outbox</c>, <c>perspective</c>).</param>
/// <param name="FiredCount">Doorbells fired for this kind, summed across live target rows.</param>
/// <param name="SuppressedCount">Doorbells suppressed (debounced) for this kind, summed across live rows.</param>
/// <param name="MaxEffectiveWindowMs">The largest current effective suppression window across targets of this kind — the floor means real-time delivery, the ceiling means an active flood.</param>
/// <param name="MaxRapidRun">The deepest current rapid-run depth across targets of this kind — how sustained the flood is.</param>
public readonly record struct NotifyDebounceKindStats(
  string PayloadKind,
  long FiredCount,
  long SuppressedCount,
  int MaxEffectiveWindowMs,
  int MaxRapidRun);
