using System;

namespace Whizbang.Core.Execution;

/// <summary>
/// What a worker consults to learn how wide it may run right now.
/// </summary>
/// <remarks>
/// <para>
/// Several workers cap their own parallelism with a constant — the outbox drain at 16 concurrent
/// streams, inbox dispatch at 8, the perspective drain at 4, the dead-letter drain at 500 per
/// tick. A constant cannot serve a bimodal workload: the same number that is generous for a
/// service handling three streams becomes a hard queue when a bulk operation produces three
/// hundred, and the host sits far below its resource limits while work waits for a slot.
/// </para>
/// <para>
/// This seam exists so that width becomes a decision rather than a literal. The default
/// implementation returns a constant, so adopting it changes no behavior; an adaptive
/// implementation can then be swapped in deliberately and measured.
/// </para>
/// <para>
/// Deliberately NOT a general-purpose limit abstraction. Govern a width whose only cost is
/// resource contention you can observe and back off from. Leave fixed any bound that exists for
/// correctness or blast-radius containment — the integrity checkpoint's per-audit repair caps
/// bound how much automated self-healing a bad audit can trigger, and a cache's entry ceiling
/// bounds memory. Growing those under load removes the property they were added for.
/// </para>
/// </remarks>
/// <docs>operations/workers/concurrency-governor</docs>
/// <tests>tests/Whizbang.Core.Tests/Execution/ConcurrencyGovernorTests.cs</tests>
public interface IConcurrencyGovernor {
  /// <summary>How many units of work may run concurrently right now.</summary>
  /// <remarks>Always between <see cref="Floor"/> and <see cref="Ceiling"/> inclusive.</remarks>
  int CurrentWidth { get; }

  /// <summary>The narrowest this governor may go — a quiet service still drains promptly.</summary>
  int Floor { get; }

  /// <summary>
  /// The widest this governor may go. Derived from the resource being protected rather than
  /// guessed: each concurrent unit here costs a database connection, and the deployed topology
  /// allots a bounded number per host. Growing past that relocates the bottleneck into connection
  /// exhaustion, which degrades unrelated work sharing the pool — strictly worse than draining
  /// slowly.
  /// </summary>
  int Ceiling { get; }

  /// <summary>
  /// Reports what the last cycle looked like so the governor can adjust.
  /// </summary>
  /// <param name="signal">Observed pressure from the most recent cycle.</param>
  void Observe(GovernorSignal signal);
}

/// <summary>
/// One cycle's worth of evidence: how much work was waiting, and whether the resource pushed back.
/// </summary>
/// <param name="QueuedItems">Units waiting for a slot when the cycle began. Zero means the
/// governor is already wide enough and should not grow.</param>
/// <param name="CompletedItems">Units the cycle actually finished. Distinct from
/// <paramref name="QueuedItems"/>, which is DEPTH — how much was waiting — and says nothing about
/// how much got done. Throughput is only computable from work completed over elapsed time, so a
/// governor that tunes on throughput needs this and cannot infer it from depth. Defaults to zero
/// for callers that do not measure it; a governor requiring it should treat zero as "unknown"
/// rather than as "nothing was accomplished".</param>
/// <param name="Contended">True when the governed resource pushed back — connection acquisition
/// waited, the pool saturated, the broker throttled. This is the decay signal, and it must
/// dominate: growing while contended is how a governor turns a slow path into an outage.</param>
/// <param name="Elapsed">Wall time the cycle took, for rate-based tuning.</param>
/// <docs>operations/workers/concurrency-governor</docs>
public readonly record struct GovernorSignal(
  int QueuedItems,
  bool Contended,
  TimeSpan Elapsed,
  int CompletedItems = 0);
