// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Tests.Observability;

/// <summary>
/// A provider that offers the storage reading is asked one question about its tables per cycle, and
/// every table gauge is fed from the answer.
/// </summary>
/// <remarks>
/// The collector runs every thirty seconds on every instance, against databases that are busy for
/// reasons of their own. Asking for the sizes, the split and the bloat in separate statements would
/// read the same catalog three times for one cycle; this pins that it is read once.
/// </remarks>
/// <code-under-test>src/Whizbang.Core/Observability/TableStatisticsCollector.cs</code-under-test>
/// <docs>operations/observability/metrics#table-statistics</docs>
[Category("Core")]
[Category("Observability")]
public class TableStatisticsCollectorStorageTests {
  [Test]
  public async Task AStorageReading_FeedsEveryTableGauge_FromOneReadAsync() {
    var provider = new StorageProvider();
    using var meterFactory = new ServiceCollection().AddMetrics().BuildServiceProvider().GetRequiredService<IMeterFactory>();
    var metrics = new TableStatisticsMetrics(new WhizbangMetrics(meterFactory));
    using var listener = _listen(meterFactory, out var readings);
    var services = new ServiceCollection();
    services.AddSingleton<ITableStatisticsProvider>(provider);
    await using var container = services.BuildServiceProvider();
    var worker = new TableStatisticsCollector(
      container.GetRequiredService<IServiceScopeFactory>(),
      metrics,
      Whizbang.Core.Workers.SchemaReadyGate.AlreadyReady(),
      NullLogger<TableStatisticsCollector>.Instance);
    var cycled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    worker.CycleCompleted += () => cycled.TrySetResult();

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await cycled.Task.WaitAsync(TimeSpan.FromSeconds(30));
    await cts.CancelAsync();
    await worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    listener.RecordObservableInstruments();

    await Assert.That(provider.SeparateReads).IsEqualTo(0)
      .Because("the sizes and the bloat ratios are in the storage reading, so asking again reads the "
             + "catalog a second and third time for nothing");
    await Assert.That(readings).Contains(("whizbang.table.estimated_bytes", 1_073_741_824d + 4_096d + 65_536d, "wh_event_body", (string?)null))
      .Because("estimated_bytes keeps meaning the whole table");
    await Assert.That(readings).Contains(("whizbang.table.component_bytes", 1_073_741_824d, "wh_event_body", "toast"));
    await Assert.That(readings).Contains(("whizbang.table.live_rows", 9_000d, "wh_event_body", (string?)null));
    await Assert.That(readings).Contains(("whizbang.table.bloat_ratio", 3.5d, "wh_per_orders", (string?)null))
      .Because("the ratio arrives in the same reading and still has to reach its gauge");
    await Assert.That(readings.Any(r => r.Name == "whizbang.table.bloat_ratio" && r.Table == "wh_event_body")).IsFalse()
      .Because("a table too small for the ratio to mean anything reports none, rather than a misleading one");
  }

  private static MeterListener _listen(
      IMeterFactory factory, out List<(string Name, double Value, string? Table, string? Component)> readings) {
    var collected = new List<(string Name, double Value, string? Table, string? Component)>();
    readings = collected;
    var listener = new MeterListener {
      InstrumentPublished = (instrument, l) => {
        if (ReferenceEquals(instrument.Meter.Scope, factory)) {
          l.EnableMeasurementEvents(instrument);
        }
      },
    };
    void Record(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags) {
      string? table = null;
      string? component = null;
      foreach (var tag in tags) {
        if (tag.Key == "table_name") {
          table = tag.Value?.ToString();
        } else if (tag.Key == "component") {
          component = tag.Value?.ToString();
        }
      }
      collected.Add((instrument.Name, value, table, component));
    }
    listener.SetMeasurementEventCallback<long>((i, v, t, _) => Record(i, v, t));
    listener.SetMeasurementEventCallback<double>((i, v, t, _) => Record(i, v, t));
    listener.Start();
    return listener;
  }

  /// <summary>Answers the storage reading, and counts any question it should not have been asked.</summary>
  private sealed class StorageProvider : ITableStatisticsProvider {
    private int _separateReads;

    public int SeparateReads => Volatile.Read(ref _separateReads);

    public Task<IReadOnlyDictionary<string, TableStorageStatistics>> GetTableStorageAsync(CancellationToken ct = default) =>
      Task.FromResult<IReadOnlyDictionary<string, TableStorageStatistics>>(new Dictionary<string, TableStorageStatistics> {
        ["wh_event_body"] = new(HeapBytes: 4_096, ToastBytes: 1_073_741_824, IndexBytes: 65_536, LiveRows: 9_000, BloatRatio: null),
        ["wh_per_orders"] = new(HeapBytes: 819_200, ToastBytes: 0, IndexBytes: 81_920, LiveRows: 2_000, BloatRatio: 3.5),
      });

    public Task<IReadOnlyDictionary<string, long>> GetEstimatedTableSizesAsync(CancellationToken ct = default) {
      Interlocked.Increment(ref _separateReads);
      return Task.FromResult<IReadOnlyDictionary<string, long>>(new Dictionary<string, long>());
    }

    public Task<IReadOnlyDictionary<string, double>> GetTableBloatRatiosAsync(CancellationToken ct = default) {
      Interlocked.Increment(ref _separateReads);
      return Task.FromResult<IReadOnlyDictionary<string, double>>(new Dictionary<string, double>());
    }

    public Task<IReadOnlyDictionary<string, long>> GetQueueDepthsAsync(CancellationToken ct = default) =>
      Task.FromResult<IReadOnlyDictionary<string, long>>(new Dictionary<string, long>());
  }
}
