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
  /// </summary>
  /// <param name="id">Unique identifier for the read model</param>
  /// <param name="model">The read model data to store</param>
  /// <param name="cancellationToken">Cancellation token</param>
  Task UpsertAsync(string id, TModel model, CancellationToken cancellationToken = default);

  /// <summary>
  /// Update specific fields of a read model.
  /// More efficient than full upsert when only changing a few fields.
  /// Uses database-specific update mechanisms (e.g., jsonb_set for PostgreSQL).
  /// </summary>
  /// <param name="id">Unique identifier for the read model</param>
  /// <param name="updates">Dictionary of field names to updated values</param>
  /// <param name="cancellationToken">Cancellation token</param>
  Task UpdateFieldsAsync(string id, Dictionary<string, object> updates, CancellationToken cancellationToken = default);
}
