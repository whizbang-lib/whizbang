using Whizbang.Sagas.Repositories;

namespace Whizbang.Sagas.Helpers;

/// <summary>
/// Live-progress resolvers consumed by dashboard GraphQL/REST surfaces
/// to render <c>processedCount</c>, <c>failedCount</c>, and
/// <c>progressPercent</c> for in-flight sagas.
/// </summary>
/// <remarks>
/// <para>
/// While a saga is running, <see cref="Models.BaseSagaModel.CompletedItems"/>
/// and <see cref="Models.BaseSagaModel.FailedItems"/> on the projection
/// row are still 0 — they only flip when <c>SagaCompletedEvent</c>
/// lands. Live progress must therefore come from a one-round-trip
/// GROUP BY aggregate against the per-item projection.
/// </para>
/// <para>
/// Once the saga is terminal, the stored counters are authoritative
/// and the helpers short-circuit to avoid a DB round-trip on every
/// dashboard refresh.
/// </para>
/// <para>
/// <strong>Explicit parameters intentionally.</strong> Concrete saga
/// projections frequently declare <c>[StreamId] public new Guid Id</c>
/// which <em>shadows</em> the base property rather than overriding it.
/// A helper that took <see cref="Models.BaseSagaModel"/> and accessed
/// <c>saga.Id</c> would resolve against the base property (always
/// <see cref="Guid.Empty"/> in shadow-pattern sagas). Forcing each
/// caller to pass the id explicitly avoids this silent zero-id bug.
/// </para>
/// </remarks>
public static class SagaLiveProgressResolvers {

  /// <summary>True once the saga has reached a terminal status.</summary>
  public static bool IsTerminal(SagaStatus status) =>
    status is SagaStatus.Completed or SagaStatus.CompletedWithFailures or SagaStatus.Failed;

  /// <summary>
  /// Returns the count of items in <see cref="SagaItemState.Completed"/>.
  /// Terminal sagas use the stored counter; running sagas read the live
  /// aggregate.
  /// </summary>
  public static async Task<int> ResolveProcessedCountAsync(
      Guid sagaId,
      SagaStatus status,
      int storedCompletedItems,
      ISagaItemRepository itemRepository,
      CancellationToken cancellationToken) {
    ArgumentNullException.ThrowIfNull(itemRepository);
    if (IsTerminal(status)) {
      return storedCompletedItems;
    }
    var agg = await itemRepository.GetAggregateForSagaAsync(sagaId, cancellationToken).ConfigureAwait(false);
    return agg.Completed;
  }

  /// <summary>
  /// Returns the count of items in <see cref="SagaItemState.Failed"/>.
  /// Terminal sagas use the stored counter; running sagas read the live
  /// aggregate.
  /// </summary>
  public static async Task<int> ResolveFailedCountAsync(
      Guid sagaId,
      SagaStatus status,
      int storedFailedItems,
      ISagaItemRepository itemRepository,
      CancellationToken cancellationToken) {
    ArgumentNullException.ThrowIfNull(itemRepository);
    if (IsTerminal(status)) {
      return storedFailedItems;
    }
    var agg = await itemRepository.GetAggregateForSagaAsync(sagaId, cancellationToken).ConfigureAwait(false);
    return agg.Failed;
  }

  /// <summary>
  /// Returns progress as an integer percent 0–100. Capped at 100 so
  /// rounding doesn't surface 101% to the UI. Returns 0 (without a repo
  /// round-trip) when <paramref name="totalItems"/> is non-positive
  /// (downstream saga awaiting upstream count).
  /// </summary>
  public static async Task<int> ResolveProgressPercentAsync(
      Guid sagaId,
      SagaStatus status,
      int totalItems,
      int storedCompletedItems,
      int storedFailedItems,
      ISagaItemRepository itemRepository,
      CancellationToken cancellationToken) {
    ArgumentNullException.ThrowIfNull(itemRepository);
    if (totalItems <= 0) {
      return 0;
    }

    int completed, failed;
    if (IsTerminal(status)) {
      completed = storedCompletedItems;
      failed = storedFailedItems;
    } else {
      var agg = await itemRepository.GetAggregateForSagaAsync(sagaId, cancellationToken).ConfigureAwait(false);
      completed = agg.Completed;
      failed = agg.Failed;
    }

    return Math.Min(100, (int)((completed + failed) / (double)totalItems * 100));
  }
}
