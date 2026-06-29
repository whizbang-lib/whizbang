namespace Whizbang.Data.Dapper.Postgres.Collective;

/// <summary>
/// A mutable <c>model type → perspective table name</c> map for the Dapper collective driver, populated at
/// registration time. Dapper has no entity model to derive table names from, so cross-perspective cohorts
/// (<c>q.Of&lt;TOther&gt;()</c>) resolve the sibling table through this registry. Both the mutated models
/// (<c>AddCollectiveExecutorDapper</c>) and query-only siblings (<c>AddCollectiveTableDapper</c>) register here.
/// </summary>
/// <docs>fundamentals/messaging/collective-events</docs>
public sealed class DapperCollectiveTableRegistry {
  private readonly Dictionary<Type, string> _tables = [];

  /// <summary>The accumulated map, handed to each <see cref="DapperCollectiveQuery"/>.</summary>
  public IReadOnlyDictionary<Type, string> Tables => _tables;

  /// <summary>Register (or overwrite) the perspective table for a model.</summary>
  public void Add(Type model, string tableName) {
    ArgumentNullException.ThrowIfNull(model);
    ArgumentException.ThrowIfNullOrWhiteSpace(tableName);
    _tables[model] = tableName;
  }
}
