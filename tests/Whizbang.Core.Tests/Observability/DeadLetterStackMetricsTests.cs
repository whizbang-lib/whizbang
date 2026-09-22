using System.Diagnostics.Metrics;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Observability;

#pragma warning disable CA1707 // test method underscores

namespace Whizbang.Core.Tests.Observability;

/// <summary>
/// <para>Locks the real-time half of the two-layer stack telemetry contract (P2 of
/// plans/dlq-stack-intelligence.md): every dead-letter arrival is counted under its
/// stack_id within seconds (the new-stack-after-deploy alarm depends on immediacy), the
/// normalization is the SAME single implementation the backfill uses, and the tag
/// cardinality is capped per process so the meter can never become unbounded.</para>
/// </summary>
/// <code-under-test>src/Whizbang.Core/Observability/DeadLetterMetrics.cs</code-under-test>
[Category("Shard2")]
public sealed class DeadLetterStackMetricsTests {

  // The meter is shared by NAME across parallel tests, so every probe builds its metrics on
  // a per-test meter factory AND stamps its arrivals with a unique source_table marker,
  // filtering on both — instance isolation via meter identity and tags. The arrivals counter
  // is passive (#711): nothing reaches the listener at RecordArrival time; Snapshot() collects
  // the observable instruments and returns one CUMULATIVE reading per series.
  private sealed class ArrivalProbe : IDisposable {
    private readonly TestMeterFactory _factory = new();
    private readonly MeterListener _listener = new();
    private readonly List<(long Value, string? Stack, string? Reason)> _recorded = [];

    public DeadLetterMetrics Metrics { get; }
    public string Marker { get; } = "src-" + Guid.NewGuid().ToString("N")[..8];

    public ArrivalProbe() {
      Metrics = new DeadLetterMetrics(new WhizbangMetrics(_factory));
      _listener.InstrumentPublished = (instrument, l) => {
        if (_factory.CreatedMeters.Contains(instrument.Meter)
            && instrument.Name == "whizbang.dead_letters.arrivals_by_stack") {
          l.EnableMeasurementEvents(instrument);
        }
      };
      _listener.SetMeasurementEventCallback<long>((_, value, tags, _) => {
        string? stack = null;
        string? reason = null;
        string? source = null;
        foreach (var tag in tags) {
          if (tag.Key == "stack_id") { stack = tag.Value?.ToString(); }
          if (tag.Key == "reason") { reason = tag.Value?.ToString(); }
          if (tag.Key == "source_table") { source = tag.Value?.ToString(); }
        }
        if (source == Marker) {
          lock (_recorded) { _recorded.Add((value, stack, reason)); }
        }
      });
      _listener.Start();
    }

    /// <summary>
    /// Collects the passive counter and returns the current cumulative value of every
    /// arrivals series stamped with this probe's marker — one entry per distinct tag set.
    /// </summary>
    public (long Value, string? Stack, string? Reason)[] Snapshot() {
      lock (_recorded) { _recorded.Clear(); }
      _listener.RecordObservableInstruments();
      lock (_recorded) { return [.. _recorded]; }
    }

    public void Dispose() {
      _listener.Dispose();
      _factory.Dispose();
    }
  }

  [Test]
  public async Task Arrival_TagsTheNormalizerStackIdAsync() {
    using var probe = new ArrivalProbe();
    var text = "System.InvalidOperationException: x\n   at A.B.<M>d__3.MoveNext()";

    probe.Metrics.RecordArrival(probe.Marker, 5, text);

    var expected = Whizbang.Core.DeadLetters.StackNormalizer.Normalize(text)!.SequenceHash;
    var snap = probe.Snapshot();
    await Assert.That(snap.Length).IsEqualTo(1);
    await Assert.That(snap[0].Value).IsEqualTo(1L);
    await Assert.That(snap[0].Stack).IsEqualTo(expected)
      .Because("the inline metric and the backfill share ONE normalizer — the dashboard's "
             + "stack_id must join to the relational layer's stack_id verbatim");
    await Assert.That(snap[0].Reason).IsEqualTo("5");
  }

  [Test]
  public async Task Arrival_WithNoErrorText_TagsNoneAsync() {
    using var probe = new ArrivalProbe();

    probe.Metrics.RecordArrival(probe.Marker, 5, null);

    var snap = probe.Snapshot();
    await Assert.That(snap.Length).IsEqualTo(1);
    await Assert.That(snap[0].Value).IsEqualTo(1L);
    await Assert.That(snap[0].Stack).IsEqualTo("none")
      .Because("an arrival with no text still counts — an untagged hole in the arrival "
             + "series would understate a storm");
  }

