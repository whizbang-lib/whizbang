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
[SuppressMessage("AOT", "IL2091:MakeGenericType", Justification = "EF Core's DbSet<T>() resolution is reflection-based by design; matches the established suppressions on the EF collective adapter.")]
[SuppressMessage("AOT", "IL3050:RequiresDynamicCode", Justification = "EF Core data layer inherently uses reflection for query translation.")]
internal sealed class EFCoreCollectiveQuery(DbContext dbContext) : ICollectiveQuery {
  public IQueryable<PerspectiveRow<TOther>> Of<TOther>() where TOther : class
    => dbContext.Set<PerspectiveRow<TOther>>();
}
