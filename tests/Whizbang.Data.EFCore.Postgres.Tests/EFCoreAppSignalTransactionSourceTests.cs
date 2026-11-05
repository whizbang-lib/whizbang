// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Signals;
using Whizbang.Data.EFCore.Postgres.Tests.Generated;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// #2076: the Postgres driver registers, for every framework DbContext, the scoped source the application signal channel
/// reads its open transaction from. Nobody wires it per application: composing the driver is enough.
/// </summary>
/// <remarks>No database is required: building a data source and a context opens no connection.</remarks>
/// <docs>fundamentals/work-coordinator/app-signals#transactional-publish</docs>
[Category("Shard3")]
public class EFCoreAppSignalTransactionSourceTests {
  private const string OFFLINE_CONNECTION_STRING =
    "Host=localhost;Port=5432;Database=whizbang_registration_probe;Username=probe;Password=probe";

  [Test]
  public async Task Postgres_RegistersTheContextsTransactionSource_WithNoApplicationWiringAsync() {
    var services = new ServiceCollection();
    services.AddLogging();
    await using var dataSource = new NpgsqlDataSourceBuilder(OFFLINE_CONNECTION_STRING).Build();
    services.AddDbContext<DriverSelectorTestDbContext>(o => o.UseNpgsql(dataSource));
    _ = new WhizbangPerspectiveBuilder(services)
      .WithEFCore<DriverSelectorTestDbContext>()
      .WithDriver.Postgres;

    await using var provider = services.BuildServiceProvider();
    await using var scope = provider.CreateAsyncScope();
    var sources = scope.ServiceProvider.GetServices<IAppSignalTransactionSource>().ToList();

    await Assert.That(sources.OfType<EFCoreAppSignalTransactionSource>()).Count().IsEqualTo(1);
    await Assert.That(sources.OfType<EFCoreAppSignalTransactionSource>().Single().CurrentTransaction).IsNull()
      .Because("a context with no transaction begun reports none");
  }

  [Test]
  public async Task Register_OneSourceForEveryContext_AndAContextOnceAsync() {
    var services = new ServiceCollection();
    await using var dataSource = new NpgsqlDataSourceBuilder(OFFLINE_CONNECTION_STRING).Build();
    services.AddDbContext<DriverSelectorTestDbContext>(o => o.UseNpgsql(dataSource));
    services.AddDbContext<WorkCoordinationDbContext>(o => o.UseNpgsql(dataSource));

    EFCoreAppSignalTransactionSource.Register(services, typeof(DriverSelectorTestDbContext));
    EFCoreAppSignalTransactionSource.Register(services, typeof(WorkCoordinationDbContext));
    EFCoreAppSignalTransactionSource.Register(services, typeof(DriverSelectorTestDbContext));

    await Assert.That(services.Count(d => d.ServiceType == typeof(IAppSignalTransactionSource))).IsEqualTo(1)
      .Because("one source reads every registered context, so a second context adds no second source");
    await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    await using var scope = provider.CreateAsyncScope();
    var source = (EFCoreAppSignalTransactionSource)scope.ServiceProvider.GetRequiredService<IAppSignalTransactionSource>();
    await Assert.That(source.ContextTypes).IsEquivalentTo([typeof(DriverSelectorTestDbContext), typeof(WorkCoordinationDbContext)]);
    await Assert.That(source.CurrentTransaction).IsNull();
    await Assert.That(() => EFCoreAppSignalTransactionSource.Register(null!, typeof(WorkCoordinationDbContext))).Throws<ArgumentNullException>();
    await Assert.That(() => EFCoreAppSignalTransactionSource.Register(services, null!)).Throws<ArgumentNullException>();
  }
}
