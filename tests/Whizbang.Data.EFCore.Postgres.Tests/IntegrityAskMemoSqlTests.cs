// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System;
using System.Threading.Tasks;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// #2349: the consumer remembers, per origin, the state at the last full match: the seal, the origin generation recorded
/// beside it, the origin's last checkpoint position and the highest position this store holds from the origin. While all
/// four are unchanged the window above the seal cannot have changed (only an emission, a receipt or a rewrite changes it),
/// so the audit skips the ask; it still asks once per maximum skip, so a loss on this side with nothing moving is found.
/// </summary>
/// <docs>resilience/stream-integrity#unchanged-origins-are-not-asked</docs>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/459_UnchangedOriginsAreNotAsked.sql</code-under-test>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/EFCoreWorkCoordinator.cs</code-under-test>
[Category("Integration")]
[Category("Shard4")]
public class IntegrityAskMemoSqlTests : EFCoreTestBase {
  private static readonly DateTimeOffset _t0 = new(2026, 11, 16, 12, 0, 0, TimeSpan.Zero);
  private static readonly TimeSpan _hour = TimeSpan.FromHours(1);

  /// <summary>What moves between two audits.</summary>
  public enum Movement {
    /// <summary>The seal advanced (a window above it verified).</summary>
    Seal,
    /// <summary>The origin generation recorded beside the seal changed (the origin rewrote history).</summary>
    Generation,
    /// <summary>A checkpoint announced a higher position (the origin emitted).</summary>
    Checkpoint,
    /// <summary>This store received a later event from the origin.</summary>
    Received,
    /// <summary>The event this store held at the highest position is gone (a local loss at the top).</summary>
    LostAtTheTop,
  }

  private static IWorkCoordinator _coordinator(WorkCoordinationDbContext ctx) =>
    new EFCoreWorkCoordinator<WorkCoordinationDbContext>(ctx, Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions());

  private async Task<NpgsqlConnection> _openAsync() {
    var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    return conn;
  }

