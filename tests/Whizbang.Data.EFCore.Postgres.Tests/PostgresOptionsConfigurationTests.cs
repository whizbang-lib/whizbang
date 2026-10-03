using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Perspectives;
using Whizbang.Data.Postgres;

#pragma warning disable CA1707 // Test method names use underscores by convention

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// <see cref="PostgresOptions"/> bind per named database from <c>Whizbang:Postgres:&lt;database&gt;</c>
/// over the code values (#1012). The database name is its connection-string name; the unnamed
/// instance the framework resolves is the first registered database's.
/// </summary>
/// <code-under-test>src/Whizbang.Data.Postgres/PostgresOptionsConfiguration.cs</code-under-test>
[Category("Shard3")]
public class PostgresOptionsConfigurationTests {
  private const string OFFLINE_CONNECTION_STRING =
    "Host=localhost;Port=5432;Database=whizbang_registration_probe;Username=probe;Password=probe";

  private static IConfiguration _config(params (string Key, string Value)[] values) =>
    new ConfigurationBuilder()
      .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
      .Build();

  [Test]
  public async Task NamedDatabase_PicksUpItsOwnKeys_AndNotAnotherDatabasesAsync() {
    var services = new ServiceCollection();
    services.AddSingleton(_config(
      ("Whizbang:Postgres:orders-db:InitialRetryAttempts", "2"),
      ("Whizbang:Postgres:orders-db:InitialRetryDelay", "00:00:04"),
      ("Whizbang:Postgres:orders-db:MaxRetryDelay", "00:03:00"),
      ("Whizbang:Postgres:orders-db:BackoffMultiplier", "1.5"),
      ("Whizbang:Postgres:orders-db:RetryIndefinitely", "false"),
      ("Whizbang:Postgres:orders-db:CommandTimeoutSeconds", "30"),
      ("Whizbang:Postgres:orders-db:MaxInFlightCommands", "12"),
      ("Whizbang:Postgres:orders-db:CollectiveApplyBatchSize", "250"),
      ("Whizbang:Postgres:orders-db:CollectiveApplyStatementTimeoutSeconds", "45"),
      ("Whizbang:Postgres:billing-db:MaxInFlightCommands", "3")));
    services.AddWhizbangPostgresOptionsBinding("orders-db");
    services.AddWhizbangPostgresOptionsBinding("billing-db");

    await using var provider = services.BuildServiceProvider();
    var monitor = provider.GetRequiredService<IOptionsMonitor<PostgresOptions>>();
    var orders = monitor.Get("orders-db");
    var billing = monitor.Get("billing-db");

    await Assert.That(orders.InitialRetryAttempts).IsEqualTo(2);
    await Assert.That(orders.InitialRetryDelay).IsEqualTo(TimeSpan.FromSeconds(4));
    await Assert.That(orders.MaxRetryDelay).IsEqualTo(TimeSpan.FromMinutes(3));
    await Assert.That(orders.BackoffMultiplier).IsEqualTo(1.5);
    await Assert.That(orders.RetryIndefinitely).IsFalse();
    await Assert.That(orders.CommandTimeoutSeconds).IsEqualTo(30);
    await Assert.That(orders.MaxInFlightCommands).IsEqualTo(12);
    await Assert.That(orders.CollectiveApplyBatchSize).IsEqualTo(250);
    await Assert.That(orders.CollectiveApplyStatementTimeoutSeconds).IsEqualTo(45);
    await Assert.That(billing.MaxInFlightCommands).IsEqualTo(3);
    await Assert.That(billing.CommandTimeoutSeconds).IsEqualTo(120)
      .Because("a key under another database's name must not leak into this one");
  }

  [Test]
  public async Task UnnamedInstance_IsTheFirstRegisteredDatabasesAsync() {
    var services = new ServiceCollection();
    services.AddSingleton(_config(
      ("Whizbang:Postgres:orders-db:MaxInFlightCommands", "12"),
      ("Whizbang:Postgres:billing-db:MaxInFlightCommands", "3")));
    services.AddWhizbangPostgresOptionsBinding("orders-db");
    services.AddWhizbangPostgresOptionsBinding("billing-db");
    services.AddWhizbangPostgresOptionsBinding("orders-db");

    await using var provider = services.BuildServiceProvider();

    await Assert.That(provider.GetRequiredService<IOptions<PostgresOptions>>().Value.MaxInFlightCommands).IsEqualTo(12);
    await Assert.That(provider.GetServices<IPostConfigureOptions<PostgresOptions>>().Count()).IsEqualTo(1);
  }

