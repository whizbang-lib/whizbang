// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Testing.Async;

/// <summary>
/// "After N passes of a loop, at most K waits are outstanding": the assertion every long-running loop owes, for each
/// thing it waits on.
/// </summary>
/// <remarks>
/// <para>
/// A loop that leaves a wait behind each pass grows without bound on an idle instance, and nothing fails until memory
/// does. The budget drives the loop one pass at a time through <c>pass</c> (which returns once that pass is parked,
/// from a signal such as <see cref="CountingTimeProvider.Created(int)"/>, never a sleep), samples every counter after
/// each pass, and throws <see cref="LoopWaitBudgetExceededException"/> naming the first counter and pass over its
/// limit. It returns the highest count it saw for each counter, so the test can also assert the positive control: the
/// counter saw the wait the loop legitimately holds.
/// </para>
/// <para>
/// Counters are plain functions, so any count works: <see cref="CountingChannelReader{T}.OutstandingWaits"/>,
/// <see cref="CountingTimeProvider.LiveTimers"/>, a signal's own waiter count.
/// </para>
/// </remarks>
/// <docs>contributors/testing/loop-waits</docs>
/// <tests>tests/Whizbang.Testing.Tests/Async/LoopWaitProbeTests.cs</tests>
public sealed class LoopWaitBudget {
  private readonly List<(string Name, Func<int> Count, int AtMost)> _counters = [];

  /// <summary>Adds a counter that must never exceed <paramref name="atMost"/> after any pass.</summary>
  /// <param name="name">What is counted, for the failure message.</param>
  /// <param name="count">Reads the count.</param>
  /// <param name="atMost">The most it may be after a pass.</param>
  /// <returns>This budget.</returns>
  public LoopWaitBudget AtMost(string name, Func<int> count, int atMost) {
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(count);
    ArgumentOutOfRangeException.ThrowIfNegative(atMost);
    _counters.Add((name, count, atMost));
    return this;
  }

  /// <summary>
  /// Drives <paramref name="passes"/> passes and checks every counter after each one.
  /// </summary>
  /// <param name="passes">How many passes.</param>
  /// <param name="pass">Drives pass <c>n</c> (from one) and returns once the loop is parked after it.</param>
  /// <returns>The highest count each counter reached, by name.</returns>
  /// <exception cref="LoopWaitBudgetExceededException">A counter went over its limit.</exception>
  public async Task<IReadOnlyDictionary<string, int>> RunAsync(int passes, Func<int, Task> pass) {
    ArgumentOutOfRangeException.ThrowIfNegativeOrZero(passes);
    ArgumentNullException.ThrowIfNull(pass);
    var highest = _counters.ToDictionary(c => c.Name, _ => 0, StringComparer.Ordinal);
    for (var n = 1; n <= passes; n++) {
      await pass(n).ConfigureAwait(false);
      foreach (var (name, count, atMost) in _counters) {
        var now = count();
        highest[name] = Math.Max(highest[name], now);
        if (now > atMost) {
          throw new LoopWaitBudgetExceededException(name, n, now, atMost);
        }
      }
    }
    return highest;
  }
}

/// <summary>A loop left more waits outstanding than its budget allows.</summary>
/// <docs>contributors/testing/loop-waits</docs>
public sealed class LoopWaitBudgetExceededException : Exception {
  /// <summary>Creates the exception.</summary>
  public LoopWaitBudgetExceededException() { }

  /// <summary>Creates the exception with a message.</summary>
  public LoopWaitBudgetExceededException(string message) : base(message) { }

  /// <summary>Creates the exception with a message and an inner exception.</summary>
  public LoopWaitBudgetExceededException(string message, Exception innerException) : base(message, innerException) { }

  internal LoopWaitBudgetExceededException(string counter, int pass, int count, int atMost)
    : base($"{counter}: {count} outstanding after pass {pass}, more than the {atMost} allowed. A wait the loop left "
      + "behind is never released on an idle instance; reuse the pending wait or cancel the loser.") {
    Counter = counter;
    Pass = pass;
    Count = count;
    AllowedAtMost = atMost;
  }

  /// <summary>The counter over its limit.</summary>
  public string? Counter { get; }

  /// <summary>The pass after which it was over.</summary>
  public int Pass { get; }

  /// <summary>Its count then.</summary>
  public int Count { get; }

  /// <summary>Its limit.</summary>
  public int AllowedAtMost { get; }
}
