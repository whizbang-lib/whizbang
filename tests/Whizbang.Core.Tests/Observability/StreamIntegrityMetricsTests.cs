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
}