  [Test]
  public async Task UnregisteredName_AndNoDatabase_KeepDefaultsAsync() {
    var services = new ServiceCollection();
    services.AddSingleton(_config(("Whizbang:Postgres:orders-db:MaxInFlightCommands", "12")));
    services.AddWhizbangPostgresOptionsBinding("billing-db");
    var bare = new ServiceCollection();
    bare.AddSingleton(_config(("Whizbang:Postgres:orders-db:MaxInFlightCommands", "12")));
    bare.AddOptions<PostgresOptions>();

    await using var provider = services.BuildServiceProvider();
    await using var bareProvider = bare.BuildServiceProvider();

    await Assert.That(provider.GetRequiredService<IOptions<PostgresOptions>>().Value.MaxInFlightCommands).IsEqualTo(50)
      .Because("billing-db has no section, so its values stay the defaults");
    await Assert.That(bareProvider.GetRequiredService<IOptions<PostgresOptions>>().Value.MaxInFlightCommands).IsEqualTo(50);
  }

  [Test]
  public async Task CodeValues_StayTheDefaults_ConfigurationOverridesPerKeyAsync() {
    var services = new ServiceCollection();
    services.AddSingleton(_config(("Whizbang:Postgres:orders-db:CommandTimeoutSeconds", "30")));
    services.Configure<PostgresOptions>(o => {
      o.CommandTimeoutSeconds = 90;
      o.MaxInFlightCommands = 8;
    });
    services.AddWhizbangPostgresOptionsBinding("orders-db");

    await using var provider = services.BuildServiceProvider();
    var options = provider.GetRequiredService<IOptions<PostgresOptions>>().Value;

    await Assert.That(options.CommandTimeoutSeconds).IsEqualTo(30);
    await Assert.That(options.MaxInFlightCommands).IsEqualTo(8);
  }

  [Test]
  public async Task Driver_BindsTheDbContextsDatabase_IntoTheWorkCoordinatorGateAsync() {
    // DriverSelectorTestDbContext's connection-string name is "driverselectortest-db".
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddSingleton(_config(("Whizbang:Postgres:driverselectortest-db:MaxInFlightCommands", "9")));
    services.AddWhizbang();
    await using var dataSource = new NpgsqlDataSourceBuilder(OFFLINE_CONNECTION_STRING).Build();
    services.AddSingleton(dataSource);
    services.AddDbContext<DriverSelectorTestDbContext>(o => o.UseNpgsql(dataSource));
    _ = new WhizbangPerspectiveBuilder(services)
      .WithEFCore<DriverSelectorTestDbContext>()
      .WithDriver.Postgres;

    await using var provider = services.BuildServiceProvider();
    var gate = provider.GetRequiredService<WorkCoordinatorGate>();

    await Assert.That(gate.MaxConcurrent).IsEqualTo(9)
      .Because("an operator retunes the database's cap from configuration, without a redeploy");
  }

  [Test]
  public async Task AddWhizbangPostgresOptionsBinding_RejectsMissingArgumentsAsync() {
    await Assert.That(() => PostgresOptionsConfiguration.AddWhizbangPostgresOptionsBinding(null!, "orders-db"))
      .Throws<ArgumentNullException>();
    await Assert.That(() => new ServiceCollection().AddWhizbangPostgresOptionsBinding(" "))
      .Throws<ArgumentException>();
  }

  [Test]
  public async Task PostConfigure_RejectsNullOptionsAsync() {
    var services = new ServiceCollection();
    services.AddWhizbangPostgresOptionsBinding("orders-db");
    await using var provider = services.BuildServiceProvider();
    var binder = provider.GetServices<IPostConfigureOptions<PostgresOptions>>().Single();

    await Assert.That(() => binder.PostConfigure("orders-db", null!)).Throws<ArgumentNullException>();
  }
}
