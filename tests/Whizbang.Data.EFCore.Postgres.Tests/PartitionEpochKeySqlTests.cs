// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Text.RegularExpressions;
using Npgsql;
using NpgsqlTypes;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Workers;
using Whizbang.Data.Postgres.Perspectives;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// The lock contract that lets a caller write streams in any order (#2577, migration 610). The epoch is a key column
/// (UNIQUE (partition_number, epoch)), and every writer holds its partition FOR KEY SHARE, so only a change that raises
/// the epoch waits for a writer, and a handover, a cancel or a redirect never does. A flip that raises the epoch (the
/// acknowledgement, the deadline sweep) never waits: it skips a partition a writer holds and flips the rest. In map
/// mode a publish never raises an epoch. An instance still on the previous release holds FOR SHARE, which the new flips
/// skip just the same, so ownership is never flipped under its write.
/// </summary>
/// <remarks>
/// Instance A owns every partition (map [A]), at epoch 1. Two sessions are coordinated by the server's own report that
/// a backend waits on a lock (or that its call finished), never by a clock.
/// </remarks>
/// <docs>fundamentals/work-coordinator/partition-assignment#write-order</docs>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/610_WritersHoldTheEpochKeyAndFlipsNeverWait.sql</code-under-test>
[Category("Shard5")]
public partial class PartitionEpochKeySqlTests : EFCoreTestBase {
  private const string SUPPORTS_MAP = """{"partition_ownership_level":1}""";
  private const int PARTITION = 5;
  private const int OTHER = 7;
  // The hold an instance on the previous release takes (581's statement): FOR SHARE, which also blocks a handover.
  private const string PREVIOUS_RELEASE_HOLD =
    "SELECT 1 FROM wh_partition_ownership y WHERE (y.partition_number, y.epoch) IN (SELECT t.p, t.e FROM unnest(@wb_hp, @wb_he) AS t(p, e)) ORDER BY y.partition_number FOR SHARE OF y";
  private static readonly Guid _a = new("00000000-0000-0000-0000-00000000000a");
  private static readonly Guid _b = new("00000000-0000-0000-0000-00000000000b");

  private static readonly int[] _heldPartitions = [PARTITION];
  private static readonly long[] _heldEpochs = [1L];

  // The functions that hold a partition at its epoch for a write, each of which must hold it FOR KEY SHARE.
  private static readonly string[] _holders = [
    "wh_hold_partition_epoch", "wh_epoch_is_current", "_wh_fence_outbox_ids", "_wh_fence_perspective_work_ids",
    "_wh_fence_items", "flush_completions", "commit_handler_batch",
  ];

  // --- the key -----------------------------------------------------------------------------------

  [Test]
  public async Task TheEpoch_IsAKeyColumn_ThroughAUniqueIndexOnPartitionAndEpochAsync() {
    await using var conn = await _openAsync();

    var keyed = await _scalarAsync(conn, null, """
      SELECT count(*) FROM pg_index i
      WHERE i.indrelid = 'wh_partition_ownership'::regclass
        AND i.indisunique AND i.indimmediate AND i.indpred IS NULL AND i.indexprs IS NULL
        AND (SELECT array_agg(a.attname::text ORDER BY k.ord) FROM unnest(i.indkey) WITH ORDINALITY AS k(attnum, ord)
             JOIN pg_attribute a ON a.attrelid = i.indrelid AND a.attnum = k.attnum) = ARRAY['partition_number', 'epoch']
      """);

    await Assert.That(keyed).IsEqualTo(1L)
      .Because("a unique, non-partial, plain index makes the epoch a key column, so only a change of it conflicts with FOR KEY SHARE");
  }

  // --- the writers' holds ------------------------------------------------------------------------

  [Test]
  [Timeout(60_000)]
  [Arguments("wh_hold_partition_epoch")]
  [Arguments("wh_epoch_is_current")]
  [Arguments("_wh_fence_items")]
  [Arguments("perspective_writer")]
  public async Task AHold_LetsAChangeThatKeepsTheEpochThrough_AndMakesAnEpochChangeWaitAsync(
      string hold, CancellationToken cancellationToken) {
    await using var observer = await _openAsync();
    await _mapModeAsync(observer);

    var handoverWaited = await _waitsForTheHoldAsync(observer, hold,
      $"UPDATE wh_partition_ownership SET state = 'handing_over', next_owner_instance_id = '{_b}', handover_deadline = clock_timestamp() WHERE partition_number = {PARTITION}",
      cancellationToken);
    var flipWaited = await _waitsForTheHoldAsync(observer, hold,
      $"UPDATE wh_partition_ownership SET epoch = epoch + 1 WHERE partition_number = {PARTITION}", cancellationToken);

    await Assert.That(handoverWaited).IsFalse().Because("a change that keeps the epoch does not conflict with FOR KEY SHARE");
    await Assert.That(flipWaited).IsTrue().Because("a change of the epoch is a key change, which waits for the write");
  }

