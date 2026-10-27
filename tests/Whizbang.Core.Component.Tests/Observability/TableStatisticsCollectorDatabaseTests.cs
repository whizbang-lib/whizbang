// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Observability;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Observability;

/// <summary>
/// Issue #1717: the collector asks the provider which database it reads, once, and the table-statistics gauges carry
/// that name as their <c>database</c> tag.
/// </summary>
/// <remarks>
/// Every wait is on a signal: a cycle's completion, or the collector arming its timer on a fake clock, which the test
/// then advances. Nothing sleeps.
/// </remarks>
/// <code-under-test>src/Whizbang.Core/Observability/TableStatisticsCollector.cs</code-under-test>
/// <docs>operations/observability/metrics#table-statistics</docs>
[Category("Core")]
[Category("Observability")]
public class TableStatisticsCollectorDatabaseTests {
  private const string DATABASE = "orders_db";
  private static readonly TimeSpan _wait = TimeSpan.FromSeconds(30);

  [Test]
  public async Task TheProvidersDatabase_TagsTheGauges_AndIsAskedForOnceAsync() {
    var clock = new ArmedTimerClock();
    var interval = TimeSpan.FromMinutes(1);
    await using var host = new CollectorHost(new NamedProvider(DATABASE), interval, clock);

    var first = host.NextCycle();
    await host.StartAsync();
    await first.WaitAsync(_wait);
    await clock.WaitForArmedAsync(interval, _wait);
    var second = host.NextCycle();
    clock.Advance(interval);
    await second.WaitAsync(_wait);
    var readings = host.Read();

    await Assert.That(readings).Contains(("whizbang.table.estimated_bytes", "wh_outbox", (string?)DATABASE))
      .Because("the database the provider reads is what tells this series apart from the same table in another database");
    await Assert.That(readings).Contains(("whizbang.queue.estimated_depth", "outbox", (string?)DATABASE));
    await Assert.That(host.Provider.NameReads).IsEqualTo(1)
      .Because("a provider's database does not change, so two cycles ask for it once");
  }

  [Test]
  public async Task AProviderThatCannotNameItsDatabase_ReportsUntaggedGaugesAsync() {
    var clock = new ArmedTimerClock();
    await using var host = new CollectorHost(new NamedProvider(null), TimeSpan.FromMinutes(1), clock);

    var first = host.NextCycle();
    await host.StartAsync();
    await first.WaitAsync(_wait);
    var readings = host.Read();

    await Assert.That(readings).Contains(("whizbang.table.estimated_bytes", "wh_outbox", (string?)null))
      .Because("a provider that cannot name its database reports what it always reported");
  }

  /// <summary>A collector on a fake clock, with a listener on its own meter.</summary>
  private sealed class CollectorHost : IAsyncDisposable {
    private readonly ServiceProvider _container;
    private readonly CancellationTokenSource _stopping = new();
    private readonly TestMeterFactory _meters = new();
    private readonly MeterListener _listener = new();
    private readonly List<(string Name, string? Subject, string? Database)> _readings = [];
    private TaskCompletionSource _next = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public CollectorHost(NamedProvider provider, TimeSpan interval, TimeProvider clock) {
      Provider = provider;
      var services = new ServiceCollection();
      services.AddSingleton<ITableStatisticsProvider>(provider);
      _container = services.BuildServiceProvider();
      var metrics = new TableStatisticsMetrics(new WhizbangMetrics(_meters));
      Collector = new TableStatisticsCollector(
        _container.GetRequiredService<IServiceScopeFactory>(),
        metrics,
        SchemaReadyGate.AlreadyReady(),
        NullLogger<TableStatisticsCollector>.Instance,
        Options.Create(new TableStatisticsOptions { CollectionInterval = interval }),
        clock);
      Collector.CycleCompleted += () => Volatile.Read(ref _next).TrySetResult();

      var meter = _meters.CreatedMeters[0];
      _listener.InstrumentPublished = (instrument, listener) => {
        if (instrument.Meter == meter) {
          listener.EnableMeasurementEvents(instrument);
        }
      };
      _listener.SetMeasurementEventCallback<long>((instrument, _, tags, _) => _record(instrument, tags));
      _listener.SetMeasurementEventCallback<double>((instrument, _, tags, _) => _record(instrument, tags));
      _listener.Start();
    }

    public NamedProvider Provider { get; }

    public TableStatisticsCollector Collector { get; }

    public Task NextCycle() {
      var next = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
      Volatile.Write(ref _next, next);
      return next.Task;
    }

    public Task StartAsync() => Collector.StartAsync(_stopping.Token);

    public List<(string Name, string? Subject, string? Database)> Read() {
      _readings.Clear();
      _listener.RecordObservableInstruments();
      return [.. _readings];
    }

    private void _record(Instrument instrument, ReadOnlySpan<KeyValuePair<string, object?>> tags) {
      string? subject = null;
      string? database = null;
      foreach (var tag in tags) {
        if (tag.Key is "table_name" or "queue_name") {
          subject = tag.Value?.ToString();
        } else if (tag.Key == "database") {
          database = tag.Value?.ToString();
        }
      }
      _readings.Add((instrument.Name, subject, database));
    }

    public async ValueTask DisposeAsync() {
      await _stopping.CancelAsync();
      await Collector.StopAsync(CancellationToken.None);
      _stopping.Dispose();
      _listener.Dispose();
      _meters.Dispose();
      await _container.DisposeAsync();
    }
  }

  /// <summary>Answers one table and one queue, names its database, and counts how often it is asked for the name.</summary>
  private sealed class NamedProvider(string? database) : ITableStatisticsProvider {
    private int _nameReads;

    public int NameReads => Volatile.Read(ref _nameReads);

    public Task<string?> GetDatabaseNameAsync(CancellationToken ct = default) {
      Interlocked.Increment(ref _nameReads);
      return Task.FromResult(database);
    }

    public Task<IReadOnlyDictionary<string, long>> GetEstimatedTableSizesAsync(CancellationToken ct = default) =>
      Task.FromResult<IReadOnlyDictionary<string, long>>(new Dictionary<string, long> { ["wh_outbox"] = 8_192 });

    public Task<IReadOnlyDictionary<string, long>> GetQueueDepthsAsync(CancellationToken ct = default) =>
      Task.FromResult<IReadOnlyDictionary<string, long>>(new Dictionary<string, long> { ["outbox"] = 3 });
  }
}
