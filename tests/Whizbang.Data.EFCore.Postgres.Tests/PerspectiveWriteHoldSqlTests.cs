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
/// The hold a fenced perspective write takes in its own statement (#2556, migration 577): the ownership row at the
/// claimed epoch, FOR SHARE until the write's transaction ends, so a flip waits for the write; an epoch that moved on
/// raises WHF02; a write that names no epoch holds nothing.
/// </summary>
/// <docs>fundamentals/work-coordinator/partition-assignment#fencing</docs>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/577_PerspectiveWritesHoldTheirPartitionEpoch.sql</code-under-test>
[Category("Shard5")]
public class PerspectiveWriteHoldSqlTests : EFCoreTestBase {
  private const string SUPPORTS_MAP = """{"partition_ownership_level":1}""";
  private static readonly Guid _a = new("00000000-0000-0000-0000-00000000000a");

  [Test]
  public async Task AWriteThatNamesNoEpoch_HoldsNothingAsync() {
    await using var writer = await _openAsync();
    await _mapModeAsync(writer);
    await using var tx = await writer.BeginTransactionAsync();

    var held = await _scalarAsync(writer, tx, "SELECT wh_hold_partition_epoch(5, NULL)");

    await Assert.That((bool)held!).IsTrue();
    await Assert.That(await _flipIsBlockedAsync(5)).IsFalse().Because("nothing is held, so a flip goes ahead");
  }

  [Test]
  public async Task TheCurrentEpoch_IsHeldUntilTheWriteCommits_SoAFlipWaitsAsync() {
    await using var writer = await _openAsync();
    await _mapModeAsync(writer);
    await using var tx = await writer.BeginTransactionAsync();

    var held = await _scalarAsync(writer, tx, "SELECT wh_hold_partition_epoch(10005, 1)");
    var blockedWhileOpen = await _flipIsBlockedAsync(5);
    await tx.CommitAsync();

    await Assert.That((bool)held!).IsTrue().Because("a row's partition past the table is held through its remainder");
    await Assert.That(blockedWhileOpen).IsTrue().Because("the flip's UPDATE waits for the write's FOR SHARE");
    await Assert.That(await _flipIsBlockedAsync(5)).IsFalse().Because("the hold ends with the write's transaction");
  }

  [Test]
  public async Task AnEpochThatMovedOn_RaisesTheFenceAsync() {
    await using var writer = await _openAsync();
    await _mapModeAsync(writer);
    await _scalarAsync(writer, null, "UPDATE wh_partition_ownership SET epoch = epoch + 1 WHERE partition_number = 5");

    var hold = await Assert.ThrowsAsync<PostgresException>(() => _scalarAsync(writer, null, "SELECT wh_hold_partition_epoch(5, 1)"));
    var stream = await Assert.ThrowsAsync<PostgresException>(() => _scalarAsync(writer, null, $"SELECT wh_held_stream('{Guid.Empty}', 5, 1)"));

    await Assert.That(hold!.SqlState).IsEqualTo("WHF02");
    await Assert.That(stream!.SqlState).IsEqualTo("WHF02");
  }

  [Test]
  public async Task TheHeldStream_IsTheStreamsIdAsync() {
    await using var writer = await _openAsync();
    await _mapModeAsync(writer);
    var stream = Guid.CreateVersion7();

    await Assert.That(await _scalarAsync(writer, null, $"SELECT wh_held_stream('{stream}', 5, 1)")).IsEqualTo(stream);
    await Assert.That(await _scalarAsync(writer, null, $"SELECT wh_held_stream('{stream}', 5, NULL)")).IsEqualTo(stream);
  }

  // Whether a flip of the partition would have to wait: NOWAIT answers at once instead of waiting.
  private async Task<bool> _flipIsBlockedAsync(int partition) {
    await using var flipper = await _openAsync();
    await using var tx = await flipper.BeginTransactionAsync();
    try {
      await _scalarAsync(flipper, tx, $"SELECT 1 FROM wh_partition_ownership WHERE partition_number = {partition} FOR NO KEY UPDATE NOWAIT");
      return false;
    } catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.LockNotAvailable) {
      return true;
    } finally {
      await tx.RollbackAsync();
    }
  }

  private async Task<NpgsqlConnection> _openAsync() {
    var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    return connection;
  }

  private static async Task _mapModeAsync(NpgsqlConnection conn) {
    await _scalarAsync(conn, null,
      $"SELECT record_heartbeat('{_a}', 'test', 'test-host', 1, '{SUPPORTS_MAP}'::jsonb, NULL, NULL, NULL, 'pooled')");
    var epoch = Convert.ToInt64(await _scalarAsync(conn, null,
      $"SELECT epoch FROM wh_vote_role('{PartitionAssignerOptions.ROLE}', '{_a}', INTERVAL '30 seconds', INTERVAL '5 seconds', NULL, ARRAY[1, 0], NULL) v WHERE v.outcome = 'granted'"),
      System.Globalization.CultureInfo.InvariantCulture);
    await using (var cmd = conn.CreateCommand()) {
      cmd.CommandText = "SELECT wh_publish_partition_assignment(@role, @a, @epoch, @members, INTERVAL '90 seconds')";
      cmd.Parameters.AddWithValue("role", PartitionAssignerOptions.ROLE);
      cmd.Parameters.AddWithValue("a", _a);
      cmd.Parameters.AddWithValue(nameof(epoch), epoch);
      cmd.Parameters.Add(new NpgsqlParameter("members", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = new[] { _a } });
      await cmd.ExecuteNonQueryAsync();
    }
    await _scalarAsync(conn, null, $"SELECT wh_open_partition_ownership_by_map('{PartitionAssignerOptions.ROLE}', '{_a}', {epoch})");
  }

  private static async Task<object?> _scalarAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, string sql) {
    await using var cmd = conn.CreateCommand();
    cmd.Transaction = tx;
    cmd.CommandText = sql;
    return await cmd.ExecuteScalarAsync();
  }
}