  [Test]
  public async Task NoInstalledFunction_HoldsAnOwnershipRowForShare_AndEveryWriterHoldsItForKeyShareAsync() {
    await using var conn = await _openAsync();
    var forShare = new List<string>();
    var forKeyShare = new HashSet<string>();

    await using (var cmd = conn.CreateCommand()) {
      cmd.CommandText = "SELECT p.proname, p.prosrc FROM pg_proc p WHERE pg_function_is_visible(p.oid) AND p.prosrc LIKE '%wh_partition_ownership%'";
      await using var reader = await cmd.ExecuteReaderAsync();
      while (await reader.ReadAsync()) {
        var name = reader.GetString(0);
        foreach (var (keyShare, _) in _ownershipLocks(reader.GetString(1))) {
          if (keyShare) {
            forKeyShare.Add(name);
          } else {
            forShare.Add(name);
          }
        }
      }
    }

    await Assert.That(forShare).IsEmpty()
      .Because("FOR SHARE blocks a handover, a cancel and a redirect, which is what let a flip wait on a caller's write");
    await Assert.That(_holders.Where(h => !forKeyShare.Contains(h))).IsEmpty()
      .Because("every writer still holds its partition, so a flip that raises the epoch waits for it (the positive control)");
  }

  [Test]
  public async Task TheGuardsParser_FindsAForShareHoldOfTheOwnershipTable_AndNoOtherTablesAsync() {
    var locks = _ownershipLocks("""
      -- FOR SHARE in a comment is not a lock
      PERFORM 1 FROM wh_partition_ownership o WHERE o.partition_number = 1 FOR SHARE;
      PERFORM 1 FROM wh_partition_ownership y JOIN wh_outbox x ON TRUE FOR SHARE OF y;
      PERFORM 1 FROM wh_outbox o JOIN wh_partition_ownership y ON TRUE FOR SHARE OF o;
      PERFORM 1 FROM wh_partition_ownership y FOR KEY SHARE OF y
      """);

    await Assert.That(locks).IsEquivalentTo([(false, 1), (false, 2), (true, 4)]);
  }

  // --- flips never wait ----------------------------------------------------------------------------

  [Test]
  [Timeout(60_000)]
  public async Task TheDeadlineSweep_AgainstAWriterHoldingOnePartition_FlipsTheOtherWithoutWaiting_AndTheHeldOneOnTheNextTickAsync(
      CancellationToken cancellationToken) {
    await using var observer = await _openAsync();
    var role = await _mapModeAsync(observer);
    await _expiredHandoverAsync(observer, PARTITION, OTHER);
    await using var writer = await _openAsync();
    await using var writing = await writer.BeginTransactionAsync(cancellationToken);
    await _execAsync(writer, writing, $"SELECT wh_hold_partition_epoch({PARTITION}, 1)");

    await using var sweeper = await _openAsync();
    var sweeping = _scalarAsync(sweeper, null, $"SELECT wh_complete_expired_handovers('{PartitionAssignerOptions.ROLE}', '{_a}', {role})");
    await _blockedOrDoneAsync(observer, sweeper.ProcessID, sweeping, cancellationToken);
    var waited = !sweeping.IsCompleted;
    await writing.CommitAsync(cancellationToken);
    var first = await sweeping;
    var nextTick = await _scalarAsync(sweeper, null, $"SELECT wh_complete_expired_handovers('{PartitionAssignerOptions.ROLE}', '{_a}', {role})");

    await Assert.That(waited).IsFalse().Because("the sweep skips what a writer holds instead of waiting while it holds the rest");
    await Assert.That(first).IsEqualTo(1);
    await Assert.That(nextTick).IsEqualTo(1).Because("the next tick flips the partition the writer held");
    await Assert.That(await _scalarAsync(observer, null,
      $"SELECT count(*) FROM wh_partition_ownership WHERE partition_number IN ({PARTITION}, {OTHER}) AND owner_instance_id = '{_b}' AND epoch = 2"))
      .IsEqualTo(2L);
  }

