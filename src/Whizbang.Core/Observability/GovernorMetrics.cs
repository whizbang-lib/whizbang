using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Whizbang.Core.Execution;

namespace Whizbang.Core.Observability;

/// <summary>
/// Makes concurrency governors observable. Meter name: <c>Whizbang.Governor</c>.
/// </summary>
/// <remarks>
/// <para>
/// A governor that changes concurrency silently cannot be operated. If it converges to its floor
/// on a healthy system, or pins to its ceiling and exhausts a connection pool, the only visible
/// evidence is the damage — a slow drain, contention in unrelated work — with nothing to point at
/// the governor as the cause. That is an unacceptable property for a component whose entire job is
/// to change a number nobody set.
/// </para>
/// <para>
/// Two instruments, chosen for what each can actually answer:
/// </para>
/// <list type="bullet">
///   <item><description><b>width</b> — an observable gauge, because width is a LEVEL. The question
///   a dashboard must answer is "how wide is it right now", which a counter cannot express.</description></item>
///   <item><description><b>adjustments</b> — a counter tagged by direction, because the value is in
///   the rate and the asymmetry. A governor shrinking far more often than it grows is oscillating,
///   and an undirected count hides exactly that.</description></item>
/// </list>
/// <para>
/// Both are tagged with the governor's name so one process running several — an outbox drain and a
/// perspective worker, say — produces separable series rather than an unreadable blend.
/// </para>
/// </remarks>
/// <docs>operations/workers/concurrency-governor</docs>
/// <tests>tests/Whizbang.Core.Tests/Observability/GovernorMetricsTests.cs</tests>
public sealed class GovernorMetrics {
  /// <summary>Meter name used by <see cref="GovernorMetrics"/>.</summary>
#pragma warning disable CA1707
  public const string METER_NAME = "Whizbang.Governor";
#pragma warning restore CA1707

  private readonly ConcurrentDictionary<string, IConcurrencyGovernor> _tracked = new(StringComparer.Ordinal);
  private readonly Counter<long> _adjustments;
  private readonly Counter<long> _completed;
  private readonly Histogram<double> _throughput;
  private readonly Histogram<long> _queueDepth;
  private readonly Counter<long> _contended;

  /// <summary>Creates the governor metrics over the supplied meter factory.</summary>
  /// <param name="whizbangMetrics">Meter factory holder.</param>
  public GovernorMetrics(WhizbangMetrics whizbangMetrics) {
    ArgumentNullException.ThrowIfNull(whizbangMetrics);
    var meter = whizbangMetrics.MeterFactory?.Create(METER_NAME) ?? new Meter(METER_NAME);

    _adjustments = meter.CreateCounter<long>(
      name: "whizbang.governor.adjustments",
      description: "Width changes a concurrency governor made, tagged by direction (grew/shrank).");

    // The EVIDENCE behind each decision, not just the decision. A width change on its own is not
    // diagnosable: 30 -> 22 could be correct backoff or a controller misreading an idle queue.
    // Exporting the inputs makes the two distinguishable after the fact.
    _completed = meter.CreateCounter<long>(
      name: "whizbang.governor.completed_items",
      description: "Units of work a governed cycle completed.");

    _throughput = meter.CreateHistogram<double>(
      name: "whizbang.governor.throughput",
      unit: "{item}/s",
      description: "Completed items per second observed by a governed cycle — the signal a "
                 + "throughput-tuning governor acts on.");

    _queueDepth = meter.CreateHistogram<long>(
      name: "whizbang.governor.queued_items",
      unit: "{item}",
      description: "Work waiting when a governed cycle began. Distinguishes real backoff from a "
                 + "governor reacting to an empty queue.");

    _contended = meter.CreateCounter<long>(
      name: "whizbang.governor.contention_reports",
      description: "Cycles where the caller explicitly reported resource pushback.");

    // Observable: polled at export time, so it reports the width in effect when the collector
    // asked — not the width at some arbitrary earlier moment we happened to record.
    _ = meter.CreateObservableGauge(
      name: "whizbang.governor.width",
      observeValues: _observeWidths,
      description: "Concurrency width a governor currently prescribes.");

    _ = meter.CreateObservableGauge(
      name: "whizbang.governor.ceiling",
      observeValues: _observeCeilings,
      description: "Upper bound a governor may grow to, derived from the governed resource's budget.");
  }

  /// <summary>
  /// Registers a governor for export under <paramref name="name"/>.
  /// </summary>
  /// <param name="name">Stable series name, e.g. <c>outbox-drain</c>.</param>
  /// <param name="governor">The governor to observe.</param>
  public void Track(string name, IConcurrencyGovernor governor) {
    ArgumentException.ThrowIfNullOrWhiteSpace(name);
    ArgumentNullException.ThrowIfNull(governor);
    _tracked[name] = governor;
  }

  /// <summary>
  /// Records that a governor changed width.
  /// </summary>
  /// <remarks>
  /// A no-change call is ignored rather than counted as an adjustment: an "adjustment" that moved
  /// nothing would inflate the rate this metric exists to expose, making a stable governor look
  /// like a busy one.
  /// </remarks>
  /// <param name="name">The governor's series name.</param>
  /// <param name="from">Width before.</param>
  /// <param name="to">Width after.</param>
  public void RecordAdjustment(string name, int from, int to) {
    if (from == to) {
      return;
    }
    _adjustments.Add(1,
      new KeyValuePair<string, object?>("governor", name),
      new KeyValuePair<string, object?>("direction", to > from ? "grew" : "shrank"));
  }

  /// <summary>
  /// Records one governed cycle: what it saw and what it accomplished.
  /// </summary>
  /// <remarks>
  /// Throughput is only emitted when the cycle actually reported completions and elapsed time.
  /// A caller that does not measure completions would otherwise contribute a stream of zeros,
  /// dragging every percentile down and making a healthy system look degraded.
  /// </remarks>
  /// <param name="name">The governor's series name.</param>
  /// <param name="signal">The observed cycle.</param>
  public void RecordObservation(string name, Whizbang.Core.Execution.GovernorSignal signal) {
    var tag = new KeyValuePair<string, object?>("governor", name);

    _queueDepth.Record(signal.QueuedItems, tag);

    if (signal.Contended) {
      _contended.Add(1, tag);
    }

    if (signal.CompletedItems > 0) {
      _completed.Add(signal.CompletedItems, tag);
      var seconds = signal.Elapsed.TotalSeconds;
      if (seconds > 0) {
        _throughput.Record(signal.CompletedItems / seconds, tag);
      }
    }
  }

  private IEnumerable<Measurement<long>> _observeWidths() {
    foreach (var (name, governor) in _tracked) {
      yield return new Measurement<long>(governor.CurrentWidth,
        new KeyValuePair<string, object?>("governor", name));
    }
  }

  private IEnumerable<Measurement<long>> _observeCeilings() {
    foreach (var (name, governor) in _tracked) {
      yield return new Measurement<long>(governor.Ceiling,
        new KeyValuePair<string, object?>("governor", name));
    }
  }
}
