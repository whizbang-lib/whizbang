using System.Diagnostics.Metrics;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Execution;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Tests.Execution;

/// <summary>
/// Covers <see cref="ObservedConcurrencyGovernor"/>, the decorator that carries a governor's
/// decisions and the evidence behind them into OpenTelemetry.
/// </summary>
/// <remarks>
/// Every test tags its governor with a unique series name and filters measurements on that tag.
/// The meter name is process-global, so a listener filtering only by meter would also capture
/// governors created by tests running in parallel and turn these assertions into a race.
/// </remarks>
[Category("Core")]
[Category("Execution")]
public class ObservedConcurrencyGovernorTests {

  /// <summary>Inner governor whose width can be scripted to move on Observe.</summary>
  private sealed class FakeGovernor : IConcurrencyGovernor {
    public int CurrentWidth { get; set; } = 10;
    public int Floor { get; set; } = 1;
    public int Ceiling { get; set; } = 64;
    /// <summary>Applied to the width when Observe is called, standing in for a real strategy.</summary>
    public Func<int, int>? OnObserve { get; set; }
    public List<GovernorSignal> Observed { get; } = [];

    public void Observe(GovernorSignal signal) {
      Observed.Add(signal);
      if (OnObserve is not null) {
        CurrentWidth = OnObserve(CurrentWidth);
      }
    }
  }

  private sealed record Measurement(string Instrument, double Value, string? Governor, string? Direction);

  /// <summary>Captures governor measurements for one series name only.</summary>
  private static (List<Measurement> Seen, MeterListener Listener) _listenFor(string seriesName) {
    var seen = new List<Measurement>();
    var listener = new MeterListener {
      InstrumentPublished = (instrument, l) => {
        if (instrument.Meter.Name == GovernorMetrics.METER_NAME) {
          l.EnableMeasurementEvents(instrument);
        }
      },
    };

    void Record(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags) {
      string? governor = null, direction = null;
      foreach (var t in tags) {
        if (t.Key == "governor") { governor = t.Value?.ToString(); }
        if (t.Key == "direction") { direction = t.Value?.ToString(); }
      }
      if (governor != seriesName) {
        return;   // another test's governor on the same process-wide meter
      }
      lock (seen) {
        seen.Add(new Measurement(instrument.Name, value, governor, direction));
      }
    }

    listener.SetMeasurementEventCallback<long>((i, v, t, _) => Record(i, v, t));
    listener.SetMeasurementEventCallback<double>((i, v, t, _) => Record(i, v, t));
    listener.Start();
    return (seen, listener);
  }

  private static string _uniqueName() => $"governor-{Guid.NewGuid():N}";

  [Test]
  [Arguments("")]
  [Arguments("   ")]
  public async Task Constructor_RejectsABlankSeriesNameAsync(string name) {
    // The name is the metric's series key. A blank one would export every governor's decisions
    // into the same unlabeled series, which is worse than not exporting at all.
    await Assert.That(() => new ObservedConcurrencyGovernor(
        name, new FakeGovernor(), new GovernorMetrics(new WhizbangMetrics())))
      .Throws<ArgumentException>();
  }

  [Test]
  public async Task Constructor_RejectsANullInnerGovernorAsync() {
    await Assert.That(() => new ObservedConcurrencyGovernor(
        _uniqueName(), null!, new GovernorMetrics(new WhizbangMetrics())))
      .Throws<ArgumentNullException>();
  }

  [Test]
  public async Task Constructor_RejectsNullMetricsAsync() {
    await Assert.That(() => new ObservedConcurrencyGovernor(_uniqueName(), new FakeGovernor(), null!))
      .Throws<ArgumentNullException>();
  }

  [Test]
  public async Task WidthFloorAndCeiling_ReadThroughToTheInnerGovernorAsync() {
    // The decorator must not cache: a governor that adjusts between reads would otherwise be
    // reported at a stale width, and the whole point of the wrapper is faithful export.
    var inner = new FakeGovernor { CurrentWidth = 12, Floor = 3, Ceiling = 40 };
    var governor = new ObservedConcurrencyGovernor(
      _uniqueName(), inner, new GovernorMetrics(new WhizbangMetrics()));

    await Assert.That(governor.CurrentWidth).IsEqualTo(12);
    await Assert.That(governor.Floor).IsEqualTo(3);
    await Assert.That(governor.Ceiling).IsEqualTo(40);

    inner.CurrentWidth = 27;
    await Assert.That(governor.CurrentWidth).IsEqualTo(27)
      .Because("the decorator reads through on every access rather than snapshotting");
  }

  [Test]
  public async Task Observe_ForwardsTheSignalUnchangedAsync() {
    // Decorating must not alter the evidence the strategy sees, or the exported inputs would
    // describe a different cycle than the one the governor actually judged.
    var inner = new FakeGovernor();
    var governor = new ObservedConcurrencyGovernor(
      _uniqueName(), inner, new GovernorMetrics(new WhizbangMetrics()));
    var signal = new GovernorSignal(QueuedItems: 9, Contended: true, Elapsed: TimeSpan.FromSeconds(2), CompletedItems: 6);

    governor.Observe(signal);

    await Assert.That(inner.Observed.Count).IsEqualTo(1);
    await Assert.That(inner.Observed[0]).IsEqualTo(signal);
  }

