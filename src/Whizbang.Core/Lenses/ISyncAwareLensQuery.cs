namespace Whizbang.Core.Lenses;

/// <summary>
/// A sync-aware lens query that waits for perspective synchronization before querying.
/// </summary>
/// <typeparam name="TModel">The read model type to query.</typeparam>
/// <remarks>
/// <para>
/// This interface extends lens query functionality with synchronization awareness,
/// ensuring read-your-writes consistency by waiting for pending events to be
/// processed before returning query results.
/// </para>
/// <para>
/// <strong>Usage:</strong>
/// </para>
/// <code>
/// var syncQuery = lensQuery.WithSync(awaiter, "OrderPerspective", options);
/// var order = await syncQuery.GetByIdAsync(orderId);
/// </code>
/// </remarks>
/// <docs>fundamentals/perspectives/perspective-sync</docs>
/// <tests>Whizbang.Core.Tests/Lenses/SyncAwareLensQueryTests.cs</tests>
public interface ISyncAwareLensQuery<TModel> where TModel : class {
  /// <summary>
  /// Queryable access to full perspective rows.
  /// </summary>
  /// <remarks>
  /// <para>
  /// When accessing this property, synchronization is NOT automatically awaited.
  /// For sync-aware queries, use <see cref="GetByIdAsync"/> or await sync explicitly
  /// before accessing the query.
  /// </para>
  /// </remarks>
  IQueryable<PerspectiveRow<TModel>> Query { get; }

  /// <summary>
  /// Fast single-item lookup by ID with synchronization.
  /// </summary>
  /// <param name="id">Unique identifier.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>The read model, or null if not found.</returns>
  /// <remarks>
  /// <para>
  /// This method waits for the configured synchronization options before querying.
  /// If sync times out and ThrowOnTimeout is false, the query proceeds with eventual consistency.
  /// </para>
  /// </remarks>
  Task<TModel?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
}
