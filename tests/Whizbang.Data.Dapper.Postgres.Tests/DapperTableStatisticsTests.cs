// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.Metrics;
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
/// A host on the Dapper driver reports table statistics the way an EF Core host does: the same
/// provider, registered by the driver, read by the same collector.
/// </summary>
/// <remarks>
/// Only the EF Core driver registered a provider, so a Dapper host's <c>Whizbang.TableStatistics</c>
/// meter existed and reported nothing at all: no sizes, no queue depths, no bloat.
/// </remarks>
/// <docs>operations/observability/metrics#table-statistics</docs>
/// <code-under-test>src/Whizbang.Data.Dapper.Postgres/ServiceCollectionExtensions.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/Observability/PostgresTableStatisticsProvider.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Observability/TableStatisticsServiceCollectionExtensions.cs</code-under-test>
public class DapperTableStatisticsTests {
  private const string PERSPECTIVE_TABLE = "wh_per_dapper_statistics_probe";
  private static readonly KeyValuePair<string, string>[] _entries = [
    new("DapperStatisticsProbe", $"CREATE TABLE IF NOT EXISTS {PERSPECTIVE_TABLE} (id uuid PRIMARY KEY, data jsonb NOT NULL);"),
  ];

  private PerTestDatabase _database;

  [Before(Test)]
  public async Task SetupAsync() {
    await SharedPostgresContainer.InitializeAsync();
    _database = await PerTestDatabaseFactory.CreateAsync("dapper_tstats");
  }

  [After(Test)]
  public async Task TeardownAsync() => await PerTestDatabaseFactory.DropAsync(_database.Name);

  [Test]
  [Timeout(120_000)]
  public async Task ADapperHost_ReportsTableStatisticsAsync(CancellationToken cancellationToken) {
    var services = new ServiceCollection();
    services.AddLogging();
    services.Configure<SchemaInitializationOptions>(o => o.NonBlockingSchemaInit = false);
    services.AddWhizbangPostgres(
      _database.ConnectionString, JsonContextRegistry.CreateCombinedOptions(), initializeSchema: true, _entries,
      managedObjects: null);
    await using var provider = services.BuildServiceProvider();
    var hosted = provider.GetServices<IHostedService>().ToList();

    await hosted.OfType<WhizbangDatabaseInitializerService>().Single().StartAsync(cancellationToken);

    var collectors = hosted.OfType<TableStatisticsCollector>().ToList();
    await Assert.That(collectors.Count).IsEqualTo(1)
      .Because("the Dapper driver registers the collector, once");
    var collector = collectors[0];
    var cycled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    collector.CycleCompleted += () => cycled.TrySetResult();
    using var stop = new CancellationTokenSource();
    await collector.StartAsync(stop.Token);
    await cycled.Task.WaitAsync(cancellationToken);
    await stop.CancelAsync();
    await collector.ExecuteTask!.WaitAsync(cancellationToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    // The gauges are observed, so a listener started now reads what the cycle wrote.
    using var listener = _listen(provider.GetRequiredService<IMeterFactory>(), out var readings);
    listener.RecordObservableInstruments();

    await Assert.That(readings.Any(r => r.Name == "whizbang.table.estimated_bytes" && r.Tag == "wh_event_store" && r.Value > 0)).IsTrue()
      .Because("the event store's size reaches the meter on a Dapper host");
    await Assert.That(readings.Any(r => r.Name == "whizbang.table.estimated_bytes" && r.Tag == PERSPECTIVE_TABLE)).IsTrue()
      .Because("the perspective tables a Dapper host declares are reported like the framework's own");
    await Assert.That(readings.Any(r => r.Name == "whizbang.table.component_bytes" && r.Tag == "wh_event_body")).IsTrue();
    await Assert.That(readings.Any(r => r.Name == "whizbang.table.live_rows" && r.Tag == "wh_outbox")).IsTrue();
    await Assert.That(readings.Any(r => r.Name == "whizbang.queue.estimated_depth" && r.Tag == "outbox" && r.Value == 0)).IsTrue()
      .Because("the queue depths are read through the connection's search path, where the Dapper tables are");
  }

  private static MeterListener _listen(IMeterFactory factory, out List<(string Name, double Value, string? Tag)> readings) {
    var collected = new List<(string Name, double Value, string? Tag)>();
    readings = collected;
    var listener = new MeterListener {
      InstrumentPublished = (instrument, l) => {
        if (ReferenceEquals(instrument.Meter.Scope, factory) && instrument.Meter.Name == TableStatisticsMetrics.METER_NAME) {
          l.EnableMeasurementEvents(instrument);
        }
      },
    };
    void Record(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags) {
      string? tag = null;
      foreach (var t in tags) {
        if (t.Key is "table_name" or "queue_name") {
          tag = t.Value?.ToString();
        }
      }
      collected.Add((instrument.Name, value, tag));
    }
    listener.SetMeasurementEventCallback<long>((i, v, t, _) => Record(i, v, t));
    listener.SetMeasurementEventCallback<double>((i, v, t, _) => Record(i, v, t));
    listener.Start();
    return listener;
  }
}
