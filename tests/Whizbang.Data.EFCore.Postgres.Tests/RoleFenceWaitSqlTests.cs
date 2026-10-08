// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Globalization;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// What the role vote and the lapsed-bridge expiry wait on beside a fenced duty (#1217, migration 198).
/// </summary>
/// <remarks>
/// <para>
/// A role holder's fenced work (the commit-order stamper) runs <c>wh_assert_role_epoch</c>, which holds the role row
/// <c>FOR SHARE</c> until the work commits, so a vote cannot reassign the role between the check and the commit. The
/// holder's own vote renews its lease through the vote lock and <c>FOR UPDATE</c> on the same row, so it waits for that
/// commit, and <c>wh_end_lapsed_bridge</c> took the same two locks before it looked at anything. Under concurrent load
/// (<c>LivenessUnderLoadScenarioTests</c>) every wait either call made was behind the stamper's transaction or behind
/// the other call.
/// </para>
/// <para>
/// The expiry is called by every bridged instance that could not take the legacy lock, on every vote cycle, and almost
/// always finds nothing to end. It now reads the row without locks first and returns at once when there is no lapsed
/// bridge. The holder's renewal still waits for its own fenced work, by design; what bounds that wait is the fenced
/// work itself, which since migration 197 costs what its batch stamps.
/// </para>
/// </remarks>
/// <docs>proposals/duty-role-assignment</docs>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/198_LapsedBridgeLooksBeforeItLocks.sql</code-under-test>
[Category("Shard3")]
public class RoleFenceWaitSqlTests : EFCoreTestBase {
  private const string ROLE = "commit-stamper";
  private static readonly Guid _holder = new("aaaaaaaa-0000-0000-0000-0000000000a1");

  [Test]
  public async Task EndLapsedBridge_NothingToEnd_DoesNotWaitForAFencedTransactionAsync() {
    await using var dataSource = NpgsqlDataSource.Create(ConnectionString);
    var epoch = await _grantAsync(dataSource);
    await using var fenced = await dataSource.OpenConnectionAsync();
    await _execAsync(fenced, "BEGIN");
    await _execAsync(fenced, $"SELECT wh_assert_role_epoch('{ROLE}', '{_holder}'::uuid, {epoch})");

    await using var bridged = await dataSource.OpenConnectionAsync();
    await _execAsync(bridged, "SET lock_timeout = '250ms'");
    var ended = await _scalarAsync(bridged, $"SELECT wh_end_lapsed_bridge('{ROLE}', NULL)");

    await Assert.That(ended).IsNull()
      .Because("the holder is live, so there is no bridge to end, and finding that out must not wait for the holder's "
        + "fenced work to commit: every bridged instance asks on every vote cycle");

    await _execAsync(fenced, "ROLLBACK");
  }

  [Test]
  public async Task EndLapsedBridge_NothingToEnd_DoesNotWaitForTheHoldersVoteAsync() {
    await using var dataSource = NpgsqlDataSource.Create(ConnectionString);
    _ = await _grantAsync(dataSource);
    // The holder's renewal, held open: it holds the vote lock and the role row.
    await using var voting = await dataSource.OpenConnectionAsync();
    await _execAsync(voting, "BEGIN");
    await _execAsync(voting, _voteSql());

    await using var bridged = await dataSource.OpenConnectionAsync();
    await _execAsync(bridged, "SET lock_timeout = '250ms'");
    var ended = await _scalarAsync(bridged, $"SELECT wh_end_lapsed_bridge('{ROLE}', NULL)");

    await Assert.That(ended).IsNull()
      .Because("with nothing to end, the expiry must not queue behind a renewal for the vote lock");

    await _execAsync(voting, "ROLLBACK");
  }

  [Test]
  public async Task HolderVote_WaitsForItsOwnFencedTransaction_ByDesignAsync() {
    await using var dataSource = NpgsqlDataSource.Create(ConnectionString);
    var epoch = await _grantAsync(dataSource);
    await using var fenced = await dataSource.OpenConnectionAsync();
    await _execAsync(fenced, "BEGIN");
    await _execAsync(fenced, $"SELECT wh_assert_role_epoch('{ROLE}', '{_holder}'::uuid, {epoch})");

    await using var voting = await dataSource.OpenConnectionAsync();
    await _execAsync(voting, "SET lock_timeout = '250ms'");

    await Assert.That(async () => await _execAsync(voting, _voteSql()))
      .Throws<PostgresException>()
      .Because("a holder's renewal writes the role row that its own fenced work holds FOR SHARE, so it waits for that "
        + "work to commit. This is the fence working: no vote may change the row between a fenced writer's epoch check "
        + "and its commit. The wait is bounded by the fenced work, which is why the stamp has to cost what its batch "
        + "stamps (197). IF THIS ASSERTION FAILS BECAUSE THE VOTE NO LONGER WAITS, check that the fence still stops a "
        + "reassignment mid-write before accepting it");

    await _execAsync(fenced, "ROLLBACK");
  }

  [Test]
  public async Task EndLapsedBridge_ALapsedBridgedHolder_IsStillFoundAsync() {
    await using var dataSource = NpgsqlDataSource.Create(ConnectionString);
    _ = await _grantAsync(dataSource);
    await using var conn = await dataSource.OpenConnectionAsync();
    // A bridged holder whose lease lapsed and whose bridge session is this connection, holding the legacy lock.
    await _execAsync(conn, $"""
      SELECT pg_advisory_lock(4242);
      UPDATE wh_role_assignments
         SET lease_expires_at = now() - INTERVAL '1 minute',
             bridge_backend_pid = pg_backend_pid(),
             bridge_backend_started_at = (SELECT backend_start FROM pg_stat_activity WHERE pid = pg_backend_pid())
       WHERE role = '{ROLE}';
      """);
    var pid = await _scalarAsync(conn, "SELECT pg_backend_pid()");

    await using var bridged = await dataSource.OpenConnectionAsync();
    var ended = await _scalarAsync(bridged, $"SELECT wh_end_lapsed_bridge('{ROLE}', 4242)");

    await Assert.That(ended).IsEqualTo(pid)
      .Because("looking before locking must not hide a bridge that has lapsed: that one is still ended, under the locks");
  }

  // ------------------------------------------------------------------

  private static string _voteSql() =>
    $"SELECT * FROM wh_vote_role('{ROLE}', '{_holder}'::uuid, INTERVAL '30 seconds', INTERVAL '5 seconds', NULL, ARRAY[1, 0], NULL)";

  private static async Task<long> _grantAsync(NpgsqlDataSource dataSource) {
    await using var conn = await dataSource.OpenConnectionAsync();
    await _execAsync(conn, $"""
      INSERT INTO wh_service_instances (instance_id, service_name, host_name, process_id, last_heartbeat_at, started_at)
      VALUES ('{_holder}', 'test', 'test-host', 1, NOW(), NOW())
      """);
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = $"SELECT epoch FROM ({_voteSql()}) v";
    return Convert.ToInt64(await cmd.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
  }

  private static async Task _execAsync(NpgsqlConnection conn, string sql) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    await cmd.ExecuteNonQueryAsync();
  }

  private static async Task<int?> _scalarAsync(NpgsqlConnection conn, string sql) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    var value = await cmd.ExecuteScalarAsync();
    return value is null or DBNull ? null : Convert.ToInt32(value, CultureInfo.InvariantCulture);
  }
}
