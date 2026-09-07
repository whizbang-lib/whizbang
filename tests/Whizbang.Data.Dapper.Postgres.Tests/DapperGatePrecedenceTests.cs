using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Workers;

namespace Whizbang.Data.Dapper.Postgres.Tests;

/// <summary>
/// The Dapper driver carries <c>PostgresOptions.MaxInFlightCommands</c> into the gate only when the
/// <c>Whizbang:WorkCoordinatorGate</c> section has not set the cap itself: the section is the operator's
/// explicit word and must not be overwritten by a driver default.
/// </summary>
/// <code-under-test>src/Whizbang.Data.Dapper.Postgres/ServiceCollectionExtensions.cs</code-under-test>
[Category("Integration")]
public class DapperGatePrecedenceTests : PostgresTestBase {
  private static readonly KeyValuePair<string, string>[] _noPerspectives = [];

  [Test]
  public async Task ASectionValue_WinsOverMaxInFlightCommandsAsync() {
    var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
      ["Whizbang:WorkCoordinatorGate:MaxConcurrent"] = "7",
    }).Build();
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddSingleton<IConfiguration>(configuration);
    services.AddWhizbangPostgres(
      ConnectionString, new JsonSerializerOptions(), initializeSchema: false, _noPerspectives,
      configureOptions: o => o.MaxInFlightCommands = 9);
    services.AddWhizbangWorkers();

    await using var provider = services.BuildServiceProvider();
    var gate = provider.GetRequiredService<WorkCoordinatorGate>();

    await Assert.That(gate.MaxConcurrent).IsEqualTo(7)
      .Because("the section set the cap; the driver's MaxInFlightCommands only fills the gap when it did not");
  }
}
