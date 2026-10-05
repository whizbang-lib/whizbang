// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Perspectives;

/// <summary>
/// A collective batch waited for its per-scope apply lock and did not get it inside the bounded wait.
/// </summary>
/// <remarks>
/// <para>
/// Distinct from a failed apply, and the distinction is the point. Nothing is wrong with the event or
/// the perspective: another batch holds the lock for the same table and scope, and this one arrived
/// while it did. A caller that treats this as a failure counts an attempt, moves the work toward
/// dead-lettering and loses the lease, none of which is warranted by a busy lock.
/// </para>
/// <para>
/// Before the wait was bounded there was no way to tell the two apart: the wait sat for the whole
/// statement timeout and surfaced as a timeout, indistinguishable from an apply that hung on its own
/// account.
/// </para>
/// </remarks>
/// <docs>fundamentals/messaging/collective-events</docs>
public sealed class CollectiveApplyLockBusyException : Exception {
  /// <summary>The perspective table the batch was applying to.</summary>
  public string Table { get; }

  /// <summary>How long the batch waited, in seconds.</summary>
  public int WaitedSeconds { get; }

  /// <summary>A busy apply lock, named as such.</summary>
  /// <param name="table">The perspective table.</param>
  /// <param name="waitedSeconds">The bounded wait that elapsed.</param>
  /// <param name="innerException">The refusal PostgreSQL raised.</param>
  public CollectiveApplyLockBusyException(string table, int waitedSeconds, Exception? innerException = null)
    : base($"A collective batch for '{table}' waited {waitedSeconds}s for its apply lock and did not get it. "
         + "Another batch holds the lock for the same table and scope; nothing was applied and nothing failed.",
      innerException) {
    Table = table;
    WaitedSeconds = waitedSeconds;
  }

  /// <summary>A busy apply lock.</summary>
  public CollectiveApplyLockBusyException() : base("A collective batch did not get its apply lock.") {
    Table = string.Empty;
  }

  /// <summary>A busy apply lock.</summary>
  /// <param name="message">The message.</param>
  public CollectiveApplyLockBusyException(string message) : base(message) => Table = string.Empty;

  /// <summary>A busy apply lock.</summary>
  /// <param name="message">The message.</param>
  /// <param name="innerException">The cause.</param>
  public CollectiveApplyLockBusyException(string message, Exception innerException)
    : base(message, innerException) => Table = string.Empty;
}
