using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Perspectives;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// The EF Core driver carries <c>PostgresOptions.MaxInFlightCommands</c> into the gate only when the
/// <c>Whizbang:WorkCoordinatorGate</c> section has not set the cap itself: the section is the operator's
/// explicit word and must not be overwritten by a driver default.
/// </summary>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/PostgresDriverExtensions.cs</code-under-test>
[Category("Shard3")]
public class PostgresGatePrecedenceTests {
  private const string OFFLINE_CONNECTION_STRING =
    "Host=localhost;Port=5432;Database=whizbang_registration_probe;Username=probe;Password=probe";

  [Test]
  public async Task ASectionValue_WinsOverMaxInFlightCommandsAsync() {
    var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
      ["Whizbang:WorkCoordinatorGate:MaxConcurrent"] = "7",
    }).Build();
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddSingleton<IConfiguration>(configuration);
    services.Configure<PostgresOptions>(o => o.MaxInFlightCommands = 9);
    services.AddWhizbang();
    await using var dataSource = new NpgsqlDataSourceBuilder(OFFLINE_CONNECTION_STRING).Build();
    services.AddSingleton(dataSource);
    services.AddDbContext<DriverSelectorTestDbContext>(o => o.UseNpgsql(dataSource));
    _ = new WhizbangPerspectiveBuilder(services)
      .WithEFCore<DriverSelectorTestDbContext>()
      .WithDriver.Postgres;

    await using var provider = services.BuildServiceProvider();
    var gate = provider.GetRequiredService<WorkCoordinatorGate>();

    await Assert.That(gate.MaxConcurrent).IsEqualTo(7)
      .Because("the section set the cap; the driver's MaxInFlightCommands only fills the gap when it did not");
  }
}