  private static async Task _executeAsync(NpgsqlConnection conn, string sql, params (string Name, object Value)[] args) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    foreach (var (name, value) in args) {
      cmd.Parameters.AddWithValue(name, value);
    }
    await cmd.ExecuteNonQueryAsync();
  }

  /// <summary>An origin sealed through <paramref name="seal"/>, a checkpoint at <paramref name="checkpoint"/>, and events
  /// received from it at the given positions.</summary>
  private async Task<Guid> _seedOriginAsync(NpgsqlConnection conn, long seal, long checkpoint, long[] received) {
    var origin = TrackedGuid.New().Value;
    await _executeAsync(conn,
      "INSERT INTO wh_integrity_seals (origin_service_id, sealed_through, updated_at) VALUES (@o, @s, NOW())",
      ("o", origin), ("s", seal));
    await using (var ctx = CreateDbContext()) {
      await _coordinator(ctx).IntegrityRecordOriginCheckpointAsync(origin, "origin-svc", "origin.requests", _t0, checkpoint);
    }
    foreach (var position in received) {
      await _receiveAsync(conn, origin, position);
    }
    return origin;
  }

  private static async Task<Guid> _receiveAsync(NpgsqlConnection conn, Guid origin, long position) {
    var eventId = TrackedGuid.New().Value;
    var stream = TrackedGuid.New().Value;
    await _executeAsync(conn, """
      INSERT INTO wh_event_store (event_id, stream_id, aggregate_id, aggregate_type, event_type, scope, version, commit_sequence,
                                  flags, created_at, origin_service_id, origin_commit_sequence)
      VALUES (@e, @s, @s, 'TestAggregate', 'Contracts.AskMemoProbe', 'null'::jsonb, 1, nextval('wh_commit_seq'), 0,
              NOW() - INTERVAL '2 hours', @o, @p)
      """, ("e", eventId), ("s", stream), ("o", origin), ("p", position));
    await _executeAsync(conn, """
      INSERT INTO wh_event_body (event_id, event_data, metadata) VALUES (@e, '{"seeded":true}'::jsonb, '{}'::jsonb)
      """, ("e", eventId));
    return eventId;
  }

  private async Task<IntegrityAskDecision> _decideAsync(Guid origin, DateTimeOffset now, TimeSpan? maxSkip = null) {
    await using var ctx = CreateDbContext();
    return await _coordinator(ctx).IntegrityDecideOriginAskAsync(origin, now, maxSkip ?? _hour);
  }

  private async Task _recordAsync(Guid origin, bool fullMatch) {
    await using var ctx = CreateDbContext();
    await _coordinator(ctx).IntegrityRecordCompareOutcomeAsync(origin, fullMatch);
  }

  [Test]
  public async Task AfterAFullMatch_AnUnchangedOrigin_IsSkippedUntilTheBound_ThenAskedAgainAsync() {
    await using var conn = await _openAsync();
    var origin = await _seedOriginAsync(conn, seal: 100, checkpoint: 120, received: [40, 80]);

    await Assert.That(await _decideAsync(origin, _t0)).IsEqualTo(IntegrityAskDecision.NothingRemembered)
      .Because("no full match is remembered yet");
    await _recordAsync(origin, fullMatch: true);

    await Assert.That(await _decideAsync(origin, _t0.AddMinutes(5))).IsEqualTo(IntegrityAskDecision.Skip)
      .Because("the seal, the generation, the checkpoint and the highest position held are all as they were at the match");
    await Assert.That(await _decideAsync(origin, _t0.AddMinutes(59))).IsEqualTo(IntegrityAskDecision.Skip);
    await Assert.That(await _decideAsync(origin, _t0.AddMinutes(60))).IsEqualTo(IntegrityAskDecision.BoundReached)
      .Because("an unchanged origin is still asked once per maximum skip, measured from the last ask");
    await Assert.That(await _decideAsync(origin, _t0.AddMinutes(61))).IsEqualTo(IntegrityAskDecision.Skip)
      .Because("the bounded ask is the new last ask; the memo still holds");
  }

  [Test]
  [Arguments(Movement.Seal)]
  [Arguments(Movement.Generation)]
  [Arguments(Movement.Checkpoint)]
  [Arguments(Movement.Received)]
  [Arguments(Movement.LostAtTheTop)]
  public async Task AnyOfTheFourMoving_AsksAsync(Movement movement) {
    await using var conn = await _openAsync();
    var origin = await _seedOriginAsync(conn, seal: 100, checkpoint: 120, received: [40]);
    var top = await _receiveAsync(conn, origin, 80);
    _ = await _decideAsync(origin, _t0);
    await _recordAsync(origin, fullMatch: true);

    switch (movement) {
      case Movement.Seal:
        await using (var ctx = CreateDbContext()) {
          await _coordinator(ctx).AdvanceIntegritySealAsync(origin, 150);
        }
        break;
      case Movement.Generation:
        await _executeAsync(conn, "UPDATE wh_integrity_seals SET origin_generation = origin_generation + 1 WHERE origin_service_id = @o",
          ("o", origin));
        break;
      case Movement.Checkpoint:
        await using (var ctx = CreateDbContext()) {
          await _coordinator(ctx).IntegrityRecordOriginCheckpointAsync(origin, "origin-svc", null, _t0.AddMinutes(1), 121);
        }
        break;
      case Movement.Received:
        _ = await _receiveAsync(conn, origin, 90);
        break;
      case Movement.LostAtTheTop:
        await _executeAsync(conn, "DELETE FROM wh_event_body WHERE event_id = @e", ("e", top));
        await _executeAsync(conn, "DELETE FROM wh_event_store WHERE event_id = @e", ("e", top));
        break;
    }

    await Assert.That(await _decideAsync(origin, _t0.AddMinutes(5))).IsEqualTo(IntegrityAskDecision.Moved)
      .Because($"{movement} moved since the last full match, so the window above the seal may have changed");
  }

  [Test]
  public async Task AMismatch_ForgetsTheMemo_SoTheNextAuditAsksAsync() {
    await using var conn = await _openAsync();
    var origin = await _seedOriginAsync(conn, seal: 100, checkpoint: 120, received: [80]);
    _ = await _decideAsync(origin, _t0);
    await _recordAsync(origin, fullMatch: true);

    await _recordAsync(origin, fullMatch: false);

    await Assert.That(await _decideAsync(origin, _t0.AddMinutes(5))).IsEqualTo(IntegrityAskDecision.NothingRemembered)
      .Because("a window that disagreed is under repair; skipping would leave the repair unverified until the bound");
  }

  [Test]
  public async Task AMatch_WhileThisStoreHoldsEventsAtOrAboveTheSeal_IsNotRememberedAsync() {
    await using var conn = await _openAsync();
    var origin = await _seedOriginAsync(conn, seal: 100, checkpoint: 120, received: [80, 100]);
    _ = await _decideAsync(origin, _t0);

    await _recordAsync(origin, fullMatch: true);

    await Assert.That(await _decideAsync(origin, _t0.AddMinutes(5))).IsEqualTo(IntegrityAskDecision.NothingRemembered)
      .Because("an event held at or above the seal is not yet verified, so the match did not cover everything held");
  }

  [Test]
  public async Task AnOriginWithNoSeal_IsAlwaysAsked_AndGetsNoRowAsync() {
    await using var conn = await _openAsync();
    var origin = TrackedGuid.New().Value;

    await _recordAsync(origin, fullMatch: true);

    await Assert.That(await _decideAsync(origin, _t0)).IsEqualTo(IntegrityAskDecision.NothingRemembered);
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT COUNT(*) FROM wh_integrity_seals WHERE origin_service_id = @o";
    cmd.Parameters.AddWithValue("o", origin);
    await Assert.That((long)(await cmd.ExecuteScalarAsync())!).IsEqualTo(0L)
      .Because("the memo lives beside the seal and never creates one; a seal row is made only by a verified window");
  }

  [Test]
  public async Task TheHighestPositionHeld_IsReadThroughTheOriginLaneIndex_UnderAGenericPlanAsync() {
    await using var conn = await _openAsync();
    // The function's statement, prepared as plpgsql prepares it: the origin is a parameter, so the plan is generic and
    // must still prove the partial index's predicate (origin_service_id IS NOT NULL) from the equality.
    await _executeAsync(conn, "SET enable_seqscan = off");
    await _executeAsync(conn, "SET plan_cache_mode = force_generic_plan");
    await _executeAsync(conn, """
      PREPARE received_max(uuid) AS
      SELECT MAX(es.origin_commit_sequence) FROM wh_event_store es WHERE es.origin_service_id = $1
      """);
    await using var plan = conn.CreateCommand();
    plan.CommandText = "EXPLAIN (COSTS OFF) EXECUTE received_max('00000000-0000-0000-0000-000000000001')";
    var lines = new System.Collections.Generic.List<string>();
    await using (var reader = await plan.ExecuteReaderAsync()) {
      while (await reader.ReadAsync()) {
        lines.Add(reader.GetString(0));
      }
    }
    await Assert.That(string.Join('\n', lines)).Contains("idx_event_store_origin_lane")
      .Because("the decision runs once per origin per audit; it reads the top of one index range, never the event store");
  }
}
