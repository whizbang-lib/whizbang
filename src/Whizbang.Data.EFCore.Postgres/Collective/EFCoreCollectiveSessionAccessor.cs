using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Whizbang.Core.Perspectives;

namespace Whizbang.Data.EFCore.Postgres.Collective;

/// <summary>
/// EF Core implementation of <see cref="ICollectiveSessionAccessor"/>: resolves the perspective
/// <typeparamref name="TDbContext"/> from the per-stream group scope and hands it to
/// <see cref="ICollectiveDispatcher.DispatchAsync"/>. <c>TDbContext</c> is the context that owns the
/// <c>wh_per_*</c> perspective tables the collective UPDATE mutates.
/// </summary>
/// <typeparam name="TDbContext">The perspective-owning EF Core context.</typeparam>
/// <docs>fundamentals/messaging/collective-events</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Collective/EFCoreCollectiveDiUnitTests.cs:SessionAccessor_ReturnsTheRegisteredDbContextAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Collective/EFCoreCollectiveDiUnitTests.cs:AddCollectiveEventsEFCore_RegistersDispatcherResolverAccessorAsync</tests>
public sealed class EFCoreCollectiveSessionAccessor<TDbContext> : ICollectiveSessionAccessor
    where TDbContext : DbContext {
  /// <inheritdoc />
  public object GetSession(IServiceProvider scopedServiceProvider) {
    ArgumentNullException.ThrowIfNull(scopedServiceProvider);
    return scopedServiceProvider.GetRequiredService<TDbContext>();
  }
}
