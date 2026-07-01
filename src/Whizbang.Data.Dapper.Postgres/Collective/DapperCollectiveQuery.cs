using System.Diagnostics.CodeAnalysis;
using Whizbang.Core.Lenses;
using Whizbang.Core.Perspectives;

namespace Whizbang.Data.Dapper.Postgres.Collective;

/// <summary>
/// Dapper binding of <see cref="ICollectiveQuery"/>. Dapper has no LINQ provider, so
/// <see cref="Of{TOther}"/> returns an inert marker queryable — the handler's <c>Where</c> expression is
/// never executed, only walked by the shared <c>CollectivePredicateSqlCompiler</c>, which reads the
/// <c>TOther</c> type argument off the <c>q.Of&lt;TOther&gt;()</c> call node and resolves its table via
/// <see cref="TableFor"/> (through <see cref="ICollectiveSiblingTableSource"/>) to emit a correlated
/// <c>EXISTS</c> subquery.
/// </summary>
/// <remarks>
/// The sibling table map is supplied at construction (the Dapper driver has no entity model to derive table
/// names from). The DI extension <c>AddCollectiveExecutorDapper&lt;TModel&gt;(tableName)</c> and
/// <c>AddCollectiveTableDapper&lt;TModel&gt;(tableName)</c> populate it; tests construct it directly.
/// </remarks>
/// <docs>fundamentals/messaging/collective-events</docs>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/Collective/DapperCollectiveApplierIntegrationTests.cs:ApplyAsync_CrossPerspectiveCohort_ScopesBySiblingTableAsync</tests>
public sealed class DapperCollectiveQuery : ICollectiveQuery, ICollectiveSiblingTableSource {
  private readonly IReadOnlyDictionary<Type, string> _tables;

  /// <summary>Creates a query context over a <c>model type → perspective table name</c> map.</summary>
  public DapperCollectiveQuery(IReadOnlyDictionary<Type, string> tables) {
    ArgumentNullException.ThrowIfNull(tables);
    _tables = tables;
  }

  /// <inheritdoc />
  /// <remarks>
  /// On the Dapper path this is never actually invoked: the handler's <c>Where</c> is an
  /// <c>Expression</c>, so <c>q.Of&lt;TOther&gt;()</c> is captured as a method-call node and read by the
  /// filter compiler, not executed. The inert marker only satisfies the return type for tree construction.
  /// </remarks>
  [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Instance method by interface contract; the marker is intentionally inert.")]
  [SuppressMessage("AOT", "IL2026:RequiresUnreferencedCode", Justification = "Inert marker; never enumerated — the Dapper filter compiler reads the q.Of<T>() node, it is not executed at runtime.")]
  [SuppressMessage("AOT", "IL3050:RequiresDynamicCode", Justification = "Inert marker; never enumerated — the Dapper filter compiler reads the q.Of<T>() node, it is not executed at runtime.")]
  public IQueryable<PerspectiveRow<TOther>> Of<TOther>() where TOther : class
    => Array.Empty<PerspectiveRow<TOther>>().AsQueryable();

  /// <summary>
  /// The perspective table for a sibling model referenced via <see cref="Of{TOther}"/>. Throws if the
  /// model has no registered table — the cohort can't be projected without it.
  /// </summary>
  public string TableFor(Type modelType) {
    ArgumentNullException.ThrowIfNull(modelType);
    return _tables.TryGetValue(modelType, out var table)
      ? table
      : throw new InvalidOperationException(
          $"No Dapper collective table registered for sibling model '{modelType.FullName}'. A handler's Where " +
          $"called q.Of<{modelType.Name}>(), but its table is unknown. Register it with " +
          $"AddCollectiveTableDapper<{modelType.Name}>(\"wh_per_…\") (or AddCollectiveExecutorDapper).");
  }
}
