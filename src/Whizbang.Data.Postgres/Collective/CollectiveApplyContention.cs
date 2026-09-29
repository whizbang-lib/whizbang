using Npgsql;

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
