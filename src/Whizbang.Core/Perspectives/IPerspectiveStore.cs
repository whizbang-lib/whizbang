namespace Whizbang.Core.Perspectives;

/// <summary>
/// Write-only abstraction for perspective data storage.
/// Hides underlying database implementation (EF Core, Dapper, Marten, etc.).
/// Perspectives use this to update read models without knowing storage details.
/// </summary>
/// <typeparam name="TModel">The read model type to store</typeparam>
public interface IPerspectiveStore<TModel> where TModel : class {
  /// <summary>
  /// Insert or update a read model.
  /// Creates new row if id doesn't exist, updates if it does.
  /// Automatically increments version for optimistic concurrency.
  /// Uses database-specific optimizations (e.g., ON CONFLICT for PostgreSQL) for single-roundtrip performance.
  /// </summary>
  /// <param name="id">Unique identifier for the read model</param>
  /// <param name="model">The read model data to store</param>
  /// <param name="cancellationToken">Cancellation token</param>
  Task UpsertAsync(string id, TModel model, CancellationToken cancellationToken = default);
}
