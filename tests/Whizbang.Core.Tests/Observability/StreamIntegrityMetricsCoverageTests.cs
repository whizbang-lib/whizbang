using System.Diagnostics.Metrics;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Tests.Observability;

/// <summary>
/// Coverage-round tests for <see cref="StreamIntegrityMetrics"/> members that
/// <see cref="StreamIntegrityMetricsTests"/> does not exercise: four duration/flow instruments
/// whose properties are only checked by instrument NAME there (never read, so a real value/tag
/// never flows through them), and the two ledger gauges besides <c>sealed_through</c> whose
/// observation callbacks are never triggered.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Observability/StreamIntegrityMetrics.cs</code-under-test>
public class StreamIntegrityMetricsCoverageTests {

  // If any of these duration histograms stopped recording -- or silently dropped the "level"
  // tag -- the compare-slower-than-arrival early warning goes blind: comparisons could fall
  // behind manifest arrivals with no signal until queued chunks exhaust memory, which is exactly
  // the failure class this meter exists to catch before it gets that far.
  [Test]
  public async Task DurationHistograms_RecordElapsedSecondsAndTagsAsync() {
    var readings = new List<(string Name, double Value, string? Level)>();
    using var listener = new MeterListener();
    listener.InstrumentPublished = (instrument, l) => {
      if (instrument.Meter.Name == StreamIntegrityMetrics.METER_NAME) {
        l.EnableMeasurementEvents(instrument);
      }
    };
    listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) => {
      string? level = null;
      foreach (var tag in tags) {
        if (tag.Key == "level") {
          level = tag.Value?.ToString();
        }
      }
      lock (readings) {
        readings.Add((instrument.Name, value, level));
      }
    });
    listener.Start();

    var metrics = new StreamIntegrityMetrics(new WhizbangMetrics());
    metrics.ManifestAnswerDuration.Record(0.5, new KeyValuePair<string, object?>("level", "Types"));
    metrics.ManifestCompareDuration.Record(1.25, new KeyValuePair<string, object?>("level", "Streams"));
    metrics.RedeliveryBuildDuration.Record(2.5);
    metrics.BucketHealSeconds.Record(90.0);

    await Assert.That(readings.Any(r =>
      r.Name == "whizbang.stream_integrity.manifest_answer_duration" && r.Value == 0.5 && r.Level == "Types"))
      .IsTrue().Because("the origin-side answer duration and its level tag must reach the collector unchanged");
    await Assert.That(readings.Any(r =>
      r.Name == "whizbang.stream_integrity.manifest_compare_duration" && r.Value == 1.25 && r.Level == "Streams"))
      .IsTrue().Because("the consumer-side compare duration is the early-warning signal -- a lost tag hides which level is slow");
    await Assert.That(readings.Any(r =>
      r.Name == "whizbang.stream_integrity.redelivery_build_duration" && r.Value == 2.5))
      .IsTrue().Because("the origin's redelivery build time must be observable to size the repair path");
    await Assert.That(readings.Any(r =>
      r.Name == "whizbang.stream_integrity.bucket_heal_seconds" && r.Value == 90.0))
      .IsTrue().Because("time-to-reconcile per divergent bucket is the reconciliation SLA an operator watches");
  }

  // If these flow counters stopped incrementing -- or lost the "origin" tag identifying which
  // remote is involved -- the redelivery/backfill pipeline would look idle on a dashboard while
  // actually shedding compares at the gate or capping cursor-follow mid-audit for one specific
  // origin, with no way to tell which lane needs attention.
  [Test]
  public async Task FlowCounters_RecordValueAndTagsAsync() {
    var readings = new List<(string Name, long Value, string? Origin)>();
    using var listener = new MeterListener();
    listener.InstrumentPublished = (instrument, l) => {
      if (instrument.Meter.Name == StreamIntegrityMetrics.METER_NAME) {
        l.EnableMeasurementEvents(instrument);
      }
    };
    listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => {
      string? origin = null;
      foreach (var tag in tags) {
        if (tag.Key == "origin") {
          origin = tag.Value?.ToString();
        }
      }
      lock (readings) {
        readings.Add((instrument.Name, value, origin));
      }
    });
    listener.Start();

    var metrics = new StreamIntegrityMetrics(new WhizbangMetrics());
    metrics.ComparesDeclined.Add(4, new KeyValuePair<string, object?>("origin", "origin-a"));
    metrics.RedeliveryEventsShipped.Add(12);
    metrics.ManifestPagesFollowed.Add(1, new KeyValuePair<string, object?>("origin", "origin-b"));
    metrics.ManifestPagesCapped.Add(2, new KeyValuePair<string, object?>("origin", "origin-b"));

    await Assert.That(readings.Any(r =>
      r.Name == "whizbang.stream_integrity.compares_declined" && r.Value == 4 && r.Origin == "origin-a"))
      .IsTrue().Because("the compare-gate pressure reading must carry which origin is causing it");
    await Assert.That(readings.Any(r =>
      r.Name == "whizbang.stream_integrity.redelivery_events_shipped" && r.Value == 12))
      .IsTrue().Because("events shipped in a redelivery bundle must be observable to size repair traffic");
    await Assert.That(readings.Any(r =>
      r.Name == "whizbang.stream_integrity.manifest_pages_followed" && r.Value == 1 && r.Origin == "origin-b"))
      .IsTrue().Because("cursor-follow progress must be attributable to the lane's origin");
    await Assert.That(readings.Any(r =>
      r.Name == "whizbang.stream_integrity.manifest_pages_capped" && r.Value == 2 && r.Origin == "origin-b"))
      .IsTrue().Because("hitting the per-window page budget must name the origin so the cap can be raised for the right lane");
  }

  // If these ledger gauges stopped reflecting live state, an operator watching the dashboard
  // during an active divergence would see stale zeros and could not tell a bucket that has
  // exhausted its repair budget (needs a human now) from a system that is quietly healing on
  // its own -- the exact distinction self-healing-by-default depends on being visible.
  [Test]
  public async Task LedgerGauges_UnhealedRepairExhaustedAndOldestAge_ReportLiveSnapshotValuesAsync() {
    long? unhealed = null;
    long? repairExhausted = null;
    double? oldestAge = null;
    using var listener = new MeterListener();
    listener.InstrumentPublished = (instrument, l) => {
      if (instrument.Meter.Name == StreamIntegrityMetrics.METER_NAME) {
        l.EnableMeasurementEvents(instrument);
      }
    };
    listener.SetMeasurementEventCallback<long>((instrument, value, _, _) => {
      if (instrument.Name == "whizbang.stream_integrity.unhealed_buckets") {
        unhealed = value;
      } else if (instrument.Name == "whizbang.stream_integrity.repair_exhausted_buckets") {
        repairExhausted = value;
      }
    });
    listener.SetMeasurementEventCallback<double>((instrument, value, _, _) => {
      if (instrument.Name == "whizbang.stream_integrity.oldest_unhealed_age_seconds") {
        oldestAge = value;
      }
    });
    listener.Start();

    var metrics = new StreamIntegrityMetrics(new WhizbangMetrics());
    metrics.UpdateLedgerGauges(new LedgerGaugeSnapshot {
      UnhealedBuckets = 7,
      RepairExhausted = 3,
      OldestUnhealedAgeSeconds = 125.5,
    });
    listener.RecordObservableInstruments();

    await Assert.That(unhealed).IsEqualTo((long?)7)
      .Because("unhealed buckets falling only as repair works is what tells an operator healing is progressing");
    await Assert.That(repairExhausted).IsEqualTo((long?)3)
      .Because("buckets that exhausted their repair budget need a human -- a stale reading hides that they stopped asking");
    await Assert.That(oldestAge).IsEqualTo((double?)125.5)
      .Because("the oldest unhealed age distinguishes a transient blip from a divergence that has been stuck for a long time");
  }
}
