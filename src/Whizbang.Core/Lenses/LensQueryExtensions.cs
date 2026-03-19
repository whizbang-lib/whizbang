using Whizbang.Core.Perspectives.Sync;

namespace Whizbang.Core.Lenses;

/// <summary>
/// Extension methods for <see cref="ILensQuery{TModel}"/> providing sync-aware query capabilities.
/// </summary>
/// <remarks>
/// <para>
/// These extensions enable read-your-writes consistency by waiting for perspective
/// synchronization before executing queries.
/// </para>
/// <para>
/// <strong>Usage:</strong>
/// </para>
/// <code>
/// // Fluent wrapper approach with generic type
/// var syncQuery = lensQuery.WithSync&lt;Order, OrderPerspective&gt;(awaiter, options);
/// var order = await syncQuery.GetByIdAsync(orderId);
///
/// // Direct query with sync
/// var order = await lensQuery.GetByIdAsync&lt;Order, OrderPerspective&gt;(
///     orderId,
///     awaiter,
///     SyncFilter.CurrentScope().Local().Build());
/// </code>
/// </remarks>
/// <docs>fundamentals/perspectives/perspective-sync</docs>
/// <tests>Whizbang.Core.Tests/Lenses/SyncAwareLensQueryTests.cs</tests>
public static class LensQueryExtensions {
  /// <summary>
  /// Creates a sync-aware wrapper around the lens query.
  /// </summary>
  /// <typeparam name="TModel">The read model type.</typeparam>
  /// <typeparam name="TPerspective">The perspective type to synchronize.</typeparam>
  /// <param name="query">The lens query to wrap.</param>
  /// <param name="awaiter">The sync awaiter service.</param>
  /// <param name="options">The synchronization options.</param>
  /// <returns>A sync-aware lens query.</returns>
  public static ISyncAwareLensQuery<TModel> WithSync<TModel, TPerspective>(
      this ILensQuery<TModel> query,
      IPerspectiveSyncAwaiter awaiter,
      PerspectiveSyncOptions options) where TModel : class {
    ArgumentNullException.ThrowIfNull(query);
    ArgumentNullException.ThrowIfNull(awaiter);
    ArgumentNullException.ThrowIfNull(options);

    return new SyncAwareLensQuery<TModel>(query, awaiter, typeof(TPerspective), options);
  }

  /// <summary>
  /// Creates a sync-aware wrapper around the lens query.
  /// </summary>
  /// <typeparam name="TModel">The read model type.</typeparam>
  /// <param name="query">The lens query to wrap.</param>
  /// <param name="awaiter">The sync awaiter service.</param>
  /// <param name="perspectiveType">The type of the perspective to synchronize.</param>
  /// <param name="options">The synchronization options.</param>
  /// <returns>A sync-aware lens query.</returns>
  public static ISyncAwareLensQuery<TModel> WithSync<TModel>(
      this ILensQuery<TModel> query,
      IPerspectiveSyncAwaiter awaiter,
      Type perspectiveType,
      PerspectiveSyncOptions options) where TModel : class {
    ArgumentNullException.ThrowIfNull(query);
    ArgumentNullException.ThrowIfNull(awaiter);
    ArgumentNullException.ThrowIfNull(perspectiveType);
    ArgumentNullException.ThrowIfNull(options);

    return new SyncAwareLensQuery<TModel>(query, awaiter, perspectiveType, options);
  }

  /// <summary>
  /// Gets a model by ID after waiting for perspective synchronization.
  /// </summary>
  /// <typeparam name="TModel">The read model type.</typeparam>
  /// <typeparam name="TPerspective">The perspective type to synchronize.</typeparam>
  /// <param name="query">The lens query.</param>
  /// <param name="id">The unique identifier.</param>
  /// <param name="awaiter">The sync awaiter service.</param>
  /// <param name="options">The synchronization options.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>The read model, or null if not found.</returns>
  public static async Task<TModel?> GetByIdAsync<TModel, TPerspective>(
      this ILensQuery<TModel> query,
      Guid id,
      IPerspectiveSyncAwaiter awaiter,
      PerspectiveSyncOptions options,
      CancellationToken cancellationToken = default) where TModel : class {
    ArgumentNullException.ThrowIfNull(query);
    ArgumentNullException.ThrowIfNull(awaiter);
    ArgumentNullException.ThrowIfNull(options);

    var syncQuery = new SyncAwareLensQuery<TModel>(query, awaiter, typeof(TPerspective), options);
    return await syncQuery.GetByIdAsync(id, cancellationToken);
  }

  /// <summary>
  /// Gets a model by ID after waiting for perspective synchronization.
  /// </summary>
  /// <typeparam name="TModel">The read model type.</typeparam>
  /// <param name="query">The lens query.</param>
  /// <param name="id">The unique identifier.</param>
  /// <param name="awaiter">The sync awaiter service.</param>
  /// <param name="perspectiveType">The type of the perspective to synchronize.</param>
  /// <param name="options">The synchronization options.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>The read model, or null if not found.</returns>
  public static async Task<TModel?> GetByIdAsync<TModel>(
      this ILensQuery<TModel> query,
      Guid id,
      IPerspectiveSyncAwaiter awaiter,
      Type perspectiveType,
      PerspectiveSyncOptions options,
      CancellationToken cancellationToken = default) where TModel : class {
    ArgumentNullException.ThrowIfNull(query);
    ArgumentNullException.ThrowIfNull(awaiter);
    ArgumentNullException.ThrowIfNull(perspectiveType);
    ArgumentNullException.ThrowIfNull(options);

    var syncQuery = new SyncAwareLensQuery<TModel>(query, awaiter, perspectiveType, options);
    return await syncQuery.GetByIdAsync(id, cancellationToken);
  }
}
