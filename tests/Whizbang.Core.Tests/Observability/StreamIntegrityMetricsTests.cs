using System.Diagnostics.Metrics;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Tests.Observability;

/// <summary>
/// Regression locks for <see cref="StreamIntegrityMetrics"/>. Instrument + meter names are the
/// observable contract dashboards and alerts rely on — self-healing by default only works when
/// operators can SEE what the healer is doing, so a silent rename breaks the safety story.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Observability/StreamIntegrityMetrics.cs</code-under-test>
public class StreamIntegrityMetricsTests {

  [Test]
  public async Task MeterName_IsStableAsync() {
#pragma warning disable TUnitAssertions0005
    await Assert.That(StreamIntegrityMetrics.METER_NAME).IsEqualTo("Whizbang.StreamIntegrity")
      .Because("operators alert on this meter name — renaming silently breaks dashboards");
#pragma warning restore TUnitAssertions0005
  }

  [Test]
  public async Task Counters_AreCreatedWithExpectedNamesAsync() {
    var observed = new List<string>();
    using var listener = new MeterListener {
      InstrumentPublished = (instrument, _) => {
        if (instrument.Meter.Name == StreamIntegrityMetrics.METER_NAME) {
          observed.Add(instrument.Name);
        }
      },
    };
    listener.Start();

    var _ = new StreamIntegrityMetrics(new WhizbangMetrics());

    await Assert.That(observed).Contains("whizbang.stream_integrity.checkpoints_published");
    await Assert.That(observed).Contains("whizbang.stream_integrity.checkpoints_received");
    await Assert.That(observed).Contains("whizbang.stream_integrity.gaps_detected");
    await Assert.That(observed).Contains("whizbang.stream_integrity.divergences_detected");
    await Assert.That(observed).Contains("whizbang.stream_integrity.coverage_gaps_detected");
    await Assert.That(observed).Contains("whizbang.stream_integrity.repairs_requested");
    await Assert.That(observed).Contains("whizbang.stream_integrity.rebuilds_requested");
    await Assert.That(observed).Contains("whizbang.stream_integrity.manifests_requested");
    await Assert.That(observed).Contains("whizbang.stream_integrity.manifest_chunks_sent");
    await Assert.That(observed).Contains("whizbang.stream_integrity.drill_downs_requested");
    await Assert.That(observed).Contains("whizbang.stream_integrity.backfills_requested");
    await Assert.That(observed).Contains("whizbang.stream_integrity.digest_buckets_verified");
    await Assert.That(observed).Contains("whizbang.stream_integrity.digest_drift_healed");
    await Assert.That(observed).Contains("whizbang.stream_integrity.redelivery_requests_received");
  }

  [Test]
  public async Task Counters_AreNotNullAsync() {
    var metrics = new StreamIntegrityMetrics(new WhizbangMetrics());
    await Assert.That(metrics.CheckpointsPublished).IsNotNull();
    await Assert.That(metrics.CheckpointsReceived).IsNotNull();
    await Assert.That(metrics.GapsDetected).IsNotNull();
    await Assert.That(metrics.DivergencesDetected).IsNotNull();
    await Assert.That(metrics.CoverageGapsDetected).IsNotNull();
    await Assert.That(metrics.RepairsRequested).IsNotNull();
    await Assert.That(metrics.RebuildsRequested).IsNotNull();
    await Assert.That(metrics.ManifestsRequested).IsNotNull();
    await Assert.That(metrics.ManifestChunksSent).IsNotNull();
    await Assert.That(metrics.DrillDownsRequested).IsNotNull();
    await Assert.That(metrics.BackfillsRequested).IsNotNull();
    await Assert.That(metrics.DigestBucketsVerified).IsNotNull();
    await Assert.That(metrics.DigestDriftHealed).IsNotNull();
    await Assert.That(metrics.RedeliveryRequestsReceived).IsNotNull();
  }

  [Test]
  public async Task DurationHistograms_AndFlowCounters_AreCreatedWithExpectedNamesAsync() {
    // Every instrument here measures work the system ALREADY does — a stopwatch around an
    // existing handler, a counter at an existing branch. Nothing below adds queries or state
    // for the metric's sake; that is the bar for this meter.
    var observed = new List<string>();
    using var listener = new MeterListener {
      InstrumentPublished = (instrument, _) => {
        if (instrument.Meter.Name == StreamIntegrityMetrics.METER_NAME) {
          observed.Add(instrument.Name);
        }
      },
    };
    listener.Start();

    var _ = new StreamIntegrityMetrics(new WhizbangMetrics());

    // Durations: the compare histogram is the alpha-59-class early warning — comparisons slower
    // than manifest arrivals queued payloads in memory until the process died, invisibly.
    await Assert.That(observed).Contains("whizbang.stream_integrity.manifest_answer_duration");
    await Assert.That(observed).Contains("whizbang.stream_integrity.manifest_compare_duration");
    await Assert.That(observed).Contains("whizbang.stream_integrity.redelivery_build_duration");
    await Assert.That(observed).Contains("whizbang.stream_integrity.bucket_heal_seconds");
    // Flow counters at existing branches.
    await Assert.That(observed).Contains("whizbang.stream_integrity.compares_declined");
    await Assert.That(observed).Contains("whizbang.stream_integrity.redelivery_events_shipped");
    await Assert.That(observed).Contains("whizbang.stream_integrity.manifest_pages_followed");
    await Assert.That(observed).Contains("whizbang.stream_integrity.manifest_pages_capped");
    // Certification progress: the per-origin verified watermark.
    await Assert.That(observed).Contains("whizbang.stream_integrity.sealed_through");
  }

  [Test]
  public async Task SealedThroughGauge_ReportsOneMeasurementPerOrigin_TaggedAsync() {
    var originA = Guid.NewGuid();
    var originB = Guid.NewGuid();
    var readings = new List<(long Value, string? Origin)>();
    using var listener = new MeterListener();
    listener.InstrumentPublished = (instrument, l) => {
      if (instrument.Meter.Name == StreamIntegrityMetrics.METER_NAME
          && instrument.Name == "whizbang.stream_integrity.sealed_through") {
        l.EnableMeasurementEvents(instrument);
      }
    };
    listener.SetMeasurementEventCallback<long>((_, value, tags, _) => {
      string? origin = null;
      foreach (var tag in tags) {
        if (tag.Key == "origin_service_id") {
          origin = tag.Value?.ToString();
        }
      }
      lock (readings) {
        readings.Add((value, origin));
      }
    });
    listener.Start();

    var metrics = new StreamIntegrityMetrics(new WhizbangMetrics());
    metrics.UpdateLedgerGauges(new LedgerGaugeSnapshot {
      UnhealedBuckets = 5,
      Seals = [new OriginSeal(originA, 300), new OriginSeal(originB, 0)],
    });
    listener.RecordObservableInstruments();

    await Assert.That(readings.Count).IsEqualTo(2)
      .Because("each audited origin's watermark is its own series — one number would hide a stuck lane");
    await Assert.That(readings.Any(r => r.Value == 300 && r.Origin == originA.ToString())).IsTrue();
    await Assert.That(readings.Any(r => r.Value == 0 && r.Origin == originB.ToString())).IsTrue();
  }
}