  [Test]
  public async Task Arrival_CardinalityCap_OverflowsToOneBucketAsync() {
    using var probe = new ArrivalProbe();
    const int arrivals = DeadLetterMetrics.MAX_DISTINCT_STACK_TAGS + 25;

    // Distinct prose templates beyond the cap. Constraints the scrubber imposes on the
    // test data: non-hex letters only (a 8+ hex run scrubs to <h>), no digits, and the
    // variance must sit INSIDE the 160-char template truncation window.
    for (var i = 0; i < arrivals; i++) {
      var tag = $"{(char)('g' + (i % 20))}{(char)('g' + (i / 20 % 20))}{(char)('g' + (i / 400 % 20))}";
      probe.Metrics.RecordArrival(probe.Marker, 5, $"unique-template-{tag} failure");
    }

    // One cumulative reading per series: at most MAX_DISTINCT_STACK_TAGS real stack ids, plus
    // the single overflow bucket holding everything past the cap.
    var snap = probe.Snapshot();
    var stacks = snap.Select(r => r.Stack).ToList();
    var distinct = stacks.Where(s => s != "overflow").Distinct().Count();
    await Assert.That(distinct).IsLessThanOrEqualTo(DeadLetterMetrics.MAX_DISTINCT_STACK_TAGS)
      .Because("stack_id cardinality is naturally bounded by dedup in any one storm, but "
             + "unbounded across a process lifetime — the cap keeps the meter honest forever");
    await Assert.That(stacks.Contains("overflow")).IsTrue()
      .Because("overflow arrivals still count, in one bucket, rather than being dropped");
    await Assert.That(snap.Sum(r => r.Value)).IsEqualTo((long)arrivals)
      .Because("the cap changes which series an arrival lands on, never whether it is counted");
  }

  [Test]
  public async Task CohortVerdicts_CountByCohortAndVerdictAsync() {
    using var factory = new TestMeterFactory();
    var metrics = new DeadLetterMetrics(new WhizbangMetrics(factory));
    var recorded = new List<(string? Cohort, string? Verdict, long Value)>();
    using var listener = new MeterListener();
    listener.InstrumentPublished = (instrument, l) => {
      if (factory.CreatedMeters.Contains(instrument.Meter)
          && instrument.Name == "whizbang.dead_letters.cohort_verdicts") {
        l.EnableMeasurementEvents(instrument);
      }
    };
    listener.SetMeasurementEventCallback<long>((_, value, tags, _) => {
      if (value == 0) {
        return;   // the untagged series and the constructor's per-verdict seeds (#711), not a verdict
      }
      string? c = null;
      string? verdict = null;
      foreach (var tag in tags) {
        if (tag.Key == "cohort") { c = tag.Value?.ToString(); }
        if (tag.Key == "verdict") { verdict = tag.Value?.ToString(); }
      }
      lock (recorded) { recorded.Add((c, verdict, value)); }
    });
    listener.Start();

    var cohort = "fp-" + Guid.NewGuid().ToString("N")[..12];
    metrics.RecordCohortVerdict(cohort, Whizbang.Core.Messaging.CanaryVerdictKind.Mixed);

    // Passive counter (#711): the series is reported only when the listener collects.
    listener.RecordObservableInstruments();
    (string?, string?, long)[] snap;
    lock (recorded) { snap = [.. recorded]; }
    await Assert.That(snap.Length).IsEqualTo(1);
    await Assert.That(snap[0].Item1).IsEqualTo(cohort);
    await Assert.That(snap[0].Item2).IsEqualTo("Mixed")
      .Because("the campaign lifecycle is a graph: pass/fail/mixed per cohort is how an "
             + "operator sees a canary program working without reading a single log line");
    await Assert.That(snap[0].Item3).IsEqualTo(1L);
  }

  [Test]
  public async Task StackHistoryPruned_CountsTheCleanupFacetAsync() {
    using var factory = new TestMeterFactory();
    var metrics = new DeadLetterMetrics(new WhizbangMetrics(factory));
    long recorded = 0;
    using var listener = new MeterListener();
    listener.InstrumentPublished = (instrument, l) => {
      if (factory.CreatedMeters.Contains(instrument.Meter)
          && instrument.Name == "whizbang.dead_letters.stack_history_pruned") {
        l.EnableMeasurementEvents(instrument);
      }
    };
    listener.SetMeasurementEventCallback<long>((_, value, _, _) => Interlocked.Add(ref recorded, value));
    listener.Start();

    metrics.RecordStackHistoryPruned(42);
    metrics.RecordStackHistoryPruned(0); // zero is not recorded

    // Passive counter (#711): one collection reports the cumulative value of its only series.
    listener.RecordObservableInstruments();
    await Assert.That(Interlocked.Read(ref recorded)).IsEqualTo(42L)
      .Because("the rolling-history cleanup is a maintenance facet an operator watches on a "
             + "dashboard; a zero pass is not noise worth a data point");
  }


  [Test]
  public async Task NewStacks_CountsTheFirstSeenFailureShapesAsync() {
    using var factory = new TestMeterFactory();
    var metrics = new DeadLetterMetrics(new WhizbangMetrics(factory));
    long recorded = 0;
    using var listener = new MeterListener();
    listener.InstrumentPublished = (instrument, l) => {
      if (factory.CreatedMeters.Contains(instrument.Meter)
          && instrument.Name == "whizbang.dead_letters.new_stacks") {
        l.EnableMeasurementEvents(instrument);
      }
    };
    listener.SetMeasurementEventCallback<long>((_, value, _, _) => Interlocked.Add(ref recorded, value));
    listener.Start();

    metrics.RecordNewStacks(3);
    metrics.RecordNewStacks(0); // a batch with no new shapes is not a data point

    // Passive counter (#711): one collection reports the cumulative value of its only series.
    listener.RecordObservableInstruments();
    await Assert.That(Interlocked.Read(ref recorded)).IsEqualTo(3L)
      .Because("a never-before-seen stack_id is the new-failure-mode alarm — a first-class "
             + "counter, not a query for 'stack with no history'");
  }

}
