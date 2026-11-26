// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Npgsql;
using NpgsqlTypes;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Workers;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// A flip never deadlocks against a writer (#2557, migration 581): every flip takes its ownership rows in partition
/// order, the order every fenced write holds them in, so of two sessions that both want two partitions, the later one
/// waits holding nothing the earlier one needs. Each test makes the flip's natural order the wrong one (the later
/// partition's row first in the heap, read by a sequential scan), so only the explicit order keeps it safe.
/// </summary>
/// <docs>fundamentals/work-coordinator/partition-assignment#handover</docs>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/581_FlipsLockInPartitionOrder.sql</code-under-test>
[Category("Shard5")]
public class FlipLockOrderSqlTests : EFCoreTestBase {
  private const string SUPPORTS_MAP = """{"partition_ownership_level":1}""";
  private static readonly Guid _a = new("00000000-0000-0000-0000-00000000000a");
  private static readonly Guid _b = new("00000000-0000-0000-0000-00000000000b");

  [Test]
  [Timeout(60_000)]
  public async Task AnAcknowledgement_AgainstAWriterHoldingTheEarlierPartition_WaitsAndFlipsBoth_NeverDeadlocksAsync(
      CancellationToken cancellationToken) {
    await using var setup = await _openAsync();
    _ = await _mapModeAsync(setup);
    // Handing over, the later partition's row written first, so it comes first in the heap.
    foreach (var partition in new[] { 6, 5 }) {
      await _execAsync(setup, null, $"""
        UPDATE wh_partition_ownership SET state = 'handing_over', next_owner_instance_id = '{_b}',
          handover_deadline = clock_timestamp() + INTERVAL '1 hour'
        WHERE partition_number = {partition}
        """);
    }
    var held = Convert.ToInt64(await _scalarAsync(setup, null, "SELECT epoch FROM wh_partition_ownership WHERE partition_number = 5"),
      System.Globalization.CultureInfo.InvariantCulture);
    await using var writer = await _openAsync();
    await using var writing = await writer.BeginTransactionAsync(cancellationToken);
    await _execAsync(writer, writing, $"SELECT wh_hold_partition_epoch(5, {held})");
    await using var flipper = await _openAsync();
    await _execAsync(flipper, null, "SET enable_indexscan = off; SET enable_bitmapscan = off; SET enable_indexonlyscan = off");
    var flipperPid = Convert.ToInt32(await _scalarAsync(flipper, null, "SELECT pg_backend_pid()"), System.Globalization.CultureInfo.InvariantCulture);

    var flipping = _acknowledgeAsync(flipper, [6, 5], [held, held]);
    await _blockedOrDoneAsync(setup, flipperPid, flipping, cancellationToken);
    // The writer now wants the later partition, as a write naming both takes them in order.
    var writerError = await _caughtAsync(() => _execAsync(writer, writing, $"SELECT wh_hold_partition_epoch(6, {held})"));
    await writing.CommitAsync(cancellationToken);
    var flipError = await _caughtAsync(() => flipping);

    await Assert.That(writerError?.SqlState).IsNull().Because("the writer is never chosen as a deadlock victim");
    await Assert.That(flipError?.SqlState).IsNull().Because("the flip waited for the writer and then flipped");
    await Assert.That(await _scalarAsync(setup, null, "SELECT count(*) FROM wh_partition_ownership WHERE partition_number IN (5, 6) AND owner_instance_id = @b",
      ("b", _b))).IsEqualTo(2L);
  }

  private static async Task _acknowledgeAsync(NpgsqlConnection conn, int[] partitions, long[] epochs) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT wh_acknowledge_handover(@a, @p, @e)";
    cmd.Parameters.AddWithValue("a", _a);
    cmd.Parameters.Add(new NpgsqlParameter("p", NpgsqlDbType.Array | NpgsqlDbType.Integer) { Value = partitions });
    cmd.Parameters.Add(new NpgsqlParameter("e", NpgsqlDbType.Array | NpgsqlDbType.Bigint) { Value = epochs });
    await cmd.ExecuteNonQueryAsync();
  }

  private static async Task<PostgresException?> _caughtAsync(Func<Task> action) {
    try {
      await action();
      return null;
    } catch (PostgresException ex) {
      return ex;
    }
  }

  // Completes once the backend waits on a lock another session holds, or once the call finished without waiting.
  private static async Task _blockedOrDoneAsync(NpgsqlConnection observer, int pid, Task call, CancellationToken cancellationToken) {
    while (!call.IsCompleted) {
      cancellationToken.ThrowIfCancellationRequested();
      if ((bool)(await _scalarAsync(observer, null, "SELECT cardinality(pg_blocking_pids(@pid)) > 0", ("pid", pid)))!) {
        return;
      }
    }
  }

  private async Task<NpgsqlConnection> _openAsync() {
    var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    return connection;
  }

  private static async Task<long> _mapModeAsync(NpgsqlConnection conn) {
    foreach (var instance in new[] { _a, _b }) {
      await _execAsync(conn, null,
        $"SELECT record_heartbeat('{instance}', 'test', 'test-host', 1, '{SUPPORTS_MAP}'::jsonb, NULL, NULL, NULL, 'pooled')");
    }
    var epoch = Convert.ToInt64(await _scalarAsync(conn, null,
      $"SELECT epoch FROM wh_vote_role('{PartitionAssignerOptions.ROLE}', '{_a}', INTERVAL '30 seconds', INTERVAL '5 seconds', NULL, ARRAY[1, 0], NULL) v WHERE v.outcome = 'granted'"),
      System.Globalization.CultureInfo.InvariantCulture);
    await _execAsync(conn, null,
      $"SELECT wh_publish_partition_assignment('{PartitionAssignerOptions.ROLE}', '{_a}', {epoch}, ARRAY['{_a}']::uuid[], INTERVAL '90 seconds')");
    await _execAsync(conn, null, $"SELECT wh_open_partition_ownership_by_map('{PartitionAssignerOptions.ROLE}', '{_a}', {epoch})");
    return epoch;
  }

  private static async Task<object?> _scalarAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, string sql, params (string Name, object Value)[] parameters) {
    await using var cmd = conn.CreateCommand();
    cmd.Transaction = tx;
    cmd.CommandText = sql;
    foreach (var (name, value) in parameters) {
      cmd.Parameters.AddWithValue(name, value);
    }
    return await cmd.ExecuteScalarAsync();
  }

  private static async Task _execAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, string sql) {
    await using var cmd = conn.CreateCommand();
    cmd.Transaction = tx;
    cmd.CommandText = sql;
    await cmd.ExecuteNonQueryAsync();
  }
}
