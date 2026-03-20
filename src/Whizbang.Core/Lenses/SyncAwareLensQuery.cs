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
/// <remarks>
/// Initializes a new instance of <see cref="SyncAwareLensQuery{TModel}"/>.
/// </remarks>
/// <param name="innerQuery">The underlying lens query.</param>
/// <param name="awaiter">The sync awaiter service.</param>
/// <param name="perspectiveType">The type of the perspective to synchronize.</param>
/// <param name="options">The synchronization options.</param>
public sealed class SyncAwareLensQuery<TModel>(
    ILensQuery<TModel> innerQuery,
    IPerspectiveSyncAwaiter awaiter,
    Type perspectiveType,
    PerspectiveSyncOptions options) : ISyncAwareLensQuery<TModel> where TModel : class {
  private readonly ILensQuery<TModel> _innerQuery = innerQuery ?? throw new ArgumentNullException(nameof(innerQuery));
  private readonly IPerspectiveSyncAwaiter _awaiter = awaiter ?? throw new ArgumentNullException(nameof(awaiter));
  private readonly Type _perspectiveType = perspectiveType ?? throw new ArgumentNullException(nameof(perspectiveType));
  private readonly PerspectiveSyncOptions _options = options ?? throw new ArgumentNullException(nameof(options));

  /// <inheritdoc />
  public IQueryable<PerspectiveRow<TModel>> Query => _innerQuery.Query;

  /// <inheritdoc />
  public async Task<TModel?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) {
    // Wait for sync before querying
    await _awaiter.WaitAsync(_perspectiveType, _options, cancellationToken);

    // Delegate to inner query
    return await _innerQuery.GetByIdAsync(id, cancellationToken);
  }
}