  [Test]
  [Timeout(60_000)]
  public async Task APublishInMapMode_AgainstAWriter_HandsOverRedirectsAndCancelsWithoutWaiting_AndKeepsTheEpochAsync(
      CancellationToken cancellationToken) {
    await using var observer = await _openAsync();
    var role = await _mapModeAsync(observer);
    var c = new Guid("00000000-0000-0000-0000-00000000000c");
    await _heartbeatAsync(observer, c);
    await using var writer = await _openAsync();
    await using var writing = await writer.BeginTransactionAsync(cancellationToken);
    await _execAsync(writer, writing, $"SELECT wh_hold_partition_epoch({PARTITION}, 1)");

    // A handover to B, a redirect to C, then a cancel, each while the writer holds the partition.
    var waited = new List<bool>();
    var states = new List<object?>();
    foreach (var members in new[] { new[] { _a, _b }, [_a, c], [_a] }) {
      await using var publisher = await _openAsync();
      var publishing = _publishAsync(publisher, role, members);
      await _blockedOrDoneAsync(observer, publisher.ProcessID, publishing, cancellationToken);
      waited.Add(!publishing.IsCompleted);
      if (!publishing.IsCompleted) {
        // A wait is the failure under test; end the write so the publish can finish and the assertions report it.
        await writing.CommitAsync(cancellationToken);
      }
      await publishing;
      states.Add(await _scalarAsync(observer, null,
        $"SELECT state || ':' || COALESCE(next_owner_instance_id::text, '-') || ':' || epoch FROM wh_partition_ownership WHERE partition_number = {PARTITION}"));
    }

    await Assert.That(waited).IsEquivalentTo([false, false, false]).Because("no publish in map mode waits for a writer");
    await Assert.That(states).IsEquivalentTo(new object?[] { $"handing_over:{_b}:1", $"handing_over:{c}:1", "active:-:1" })
      .Because("a handover, a redirect and a cancel all keep the epoch the writer holds");
  }

  // --- rolling deploy: an instance on the previous release ----------------------------------------

  [Test]
  [Timeout(60_000)]
  public async Task AWriterOnThePreviousRelease_HoldingForShare_IsNeverFlippedUnder_AndItsPartitionMovesOnceItCommitsAsync(
      CancellationToken cancellationToken) {
    await using var observer = await _openAsync();
    var role = await _mapModeAsync(observer);
    await _expiredHandoverAsync(observer, PARTITION, OTHER);
    await using var writer = await _openAsync();
    await using var writing = await writer.BeginTransactionAsync(cancellationToken);
    await _previousReleaseHoldAsync(writer, writing, PARTITION, 1);

    await using var acker = await _openAsync();
    var acking = _scalarAsync(acker, null,
      $"SELECT count(*) FROM wh_acknowledge_handover('{_a}', ARRAY[{PARTITION}, {OTHER}], ARRAY[1, 1]::bigint[])");
    await _blockedOrDoneAsync(observer, acker.ProcessID, acking, cancellationToken);
    var heldEpoch = await _scalarAsync(observer, null, $"SELECT epoch FROM wh_partition_ownership WHERE partition_number = {PARTITION}");
    await writing.CommitAsync(cancellationToken);
    var acked = await acking;
    var swept = await _scalarAsync(observer, null, $"SELECT wh_complete_expired_handovers('{PartitionAssignerOptions.ROLE}', '{_a}', {role})");

    await Assert.That(acked).IsEqualTo(1L).Because("the acknowledgement skips the partition the older writer holds and flips the other");
    await Assert.That(heldEpoch).IsEqualTo(1L).Because("the older writer's epoch never moves while it writes, so its fence holds");
    await Assert.That(swept).IsEqualTo(1).Because("once it commits, the partition moves");
  }

  [Test]
  [Timeout(60_000)]
  public async Task APublish_AgainstAWriterOnThePreviousRelease_WaitsForIt_ThenHandsOverKeepingTheEpochAsync(
      CancellationToken cancellationToken) {
    await using var observer = await _openAsync();
    var role = await _mapModeAsync(observer);
    await _heartbeatAsync(observer, _b);
    await using var writer = await _openAsync();
    await using var writing = await writer.BeginTransactionAsync(cancellationToken);
    await _previousReleaseHoldAsync(writer, writing, PARTITION, 1);

    await using var publisher = await _openAsync();
    var publishing = _publishAsync(publisher, role, [_a, _b]);
    await _blockedOrDoneAsync(observer, publisher.ProcessID, publishing, cancellationToken);
    var waited = !publishing.IsCompleted;
    await writing.CommitAsync(cancellationToken);
    await publishing;

    await Assert.That(waited).IsTrue()
      .Because("FOR SHARE, the previous release's hold, still blocks a handover: the documented limit while both releases run");
    await Assert.That((bool)(await _scalarAsync(observer, null,
      $"SELECT epoch = 1 AND state = 'handing_over' AND owner_instance_id = '{_a}' AND next_owner_instance_id = '{_b}' FROM wh_partition_ownership WHERE partition_number = {PARTITION}"))!)
      .IsTrue();
  }

