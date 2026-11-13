// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Npgsql;
using NpgsqlTypes;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Serialization;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// #2259: a ledger row whose repair was answered is re-checked on the next re-check pass, ahead of rows that carry no new
/// information. The answer stores the redelivered events and stamps the row answered; until the bucket is compared again
/// the row stays open, so the re-check claims a row answered and not re-checked since before any other, whatever its
/// report's age and however recently it was last re-checked before the answer. Once re-checked after the answer it waits
/// like any other row.
/// </summary>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/402_AnsweredRowsAreRecheckedFirst.sql</code-under-test>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/EFCoreWorkCoordinator.cs</code-under-test>
/// <docs>resilience/stream-integrity#idle-ledger-recheck</docs>
[Category("Integration")]
[Category("Shard5")]
public class IntegrityAnsweredRecheckSqlTests : EFCoreTestBase {
  private const string MESSAGE_TYPE = "Upstream.Contracts.OrderPlaced, Upstream.Contracts";
  // Fixed instants, not the host clock: every function under test takes the caller's clock.
  private static readonly DateTimeOffset _t0 = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);
  private static readonly TimeSpan _cooldown = TimeSpan.FromMinutes(60);
  private static readonly TimeSpan _threshold = TimeSpan.FromMinutes(120);
  private readonly List<WorkCoordinationDbContext> _contexts = [];

  [Test]
  public async Task Claim_ARecentlyReportedRowWhoseRepairWasAnswered_IsClaimedBeforeOlderStaleRowsAsync() {
    var coordinator = _coordinator();
    await using var conn = await _openAsync();
    var origin = TrackedGuid.New().Value;
    _ = await _reportAsync(coordinator, conn, origin, _t0);                  // stale, and older than the answered row
    _ = await _reportAsync(coordinator, conn, origin, _t0.AddMinutes(10));   // stale
    var now = _t0.AddDays(1);
    var answered = await _reportAsync(coordinator, conn, origin, now.AddMinutes(-30));   // reported recently: not stale
    await _answerAsync(coordinator, conn, answered, now.AddMinutes(-5));

    var claimed = await _claimAsync(coordinator, origin, now, 1);

    await Assert.That(claimed.Select(c => c.StreamId).ToList()).IsEquivalentTo([answered.StreamId])
      .Because("an answered bucket may already be healed, so it is compared before rows with no new information");
  }

  [Test]
  public async Task Claim_ARowReCheckedBeforeItsAnswer_IsClaimedAgainOnTheNextPassAsync() {
    var coordinator = _coordinator();
    await using var conn = await _openAsync();
    var origin = TrackedGuid.New().Value;
    var answered = await _reportAsync(coordinator, conn, origin, _t0);
    var firstPass = _t0.AddDays(1);
    _ = await _claimAsync(coordinator, origin, firstPass, 10);               // re-checked once, before any answer
    var waiting = await _reportAsync(coordinator, conn, origin, _t0.AddMinutes(1));   // stale and never re-checked
    await _answerAsync(coordinator, conn, answered, firstPass.AddMinutes(10));

    var nextPass = firstPass.AddMinutes(20);   // well inside the threshold since the first re-check
    var claimed = await _claimAsync(coordinator, origin, nextPass, 1);

    await Assert.That(claimed.Select(c => c.StreamId).ToList()).IsEquivalentTo([answered.StreamId])
      .Because("the answer arrived after its last re-check, so that re-check no longer describes the bucket");
    var rest = await _claimAsync(coordinator, origin, nextPass, 10);
    await Assert.That(rest.Select(c => c.StreamId).ToList()).IsEquivalentTo([waiting.StreamId])
      .Because("the pass then fills its bound with the stale rows, oldest report first");
  }

  [Test]
  public async Task Claim_AnAnsweredRowReCheckedSinceItsAnswer_WaitsLikeAnyOtherRowAsync() {
    var coordinator = _coordinator();
    await using var conn = await _openAsync();
    var origin = TrackedGuid.New().Value;
    var now = _t0.AddDays(1);
    var answered = await _reportAsync(coordinator, conn, origin, now.AddMinutes(-30));
    await _answerAsync(coordinator, conn, answered, now.AddMinutes(-5));
    _ = await _claimAsync(coordinator, origin, now, 10);

    var soon = await _claimAsync(coordinator, origin, now.AddMinutes(10), 10);

    await Assert.That(soon).IsEmpty()
      .Because("the answer has been compared once; a row asked about again every tick would flood its origin");
  }

  [Test]
  public async Task Claim_AnsweredRows_ComeOldestAnswerFirst_AndOnlyForTheOriginsAskedAsync() {
    var coordinator = _coordinator();
    await using var conn = await _openAsync();
    var origin = TrackedGuid.New().Value;
    var other = TrackedGuid.New().Value;
    var now = _t0.AddDays(1);
    var later = await _reportAsync(coordinator, conn, origin, now.AddMinutes(-40));
    var earlier = await _reportAsync(coordinator, conn, origin, now.AddMinutes(-30));
    var otherOrigin = await _reportAsync(coordinator, conn, other, now.AddMinutes(-30));
    await _answerAsync(coordinator, conn, earlier, now.AddMinutes(-20));
    await _answerAsync(coordinator, conn, later, now.AddMinutes(-10));
    await _answerAsync(coordinator, conn, otherOrigin, now.AddMinutes(-25));

    var first = await _claimAsync(coordinator, origin, now, 1);
    var second = await _claimAsync(coordinator, origin, now, 10);

    await Assert.That(first.Single().StreamId).IsEqualTo(earlier.StreamId).Because("the longest-waiting answer is compared first");
    await Assert.That(second.Select(c => c.StreamId).ToList()).IsEquivalentTo([later.StreamId])
      .Because("an origin the re-check cannot ask is never claimed, answered or not");
  }

  [Test]
  public async Task Claim_TheAnsweredLane_UsesThePartialIndex_OnAWideLedgerAsync() {
    // The answered-first lane is read on every pass; on a wide ledger it must reach the rows through the partial index,
    // not by scanning every row of the origin.
    await using var conn = await _openAsync();
    var origin = TrackedGuid.New().Value;
    await _executeAsync(conn, """
      INSERT INTO wh_integrity_ledger
        (origin_service_id, tenant_scope, event_type, stream_id, origin_lo, origin_hi, local_lo, local_hi,
         first_seen_at, last_reported_at, last_touched, is_deficit)
      SELECT $1, '', 'Contracts.Wide', gen_random_uuid(), 11, 21, 0, 0, $2, $2, $2, true
      FROM generate_series(1, 20000)
      """, origin, _t0);
    await _executeAsync(conn, "ANALYZE wh_integrity_ledger");

    var plan = await _planAsync(conn, origin);

    await Assert.That(plan).Contains("idx_integrity_ledger_answered_unrechecked")
      .Because($"the answered lane is found through its partial index (plan: {plan})");
  }

  // ===== helpers =====

  private EFCoreWorkCoordinator<WorkCoordinationDbContext> _coordinator() {
    var ctx = CreateDbContext();
    _contexts.Add(ctx);
    return new EFCoreWorkCoordinator<WorkCoordinationDbContext>(ctx, JsonContextRegistry.CreateCombinedOptions());
  }

  [After(Test)]
  public async Task DisposeContextsAsync() {
    foreach (var ctx in _contexts) {
      await ctx.DisposeAsync();
    }
  }

  // Through the interface, as the re-check worker calls it.
  private static Task<IReadOnlyList<IntegrityRepairDrainItem>> _claimAsync(
      IWorkCoordinator coordinator, Guid origin, DateTimeOffset now, int limit) =>
    coordinator.IntegrityClaimLedgerRecheckAsync([origin], now - _threshold, now, limit);

  /// <summary>Reports one deficit bucket of a new stream at <paramref name="reportedAt"/> and returns its key.</summary>
  private static async Task<IntegrityRepairLedger.DivergenceKey> _reportAsync(
      EFCoreWorkCoordinator<WorkCoordinationDbContext> coordinator, NpgsqlConnection conn, Guid origin, DateTimeOffset reportedAt) {
    var type = await _scalarAsync<string>(conn, "SELECT normalize_event_type($1)", MESSAGE_TYPE);
    var key = new IntegrityRepairLedger.DivergenceKey(origin, null, type, TrackedGuid.New().Value);
    _ = await coordinator.IntegrityTryBeginReportBatchAsync(
      origin, [new IntegrityReportObservation(key, 11, 21, 0, 0, IsDeficit: true, DeficitEvents: 1)], reportedAt, _cooldown)
      ?? throw new InvalidOperationException("the report batch failed");
    return key;
  }

  /// <summary>Stores a redelivered event of the bucket's stream and records the bundle that carried it, as the repair
  /// answer path does: the row is stamped answered at <paramref name="at"/>.</summary>
  private static async Task _answerAsync(
      EFCoreWorkCoordinator<WorkCoordinationDbContext> coordinator, NpgsqlConnection conn,
      IntegrityRepairLedger.DivergenceKey key, DateTimeOffset at) {
    var eventId = TrackedGuid.New().Value;
    await _executeAsync(conn, """
      INSERT INTO wh_event_store
        (event_id, stream_id, aggregate_id, aggregate_type, event_type, version, commit_sequence, flags, created_at,
         origin_service_id, origin_commit_sequence)
      VALUES ($1, $2, $2, 'Upstream', normalize_event_type($3), 1, nextval('wh_commit_seq'), 0, NOW(), $4, 15)
      """, eventId, key.StreamId, MESSAGE_TYPE, key.OriginServiceId);
    _ = await coordinator.IntegrityRecordBundleAnswerAsync(
      key.OriginServiceId, [eventId], [15], new RedeliveryAnswerWindow(10, 20), at)
      ?? throw new InvalidOperationException("the bundle answer was not recorded");
    var stamped = await _scalarAsync<bool>(conn,
      "SELECT answered_at = $2 FROM wh_integrity_ledger WHERE stream_id = $1", key.StreamId, at);
    if (!stamped) {
      throw new InvalidOperationException($"precondition: the answer stamps the row answered at {at}");
    }
  }

  private static async Task<string> _planAsync(NpgsqlConnection conn, Guid origin) {
    await using var cmd = conn.CreateCommand();
    // The answered lane's query as the claim function runs it, planned on its own.
    cmd.CommandText = """
      EXPLAIN SELECT l.origin_service_id, l.tenant_scope, l.event_type, l.stream_id
      FROM wh_integrity_ledger l
      WHERE l.origin_service_id = ANY($1)
        AND l.stream_id <> '00000000-0000-0000-0000-000000000000'::uuid
        AND l.answered_at IS NOT NULL
        AND (l.rechecked_at IS NULL OR l.rechecked_at < l.answered_at)
      ORDER BY l.answered_at ASC, l.first_seen_at ASC
      LIMIT 100
      """;
    cmd.Parameters.Add(new NpgsqlParameter { Value = new[] { origin }, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Uuid });
    var lines = new List<string>();
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync()) {
      lines.Add(reader.GetString(0));
    }
    return string.Join("\n", lines);
  }

  private async Task<NpgsqlConnection> _openAsync() {
    var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    return connection;
  }

  private static async Task _executeAsync(NpgsqlConnection conn, string sql, params object[] args) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    foreach (var arg in args) {
      cmd.Parameters.Add(new NpgsqlParameter { Value = arg });
    }
    await cmd.ExecuteNonQueryAsync();
  }

  private static async Task<T> _scalarAsync<T>(NpgsqlConnection conn, string sql, params object[] args) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    foreach (var arg in args) {
      cmd.Parameters.Add(new NpgsqlParameter { Value = arg });
    }
    return (T)(await cmd.ExecuteScalarAsync())!;
  }
}
