using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore;
using Whizbang.Core.Lenses;
using Whizbang.Core.Perspectives;

namespace Whizbang.Data.EFCore.Postgres.Collective;

/// <summary>
/// EF Core binding of <see cref="ICollectiveQuery"/>. <see cref="Of{TOther}"/> returns the live
/// <c>DbContext.Set&lt;PerspectiveRow&lt;TOther&gt;&gt;()</c>, so a handler's <c>Where</c> that calls
/// <c>q.Of&lt;TOther&gt;().Any(correlated)</c> is funcletized by EF (the <c>q.Of()</c> call doesn't depend on
/// the outer row, so EF evaluates it to the sibling <c>DbSet</c>) and the <c>.Any(...)</c> is translated to a
/// correlated <c>EXISTS</c> subquery in the same <c>ExecuteUpdateAsync</c> statement.
/// </summary>
/// <docs>fundamentals/messaging/collective-events</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Collective/CollectiveDispatcherEFCoreIntegrationTests.cs:DispatchAsync_CrossPerspectiveCohort_ScopesBySiblingTableAsync</tests>
[SuppressMessage("AOT", "IL2091:MakeGenericType", Justification = "EF Core's DbSet resolution is reflection-based by design; matches the established suppressions on the EF collective adapter.")]
[SuppressMessage("AOT", "IL3050:RequiresDynamicCode", Justification = "EF Core data layer inherently uses reflection for query translation.")]
internal sealed class EFCoreCollectiveQuery(DbContext dbContext) : ICollectiveQuery, ICollectiveSiblingTableSource {
  public IQueryable<PerspectiveRow<TOther>> Of<TOther>() where TOther : class
    => dbContext.Set<PerspectiveRow<TOther>>();

  /// <summary>
  /// Resolves a sibling model's perspective table from the EF Core model, for the raw jsonb_set path's
  /// correlated <c>EXISTS</c> (the native <c>ExecuteUpdateAsync</c> path lets EF translate the sibling
  /// <c>DbSet</c> directly instead).
  /// </summary>
  public string TableFor(Type modelType) {
    ArgumentNullException.ThrowIfNull(modelType);
    var rowType = typeof(PerspectiveRow<>).MakeGenericType(modelType);
    var entityType = dbContext.Model.FindEntityType(rowType)
      ?? throw new InvalidOperationException(
        $"No EF Core entity mapped for PerspectiveRow<{modelType.Name}> — a handler's Where referenced a " +
        $"sibling perspective q.Of<{modelType.Name}>() whose DbSet is not registered in this DbContext.");
    return entityType.GetTableName()
      ?? throw new InvalidOperationException($"PerspectiveRow<{modelType.Name}> has no table name.");
  }
}
