// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
    await Assert.That(orders.MaxInFlightCommands).IsEqualTo(12);
    await Assert.That(orders.CollectiveApplyBatchSize).IsEqualTo(250);
    await Assert.That(orders.CollectiveApplyStatementTimeoutSeconds).IsEqualTo(45);
    await Assert.That(billing.MaxInFlightCommands).IsEqualTo(3);
    await Assert.That(billing.InitialRetryAttempts).IsEqualTo(5)
      .Because("a key under another database's name must not leak into this one");
  }

  /// <summary>
  /// A key directly under <c>Whizbang:Postgres</c> is the default for every database, so a service
  /// with one database sets <c>Whizbang__Postgres__InitialRetryAttempts</c> without repeating the
  /// database's name.
  /// </summary>
  [Test]
  public async Task SectionLevelKey_IsTheDefaultForEveryDatabaseAsync() {
    var services = new ServiceCollection();
    services.AddSingleton(_config(
      ("Whizbang:Postgres:InitialRetryAttempts", "9"),
      ("Whizbang:Postgres:MaxInFlightCommands", "7")));
    services.AddWhizbangPostgresOptionsBinding("orders-db");
    services.AddWhizbangPostgresOptionsBinding("billing-db");

    await using var provider = services.BuildServiceProvider();
    var monitor = provider.GetRequiredService<IOptionsMonitor<PostgresOptions>>();

    await Assert.That(monitor.Get("orders-db").InitialRetryAttempts).IsEqualTo(9);
    await Assert.That(monitor.Get("billing-db").InitialRetryAttempts).IsEqualTo(9);
    await Assert.That(provider.GetRequiredService<IOptions<PostgresOptions>>().Value.InitialRetryAttempts).IsEqualTo(9)
      .Because("the unnamed instance is the first database's, and the default reaches it too");
    await Assert.That(monitor.Get("billing-db").MaxInFlightCommands).IsEqualTo(7);
  }

  /// <summary>A key under a database's name overrides the section-level default for that database only.</summary>
  [Test]
  public async Task DatabaseKey_OverridesTheSectionLevelDefaultAsync() {
    var services = new ServiceCollection();
    services.AddSingleton(_config(
      ("Whizbang:Postgres:InitialRetryAttempts", "9"),
      ("Whizbang:Postgres:orders-db:InitialRetryAttempts", "2")));
    services.AddWhizbangPostgresOptionsBinding("orders-db");
    services.AddWhizbangPostgresOptionsBinding("billing-db");

    await using var provider = services.BuildServiceProvider();
    var monitor = provider.GetRequiredService<IOptionsMonitor<PostgresOptions>>();

    await Assert.That(monitor.Get("orders-db").InitialRetryAttempts).IsEqualTo(2);
    await Assert.That(monitor.Get("billing-db").InitialRetryAttempts).IsEqualTo(9);
  }

  /// <summary>
  /// <c>CommandTimeoutSeconds</c> never reached a command, so it is retired: setting it at either
  /// level changes nothing, and startup says where a timeout is set instead.
  /// </summary>
  [Test]
  [Arguments("Whizbang:Postgres:CommandTimeoutSeconds")]
  public async Task RetiredCommandTimeoutSeconds_IsReportedAtStartupAsync(string key) {
    var logged = new List<string>();
    var services = new ServiceCollection();
    services.AddLogging(b => b.AddProvider(new ListLoggerProvider(logged)));
    services.AddSingleton(_config((key, "60")));
    services.AddWhizbangPostgresOptionsBinding("orders-db");

    await using var provider = services.BuildServiceProvider();
    _ = provider.GetRequiredService<IOptionsMonitor<PostgresOptions>>().Get("orders-db");

    await Assert.That(logged).Count().IsEqualTo(1);
    await Assert.That(logged[0]).Contains(key).And.Contains("Whizbang:Postgres:db-init:CommandTimeoutSeconds");
  }

  /// <summary>
  /// A key under a connection's name is that connection's own timeout, not the retired key: only the
  /// one directly under the section is reported, and only once.
  /// </summary>
  [Test]
  public async Task PerConnectionTimeoutKey_IsNotReported_TheRetiredKeyOnceAsync() {
    var logged = new List<string>();
    var services = new ServiceCollection();
    services.AddLogging(b => b.AddProvider(new ListLoggerProvider(logged)));
    services.AddSingleton(_config(
      ("Whizbang:Postgres:CommandTimeoutSeconds", "60"),
      ("Whizbang:Postgres:orders-db:CommandTimeoutSeconds", "30")));
    services.AddWhizbangPostgresOptionsBinding("orders-db");

    await using var provider = services.BuildServiceProvider();
    _ = provider.GetRequiredService<IOptionsMonitor<PostgresOptions>>().Get("orders-db");
    _ = provider.GetRequiredService<IOptions<PostgresOptions>>().Value;

    await Assert.That(logged).Count().IsEqualTo(1);
    await Assert.That(logged[0]).StartsWith("Whizbang:Postgres:CommandTimeoutSeconds");
  }

  [Test]
  public async Task PerConnectionTimeoutKey_Alone_ReportsNothingAsync() {
    var logged = new List<string>();
    var services = new ServiceCollection();
    services.AddLogging(b => b.AddProvider(new ListLoggerProvider(logged)));
    services.AddSingleton(_config(("Whizbang:Postgres:orders-db:CommandTimeoutSeconds", "30")));
    services.AddWhizbangPostgresOptionsBinding("orders-db");

    await using var provider = services.BuildServiceProvider();
    _ = provider.GetRequiredService<IOptionsMonitor<PostgresOptions>>().Get("orders-db");

    await Assert.That(logged).IsEmpty();
  }

  [Test]
  public async Task RetiredKey_WithoutLogging_ChangesNothingAsync() {
    var services = new ServiceCollection();
    services.AddSingleton(_config(("Whizbang:Postgres:CommandTimeoutSeconds", "60")));
    services.AddWhizbangPostgresOptionsBinding("orders-db");

    await using var provider = services.BuildServiceProvider();
    var options = provider.GetRequiredService<IOptions<PostgresOptions>>().Value;

    await Assert.That(options.MaxInFlightCommands).IsEqualTo(50);
  }

  [Test]
  public async Task NoRetiredKey_ReportsNothingAsync() {
    var logged = new List<string>();
    var services = new ServiceCollection();
    services.AddLogging(b => b.AddProvider(new ListLoggerProvider(logged)));
    services.AddSingleton(_config(("Whizbang:Postgres:orders-db:MaxInFlightCommands", "12")));
    services.AddWhizbangPostgresOptionsBinding("orders-db");

    await using var provider = services.BuildServiceProvider();
    _ = provider.GetRequiredService<IOptionsMonitor<PostgresOptions>>().Get("orders-db");

    await Assert.That(logged).IsEmpty();
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
    services.AddSingleton(_config(("Whizbang:Postgres:orders-db:InitialRetryAttempts", "2")));
    services.Configure<PostgresOptions>(o => {
      o.InitialRetryAttempts = 7;
      o.MaxInFlightCommands = 8;
    });
    services.AddWhizbangPostgresOptionsBinding("orders-db");

    await using var provider = services.BuildServiceProvider();
    var options = provider.GetRequiredService<IOptions<PostgresOptions>>().Value;

    await Assert.That(options.InitialRetryAttempts).IsEqualTo(2);
    await Assert.That(options.MaxInFlightCommands).IsEqualTo(8);
  }

  [Test]
  public async Task Driver_BindsTheDbContextsDatabase_IntoTheWorkCoordinatorGateAsync() {
    // DriverSelectorTestDbContext names no connection string, so its database is "db".
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddSingleton(_config(("Whizbang:Postgres:db:MaxInFlightCommands", "9")));
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

  /// <summary>
  /// A service still configured under the derived name keeps its per-database section
  /// (Whizbang:Postgres:&lt;legacy name&gt;) until its keys are renamed.
  /// </summary>
  [Test]
  public async Task Driver_WithOnlyTheLegacyName_BindsTheLegacySectionAsync() {
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddSingleton(_config(
      ("ConnectionStrings:driverselectortest-db", OFFLINE_CONNECTION_STRING),
      ("Whizbang:Postgres:driverselectortest-db:MaxInFlightCommands", "6")));
    services.AddWhizbang();
    await using var dataSource = new NpgsqlDataSourceBuilder(OFFLINE_CONNECTION_STRING).Build();
    services.AddSingleton(dataSource);
    services.AddDbContext<DriverSelectorTestDbContext>(o => o.UseNpgsql(dataSource));
    _ = new WhizbangPerspectiveBuilder(services)
      .WithEFCore<DriverSelectorTestDbContext>()
      .WithDriver.Postgres;

    await using var provider = services.BuildServiceProvider();

    await Assert.That(provider.GetRequiredService<IOptions<PostgresOptions>>().Value.MaxInFlightCommands).IsEqualTo(6);
  }

  [Test]
  public async Task LegacySectionFallback_IsReportedOnceAsync() {
    var logged = new List<string>();
    var services = new ServiceCollection();
    services.AddLogging(b => b.AddProvider(new ListLoggerProvider(logged)));
    services.AddSingleton(_config(
      ("ConnectionStrings:orders-db", OFFLINE_CONNECTION_STRING),
      ("Whizbang:Postgres:orders-db:MaxInFlightCommands", "4")));
    services.AddWhizbangPostgresOptionsBinding("db", "orders-db");

    await using var provider = services.BuildServiceProvider();
    var unnamed = provider.GetRequiredService<IOptions<PostgresOptions>>().Value;
    var named = provider.GetRequiredService<IOptionsMonitor<PostgresOptions>>().Get("db");

    await Assert.That(unnamed.MaxInFlightCommands).IsEqualTo(4);
    await Assert.That(named.MaxInFlightCommands).IsEqualTo(4);
    await Assert.That(logged).Count().IsEqualTo(1);
    await Assert.That(logged[0]).Contains("orders-db");
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

  private sealed class ListLoggerProvider(List<string> sink) : ILoggerProvider {
    public ILogger CreateLogger(string categoryName) => new ListLogger(sink);
    public void Dispose() { }
  }

  private sealed class ListLogger(List<string> sink) : ILogger {
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) {
      if (IsEnabled(logLevel)) {
        lock (sink) {
          sink.Add(formatter(state, exception));
        }
      }
    }
  }
}
