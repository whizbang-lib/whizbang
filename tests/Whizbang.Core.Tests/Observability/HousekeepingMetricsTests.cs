using System.Diagnostics.Metrics;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Observability;

/// <summary>
/// Locks the arbitration observability: every verdict is countable by activity, the running slot is
/// a live gauge, and the idle tracker surfaces as seconds-since-activity with its source. Before
/// this meter, "is recovery running, deferred, or idle — and why?" was a log grep.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Observability/HousekeepingMetrics.cs</code-under-test>
public class HousekeepingMetricsTests {

  private sealed class FakeTracker : IIdleActivityTracker {
    public TimeSpan TimeSinceLastActivity => TimeSpan.FromSeconds(42);
    public string LastActivitySource => "test-source";

    public DateTimeOffset LastActivityAt => throw new NotImplementedException();

    public void Touch(string source) { }
  }

  private static (HousekeepingMetrics Metrics, List<(string Name, long Value, string? Activity, string? Verdict)> Seen, MeterListener Listener)
      _listen(IIdleActivityTracker? tracker = null) {
    var metrics = new HousekeepingMetrics(new WhizbangMetrics(), tracker);
    var seen = new List<(string, long, string?, string?)>();
    var listener = new MeterListener();
    listener.InstrumentPublished = (inst, l) => {
      if (inst.Meter == metrics.Decisions.Meter) { l.EnableMeasurementEvents(inst); }
    };
    listener.SetMeasurementEventCallback<long>((inst, value, tags, _) => {
      string? act = null, verd = null;
      foreach (var t in tags) {
        if (t.Key == "activity") { act = t.Value?.ToString(); }
        if (t.Key == "verdict") { verd = t.Value?.ToString(); }
      }
      lock (seen) { seen.Add((inst.Name, value, act, verd)); }
    });
    listener.Start();
    return (metrics, seen, listener);
  }

  [Test]
  public async Task EveryVerdict_IsCounted_WithActivityAndVerdictTagsAsync() {
    var (metrics, seen, listener) = _listen();
    using var l_ = listener;

    var c = new HousekeepingCoordinator(new HousekeepingCoordinator.Settings(), metrics);
    c.TryBegin(HousekeepingCoordinator.Activity.DeadLetterRecovery,
      new ServiceBacklog { UnprocessedInboxRows = 500, ActiveLeasedRows = 1 });

    lock (seen) {
      var d = seen.Single(m => m.Name == "whizbang.housekeeping.decisions");
      _ = d;
    }
    await Assert.That(seen.Any(m =>
        m.Name == "whizbang.housekeeping.decisions"
        && m.Activity == "DeadLetterRecovery" && m.Verdict == "ServiceBusy")).IsTrue()
      .Because("a deferred recovery must be visible as a fact on a dashboard, not a log grep");
  }

  [Test]
  public async Task RunningGauge_TracksTheSlot_UpOnGrantDownOnEndAsync() {
    var (metrics, seen, listener) = _listen();
    using var l_ = listener;
    var c = new HousekeepingCoordinator(new HousekeepingCoordinator.Settings(), metrics);
    var settled = new ServiceBacklog { UnprocessedInboxRows = 0, ActiveLeasedRows = 0 };

    c.TryBegin(HousekeepingCoordinator.Activity.DeadLetterRecovery, settled);
    c.End(HousekeepingCoordinator.Activity.DeadLetterRecovery);

    List<long> running;
    lock (seen) {
      running = [.. seen.Where(m => m.Name == "whizbang.housekeeping.running" && m.Activity == "DeadLetterRecovery").Select(m => m.Value)];
    }
    await Assert.That(running).IsEquivalentTo(new long[] { 1, -1 })
      .Because("the running gauge answers WHAT is holding the slot right now; it must sum to zero "
             + "when nothing is");
  }

  [Test]
  public async Task RefusedVerdicts_DoNotTouchTheRunningGaugeAsync() {
    var (metrics, seen, listener) = _listen();
    using var l_ = listener;
    var c = new HousekeepingCoordinator(new HousekeepingCoordinator.Settings(), metrics);

    c.TryBegin(HousekeepingCoordinator.Activity.DeadLetterRecovery,
      new ServiceBacklog { UnprocessedInboxRows = 9, ActiveLeasedRows = 0 });

    lock (seen) {
      var running = seen.Count(m => m.Name == "whizbang.housekeeping.running");
      _ = running;
    }
    await Assert.That(seen.Any(m => m.Name == "whizbang.housekeeping.running")).IsFalse()
      .Because("a refused slot was never held; counting it would make the gauge drift negative");
  }

  [Test]
  public async Task IdleTracker_SurfacesAsAGauge_WithItsSourceAsync() {
    var metrics = new HousekeepingMetrics(new WhizbangMetrics(), new FakeTracker());
    var got = new List<(double Value, string? Source)>();
    using var listener = new MeterListener();
    listener.InstrumentPublished = (inst, l) => {
      if (inst.Name == "whizbang.idle.seconds_since_activity" && inst.Meter == metrics.Decisions.Meter) {
        l.EnableMeasurementEvents(inst);
      }
    };
    listener.SetMeasurementEventCallback<double>((inst, value, tags, _) => {
      string? src = null;
      foreach (var t in tags) { if (t.Key == "last_source") { src = t.Value?.ToString(); } }
      lock (got) { got.Add((value, src)); }
    });
    listener.Start();
    listener.RecordObservableInstruments();

    lock (got) {
      _ = got.Count;
    }
    await Assert.That(got.Any(g => Math.Abs(g.Value - 42) < 0.001 && g.Source == "test-source")).IsTrue()
      .Because("active-versus-idle, with WHY it was last active, is the facet question this exists for");
  }
}
