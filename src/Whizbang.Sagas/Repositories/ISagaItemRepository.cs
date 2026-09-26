using Whizbang.Sagas.Models;

namespace Whizbang.Sagas.Repositories;

/// <summary>
/// Read surface over the per-item projection (<see cref="SagaItemModel"/>).
/// Backs <see cref="Helpers.SagaLiveProgressResolvers"/> and any
/// completion logic that needs to query "what's the current count of
/// completed/failed items for this saga?" without loading every row.
/// </summary>
/// <remarks>
/// Consumers either implement this directly over their data layer (EF
/// Core, Dapper, raw SQL) or use a Whizbang-shipped concrete
/// implementation built on <c>ILensQuery&lt;SagaItemModel&gt;</c>.
/// </remarks>
public interface ISagaItemRepository {

  /// <summary>
  /// Single-round-trip GROUP BY state aggregate. Backs live-progress
  /// resolvers — calling this once per dashboard refresh is cheaper than
  /// loading every per-item row.
  /// </summary>
  Task<SagaItemAggregate> GetAggregateForSagaAsync(Guid sagaId, CancellationToken cancellationToken);

  /// <summary>
  /// All item rows for a saga, ordered however the underlying data layer
  /// orders them (typically by item identifier or by insertion). Used by
  /// dashboard detail views and by
  /// <see cref="Helpers.SagaItemCompletionReconciler"/>.
  /// </summary>
  Task<IReadOnlyList<SagaItemModel>> GetItemsAsync(Guid sagaId, CancellationToken cancellationToken);

  /// <summary>When any of the saga's items last changed, or <see langword="null"/> when it has none.</summary>
  /// <remarks>
  /// The stranded-saga sweep reads it to tell a saga that is still moving from one that has stopped.
  /// The default reads every item row; a repository over a database should override it with a single
  /// <c>MAX(updated_at)</c>-style query.
  /// </remarks>
  /// <docs>fundamentals/sagas/completion-orchestration#stranded-sagas</docs>
  /// <tests>tests/Whizbang.Sagas.Tests/Services/StrandedSagaSweepTests.cs</tests>
  async Task<DateTimeOffset?> GetLastActivityAsync(Guid sagaId, CancellationToken cancellationToken) {
    var items = await GetItemsAsync(sagaId, cancellationToken).ConfigureAwait(false);
    return items.Count == 0 ? null : items.Max(i => i.UpdatedAt);
  }
}
