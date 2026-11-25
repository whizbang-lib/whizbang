// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Globalization;
using Npgsql;
using NpgsqlTypes;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Workers;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// A caller that writes several streams one after another inside its own transaction holds their partitions in the order
/// it writes them, which the framework cannot know in advance (#2577). Every flip used to take its ownership rows in
/// partition order (581) and wait on each, so a caller holding a higher partition, then a lower one a flip already held
/// while it waited for the higher one, closed a cycle. Now a writer's hold never blocks a change that keeps the epoch,
/// a flip that raises the epoch skips what a writer holds instead of waiting, and a publish in map mode never raises an
/// epoch itself, so a caller may write streams in any order.
/// </summary>
/// <remarks>
/// Instance A owns every partition (map [A]); a publish of [A, B] hands the odd partitions over to B. The caller holds
/// partition 5 first and partition 3 second, the reverse of partition order. The two sessions are coordinated by the
/// server's own report that the flip's backend waits on a lock (or that the flip finished), never by a clock.
/// </remarks>
/// <docs>fundamentals/work-coordinator/partition-assignment#write-order</docs>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/610_WritersHoldTheEpochKeyAndFlipsNeverWait.sql</code-under-test>
[Category("Shard5")]
public class MultiStreamWriteOrderSqlTests : EFCoreTestBase {
  private const string SUPPORTS_MAP = """{"partition_ownership_level":1}""";
  private const int LOW = 3;
  private const int HIGH = 5;
  private static readonly Guid _a = new("00000000-0000-0000-0000-00000000000a");
  private static readonly Guid _b = new("00000000-0000-0000-0000-00000000000b");

  [Test]
  [Timeout(60_000)]
  public async Task Publish_AgainstACallerHoldingItsPartitionsOutOfOrder_NeverDeadlocks_AndHandsBothOverAsync(
      CancellationToken cancellationToken) {
    await using var setup = await _openAsync();
    var role = await _mapModeAsync(setup);
    var epoch = await _epochAsync(setup, HIGH);

    var outcome = await _flipWhileACallerHoldsOutOfOrderAsync(setup, epoch,
      flip: conn => _publishAsync(conn, role, [_a, _b]), cancellationToken);

    await Assert.That(outcome.Deadlock).IsNull()
      .Because("a caller holding its partitions in write order must not deadlock against a publish");
    await Assert.That(outcome.CallerFenced).IsFalse()
      .Because("a handover keeps the epoch, so the caller's writes at it still land");
    await Assert.That(await _scalarAsync(setup,
      "SELECT count(*) FROM wh_partition_ownership WHERE partition_number IN (3, 5) AND state = 'handing_over' AND epoch = @e AND next_owner_instance_id = @b",
      ("e", epoch), ("b", _b))).IsEqualTo(2L);
  }

  [Test]
  [Timeout(60_000)]
  public async Task Acknowledgement_AgainstACallerHoldingItsPartitionsOutOfOrder_NeverDeadlocks_AndFlipsWhatIsFreeAsync(
      CancellationToken cancellationToken) {
    await using var setup = await _openAsync();
    var role = await _mapModeAsync(setup);
    await _publishAsync(setup, role, [_a, _b]);
    var epoch = await _epochAsync(setup, HIGH);

    var outcome = await _flipWhileACallerHoldsOutOfOrderAsync(setup, epoch,
      flip: conn => _execAsync(conn, "SELECT count(*) FROM wh_acknowledge_handover(@a, @p, @e)",
        ("a", _a), ("p", new[] { LOW, HIGH }), ("e", new[] { epoch, epoch })), cancellationToken);
    var reasked = await _scalarAsync(setup, "SELECT count(*) FROM wh_acknowledge_handover(@a, @p, @e)",
      ("a", _a), ("p", new[] { LOW, HIGH }), ("e", new[] { epoch, epoch }));

    await Assert.That(outcome.Deadlock).IsNull()
      .Because("a caller holding its partitions in write order must not deadlock against an acknowledgement");
    await Assert.That(reasked).IsEqualTo(1L)
      .Because("the acknowledgement skipped the partition the caller held, and the next ask flips it once the caller is done");
    await Assert.That(await _scalarAsync(setup,
      "SELECT count(*) FROM wh_partition_ownership WHERE partition_number IN (3, 5) AND state = 'active' AND owner_instance_id = @b AND epoch = @e + 1",
      ("b", _b), ("e", epoch))).IsEqualTo(2L);
  }

  [Test]
  [Timeout(60_000)]
  public async Task DeadlineSweep_AgainstACallerHoldingItsPartitionsOutOfOrder_NeverDeadlocks_AndFlipsWhatIsFreeAsync(
      CancellationToken cancellationToken) {
    await using var setup = await _openAsync();
    var role = await _mapModeAsync(setup);
    await _publishAsync(setup, role, [_a, _b]);
    await _execAsync(setup, "UPDATE wh_partition_ownership SET handover_deadline = clock_timestamp() - INTERVAL '1 minute' WHERE state = 'handing_over'");
    var epoch = await _epochAsync(setup, HIGH);

    var outcome = await _flipWhileACallerHoldsOutOfOrderAsync(setup, epoch,
      flip: conn => _execAsync(conn, "SELECT wh_complete_expired_handovers(@role, @a, @epoch)",
        ("role", PartitionAssignerOptions.ROLE), ("a", _a), ("epoch", role)), cancellationToken);
    var nextTick = await _scalarAsync(setup, "SELECT wh_complete_expired_handovers(@role, @a, @epoch)",
      ("role", PartitionAssignerOptions.ROLE), ("a", _a), ("epoch", role));

    await Assert.That(outcome.Deadlock).IsNull()
      .Because("a caller holding its partitions in write order must not deadlock against the deadline sweep");
    await Assert.That(nextTick).IsEqualTo(1)
      .Because("the sweep skipped the partition the caller held, and the next tick flips it once the caller is done");
    await Assert.That(await _scalarAsync(setup,
      "SELECT count(*) FROM wh_partition_ownership WHERE state = 'handing_over'")).IsEqualTo(0L);
  }

