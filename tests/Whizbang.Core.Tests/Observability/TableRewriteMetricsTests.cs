// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.Metrics;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Observability;

/// <summary>
/// #2319: the table-rewrite instruments on the table statistics meter. A rewrite used to emit nothing but logs and a
/// history row; each attempt is now counted by table, tool, reason and outcome, timed, and its returned bytes added
/// up, and the queue, each table's post-rewrite floor and the bytes a rewrite would free now are gauges.
/// </summary>
/// <remarks>Each test reads the instruments the way an exporter does, through a listener on this test's own meter.</remarks>
/// <code-under-test>src/Whizbang.Core/Observability/TableStatisticsMetrics.cs</code-under-test>
/// <docs>operations/observability/metrics#table-rewrites</docs>
[Category("Core")]
[Category("Observability")]
public class TableRewriteMetricsTests {
  private sealed record Reading(string Instrument, string? Unit, double Value, IReadOnlyDictionary<string, object?> Tags, bool Observable);

  /// <summary>Listens to every instrument on one metrics instance's own meter.</summary>
  private sealed class Instruments : IDisposable {
    private readonly TestMeterFactory _factory = new();
    private readonly MeterListener _listener = new();
    private readonly List<Reading> _readings = [];

    public Instruments() {
      Metrics = new TableStatisticsMetrics(new WhizbangMetrics(_factory));
      var meter = _factory.CreatedMeters[0];
      _listener.InstrumentPublished = (instrument, listener) => {
        if (instrument.Meter == meter) {
          listener.EnableMeasurementEvents(instrument);
        }
      };
      _listener.SetMeasurementEventCallback<long>((i, value, tags, _) => _record(i, value, tags));
      _listener.SetMeasurementEventCallback<double>((i, value, tags, _) => _record(i, value, tags));
      _listener.Start();
    }

    public TableStatisticsMetrics Metrics { get; }

    private void _record(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags) {
      // A passive counter reports its untagged series at zero from construction; the tests read the tagged ones.
      if (tags.IsEmpty) {
        return;
      }
      var map = new Dictionary<string, object?>(StringComparer.Ordinal);
      foreach (var tag in tags) {
        map[tag.Key] = tag.Value;
      }
      _readings.Add(new Reading(instrument.Name, instrument.Unit, value, map, instrument.IsObservable));
    }

    /// <summary>The readings of one instrument, with the observable ones taken afresh, as one collection reads them.</summary>
    public List<Reading> Of(string instrument) {
      _readings.RemoveAll(r => r.Observable);
      _listener.RecordObservableInstruments();
      return [.. _readings.Where(r => r.Instrument == instrument)];
    }

    public void Dispose() {
      _listener.Dispose();
      _factory.Dispose();
    }
  }

  private static TableRewriteAttempt _attempt(
      TableRewriteMetricOutcome outcome, long? reclaimed = 4096, string strategy = TableRewriteStrategies.PG_SQUEEZE,
      TableRewriteReason reason = TableRewriteReason.Bloat) =>
    new("wh_outbox", strategy, reason, outcome, TimeSpan.FromMilliseconds(1250), reclaimed);

  [Test]
  public async Task ARewrittenAttempt_IsCounted_Timed_AndItsBytesAddedAsync() {
    using var instruments = new Instruments();

    instruments.Metrics.RecordRewrite(_attempt(TableRewriteMetricOutcome.Rewritten));

    var count = instruments.Of("whizbang.table.rewrites").Single();
    await Assert.That(count.Value).IsEqualTo(1d);
    await Assert.That(count.Unit).IsEqualTo("{rewrite}");
    await Assert.That(count.Tags).IsEquivalentTo(new Dictionary<string, object?> {
      ["table_name"] = "wh_outbox",
      ["tool"] = "pg_squeeze",
      ["reason"] = "bloat",
      ["outcome"] = "rewritten",
    });
    var duration = instruments.Of("whizbang.table.rewrite.duration").Single();
    await Assert.That(duration.Value).IsEqualTo(1250d);
    await Assert.That(duration.Unit).IsEqualTo("ms");
    await Assert.That(duration.Tags).IsEquivalentTo(new Dictionary<string, object?> {
      ["table_name"] = "wh_outbox",
      ["tool"] = "pg_squeeze",
      ["outcome"] = "rewritten",
    });
    var reclaimed = instruments.Of("whizbang.table.rewrite.reclaimed_bytes").Single();
    await Assert.That(reclaimed.Value).IsEqualTo(4096d);
    await Assert.That(reclaimed.Unit).IsEqualTo("bytes");
    await Assert.That(reclaimed.Tags).IsEquivalentTo(new Dictionary<string, object?> {
      ["table_name"] = "wh_outbox",
      ["tool"] = "pg_squeeze",
    });
  }

