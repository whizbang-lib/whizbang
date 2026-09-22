using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Workers;

namespace Whizbang.Data.Dapper.Postgres.Tests;

/// <summary>
/// <c>PostgresOptions.MaxInFlightCommands</c> is the documented cap on concurrent coordinator calls.
/// It used to reach nothing (the worker pipeline built its gate with a literal 50); the Dapper
/// registration now carries it into <see cref="WorkCoordinatorGateOptions"/>.
/// </summary>
/// <code-under-test>src/Whizbang.Data.Dapper.Postgres/ServiceCollectionExtensions.cs</code-under-test>
[Category("Integration")]
public class DapperGateWiringTests : PostgresTestBase {
  private static readonly KeyValuePair<string, string>[] _noPerspectives = [];

  [Test]
  public async Task MaxInFlightCommands_ReachesTheWorkCoordinatorGateAsync() {
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddWhizbangPostgres(
      ConnectionString, new JsonSerializerOptions(), initializeSchema: false, _noPerspectives,
      configureOptions: o => o.MaxInFlightCommands = 7);
    services.AddWhizbangWorkers();

    await using var provider = services.BuildServiceProvider();
    var gate = provider.GetRequiredService<WorkCoordinatorGate>();

    await Assert.That(gate.MaxConcurrent).IsEqualTo(7)
      .Because("the option that has always been documented is the one that takes effect");
  }

  [Test]
  public async Task MaxInFlightCommands_ReachesTheGate_ThroughTheSchemaSqlOverloadAsync() {
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddWhizbangPostgres(
      ConnectionString, new JsonSerializerOptions(), initializeSchema: false, perspectiveSchemaSql: null,
      configureOptions: o => o.MaxInFlightCommands = 7);
    services.AddWhizbangWorkers();

    await using var provider = services.BuildServiceProvider();
    var gate = provider.GetRequiredService<WorkCoordinatorGate>();

    await Assert.That(gate.MaxConcurrent).IsEqualTo(7)
      .Because("both registration overloads carry the option into the gate");
  }
}