  private sealed record Outcome(string? Deadlock, bool CallerFenced);

  /// <summary>
  /// The caller holds <see cref="HIGH"/>, the flip starts and either finishes or is left waiting on a lock, then the
  /// caller holds <see cref="LOW"/> and commits. Returns the deadlock either side was chosen to lose (or null), and
  /// whether the caller's second hold was fenced because the flip had already moved that partition on.
  /// </summary>
  private async Task<Outcome> _flipWhileACallerHoldsOutOfOrderAsync(
      NpgsqlConnection observer, long epoch, Func<NpgsqlConnection, Task> flip, CancellationToken cancellationToken) {
    await using var caller = await _openAsync();
    await using var callerTx = await caller.BeginTransactionAsync(cancellationToken);
    await _holdAsync(caller, callerTx, HIGH, epoch);

    await using var flipper = await _openAsync();
    var flipperPid = flipper.ProcessID;
    var flipping = flip(flipper);
    await _blockedOrDoneAsync(observer, flipperPid, flipping, cancellationToken);

    string? deadlock = null;
    var fenced = false;
    try {
      await _holdAsync(caller, callerTx, LOW, epoch);
      await callerTx.CommitAsync(cancellationToken);
    } catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.DeadlockDetected) {
      deadlock = $"the caller lost: {ex.Detail}";
    } catch (PostgresException ex) when (ex.SqlState == "WHF02") {
      // Fenced: the flip moved the lower partition on before the caller reached it. Not a deadlock.
      fenced = true;
      await callerTx.RollbackAsync(cancellationToken);
    }
    try {
      await flipping;
    } catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.DeadlockDetected) {
      deadlock = $"the flip lost: {ex.Detail}";
    }
    return new Outcome(deadlock, fenced);
  }

  private static async Task _holdAsync(NpgsqlConnection conn, NpgsqlTransaction tx, int partition, long epoch) {
    await using var cmd = conn.CreateCommand();
    cmd.Transaction = tx;
    cmd.CommandText = "SELECT wh_hold_partition_epoch(@p, @e)";
    cmd.Parameters.AddWithValue("p", partition);
    cmd.Parameters.AddWithValue("e", epoch);
    await cmd.ExecuteNonQueryAsync();
  }

  /// <summary>Completes once the flip's backend waits on a lock another session holds, or once the flip finished.</summary>
  private static async Task _blockedOrDoneAsync(NpgsqlConnection observer, int pid, Task call, CancellationToken cancellationToken) {
    while (!call.IsCompleted) {
      cancellationToken.ThrowIfCancellationRequested();
      if ((bool)(await _scalarAsync(observer, "SELECT cardinality(pg_blocking_pids(@pid)) > 0", ("pid", pid)))!) {
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
      await _execAsync(conn, "SELECT record_heartbeat(@inst, 'test', 'test-host', 1, @metadata::jsonb, NULL, NULL, NULL, 'pooled')",
        ("inst", instance), ("metadata", SUPPORTS_MAP));
    }
    var epoch = Convert.ToInt64(await _scalarAsync(conn,
      "SELECT epoch FROM wh_vote_role(@role, @a, INTERVAL '30 seconds', INTERVAL '5 seconds', NULL, ARRAY[1, 0], NULL) v WHERE v.outcome = 'granted'",
      ("role", PartitionAssignerOptions.ROLE), ("a", _a)), CultureInfo.InvariantCulture);
    await _publishAsync(conn, epoch, [_a]);
    var answer = (string)(await _scalarAsync(conn, "SELECT wh_open_partition_ownership_by_map(@role, @a, @epoch)",
      ("role", PartitionAssignerOptions.ROLE), ("a", _a), ("epoch", epoch)))!;
    if (answer != "opened") {
      throw new InvalidOperationException($"precondition: the switch did not turn on ({answer})");
    }
    return epoch;
  }

  private static async Task _publishAsync(NpgsqlConnection conn, long epoch, Guid[] members) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT wh_publish_partition_assignment(@role, @a, @epoch, @members, INTERVAL '90 seconds')";
    cmd.Parameters.AddWithValue("role", PartitionAssignerOptions.ROLE);
    cmd.Parameters.AddWithValue("a", _a);
    cmd.Parameters.AddWithValue(nameof(epoch), epoch);
    cmd.Parameters.Add(new NpgsqlParameter(nameof(members), NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = members });
    await cmd.ExecuteNonQueryAsync();
  }

  private static async Task<long> _epochAsync(NpgsqlConnection conn, int partition) =>
    Convert.ToInt64(await _scalarAsync(conn, "SELECT epoch FROM wh_partition_ownership WHERE partition_number = @p", ("p", partition)),
      CultureInfo.InvariantCulture);

  private static async Task<object?> _scalarAsync(NpgsqlConnection conn, string sql, params (string Name, object Value)[] parameters) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    foreach (var (name, value) in parameters) {
      cmd.Parameters.AddWithValue(name, value);
    }
    return await cmd.ExecuteScalarAsync();
  }

  private static async Task _execAsync(NpgsqlConnection conn, string sql, params (string Name, object Value)[] parameters) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    foreach (var (name, value) in parameters) {
      cmd.Parameters.AddWithValue(name, value);
    }
    await cmd.ExecuteNonQueryAsync();
  }
}
