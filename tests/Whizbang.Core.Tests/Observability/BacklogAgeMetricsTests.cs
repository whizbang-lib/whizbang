using System.Diagnostics.Metrics;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Tests.Observability;

/// <summary>
/// Covers <see cref="BacklogAgeMetrics"/>'s three observable gauges and the sample
/// bookkeeping behind them.
/// </summary>
/// <remarks>
/// Observable gauges only run their callback when something collects them, so these
/// tests attach a <see cref="MeterListener"/> and call RecordObservableInstruments
/// rather than asserting on the update methods alone — otherwise the projection from
/// stored sample to tagged measurement is never executed.
/// </remarks>
[Category("Core")]
[Category("Observability")]
public class BacklogAgeMetricsTests {

  private sealed record Recorded(string Instrument, double Value, Dictionary<string, string?> Tags);

  /// <summary>
  /// Collects one round of observable measurements, keeping only those tagged with
  /// <paramref name="key"/>. Every BacklogAgeMetrics instance publishes under the same
  /// meter name, so a listener also sees instances built by tests running in parallel —
  /// filtering on a per-test key keeps this from depending on what else is running.
  /// </summary>
  private static List<Recorded> _collect(string key, Action collectTrigger) {
    var recorded = new List<Recorded>();
    using var listener = new MeterListener();
    listener.InstrumentPublished = (instrument, l) => {
      if (instrument.Meter.Name == BacklogAgeMetrics.METER_NAME) {
        l.EnableMeasurementEvents(instrument);
      }
    };
    listener.SetMeasurementEventCallback<long>((inst, value, tags, _) =>
      recorded.Add(new Recorded(inst.Name, value, _toDict(tags))));
    listener.SetMeasurementEventCallback<double>((inst, value, tags, _) =>
      recorded.Add(new Recorded(inst.Name, value, _toDict(tags))));
    listener.Start();
    collectTrigger();
    listener.RecordObservableInstruments();
    return recorded.Where(r => r.Tags.ContainsValue(key)).ToList();
  }

  private static Dictionary<string, string?> _toDict(ReadOnlySpan<KeyValuePair<string, object?>> tags) {
    var d = new Dictionary<string, string?>(StringComparer.Ordinal);
    foreach (var t in tags) {
      d[t.Key] = t.Value?.ToString();
    }
    return d;
  }

  [Test]
  public async Task Constructor_WithNullMetrics_ThrowsAsync() {
    await Assert.That(() => new BacklogAgeMetrics(null!)).ThrowsExactly<ArgumentNullException>();
  }

  [Test]
  public async Task BacklogDepthGauge_ReportsStoredDepthWithTagsAsync() {
    var metrics = new BacklogAgeMetrics(new WhizbangMetrics());

    var entity = $"inbox.orders.{Guid.NewGuid():N}";
    var recorded = _collect(entity, () => metrics.UpdateBacklogs(new Dictionary<string, BacklogAgeMetrics.BacklogGaugeSample> {
      [entity] = new("asb", "ns-1", "bulk", Depth: 42, OldestAgeSeconds: 90)
    }));

    var depth = recorded.Single(r => r.Instrument == "whizbang.traffic_class.backlog_depth");
    await Assert.That(depth.Value).IsEqualTo(42);
    await Assert.That(depth.Tags["entity"]).IsEqualTo(entity);
    await Assert.That(depth.Tags["transport"]).IsEqualTo("asb");
    await Assert.That(depth.Tags["transport_namespace"]).IsEqualTo("ns-1");
    await Assert.That(depth.Tags["traffic_class"]).IsEqualTo("bulk");
  }

  [Test]
  public async Task BacklogAgeGauge_ReportsOldestAgeAsync() {
    var metrics = new BacklogAgeMetrics(new WhizbangMetrics());

    var entity = $"inbox.orders.{Guid.NewGuid():N}";
    var recorded = _collect(entity, () => metrics.UpdateBacklogs(new Dictionary<string, BacklogAgeMetrics.BacklogGaugeSample> {
      [entity] = new("asb", "ns-1", "bulk", Depth: 42, OldestAgeSeconds: 90)
    }));

    var age = recorded.Single(r => r.Instrument == "whizbang.traffic_class.backlog_age_seconds");
    await Assert.That(age.Value).IsEqualTo(90);
  }

