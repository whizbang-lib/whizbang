using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Linq;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Tests.Observability;

/// <summary>
/// Tests for <see cref="NotifyDebounceMetrics"/> cache + meter creation. ObservableGauge callbacks
/// are only sampled by an active listener, so the cache (via the GetForTest seam) is the direct
/// way to prove a reading was fed to the gauges.
/// </summary>
/// <tests>src/Whizbang.Core/Observability/NotifyDebounceMetrics.cs</tests>
[Category("Core")]
[Category("Observability")]
public class NotifyDebounceMetricsTests {

  [Test]
  public async Task MeterName_IsWhizbangNotifyDebounceAsync() {
    var meterName = NotifyDebounceMetrics.METER_NAME;
    await Assert.That(meterName).IsEqualTo("Whizbang.NotifyDebounce");
  }

  [Test]
  public async Task Constructor_CreatesWithoutErrorAsync() {
    var metrics = new NotifyDebounceMetrics(new WhizbangMetrics());
    await Assert.That(metrics).IsNotNull();
  }

  [Test]
  public async Task Update_StoresPerKindReadings_IncludingTheRegimeAsync() {
    var metrics = new NotifyDebounceMetrics(new WhizbangMetrics());
    metrics.Update([
      new NotifyDebounceKindStats("inbox", FiredCount: 12, SuppressedCount: 3, MaxEffectiveWindowMs: 50, MaxRapidRun: 0),
      new NotifyDebounceKindStats("outbox", FiredCount: 4, SuppressedCount: 99, MaxEffectiveWindowMs: 7000, MaxRapidRun: 11),
    ]);

    var inbox = metrics.GetForTest("inbox");
    await Assert.That(inbox.HasValue).IsTrue();
    await Assert.That(inbox!.Value.FiredCount).IsEqualTo(12L);
    await Assert.That(inbox.Value.MaxEffectiveWindowMs).IsEqualTo(50)
      .Because("inbox at the floor is the real-time regime — the gauge must show it");

    var outbox = metrics.GetForTest("outbox");
    await Assert.That(outbox.HasValue).IsTrue();
    await Assert.That(outbox!.Value.MaxEffectiveWindowMs).IsEqualTo(7000)
      .Because("outbox at the ceiling is the flooding regime — the state this metric exists to surface");
    await Assert.That(outbox.Value.SuppressedCount).IsEqualTo(99L);
    await Assert.That(outbox.Value.MaxRapidRun).IsEqualTo(11);
  }

  [Test]
  public async Task Update_LatestReadingWins_PerKindAsync() {
    var metrics = new NotifyDebounceMetrics(new WhizbangMetrics());
    metrics.Update([new NotifyDebounceKindStats("inbox", 1, 0, 50, 0)]);
    metrics.Update([new NotifyDebounceKindStats("inbox", 5, 2, 7000, 8)]);

    var inbox = metrics.GetForTest("inbox");
    await Assert.That(inbox!.Value.FiredCount).IsEqualTo(5L)
      .Because("each cycle overwrites the previous reading for a kind");
    await Assert.That(inbox.Value.MaxRapidRun).IsEqualTo(8);
  }

  [Test]
  public async Task GetForTest_UnknownKind_IsNullAsync() {
    var metrics = new NotifyDebounceMetrics(new WhizbangMetrics());
    await Assert.That(metrics.GetForTest("never-seen").HasValue).IsFalse();
  }

  private sealed record Sampled(string Instrument, double Value, string PayloadKind);

  /// <summary>
  /// Samples the four observable gauges once. Every instance publishes under the same meter name,
  /// so the readings are filtered down to the payload kinds this test seeded — otherwise a metrics
  /// instance built by a parallel test would contribute measurements to the assertion.
  /// </summary>
  private static List<Sampled> _sample(NotifyDebounceMetrics metrics, HashSet<string> kinds) {
    var recorded = new List<Sampled>();
    using var listener = new MeterListener();
    listener.InstrumentPublished = (instrument, l) => {
      if (instrument.Meter.Name == NotifyDebounceMetrics.METER_NAME) {
        l.EnableMeasurementEvents(instrument);
      }
    };
    listener.SetMeasurementEventCallback<long>((inst, value, tags, _) =>
      recorded.Add(new Sampled(inst.Name, value, _payloadKind(tags))));
    listener.SetMeasurementEventCallback<int>((inst, value, tags, _) =>
      recorded.Add(new Sampled(inst.Name, value, _payloadKind(tags))));
    listener.Start();
    listener.RecordObservableInstruments();
    GC.KeepAlive(metrics);
    return recorded.Where(r => kinds.Contains(r.PayloadKind)).ToList();
  }

