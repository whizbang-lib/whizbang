// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

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

/// <summary>
/// The collective session of a context known by its type (#2620): what the EF Core Postgres driver registers for a host
/// that declares a perspective fan-out, where the context's type is a value.
/// </summary>
/// <param name="dbContextType">The <see cref="DbContext"/> type the applies run on.</param>
/// <docs>fundamentals/perspectives/multi-stream#how-a-fan-out-applies-live</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Collective/EFCoreCollectiveDiUnitTests.cs:SessionAccessor_ForAContextType_ReturnsTheRegisteredDbContextAsync</tests>
public sealed class EFCoreCollectiveSessionAccessor(Type dbContextType) : ICollectiveSessionAccessor {
  private readonly Type _dbContextType = dbContextType ?? throw new ArgumentNullException(nameof(dbContextType));

  /// <inheritdoc />
  public object GetSession(IServiceProvider scopedServiceProvider) {
    ArgumentNullException.ThrowIfNull(scopedServiceProvider);
    return scopedServiceProvider.GetRequiredService(_dbContextType);
  }
}
