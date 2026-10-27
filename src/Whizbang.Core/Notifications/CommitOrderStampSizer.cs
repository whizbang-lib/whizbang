// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Notifications;

/// <summary>
/// The size of the commit-order stamper's next call, carried from one call to the next: what
/// <see cref="CommitOrderStampBatch"/> decides from a single call, plus what only a run of calls shows.
/// </summary>
/// <remarks>
/// <para>
/// A call's cost has a part that follows its size and a part that does not (a round trip, the visibility horizon, the
/// role fence, and anything the database is busy with). Halving a call past the budget answers the first part only. When
/// a halved call cost about what the larger one did, the rest of the cost is the fixed part, and a smaller batch would
/// pay it more often for the same rows: the sizer stops shrinking there, at a floor, instead of falling to one row
/// (#1786). Precisely: with two full calls past the budget, the larger first, a cost of the form
/// <c>fixed + perRow * size</c> through both is fixed-dominated at the smaller size when the per-row part there is at
/// most half the fixed part, which is <c>3 * smaller * (largerTook - took) &lt;= took * (larger - smaller)</c>. For a
/// halving that is a call that cost at least three quarters of the one before it.
/// </para>
/// <para>
/// Growth needs no cap of its own: only a full call well inside the budget grows, and that call already shows the
/// doubled one should fit. A size a slow call or a timeout backed off to therefore holds only while calls stay slow, and
/// once they are cheap again (the I/O that slowed them is over) the batch grows past it. The floor ends the same way:
/// with a cheap call, a timeout, or a drained backlog, since it describes the conditions that measured it.
/// </para>
/// <para>
/// Only the stamper's own loop reads or writes it, and only one instance holds the stamper role, so it needs no
/// synchronization of its own.
/// </para>
/// </remarks>
/// <docs>fundamentals/work-coordinator/commit-sequence#batch-size</docs>
/// <tests>tests/Whizbang.Core.Tests/Notifications/CommitOrderStampSizerTests.cs</tests>
public sealed class CommitOrderStampSizer {
  private readonly int _configured;
  private readonly int _ceiling;
  // Whether the current size is where a halving stopped making calls cheaper. Set, the size holds through slow calls.
  private bool _atFloor;
  // The last full call that ran past the budget, which the next one is compared with.
  private (int Asked, TimeSpan Took)? _lastSlow;

  /// <summary>
  /// Starts at the steady-state size.
  /// </summary>
  /// <param name="configured">The steady-state size, which a drained set returns to.</param>
  /// <param name="ceiling">The largest size to grow to.</param>
  public CommitOrderStampSizer(int configured, int ceiling) {
    _configured = configured;
    _ceiling = ceiling;
    Size = configured;
  }

  /// <summary>The size the next call asks for.</summary>
  public int Size { get; private set; }

  /// <summary>
  /// Sizes the next call from what this one returned and how long it took.
  /// </summary>
  /// <param name="stamped">How many rows the call stamped.</param>
  /// <param name="took">How long the call took.</param>
  /// <param name="budget">The most a call should take.</param>
  public void AfterCall(int stamped, TimeSpan took, TimeSpan budget) {
    var asked = Size;
    var lastSlow = _lastSlow;
    _lastSlow = null;
    if (stamped < asked || took * CommitOrderStampBatch.GROWTH <= budget) {
      // Drained, or cheap: whatever measured the floor is over.
      _atFloor = false;
    } else if (took > budget) {
      _lastSlow = (asked, took);
      if (_atFloor) {
        return;
      }
      // The last slow call is the one that halved to this size: every slow call either halves or reaches the floor.
      if (lastSlow is { } larger && _isFixedCost(larger.Asked, larger.Took, asked, took)) {
        _atFloor = true;
        return;
      }
    }
    Size = CommitOrderStampBatch.Next(asked, stamped, _configured, _ceiling, timeoutCap: null, took, budget);
  }

  /// <summary>
  /// Sizes the next call after one timed out and stamped nothing: half (#1217). A timeout says nothing about how the
  /// cost divides, so the floor and the last slow call are forgotten.
  /// </summary>
  public void AfterTimeout() {
    _atFloor = false;
    _lastSlow = null;
    Size = CommitOrderStampBatch.AfterTimeout(Size);
  }

  private static bool _isFixedCost(int larger, TimeSpan largerTook, int smaller, TimeSpan took) =>
    3.0 * smaller * (largerTook - took).Ticks <= (double)took.Ticks * (larger - smaller);
}