  private static string _payloadKind(ReadOnlySpan<KeyValuePair<string, object?>> tags) {
    foreach (var t in tags) {
      if (string.Equals(t.Key, "payload_kind", StringComparison.Ordinal)) {
        return t.Value?.ToString() ?? "";
      }
    }
    return "";
  }

  [Test]
  public async Task Gauges_ProjectEveryCachedReading_TaggedByPayloadKindAsync() {
    // The cache is only half the contract: a collector sampling the gauges must see one
    // measurement PER KIND on each instrument, tagged with that kind. Without the per-kind
    // projection the whole point of these meters is lost -- "one kind flooding while another
    // stays real-time" is exactly the comparison a single untagged number cannot express.
    var inbox = $"inbox-{Guid.NewGuid():N}";
    var outbox = $"outbox-{Guid.NewGuid():N}";
    var metrics = new NotifyDebounceMetrics(new WhizbangMetrics());
    metrics.Update([
      new NotifyDebounceKindStats(inbox, FiredCount: 12, SuppressedCount: 3, MaxEffectiveWindowMs: 50, MaxRapidRun: 0),
      new NotifyDebounceKindStats(outbox, FiredCount: 4, SuppressedCount: 99, MaxEffectiveWindowMs: 7000, MaxRapidRun: 11),
    ]);

    var sampled = _sample(metrics, new HashSet<string>(StringComparer.Ordinal) { inbox, outbox });

    await Assert.That(sampled.Count).IsEqualTo(8)
      .Because("four gauges times two seeded kinds -- a gauge that reported a single aggregate "
             + "number instead of one measurement per kind would report fewer");

    async Task AssertGaugeAsync(string instrument, string kind, double expected, string because) {
      var match = sampled.Where(s => s.Instrument == instrument && s.PayloadKind == kind).ToList();
      await Assert.That(match.Count).IsEqualTo(1)
        .Because($"{instrument} must report exactly one measurement for {kind}");
      await Assert.That(match[0].Value).IsEqualTo(expected).Because(because);
    }

    await AssertGaugeAsync("whizbang.notify.doorbell_fired", inbox, 12,
      "doorbell_fired must carry the fired count of the kind it is tagged with");
    await AssertGaugeAsync("whizbang.notify.doorbell_fired", outbox, 4,
      "the kinds must not be summed together -- they are the comparison");
    await AssertGaugeAsync("whizbang.notify.doorbell_suppressed", outbox, 99,
      "suppressed is the redundant notify load the debounce absorbed for that kind");
    await AssertGaugeAsync("whizbang.notify.doorbell_suppressed", inbox, 3,
      "a real-time kind still reports its own (small) suppressed count");
    await AssertGaugeAsync("whizbang.notify.effective_window_ms", inbox, 50,
      "at the floor the doorbell is real-time -- the regime gauge must say so");
    await AssertGaugeAsync("whizbang.notify.effective_window_ms", outbox, 7000,
      "at the ceiling the controller is debouncing a flood -- the state the gauge exists for");
    await AssertGaugeAsync("whizbang.notify.rapid_run", outbox, 11,
      "rapid_run reports how sustained the flood toward that kind is");
    await AssertGaugeAsync("whizbang.notify.rapid_run", inbox, 0,
      "a quiet kind reports depth zero rather than being omitted");
  }

  [Test]
  public async Task Gauges_ReportTheLatestCachedReading_NotTheFirstAsync() {
    // A gauge that captured its value at construction would keep reporting the first cycle
    // forever -- the flood would be invisible on the dashboard while it was happening.
    var kind = $"inbox-{Guid.NewGuid():N}";
    var metrics = new NotifyDebounceMetrics(new WhizbangMetrics());
    metrics.Update([new NotifyDebounceKindStats(kind, 1, 0, 50, 0)]);
    var before = _sample(metrics, new HashSet<string>(StringComparer.Ordinal) { kind });

    metrics.Update([new NotifyDebounceKindStats(kind, 5, 2, 7000, 8)]);
    var after = _sample(metrics, new HashSet<string>(StringComparer.Ordinal) { kind });

    await Assert.That(before.Single(s => s.Instrument == "whizbang.notify.effective_window_ms").Value)
      .IsEqualTo(50);
    await Assert.That(after.Single(s => s.Instrument == "whizbang.notify.effective_window_ms").Value)
      .IsEqualTo(7000)
      .Because("the gauge reads the cache at collection time, so a later cycle overwrites it");
    await Assert.That(after.Single(s => s.Instrument == "whizbang.notify.rapid_run").Value)
      .IsEqualTo(8);
    await Assert.That(after.Single(s => s.Instrument == "whizbang.notify.doorbell_fired").Value)
      .IsEqualTo(5);
    await Assert.That(after.Single(s => s.Instrument == "whizbang.notify.doorbell_suppressed").Value)
      .IsEqualTo(2);
  }
}