  [Test]
  [Arguments("wh_publish_partition_assignment(text, uuid, bigint, uuid[], interval, smallint[], interval)")]
  [Arguments("wh_acknowledge_handover(uuid, integer[], bigint[])")]
  [Arguments("wh_complete_expired_handovers(text, uuid, bigint)")]
  [Arguments("wh_hold_partition_epoch(integer, bigint)")]
  [Arguments("wh_held_stream(uuid, integer, bigint)")]
  [Arguments("wh_epoch_is_current(integer, bigint)")]
  [Arguments("_wh_fence_outbox_ids(uuid[], bigint[])")]
  [Arguments("_wh_fence_perspective_work_ids(uuid[], bigint[])")]
  [Arguments("_wh_fence_items(jsonb, text)")]
  [Arguments("_wh_handler_commit_is_fenced(jsonb)")]
  [Arguments("flush_completions(uuid[], jsonb, uuid[], jsonb, bigint[], bigint[])")]
  [Arguments("commit_handler_batch(jsonb)")]
  public async Task ACallThePreviousReleaseMakes_StillResolvesAsync(string signature) {
    await using var conn = await _openAsync();

    var resolved = (bool)(await _scalarAsync(conn, null, $"SELECT to_regprocedure('{signature}') IS NOT NULL"))!;

    await Assert.That(resolved).IsTrue().Because("an instance on the previous release calls the same signatures while both run");
  }

  // --- helpers -------------------------------------------------------------------------------------

  /// <summary>
  /// Every row lock a function body takes of the ownership table: whether it is FOR KEY SHARE, and the statement's
  /// position. A lock counts when it names an alias of the ownership table, or names none in a statement that reads it.
  /// </summary>
  private static List<(bool KeyShare, int Statement)> _ownershipLocks(string body) {
    var found = new List<(bool, int)>();
    var statements = _comment().Replace(body, string.Empty).Split(';');
    for (var i = 0; i < statements.Length; i++) {
      var aliases = _ownershipAlias().Matches(statements[i]).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
      if (aliases.Count == 0) {
        continue;
      }
      foreach (Match lockMatch in _shareLock().Matches(statements[i])) {
        var of = lockMatch.Groups["of"];
        if (!of.Success || aliases.Contains(of.Value)) {
          found.Add((lockMatch.Groups["key"].Success, i + 1));
        }
      }
    }
    return found;
  }

  [GeneratedRegex(@"--[^\n]*")]
  private static partial Regex _comment();

  [GeneratedRegex(@"wh_partition_ownership\s+(?:AS\s+)?(\w+)", RegexOptions.IgnoreCase)]
  private static partial Regex _ownershipAlias();

  [GeneratedRegex(@"\bFOR\s+(?<key>KEY\s+)?SHARE\b(?:\s+OF\s+(?<of>\w+))?", RegexOptions.IgnoreCase)]
  private static partial Regex _shareLock();

  /// <summary>
  /// Opens a write that takes <paramref name="hold"/> on <see cref="PARTITION"/> at epoch 1, runs
  /// <paramref name="change"/> in another session, and answers whether the change had to wait for the write. The write
  /// commits and the change rolls back either way, so the next check starts from the same state.
  /// </summary>
  private async Task<bool> _waitsForTheHoldAsync(NpgsqlConnection observer, string hold, string change, CancellationToken cancellationToken) {
    await using var writer = await _openAsync();
    await using var writing = await writer.BeginTransactionAsync(cancellationToken);
    await _holdAsync(writer, writing, hold);
    await using var changer = await _openAsync();
    await using var changing = await changer.BeginTransactionAsync(cancellationToken);
    var running = _execAsync(changer, changing, change);
    await _blockedOrDoneAsync(observer, changer.ProcessID, running, cancellationToken);
    var waited = !running.IsCompleted;
    await writing.CommitAsync(cancellationToken);
    await running;
    await changing.RollbackAsync(cancellationToken);
    return waited;
  }

