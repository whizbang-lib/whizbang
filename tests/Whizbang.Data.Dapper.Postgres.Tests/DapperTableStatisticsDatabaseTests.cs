// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.Metrics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Observability;
using Whizbang.Core.Serialization;
using Whizbang.Core.Workers;
using Whizbang.Data.Postgres;
using Whizbang.Testing.Containers;

namespace Whizbang.Data.Dapper.Postgres.Tests;

/// <summary>
/// Issue #1717: hosts on two databases report two series for the same table, told apart by the <c>database</c> tag,
/// and the tracked-table cap a host is configured with is the cap its readings are folded at.
/// </summary>
/// <remarks>
/// Each host runs one collector cycle, waited for on <see cref="TableStatisticsCollector.CycleCompleted"/>, and its
/// gauges are then read the way an exporter reads them.
/// </remarks>
/// <docs>operations/observability/metrics#table-statistics</docs>
/// <code-under-test>src/Whizbang.Core/Observability/TableStatisticsMetrics.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Observability/TableStatisticsCollector.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/Observability/PostgresTableStatisticsProvider.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Dapper.Postgres/ServiceCollectionExtensions.cs</code-under-test>
public class DapperTableStatisticsDatabaseTests {
  private const string ESTIMATED_BYTES = "whizbang.table.estimated_bytes";

  private readonly List<PerTestDatabase> _databases = [];

  [Before(Test)]
  public async Task SetupAsync() => await SharedPostgresContainer.InitializeAsync();

  [After(Test)]
  public async Task TeardownAsync() {
    foreach (var database in _databases) {
      await PerTestDatabaseFactory.DropAsync(database.Name);
    }
  }

  [Test]
  [Timeout(180_000)]
  public async Task TwoDatabases_ReportSeparateSeriesForTheSameTableAsync(CancellationToken cancellationToken) {
    var first = await _createDatabaseAsync("tstats_db_a");
    var second = await _createDatabaseAsync("tstats_db_b");
    await using var firstHost = await _runOneCycleAsync(first, settings: [], cancellationToken);
    await using var secondHost = await _runOneCycleAsync(second, settings: [], cancellationToken);

    var readings = _read(firstHost, secondHost);
    var outbox = readings.Where(r => r.Name == ESTIMATED_BYTES && r.Table == "wh_outbox").ToList();

    await Assert.That(outbox.Select(r => r.Database).Order(StringComparer.Ordinal).ToList())
      .IsEquivalentTo(new List<string?> { first.Name, second.Name }.Order(StringComparer.Ordinal).ToList())
      .Because("the same table in two databases is two series, each named by the database it was read from");
    await Assert.That(readings.All(r => r.Database is not null)).IsTrue()
      .Because("every gauge on the meter carries the database, so no series is shared between databases");
  }

  [Test]
  [Timeout(120_000)]
  public async Task TheConfiguredCap_FoldsTheSmallestTablesUnderOtherAsync(CancellationToken cancellationToken) {
    var database = await _createDatabaseAsync("tstats_cap");
    await using var host = await _runOneCycleAsync(
      database, settings: [new("Whizbang:TableStatistics:MaxTrackedTables", "2")], cancellationToken);

    var tables = _read(host)
      .Where(r => r.Name == ESTIMATED_BYTES)
      .Select(r => r.Table)
      .ToList();

    await Assert.That(tables.Count).IsEqualTo(3)
      .Because("a cap of 2 bound from configuration names the two largest tables and sums the rest");
    await Assert.That(tables).Contains(TableStatisticsOptions.OTHER_TABLES);
  }

  private async Task<PerTestDatabase> _createDatabaseAsync(string prefix) {
    var database = await PerTestDatabaseFactory.CreateAsync(prefix);
    _databases.Add(database);
    return database;
  }

  /// <summary>Builds a Dapper host on <paramref name="database"/> and runs one collector cycle to completion.</summary>
  private static async Task<ServiceProvider> _runOneCycleAsync(
      PerTestDatabase database, KeyValuePair<string, string?>[] settings, CancellationToken cancellationToken) {
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
    services.Configure<SchemaInitializationOptions>(o => o.NonBlockingSchemaInit = false);
    services.AddWhizbangPostgres(
      database.ConnectionString, JsonContextRegistry.CreateCombinedOptions(), initializeSchema: true,
      perspectiveEntries: [], managedObjects: null);
    var provider = services.BuildServiceProvider();
    var hosted = provider.GetServices<IHostedService>().ToList();
    await hosted.OfType<WhizbangDatabaseInitializerService>().Single().StartAsync(cancellationToken);

    var collector = hosted.OfType<TableStatisticsCollector>().Single();
    var cycled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    collector.CycleCompleted += () => cycled.TrySetResult();
    using var stop = new CancellationTokenSource();
    await collector.StartAsync(stop.Token);
    await cycled.Task.WaitAsync(cancellationToken);
    await stop.CancelAsync();
    await collector.ExecuteTask!.WaitAsync(cancellationToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    return provider;
  }

  /// <summary>Reads every table-statistics gauge of the given hosts, with each reading's table and database.</summary>
  private static List<(string Name, string? Table, string? Database)> _read(params ServiceProvider[] hosts) {
    var factories = hosts.Select(host => host.GetRequiredService<IMeterFactory>()).ToList();
    var readings = new List<(string Name, string? Table, string? Database)>();
    using var listener = new MeterListener {
      InstrumentPublished = (instrument, l) => {
        if (instrument.Meter.Name == TableStatisticsMetrics.METER_NAME
            && factories.Exists(factory => ReferenceEquals(instrument.Meter.Scope, factory))) {
          l.EnableMeasurementEvents(instrument);
        }
      },
    };
    void Record(Instrument instrument, ReadOnlySpan<KeyValuePair<string, object?>> tags) {
      string? table = null;
      string? database = null;
      foreach (var tag in tags) {
        if (tag.Key == "table_name") {
          table = tag.Value?.ToString();
        } else if (tag.Key == "database") {
          database = tag.Value?.ToString();
        }
      }
      readings.Add((instrument.Name, table, database));
    }
    listener.SetMeasurementEventCallback<long>((i, _, t, _) => Record(i, t));
    listener.SetMeasurementEventCallback<double>((i, _, t, _) => Record(i, t));
    listener.Start();
    listener.RecordObservableInstruments();
    return readings;
  }
}
