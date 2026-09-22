using System;

namespace Whizbang.Core.Execution;

/// <summary>
/// The constant, as a strategy. Adopting this changes nothing — which is the point.
/// </summary>
/// <remarks>
/// Every worker that hard-codes a width can move to the seam without a behavior change, so the
/// refactor lands and can be reviewed on its own. Adaptivity then arrives as a separate,
/// swappable implementation instead of riding in on a change that also moves call sites.
/// </remarks>
/// <docs>operations/workers/concurrency-governor</docs>
/// <tests>tests/Whizbang.Core.Tests/Execution/ConcurrencyGovernorTests.cs</tests>
public sealed class FixedWidthGovernor : IConcurrencyGovernor {
  /// <summary>Creates a governor pinned to one width.</summary>
  /// <param name="width">The constant width; clamped to at least 1.</param>
  public FixedWidthGovernor(int width) {
    var w = Math.Max(1, width);
    CurrentWidth = w;
    Floor = w;
    Ceiling = w;
  }

  /// <inheritdoc />
  public int CurrentWidth { get; }

  /// <inheritdoc />
  public int Floor { get; }

  /// <inheritdoc />
  public int Ceiling { get; }

  /// <inheritdoc />
  /// <remarks>Intentionally inert: a fixed governor has nothing to learn.</remarks>
  public void Observe(GovernorSignal signal) {
    // No-op by design.
  }
}

/// <summary>
/// Grows while work waits and the resource is quiet; backs off hard the moment it pushes back.
/// </summary>
/// <remarks>
/// <para>
/// Additive increase, multiplicative decrease. Growth is cautious because over-widening is the
/// expensive mistake — it converts a slow drain into contention that harms unrelated work sharing
/// the same pool. Decay is aggressive for the same reason.
/// </para>
/// <para>
/// Contention dominates: a cycle that is both backlogged AND contended must shrink. Treating a
/// backlog as permission to grow through pushback is precisely how this class of controller
/// causes the outage it was meant to prevent.
/// </para>
/// </remarks>
/// <docs>operations/workers/concurrency-governor</docs>
/// <tests>tests/Whizbang.Core.Tests/Execution/ConcurrencyGovernorTests.cs</tests>
public sealed class AdaptiveConcurrencyGovernor : IConcurrencyGovernor {
  /// <summary>Creates an adaptive governor that starts at its floor.</summary>
  /// <param name="floor">Narrowest width; clamped to at least 1.</param>
  /// <param name="ceiling">Widest width, derived from the governed resource's budget.</param>
  public AdaptiveConcurrencyGovernor(int floor, int ceiling) {
    Floor = Math.Max(1, floor);
    Ceiling = Math.Max(Floor, ceiling);
    CurrentWidth = Floor;
  }

  /// <inheritdoc />
  public int CurrentWidth { get; private set; }

  /// <inheritdoc />
  public int Floor { get; }

  /// <inheritdoc />
  public int Ceiling { get; }

  /// <inheritdoc />
  public void Observe(GovernorSignal signal) {
    // Contention is checked FIRST and returns, so a backlog can never authorize growth through
    // pushback. That ordering is the whole safety property: the moment those two conditions are
    // weighed against each other, a large enough queue wins and the governor grows into the
    // resource that is already refusing it.
    if (signal.Contended) {
      // Multiplicative decrease — give back much more than one cycle of growth, because the
      // cost of staying too wide falls on everything else sharing the resource.
      CurrentWidth = Math.Max(Floor, CurrentWidth / 2);
      return;
    }

    // No queue means the current width already covers demand. Growing here would hold the
    // resource against work that does not exist.
    if (signal.QueuedItems <= 0) {
      return;
    }

    // Additive increase — one slot per quiet, backlogged cycle. Deliberately slower than the
    // decay: arriving at the right width late costs latency, overshooting costs a shared pool.
    CurrentWidth = Math.Min(Ceiling, CurrentWidth + 1);
  }
}
