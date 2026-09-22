using System;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;

namespace Whizbang.Data.Postgres;

/// <summary>
/// Asks whether some other session currently holds a given advisory lock.
/// </summary>
/// <remarks>
/// <para>
/// A failed <c>pg_try_advisory_lock</c> says only "not yours". It does not say whether the holder is
/// still alive, and those two situations call for opposite behavior: an instance that lost the
/// schema lock to a working migrator should wait for the result, while one that lost it to a pod
/// that has since died must take over or the schema never advances. The lock's own presence in
/// <c>pg_locks</c> distinguishes them, and it does so without a timeout to tune, because a lock
/// disappears when its session ends whether that ending was clean or not.
/// </para>
/// <para>
/// The key has to be reassembled rather than compared directly. PostgreSQL stores a single-bigint
/// advisory key split across <c>classid</c> (the high 32 bits) and <c>objid</c> (the low 32), so a
/// predicate written as <c>objid = key</c> matches only keys that happen to fit in 32 bits and
/// silently aliases any two keys sharing a low half. Every key this framework computes is a full
/// 64-bit hash, so that predicate would be wrong for all of them.
/// </para>
/// <para>
/// Deliberately "elsewhere": the caller's own session is excluded. The question being asked is
/// whether another instance is at work, and a caller that holds the lock itself has no reason to
/// ask. Only granted locks count, so a queue of waiters is not mistaken for a holder, and the
/// current database is required, because advisory locks are database-local while
/// <c>pg_locks</c> is not.
/// </para>
/// </remarks>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Migrations/AdvisoryLockProbeTests.cs</tests>
public static class AdvisoryLockProbe {
  /// <summary>
  /// The query that reassembles the 64-bit key from the two halves PostgreSQL reports.
  /// </summary>
  /// <remarks>
  /// The shift is intentionally unchecked: <c>classid</c> reaches 4294967295, and shifting that into
  /// the high half of a signed 64-bit value is exactly how a negative key is represented. Postgres
  /// bit shifts do not raise on overflow, so this reproduces the original key bit for bit.
  /// </remarks>
  private const string SQL = """
    SELECT EXISTS (
      SELECT 1
      FROM pg_locks l
      WHERE l.locktype = 'advisory'
        AND l.granted
        AND l.objsubid = 1
        AND l.database = (SELECT d.oid FROM pg_database d WHERE d.datname = current_database())
        AND ((l.classid::bigint << 32) | (l.objid::bigint & 4294967295)) = $1
        AND l.pid <> pg_backend_pid())
    """;

  /// <summary>
  /// Whether a session other than this one holds the advisory lock <paramref name="key"/>.
  /// </summary>
  /// <param name="connection">An open connection. Its own holdings are not counted.</param>
  /// <param name="key">The advisory lock key, as passed to <c>pg_try_advisory_lock</c>.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>
  /// <see langword="true"/> while another session holds it. Both lock scopes count: a
  /// transaction-scoped lock is reported for as long as its transaction is open, which is the
  /// window the schema initializer's holder occupies.
  /// </returns>
  public static async Task<bool> IsHeldElsewhereAsync(
      NpgsqlConnection connection,
      long key,
      CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(connection);

    await using var command = new NpgsqlCommand(SQL, connection);
    command.Parameters.AddWithValue(key);
    return await command.ExecuteScalarAsync(cancellationToken) is true;
  }
}
