using System;

namespace Whizbang.Core.Execution;

/// <summary>
/// Finds its own width by watching whether widening actually improves throughput.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="AdaptiveConcurrencyGovernor"/> requires a caller to report contention. Nothing in the
/// drain path produces that signal today, and a governor that never observes pushback grows to its
/// ceiling and stays there — strictly worse than the constant it replaces. That gap is why the
/// adaptive strategy shipped disabled.
/// </para>
/// <para>
/// This governor closes it without new instrumentation. Every cycle already reports how much work
/// was waiting and how long the cycle took, and that is throughput. If throughput stops improving
/// as width grows, the extra width is buying nothing and is likely costing something shared —
/// which is the contention signal, inferred rather than measured.
/// </para>
/// <para>
/// The hard part is not the growth rule, it is refusing to react to things that merely LOOK like
/// contention. Throughput also falls when the queue empties, and when individual items get more
/// expensive. Neither means "too wide", and a controller that shrinks on either converges to its
/// floor on a healthy system and arrives at the next burst already narrowed.
/// </para>
/// </remarks>
/// <docs>operations/workers/concurrency-governor</docs>
/// <tests>tests/Whizbang.Core.Tests/Execution/ThroughputGovernorTests.cs</tests>
public sealed class ThroughputGovernor : IConcurrencyGovernor {
  /// <summary>Creates a self-tuning governor.</summary>
  /// <param name="floor">Narrowest width; clamped to at least 1.</param>
  /// <param name="ceiling">Widest width, derived from the governed resource's budget.</param>
  /// <param name="start">
  /// Width to begin at, clamped into the band. Defaults to the floor — appropriate when nothing is
  /// known about the workload. Callers REPLACING an existing constant should pass that constant, so
  /// the first cycle behaves exactly like the width being replaced and any change has to be earned
  /// from a measurement. Starting an upgrade at the floor would narrow a running deployment on
  /// restart: a throughput regression arriving disguised as an improvement.
  /// </param>
  public ThroughputGovernor(int floor, int ceiling, int? start = null) {
    Floor = Math.Max(1, floor);
    Ceiling = Math.Max(Floor, ceiling);
    CurrentWidth = Math.Clamp(start ?? Floor, Floor, Ceiling);
  }

  /// <inheritdoc />
  public int CurrentWidth { get; private set; }

  /// <inheritdoc />
  public int Floor { get; }

  /// <inheritdoc />
  public int Ceiling { get; }

  // Throughput of the best cycle seen so far, in items per second. The comparison baseline:
  // "is the current width still doing at least as well as our best?"
  private double _bestRate;

  // Consecutive cycles measurably worse than the best. One bad cycle is noise — a GC pause, a
  // slow query, an unlucky batch. Only a run of them is evidence.
  private int _worseStreak;

  // A cycle must beat the best by this margin to count as improvement, and fall below it by this
  // margin to count as decline. Without a dead band the controller oscillates on measurement noise.
  private const double IMPROVE_MARGIN = 1.05;
  private const double DECLINE_MARGIN = 0.80;

  // How many consecutive declining cycles before giving width back. Small enough to react, large
  // enough that a single hiccup cannot shrink a healthy system.
  private const int DECLINE_PATIENCE = 3;

  /// <inheritdoc />
  public void Observe(GovernorSignal signal) {
    // An explicitly reported contention beats anything inferred. A caller with a real pressure
    // source has strictly better evidence than throughput arithmetic, and ignoring it in favor of
    // our own inference would be the controller preferring its guess to a measurement.
    if (signal.Contended) {
      CurrentWidth = Math.Max(Floor, CurrentWidth / 2);
      _bestRate = 0;          // the old best was measured at a width we no longer run
      _worseStreak = 0;
      return;
    }

    // Nothing queued means nothing to learn. Throughput collapses when the work runs out, and
    // reading that as pushback would decay a healthy governor to its floor during every quiet
    // spell — arriving at the next burst already narrowed, which is when width matters most.
    if (signal.QueuedItems <= 0) {
      return;
    }

    var seconds = signal.Elapsed.TotalSeconds;
    if (seconds <= 0) {
      return;                 // no elapsed time means no measurable rate
    }

    // Completed work per SECOND — deliberately not queue depth, and not items per cycle.
    //
    // Depth is how much was WAITING; it says nothing about how much got done, so depth/time is
    // not throughput. And per-cycle counts confuse "each item costs more" with "we are getting
    // less done": heavier work lengthens the cycle without slowing the system.
    //
    // A caller that does not report completions cannot be tuned this way. Treat that as unknown
    // and leave the width alone rather than inventing a rate from depth.
    if (signal.CompletedItems <= 0) {
      return;
    }
    var rate = signal.CompletedItems / seconds;

    if (_bestRate <= 0) {
      _bestRate = rate;       // first measurement establishes the baseline
      return;
    }

    if (rate >= _bestRate * IMPROVE_MARGIN) {
      // Widening is still paying. Additive increase — cautious, because overshoot is the
      // expensive direction.
      _bestRate = rate;
      _worseStreak = 0;
      CurrentWidth = Math.Min(Ceiling, CurrentWidth + 1);
      return;
    }

    if (rate <= _bestRate * DECLINE_MARGIN) {
      // Measurably worse at the same width, with work still queued. That is what contention looks
      // like from the inside: there is no other reason for the same width to accomplish less.
      if (++_worseStreak >= DECLINE_PATIENCE) {
        CurrentWidth = Math.Max(Floor, CurrentWidth * 3 / 4);
        _worseStreak = 0;
        // Re-baseline to the degraded rate so recovery is measured from where we actually are.
        // Holding the old best would make every later cycle look like a decline and ratchet the
        // width down forever.
        _bestRate = rate;
      }
      return;
    }

    // Neither better nor worse: the plateau. Stop growing — the extra width bought nothing, and
    // continuing would consume a shared resource for no gain. Holding the width IS the action here;
    // there is nothing to accumulate, so the decline streak simply resets.
    _worseStreak = 0;
  }
}
