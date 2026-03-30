#pragma warning disable CS0618

using Whizbang.Core.Perspectives.Sync;

namespace Whizbang.Core.Lenses;

/// <summary>
/// Wrapper around <see cref="ILensQuery{TModel}"/> that awaits perspective synchronization.
/// </summary>
/// <typeparam name="TModel">The read model type to query.</typeparam>
/// <remarks>
/// <para>
/// This wrapper ensures read-your-writes consistency by waiting for pending events
/// to be processed by the perspective before executing queries.
/// </para>
/// <para>
/// <strong>Usage:</strong>
/// </para>
/// <code>
/// var syncQuery = new SyncAwareLensQuery&lt;Order&gt;(
///     lensQuery,
///     awaiter,
///     typeof(OrderPerspective),
///     SyncFilter.CurrentScope().Local().Build());
///
/// var order = await syncQuery.GetByIdAsync(orderId);
/// </code>
/// </remarks>
/// <docs>fundamentals/perspectives/perspective-sync</docs>
/// <tests>Whizbang.Core.Tests/Lenses/SyncAwareLensQueryTests.cs</tests>
public sealed class SyncAwareLensQuery<TModel> : ISyncAwareLensQuery<TModel> where TModel : class {
  private readonly ILensQuery<TModel> _innerQuery;
  private readonly IPerspectiveSyncAwaiter _awaiter;
  private readonly Type _perspectiveType;
  private readonly PerspectiveSyncOptions _options;

  /// <summary>
  /// Initializes a new instance of <see cref="SyncAwareLensQuery{TModel}"/>.
  /// </summary>
  /// <param name="innerQuery">The underlying lens query.</param>
  /// <param name="awaiter">The sync awaiter service.</param>
  /// <param name="perspectiveType">The type of the perspective to synchronize.</param>
  /// <param name="options">The synchronization options.</param>
  public SyncAwareLensQuery(
      ILensQuery<TModel> innerQuery,
      IPerspectiveSyncAwaiter awaiter,
      Type perspectiveType,
      PerspectiveSyncOptions options) {
    _innerQuery = innerQuery ?? throw new ArgumentNullException(nameof(innerQuery));
    _awaiter = awaiter ?? throw new ArgumentNullException(nameof(awaiter));
    _perspectiveType = perspectiveType ?? throw new ArgumentNullException(nameof(perspectiveType));
    _options = options ?? throw new ArgumentNullException(nameof(options));
  }

  /// <inheritdoc />
  public IQueryable<PerspectiveRow<TModel>> Query => _innerQuery.DefaultScope.Query;

  /// <inheritdoc />
  public async Task<TModel?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) {
    // Wait for sync before querying
    await _awaiter.WaitAsync(_perspectiveType, _options, cancellationToken);

    // Delegate to inner query via DefaultScope
    return await _innerQuery.DefaultScope.GetByIdAsync(id, cancellationToken);
  }
}