  private static async Task _holdAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string hold) {
    if (hold == "perspective_writer") {
      // The set-based perspective writer's own statement (the driver), bound as it binds it.
      await using var cmd = conn.CreateCommand();
      cmd.Transaction = tx;
      cmd.CommandText = new PerspectiveWriteFence(string.Empty, _ => null).HoldAllStatement();
      cmd.Parameters.Add(new NpgsqlParameter("wb_hp", NpgsqlDbType.Array | NpgsqlDbType.Integer) { Value = _heldPartitions });
      cmd.Parameters.Add(new NpgsqlParameter("wb_he", NpgsqlDbType.Array | NpgsqlDbType.Bigint) { Value = _heldEpochs });
      await cmd.ExecuteNonQueryAsync();
      return;
    }
    var sql = hold switch {
      "wh_hold_partition_epoch" => $"SELECT wh_hold_partition_epoch({PARTITION}, 1)",
      "wh_epoch_is_current" => $"SELECT wh_epoch_is_current({PARTITION}, 1)",
      _ => $$"""SELECT _wh_fence_items('[{"PartitionNumber": {{PARTITION}}, "PartitionEpoch": 1}]'::jsonb, 'test')""",
    };
    await _execAsync(conn, tx, sql);
  }

  private static async Task _previousReleaseHoldAsync(NpgsqlConnection conn, NpgsqlTransaction tx, int partition, long epoch) {
    await using var cmd = conn.CreateCommand();
    cmd.Transaction = tx;
    cmd.CommandText = PREVIOUS_RELEASE_HOLD;
    cmd.Parameters.Add(new NpgsqlParameter("wb_hp", NpgsqlDbType.Array | NpgsqlDbType.Integer) { Value = new[] { partition } });
    cmd.Parameters.Add(new NpgsqlParameter("wb_he", NpgsqlDbType.Array | NpgsqlDbType.Bigint) { Value = new[] { epoch } });
    await cmd.ExecuteNonQueryAsync();
  }

  private static Task _expiredHandoverAsync(NpgsqlConnection conn, params int[] partitions) =>
    _execAsync(conn, null, $"""
      UPDATE wh_partition_ownership SET state = 'handing_over', next_owner_instance_id = '{_b}',
        handover_deadline = clock_timestamp() - INTERVAL '1 minute'
      WHERE partition_number IN ({string.Join(", ", partitions)})
      """);

  /// <summary>Completes once the backend waits on a lock another session holds, or once its call finished.</summary>
  private static async Task _blockedOrDoneAsync(NpgsqlConnection observer, int pid, Task call, CancellationToken cancellationToken) {
    while (!call.IsCompleted) {
      cancellationToken.ThrowIfCancellationRequested();
      if ((bool)(await _scalarAsync(observer, null, $"SELECT cardinality(pg_blocking_pids({pid})) > 0"))!) {
        return;
      }
    }
  }

  private async Task<NpgsqlConnection> _openAsync() {
    var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    return connection;
  }

  private static Task _heartbeatAsync(NpgsqlConnection conn, Guid instance) =>
    _execAsync(conn, null, $"SELECT record_heartbeat('{instance}', 'test', 'test-host', 1, '{SUPPORTS_MAP}'::jsonb, NULL, NULL, NULL, 'pooled')");

  private static async Task<long> _mapModeAsync(NpgsqlConnection conn) {
    await _heartbeatAsync(conn, _a);
    await _heartbeatAsync(conn, _b);
    var epoch = Convert.ToInt64(await _scalarAsync(conn, null,
      $"SELECT epoch FROM wh_vote_role('{PartitionAssignerOptions.ROLE}', '{_a}', INTERVAL '30 seconds', INTERVAL '5 seconds', NULL, ARRAY[1, 0], NULL) v WHERE v.outcome = 'granted'"),
      CultureInfo.InvariantCulture);
    await _publishAsync(conn, epoch, [_a]);
    var answer = (string)(await _scalarAsync(conn, null,
      $"SELECT wh_open_partition_ownership_by_map('{PartitionAssignerOptions.ROLE}', '{_a}', {epoch})"))!;
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

  private static async Task<object?> _scalarAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, string sql) {
    await using var cmd = conn.CreateCommand();
    cmd.Transaction = tx;
    cmd.CommandText = sql;
    return await cmd.ExecuteScalarAsync();
  }

  private static async Task _execAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, string sql) {
    await using var cmd = conn.CreateCommand();
    cmd.Transaction = tx;
    cmd.CommandText = sql;
    await cmd.ExecuteNonQueryAsync();
  }
}
