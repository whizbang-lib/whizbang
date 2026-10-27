// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Notifications;

/// <summary>
/// The size of the commit-order stamper's next call, carried from one call to the next: what
/// <see cref="CommitOrderStampBatch"/> decides from a single call, plus what only a run of calls shows.
/// </summary>
/// <remarks>
/// Only the stamper's own loop reads or writes it, and only one instance holds the stamper role, so it needs no
/// synchronization of its own.
/// </remarks>
/// <docs>fundamentals/work-coordinator/commit-sequence</docs>
/// <tests>tests/Whizbang.Core.Tests/Notifications/CommitOrderStampSizerTests.cs</tests>
public sealed class CommitOrderStampSizer {
  private readonly int _configured;
  private readonly int _ceiling;
  private int? _timeoutCap;

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
    Size = CommitOrderStampBatch.Next(asked, stamped, _configured, _ceiling, _timeoutCap, took, budget);
    if (stamped < asked) {
      _timeoutCap = null;
    } else if (took > budget) {
      _timeoutCap = Size;
    }
  }

  /// <summary>
  /// Sizes the next call after one timed out and stamped nothing.
  /// </summary>
  public void AfterTimeout() {
    Size = CommitOrderStampBatch.AfterTimeout(Size);
    _timeoutCap = Size;
  }
}
