using System;
using Microsoft.Extensions.DependencyInjection;
using Whizbang.Core.Data;
using Whizbang.Core.Perspectives;

namespace Whizbang.Data.Dapper.Postgres.Collective;

/// <summary>
/// Dapper implementation of <see cref="ICollectiveSessionAccessor"/>: hands the worker the
/// <see cref="IDbConnectionFactory"/>, which the Dapper apply chain opens a short-lived connection from.
/// (The EF accessor returns a scoped <c>DbContext</c>; Dapper has no scoped session, so the factory is
/// the session and each apply owns its own connection.)
/// </summary>
/// <docs>fundamentals/messaging/collective-events</docs>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/Collective/DapperCollectiveUnitTests.cs:SessionAccessor_ReturnsConnectionFactoryAsync</tests>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/Collective/DapperCollectiveUnitTests.cs:AddCollectiveEventsDapper_RegistersDispatcherResolverAccessorAsync</tests>
public sealed class DapperCollectiveSessionAccessor : ICollectiveSessionAccessor {
  /// <inheritdoc />
  public object GetSession(IServiceProvider scopedServiceProvider) {
    ArgumentNullException.ThrowIfNull(scopedServiceProvider);
    return scopedServiceProvider.GetRequiredService<IDbConnectionFactory>();
  }
}
