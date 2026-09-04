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

  // The meter is shared by NAME across parallel tests, so every test stamps its arrivals
  // with a unique source_table marker and filters on it — instance isolation via tags.
  private static (DeadLetterMetrics Metrics, string Marker, List<(long Value, string? Stack, string? Reason)> Recorded, MeterListener Listener) _arm() {
    var metrics = new DeadLetterMetrics(new WhizbangMetrics());
    var marker = "src-" + Guid.NewGuid().ToString("N")[..8];
    var recorded = new List<(long, string?, string?)>();
    var listener = new MeterListener();
    listener.InstrumentPublished = (instrument, l) => {
      if (instrument.Meter.Name == DeadLetterMetrics.METER_NAME
          && instrument.Name == "whizbang.dead_letters.arrivals_by_stack") {
        l.EnableMeasurementEvents(instrument);
      }
    };
    listener.SetMeasurementEventCallback<long>((_, value, tags, _) => {
      string? stack = null;
      string? reason = null;
      string? source = null;
      foreach (var tag in tags) {
        if (tag.Key == "stack_id") { stack = tag.Value?.ToString(); }
        if (tag.Key == "reason") { reason = tag.Value?.ToString(); }
        if (tag.Key == "source_table") { source = tag.Value?.ToString(); }
      }
      if (source == marker) {
        lock (recorded) { recorded.Add((value, stack, reason)); }
      }
    });
    listener.Start();
    return (metrics, marker, recorded, listener);
  }

  [Test]
  public async Task Arrival_TagsTheNormalizerStackIdAsync() {
    var (metrics, marker, recorded, listener) = _arm();
    using var _ = listener;
    var text = "System.InvalidOperationException: x\n   at A.B.<M>d__3.MoveNext()";

    metrics.RecordArrival(marker, 5, text);

    var expected = Whizbang.Core.DeadLetters.StackNormalizer.Normalize(text)!.SequenceHash;
    (long, string?, string?)[] snap;
    lock (recorded) { snap = [.. recorded]; }
    await Assert.That(snap.Length).IsEqualTo(1);
    await Assert.That(snap[0].Item2).IsEqualTo(expected)
      .Because("the inline metric and the backfill share ONE normalizer — the dashboard's "
             + "stack_id must join to the relational layer's stack_id verbatim");
    await Assert.That(snap[0].Item3).IsEqualTo("5");
  }

  [Test]
  public async Task Arrival_WithNoErrorText_TagsNoneAsync() {
    var (metrics, marker, recorded, listener) = _arm();
    using var _ = listener;

    metrics.RecordArrival(marker, 5, null);

    (long, string?, string?)[] snap;
    lock (recorded) { snap = [.. recorded]; }
    await Assert.That(snap.Length).IsEqualTo(1);
    await Assert.That(snap[0].Item2).IsEqualTo("none")
      .Because("an arrival with no text still counts — an untagged hole in the arrival "
             + "series would understate a storm");
  }

  [Test]
  public async Task Arrival_CardinalityCap_OverflowsToOneBucketAsync() {
    var (metrics, marker, recorded, listener) = _arm();
    using var _ = listener;

    // Distinct prose templates beyond the cap. Constraints the scrubber imposes on the
    // test data: non-hex letters only (a 8+ hex run scrubs to <h>), no digits, and the
    // variance must sit INSIDE the 160-char template truncation window.
    for (var i = 0; i < DeadLetterMetrics.MAX_DISTINCT_STACK_TAGS + 25; i++) {
      var tag = $"{(char)('g' + (i % 20))}{(char)('g' + (i / 20 % 20))}{(char)('g' + (i / 400 % 20))}";
      metrics.RecordArrival(marker, 5, $"unique-template-{tag} failure");
    }

    List<string?> stacks;
    lock (recorded) { stacks = [.. recorded.Select(r => r.Item2)]; }
    var distinct = stacks.Where(s => s != "overflow").Distinct().Count();
    await Assert.That(distinct).IsLessThanOrEqualTo(DeadLetterMetrics.MAX_DISTINCT_STACK_TAGS)
      .Because("stack_id cardinality is naturally bounded by dedup in any one storm, but "
             + "unbounded across a process lifetime — the cap keeps the meter honest forever");
    await Assert.That(stacks.Contains("overflow")).IsTrue()
      .Because("overflow arrivals still count, in one bucket, rather than being dropped");
  }

  [Test]
  public async Task CohortVerdicts_CountByCohortAndVerdictAsync() {
    var metrics = new DeadLetterMetrics(new WhizbangMetrics());
    var recorded = new List<(string? Cohort, string? Verdict)>();
    using var listener = new MeterListener();
    listener.InstrumentPublished = (instrument, l) => {
      if (instrument.Name == "whizbang.dead_letters.cohort_verdicts") {
        l.EnableMeasurementEvents(instrument);
      }
    };
    listener.SetMeasurementEventCallback<long>((_, _, tags, _) => {
      string? c = null;
      string? verdict = null;
      foreach (var tag in tags) {
        if (tag.Key == "cohort") { c = tag.Value?.ToString(); }
        if (tag.Key == "verdict") { verdict = tag.Value?.ToString(); }
      }
      lock (recorded) { recorded.Add((c, verdict)); }
    });
    listener.Start();

    var cohort = "fp-" + Guid.NewGuid().ToString("N")[..12];
    metrics.RecordCohortVerdict(cohort, Whizbang.Core.Messaging.CanaryVerdictKind.Mixed);

    (string?, string?)[] snap;
    lock (recorded) { snap = [.. recorded]; }
    await Assert.That(snap.Length).IsEqualTo(1);
    await Assert.That(snap[0].Item1).IsEqualTo(cohort);
    await Assert.That(snap[0].Item2).IsEqualTo("Mixed")
      .Because("the campaign lifecycle is a graph: pass/fail/mixed per cohort is how an "
             + "operator sees a canary program working without reading a single log line");
  }

  [Test]
  public async Task StackHistoryPruned_CountsTheCleanupFacetAsync() {
    var metrics = new DeadLetterMetrics(new WhizbangMetrics());
    long recorded = 0;
    using var listener = new MeterListener();
    listener.InstrumentPublished = (instrument, l) => {
      if (instrument.Name == "whizbang.dead_letters.stack_history_pruned") {
        l.EnableMeasurementEvents(instrument);
      }
    };
    listener.SetMeasurementEventCallback<long>((_, value, _, _) => { Interlocked.Add(ref recorded, value); });
    listener.Start();

    metrics.RecordStackHistoryPruned(42);
    metrics.RecordStackHistoryPruned(0); // zero is not recorded

    await Assert.That(Interlocked.Read(ref recorded)).IsEqualTo(42L)
      .Because("the rolling-history cleanup is a maintenance facet an operator watches on a "
             + "dashboard; a zero pass is not noise worth a data point");
  }

}
