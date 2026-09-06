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
}