  [Test]
  [Arguments(TableRewriteMetricOutcome.NoGain, "no_gain")]
  [Arguments(TableRewriteMetricOutcome.Failed, "failed")]
  public async Task AnAttemptThatReturnedNothing_IsCountedByItsOutcome_AndAddsNoBytesAsync(TableRewriteMetricOutcome outcome, string tag) {
    using var instruments = new Instruments();

    instruments.Metrics.RecordRewrite(_attempt(outcome));

    await Assert.That(instruments.Of("whizbang.table.rewrites").Single().Tags["outcome"]).IsEqualTo(tag);
    await Assert.That(instruments.Of("whizbang.table.rewrite.duration").Single().Tags["outcome"]).IsEqualTo(tag);
    await Assert.That(instruments.Of("whizbang.table.rewrite.reclaimed_bytes")).IsEmpty();
  }

  [Test]
  public async Task ARewriteWhoseSizeWasNotRead_AddsNoBytes_AndAFillFactorReasonIsTaggedAsync() {
    using var instruments = new Instruments();

    instruments.Metrics.RecordRewrite(_attempt(TableRewriteMetricOutcome.Rewritten, reclaimed: null, reason: TableRewriteReason.FillFactorDecrease));

    await Assert.That(instruments.Of("whizbang.table.rewrites").Single().Tags["reason"]).IsEqualTo("fillfactor");
    await Assert.That(instruments.Of("whizbang.table.rewrite.reclaimed_bytes")).IsEmpty();
  }

  [Test]
  public async Task TheDatabase_TagsTheRecordedInstrumentsOnceKnownAsync() {
    using var instruments = new Instruments();
    instruments.Metrics.UpdateDatabase("orders");

    instruments.Metrics.RecordRewrite(_attempt(TableRewriteMetricOutcome.Rewritten));

    await Assert.That(instruments.Of("whizbang.table.rewrites").Single().Tags["database"]).IsEqualTo("orders");
  }

  [Test]
  [Arguments(TableRewriteStrategies.PG_SQUEEZE, "pg_squeeze")]
  [Arguments(TableRewriteStrategies.PG_REPACK, "pg_repack")]
  [Arguments(TableRewriteStrategies.VACUUM_FULL, "vacuum_full")]
  [Arguments(TableRewriteStrategies.BLUE_GREEN, "blue_green")]
  [Arguments("Custom", "custom")]
  public async Task EachStrategy_IsTaggedByItsToolAsync(string strategy, string tool) {
    await Assert.That(TableStatisticsMetrics.ToolOf(strategy)).IsEqualTo(tool);
  }

  [Test]
  public async Task Reclaimed_IsTheDropClampedAtZero_AndNeedsBothSizesAsync() {
    await Assert.That(TableStatisticsMetrics.Reclaimed(1000, 400)).IsEqualTo(600L);
    await Assert.That(TableStatisticsMetrics.Reclaimed(400, 1000)).IsEqualTo(0L)
      .Because("a rewrite at a lower fillfactor can grow a table, which returned nothing rather than a negative");
    await Assert.That(TableStatisticsMetrics.Reclaimed(null, 400)).IsNull();
    await Assert.That(TableStatisticsMetrics.Reclaimed(1000, null)).IsNull();
  }

