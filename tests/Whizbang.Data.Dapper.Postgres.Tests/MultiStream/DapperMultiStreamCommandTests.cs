// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.MultiStream;
using Whizbang.Core.Observability;
using Whizbang.Core.Workers;
using Whizbang.Data.Postgres.MultiStream;

namespace Whizbang.Data.Dapper.Postgres.Tests.MultiStream;

/// <summary>
/// The Dapper driver wires multi-stream commands too (#1358): the Postgres store over the connection string it was given,
/// and the worker. Without the notification connection it has no new-run notice, so its worker finds runs by polling;
/// without the elected partition assigner it ranks the live instances itself, as Dapper's work claim does.
/// </summary>
/// <docs>fundamentals/dispatcher/multi-stream-commands#registration</docs>
[Category("Integration")]
public class DapperMultiStreamCommandTests : PostgresTestBase {
  private static readonly KeyValuePair<string, string>[] _noPerspectives = [];

  private ServiceProvider _provider(bool schemaSqlOverload) => _services(schemaSqlOverload).BuildServiceProvider();

  private ServiceCollection _services(bool schemaSqlOverload) {
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddSingleton<Whizbang.Core.ITimeProvider, Whizbang.Core.SystemTimeProvider>();
    services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection([]).Build());
    services.AddSingleton<IServiceInstanceProvider>(new ServiceInstanceProvider(Guid.CreateVersion7(), "dapper", "dapper-host", 1));
    if (schemaSqlOverload) {
      services.AddWhizbangPostgres(ConnectionString, new JsonSerializerOptions(), initializeSchema: false, perspectiveSchemaSql: null, configureOptions: null);
    } else {
      services.AddWhizbangPostgres(ConnectionString, new JsonSerializerOptions(), initializeSchema: false, _noPerspectives);
    }
    services.AddWhizbangWorkers();
    return services;
  }

  [Test]
  [Arguments(false)]
  [Arguments(true)]
  public async Task AddWhizbangPostgres_WiresTheStoreAndTheWorkerAsync(bool schemaSqlOverload) {
    var services = _services(schemaSqlOverload);
    await using var provider = services.BuildServiceProvider();

    await Assert.That(provider.GetRequiredService<IMultiStreamCommandStore>()).IsTypeOf<PgMultiStreamCommandStore>()
      .Because("without a store the intake refuses every multi-stream command");
    await Assert.That(services.Count(d => d.ServiceType == typeof(MultiStreamCommandWorker))).IsEqualTo(1);
    await Assert.That(services.Any(d => d.ImplementationType == typeof(MultiStreamCommandSubscriber))).IsFalse()
      .Because("Dapper has no shared notification connection, so its worker polls");
    await Assert.That(provider.GetRequiredService<IPartitionAssignmentSource>()).IsSameReferenceAs(NullPartitionAssignmentSource.Instance)
      .Because("with no elected assigner the claim ranks the live instances itself");
    await Assert.That(provider.GetRequiredService<MultiStreamCommandWorker>()).IsNotNull()
      .Because("every dependency the worker requires resolves in a Dapper host");
  }

  [Test]
  public async Task TheDapperStore_RecordsAndClaimsOverTheGivenConnectionStringAsync() {
    await using var provider = _provider(schemaSqlOverload: false);
    var store = provider.GetRequiredService<IMultiStreamCommandStore>();
    var stream = Guid.CreateVersion7();
    var run = new MultiStreamRun(Guid.CreateVersion7(), "Probe.Command, Probe", "{}", [stream], null);

    var recorded = await store.RecordRunAsync(run, CancellationToken.None);
    var claimed = await store.ClaimAsync(new MultiStreamClaim(Guid.CreateVersion7(), 10, TimeSpan.FromMinutes(1), null), CancellationToken.None);

    await Assert.That(recorded).IsTrue();
    await Assert.That(claimed.Single().StreamId).IsEqualTo(stream)
      .Because("a lone instance ranks first among the live instances and owns every partition");
  }
}
