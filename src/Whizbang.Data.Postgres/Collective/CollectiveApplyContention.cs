// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Npgsql;
using Whizbang.Core.Perspectives;

namespace Whizbang.Data.Postgres.Collective;

/// <summary>
/// Reading a contended apply lock: whether PostgreSQL refused the wait, and which table the batch was
/// waiting on.
/// </summary>
/// <remarks>
/// <para>
/// Both drivers bound their lock wait and both have to say the same thing about the result, so the two
/// questions live here rather than once per driver. A batch that did not get its lock is not a failed
/// apply, and the distinction is only useful if every driver draws it the same way.
/// </para>
/// <para>
/// The table comes out of the batch's own SELECT because that statement is what the applier holds at the
/// point of refusal -- there is no separate table name to pass down, and an operator's first question is
/// where the contention is.
/// </para>
/// </remarks>
/// <docs>fundamentals/messaging/collective-events</docs>
public static class CollectiveApplyContention {
  /// <summary>
  /// Whether <paramref name="exception"/> is PostgreSQL refusing a lock wait that ran past
  /// <c>lock_timeout</c> (SQLSTATE 55P03), at whatever depth the provider wrapped it.
  /// </summary>
  /// <remarks>
  /// Unwrapped because the drivers differ: one surfaces the provider's exception directly, the other
  /// hands back whatever Entity Framework wrapped it in.
  /// </remarks>
  /// <param name="exception">The exception the lock statement raised.</param>
  /// <returns>True when the wait was refused, false for every other failure.</returns>
  public static bool IsLockTimeout(Exception? exception) {
    for (var inner = exception; inner is not null; inner = inner.InnerException) {
      if (inner is PostgresException { SqlState: "55P03" }) {
        return true;
      }
    }
    return false;
  }

  /// <summary>
  /// Runs one batch, waiting again for its apply lock after each bounded wait that ends without it (#964): up to
  /// <see cref="CollectiveApplyOptions.LockWaitRenewals"/> more times, reporting progress through
  /// <paramref name="onProgress"/> before each so the caller renews its work lease while it waits. When every wait is
  /// used up the refusal is rethrown with the total time waited.
  /// </summary>
  /// <typeparam name="T">What the batch returns.</typeparam>
  /// <param name="batch">One attempt at the batch, in a transaction of its own.</param>
  /// <param name="options">The apply options; their renewal count bounds the waiting.</param>
  /// <param name="onProgress">The caller's progress callback, the one it renews its lease from; may be null.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>What the batch returned once it held the lock.</returns>
  /// <docs>fundamentals/messaging/collective-events</docs>
  /// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/Collective/DapperCollectiveApplierIntegrationTests.cs:LockHeldThroughEveryRenewal_GivesUpAsBusyAsync</tests>
  public static async Task<T> WaitingForTheLockAsync<T>(
      Func<Task<T>> batch, CollectiveApplyOptions options,
      Func<CancellationToken, ValueTask>? onProgress, CancellationToken cancellationToken) {
    ArgumentNullException.ThrowIfNull(batch);
    ArgumentNullException.ThrowIfNull(options);
    var waits = 0;
    while (true) {
      try {
        return await batch().ConfigureAwait(false);
      } catch (CollectiveApplyLockBusyException) when (waits < options.LockWaitRenewals) {
        waits++;
        if (onProgress is not null) {
          await onProgress(cancellationToken).ConfigureAwait(false);
        }
      } catch (CollectiveApplyLockBusyException busy) when (waits > 0) {
        throw new CollectiveApplyLockBusyException(busy.Table, busy.WaitedSeconds * (waits + 1), busy.InnerException);
      }
    }
  }

  /// <summary>The table a batch's SELECT reads, for a message that names where the contention is.</summary>
  /// <param name="selectSql">The batch's keyset SELECT.</param>
  /// <returns>
  /// The table as the statement spells it, or <c>(unknown)</c> when the statement has no <c>FROM</c> --
  /// a message with a placeholder in it beats losing the refusal to a parsing failure.
  /// </returns>
  public static string TableOf(string? selectSql) {
    const string from = " FROM ";
    var start = selectSql?.IndexOf(from, StringComparison.Ordinal) ?? -1;
    if (start < 0) {
      return "(unknown)";
    }
    var rest = selectSql![(start + from.Length)..].TrimStart();
    var end = rest.IndexOfAny([' ', '\r', '\n']);
    return end < 0 ? rest : rest[..end];
  }
}