  [Test]
  public async Task BacklogAgeGauge_SkipsEntriesWithNoAgeAsync() {
    // Depth without an age is a backlog we could not date; emitting zero would read as
    // "brand new" and mask a stuck consumer.
    var metrics = new BacklogAgeMetrics(new WhizbangMetrics());

    var entity = $"inbox.orders.{Guid.NewGuid():N}";
    var recorded = _collect(entity, () => metrics.UpdateBacklogs(new Dictionary<string, BacklogAgeMetrics.BacklogGaugeSample> {
      [entity] = new("asb", "ns-1", "bulk", Depth: 7, OldestAgeSeconds: null)
    }));

    await Assert.That(recorded.Any(r => r.Instrument == "whizbang.traffic_class.backlog_depth")).IsTrue();
    await Assert.That(recorded.Any(r => r.Instrument == "whizbang.traffic_class.backlog_age_seconds")).IsFalse();
  }

  [Test]
  public async Task OpsRateGauge_ReportsStoredRateWithTagsAsync() {
    var metrics = new BacklogAgeMetrics(new WhizbangMetrics());

    var ns = $"ns.{Guid.NewGuid():N}";
    var recorded = _collect(ns, () => metrics.UpdateOpsRates(new Dictionary<string, BacklogAgeMetrics.OpsRateGaugeSample> {
      [ns] = new("asb", ns, "control", OpsPerSecond: 12.5)
    }));

    var ops = recorded.Single(r => r.Instrument == "whizbang.traffic_class.ops_rate");
    await Assert.That(ops.Value).IsEqualTo(12.5);
    await Assert.That(ops.Tags["transport_namespace"]).IsEqualTo(ns);
    await Assert.That(ops.Tags["traffic_class"]).IsEqualTo("control");
  }

  [Test]
  public async Task UpdateBacklogs_DropsEntitiesMissingFromTheNewSampleAsync() {
    // Stale entries must go, or a deleted subscription keeps reporting its last depth
    // forever and an operator chases a backlog that no longer exists.
    var metrics = new BacklogAgeMetrics(new WhizbangMetrics());

    metrics.UpdateBacklogs(new Dictionary<string, BacklogAgeMetrics.BacklogGaugeSample> {
      ["gone"] = new("asb", "ns-1", "bulk", 5, 10),
      ["kept"] = new("asb", "ns-1", "bulk", 6, 11)
    });
    metrics.UpdateBacklogs(new Dictionary<string, BacklogAgeMetrics.BacklogGaugeSample> {
      ["kept"] = new("asb", "ns-1", "bulk", 7, 12)
    });

    await Assert.That(metrics.GetBacklogForTest("gone")).IsNull();
    await Assert.That(metrics.GetBacklogForTest("kept")!.Value.Depth).IsEqualTo(7);
  }

  [Test]
  public async Task UpdateOpsRates_DropsNamespacesMissingFromTheNewSampleAsync() {
    var metrics = new BacklogAgeMetrics(new WhizbangMetrics());

    metrics.UpdateOpsRates(new Dictionary<string, BacklogAgeMetrics.OpsRateGaugeSample> {
      ["gone"] = new("asb", "gone", "bulk", 1),
      ["kept"] = new("asb", "kept", "bulk", 2)
    });
    metrics.UpdateOpsRates(new Dictionary<string, BacklogAgeMetrics.OpsRateGaugeSample> {
      ["kept"] = new("asb", "kept", "bulk", 3)
    });

    await Assert.That(metrics.GetOpsRateForTest("gone")).IsNull();
    await Assert.That(metrics.GetOpsRateForTest("kept")!.Value.OpsPerSecond).IsEqualTo(3);
  }

  [Test]
  public async Task UpdateBacklogs_WithNullSamples_ThrowsAsync() {
    var metrics = new BacklogAgeMetrics(new WhizbangMetrics());

    await Assert.That(() => metrics.UpdateBacklogs(null!)).ThrowsExactly<ArgumentNullException>();
  }

  [Test]
  public async Task UpdateOpsRates_WithNullSamples_ThrowsAsync() {
    var metrics = new BacklogAgeMetrics(new WhizbangMetrics());

    await Assert.That(() => metrics.UpdateOpsRates(null!)).ThrowsExactly<ArgumentNullException>();
  }
}