  [Test]
  public async Task Observe_AttributesAWideningToTheObservationThatCausedItAsync() {
    // Width is read before AND after the inner call so the adjustment belongs to this cycle.
    // Recording only the new width would leave direction to be inferred across scrapes, which is
    // exactly the inference that goes wrong when someone is diagnosing a throughput change.
    var name = _uniqueName();
    var (seen, listener) = _listenFor(name);
    using var _l = listener;
    var inner = new FakeGovernor { CurrentWidth = 8, OnObserve = w => w + 4 };
    var governor = new ObservedConcurrencyGovernor(name, inner, new GovernorMetrics(new WhizbangMetrics()));

    governor.Observe(new GovernorSignal(QueuedItems: 50, Contended: false, Elapsed: TimeSpan.FromSeconds(1)));

    var adjustments = seen.Where(m => m.Instrument == "whizbang.governor.adjustments").ToList();
    await Assert.That(adjustments.Count).IsEqualTo(1);
    await Assert.That(adjustments[0].Direction).IsEqualTo("grew");
  }

  [Test]
  public async Task Observe_AttributesANarrowingToTheObservationThatCausedItAsync() {
    var name = _uniqueName();
    var (seen, listener) = _listenFor(name);
    using var _l = listener;
    var inner = new FakeGovernor { CurrentWidth = 20, OnObserve = w => w - 6 };
    var governor = new ObservedConcurrencyGovernor(name, inner, new GovernorMetrics(new WhizbangMetrics()));

    governor.Observe(new GovernorSignal(QueuedItems: 0, Contended: true, Elapsed: TimeSpan.FromSeconds(1)));

    var adjustments = seen.Where(m => m.Instrument == "whizbang.governor.adjustments").ToList();
    await Assert.That(adjustments.Count).IsEqualTo(1);
    await Assert.That(adjustments[0].Direction).IsEqualTo("shrank");
  }

  [Test]
  public async Task Observe_RecordsNoAdjustmentWhenTheWidthDidNotMoveAsync() {
    // A steady governor must not emit adjustments. Counting no-ops would inflate the very rate
    // this metric exists to expose and make a stable system look like a thrashing one.
    var name = _uniqueName();
    var (seen, listener) = _listenFor(name);
    using var _l = listener;
    var inner = new FakeGovernor { CurrentWidth = 16 };   // OnObserve unset: width holds
    var governor = new ObservedConcurrencyGovernor(name, inner, new GovernorMetrics(new WhizbangMetrics()));

    governor.Observe(new GovernorSignal(QueuedItems: 4, Contended: false, Elapsed: TimeSpan.FromSeconds(1)));

    await Assert.That(seen.Any(m => m.Instrument == "whizbang.governor.adjustments")).IsFalse()
      .Because("an adjustment that moved nothing is not an adjustment");
  }

  [Test]
  public async Task Observe_ExportsTheEvidenceBehindTheDecisionAsync() {
    // The decision alone is not diagnosable. Queue depth and contention are what distinguish
    // correct backoff from a governor misreading an idle queue.
    var name = _uniqueName();
    var (seen, listener) = _listenFor(name);
    using var _l = listener;
    var governor = new ObservedConcurrencyGovernor(
      name, new FakeGovernor(), new GovernorMetrics(new WhizbangMetrics()));

    governor.Observe(new GovernorSignal(QueuedItems: 37, Contended: true, Elapsed: TimeSpan.FromSeconds(2), CompletedItems: 8));

    var queued = seen.SingleOrDefault(m => m.Instrument == "whizbang.governor.queued_items");
    await Assert.That(queued).IsNotNull();
    await Assert.That(queued!.Value).IsEqualTo(37);
    await Assert.That(seen.Any(m => m.Instrument == "whizbang.governor.contention_reports")).IsTrue()
      .Because("contention is the input that explains a narrowing");
    await Assert.That(seen.Any(m => m.Instrument == "whizbang.governor.completed_items")).IsTrue();
  }

  [Test]
  public async Task Observe_WithNoCompletions_SkipsThroughputAsync() {
    // A caller that does not measure completions would otherwise contribute a stream of zeros,
    // dragging every throughput percentile down and making a healthy system look degraded.
    var name = _uniqueName();
    var (seen, listener) = _listenFor(name);
    using var _l = listener;
    var governor = new ObservedConcurrencyGovernor(
      name, new FakeGovernor(), new GovernorMetrics(new WhizbangMetrics()));

    governor.Observe(new GovernorSignal(QueuedItems: 5, Contended: false, Elapsed: TimeSpan.FromSeconds(3)));

    await Assert.That(seen.Any(m => m.Instrument == "whizbang.governor.throughput")).IsFalse()
      .Because("a cycle that reported no completions has no throughput to report");
  }
}
