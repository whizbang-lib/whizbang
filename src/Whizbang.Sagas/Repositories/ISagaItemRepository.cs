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
}