  [Test]
  public async Task TheQueueGauge_ReportsEachStateAsync() {
    using var instruments = new Instruments();
    await Assert.That(instruments.Of("whizbang.table.rewrite.queue")).IsEmpty().Because("nothing is reported before a cycle");

    instruments.Metrics.UpdateRewriteQueue(requested: 55, candidates: 4, heldByFloor: 6, overBudget: 1);

    var readings = instruments.Of("whizbang.table.rewrite.queue");
    await Assert.That(readings.Select(r => ((string)r.Tags["state"]!, r.Value))).IsEquivalentTo([
      ("requested", 55d), ("candidate", 4d), ("held_by_floor", 6d), ("over_budget", 1d),
    ]);
    await Assert.That(readings[0].Unit).IsEqualTo("{table}");
  }

  [Test]
  public async Task TheFloorGauge_ReportsTheLatestFloors_AndDropsAStaleTableAsync() {
    using var instruments = new Instruments();
    instruments.Metrics.UpdateRewriteFloors(new Dictionary<string, double> { ["wh_outbox"] = 1.8, ["wh_inbox"] = 1.5 });
    instruments.Metrics.UpdateRewriteFloors(new Dictionary<string, double> { ["wh_outbox"] = 1.6 });

    var readings = instruments.Of("whizbang.table.rewrite.floor");

    await Assert.That(readings.Select(r => ((string)r.Tags["table_name"]!, r.Value))).IsEquivalentTo([("wh_outbox", 1.6d)]);
    await Assert.That(readings[0].Unit).IsEqualTo("ratio");
  }

  [Test]
  public async Task TheReclaimableGauge_ReportsEachPartAStatisticsPassMeasuredAsync() {
    using var instruments = new Instruments();
    instruments.Metrics.UpdateTableStorage(new Dictionary<string, TableStorageStatistics> {
      ["wh_outbox"] = new(8192, 65536, 0, 2000, 1.2) { ToastBloatRatio = 4.0, ReclaimableHeapBytes = 1024, ReclaimableToastBytes = 49152 },
      ["wh_inbox"] = new(8192, 0, 0, 10, null),
    });

    var readings = instruments.Of("whizbang.table.reclaimable_bytes");

    await Assert.That(readings.Select(r => ((string)r.Tags["table_name"]!, (string)r.Tags["part"]!, r.Value))).IsEquivalentTo([
      ("wh_outbox", "heap", 1024d), ("wh_outbox", "toast", 49152d),
    ]).Because("a table too small for a ratio has nothing to report");
    await Assert.That(readings[0].Unit).IsEqualTo("bytes");
  }

  [Test]
  public async Task TheBloatRatio_ReportsTheHeapAndTheToastRelation_ByPartAsync() {
    using var instruments = new Instruments();
    instruments.Metrics.UpdateTableBloat(new Dictionary<string, double> { ["wh_outbox"] = 1.2 });
    instruments.Metrics.UpdateTableStorage(new Dictionary<string, TableStorageStatistics> {
      ["wh_outbox"] = new(8192, 65536, 0, 2000, 1.2) { ToastBloatRatio = 4.0 },
      ["wh_inbox"] = new(8192, 0, 0, 2000, 1.0),
    });
    instruments.Metrics.UpdateMeasuredTableBloat(new Dictionary<string, BloatMeasurement> {
      ["wh_outbox"] = new(3.9, BloatMeasurementMethod.Approx),
    });

    var readings = instruments.Of("whizbang.table.bloat_ratio");

    await Assert.That(readings.Select(r => ((string)r.Tags["table_name"]!, (string)r.Tags["method"]!, (string)r.Tags["part"]!, r.Value)))
      .IsEquivalentTo([
        ("wh_outbox", "estimate", "heap", 1.2d),
        ("wh_outbox", "estimate", "toast", 4.0d),
        ("wh_outbox", "approx", "table", 3.9d),
      ]).Because("most of a table's reclaimable bytes can be in its TOAST relation, which the heap's ratio never showed");
  }
}
