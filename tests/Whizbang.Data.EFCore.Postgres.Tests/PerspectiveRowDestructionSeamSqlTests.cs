#pragma warning disable CA1707

using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lifecycle;
using Whizbang.Core.Messaging;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// The pre-destruction seam's SQL substrate: holds postpone eviction in BOTH row sweeps, the
/// collect function offers exactly what the sweeps would destroy (predicate parity, including the
/// acknowledgement gate on the expiry side and its absence on the cap side), the failure ladder
/// holds-then-forces-or-keeps, stale holds self-clean, and the acknowledgement API un-gates
/// enforcement after the backlog preview.
/// </summary>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/111_PerspectiveRowDestructionSeam.sql</code-under-test>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/EFCoreWorkCoordinator.cs</code-under-test>
[Category("Integration")]
[NotInParallel("EFCorePostgresTests")]
[Category("Shard4")]
public class PerspectiveRowDestructionSeamSqlTests : EFCoreTestBase {
  private const string TABLE = "wh_per_seam_guarded";
  private const string CLR_TYPE = "TestApp.SeamGuardedModel";

  private IWorkCoordinator _coordinator(WorkCoordinationDbContext ctx) =>
    new EFCoreWorkCoordinator<WorkCoordinationDbContext>(
      ctx, Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions());

  /// <summary>
  /// Creates the perspective table and an enrolled registry row. Acknowledgement deliberately NOT
  /// seeded — tests that need it go through the coordinator API, which is its first real coverage.
  /// </summary>
  private static async Task _arrangeAsync(
      NpgsqlConnection conn, int? ttlSeconds = 3600, int? capPerScope = null, bool acknowledged = false) {
    await using var ddl = new NpgsqlCommand($@"
      DROP TABLE IF EXISTS {TABLE};
      CREATE TABLE {TABLE} (
        id UUID NOT NULL PRIMARY KEY,
        data JSONB NOT NULL, metadata JSONB NOT NULL, scope JSONB NOT NULL,
        created_at TIMESTAMPTZ NOT NULL, updated_at TIMESTAMPTZ NOT NULL,
        sys_created_at TIMESTAMPTZ, sys_updated_at TIMESTAMPTZ,
        expires_at TIMESTAMPTZ, version INTEGER NOT NULL);
      DELETE FROM wh_perspective_row_hold WHERE table_name = '{TABLE}';
      DELETE FROM wh_perspective_registry WHERE clr_type_name = '{CLR_TYPE}';
      INSERT INTO wh_perspective_registry
        (clr_type_name, table_name, schema_json, schema_hash, service_name,
         row_retention_enrolled, retention_enforcement_acknowledged, row_ttl_seconds, row_cap_per_scope, row_cap_scope_key)
      VALUES ('{CLR_TYPE}', '{TABLE}', '{{}}'::jsonb, 'h', 'svc',
              TRUE, {(acknowledged ? "TRUE" : "FALSE")},
              {(ttlSeconds is null ? "NULL" : ttlSeconds.Value.ToString(System.Globalization.CultureInfo.InvariantCulture))},
              {(capPerScope is null ? "NULL" : capPerScope.Value.ToString(System.Globalization.CultureInfo.InvariantCulture))},
              {(capPerScope is null ? "NULL" : "'u'")});", conn);
    await ddl.ExecuteNonQueryAsync();
  }

  private static async Task _seedRowAsync(NpgsqlConnection conn, Guid id, string user, int idleHours) {
    await using var cmd = new NpgsqlCommand($@"
      INSERT INTO {TABLE} (id, data, metadata, scope, created_at, updated_at, version)
      VALUES (@id, jsonb_build_object('blobName', @id::text), '{{}}'::jsonb, jsonb_build_object('u', @u),
              NOW() - make_interval(hours => @h), NOW() - make_interval(hours => @h), 1)", conn);
    cmd.Parameters.AddWithValue("id", id);
    cmd.Parameters.AddWithValue("u", user);
    cmd.Parameters.AddWithValue("h", idleHours);
    await cmd.ExecuteNonQueryAsync();
  }

  private static async Task<bool> _survivesAsync(NpgsqlConnection conn, Guid id) {
    await using var cmd = new NpgsqlCommand($"SELECT COUNT(*) FROM {TABLE} WHERE id = @id", conn);
    cmd.Parameters.AddWithValue("id", id);
    return Convert.ToInt64(
      await cmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture) > 0;
  }

  [Test]
  [Timeout(60000)]
  public async Task Hold_BlocksTheExpirySweep_UntilReleasedAsync(CancellationToken cancellationToken) {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync(cancellationToken);
    await _arrangeAsync(conn, ttlSeconds: 3600, acknowledged: true);
    var expired = Guid.NewGuid();
    await _seedRowAsync(conn, expired, "user-1", idleHours: 48);
    await using var ctx = CreateDbContext();
    var coordinator = _coordinator(ctx);

    await coordinator.HoldPerspectiveRowDestructionAsync(
      [new PerspectiveRowRef(TABLE, expired)], DateTimeOffset.UtcNow.AddHours(1), cancellationToken);
    await coordinator.ReapEnrolledPerspectiveRowsAsync(cancellationToken: cancellationToken);
    await Assert.That(await _survivesAsync(conn, expired)).IsTrue()
      .Because("a guard's Defer is durable — the expiry ladder must not take a held row, however expired");

    await coordinator.ReleasePerspectiveRowHoldsAsync([new PerspectiveRowRef(TABLE, expired)], cancellationToken);
    await coordinator.ReapEnrolledPerspectiveRowsAsync(cancellationToken: cancellationToken);
    await Assert.That(await _survivesAsync(conn, expired)).IsFalse()
      .Because("a released row is destroyed by the very next sweep — the hold postpones, never spares");
  }

  [Test]
  [Timeout(60000)]
  public async Task Hold_BlocksTheCapSweep_TooAsync(CancellationToken cancellationToken) {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync(cancellationToken);
    await _arrangeAsync(conn, ttlSeconds: null, capPerScope: 1, acknowledged: true);
    var newest = Guid.NewGuid();
    var overflow = Guid.NewGuid();
    await _seedRowAsync(conn, newest, "user-1", idleHours: 1);
    await _seedRowAsync(conn, overflow, "user-1", idleHours: 10);
    await using var ctx = CreateDbContext();
    var coordinator = _coordinator(ctx);

    await coordinator.HoldPerspectiveRowDestructionAsync(
      [new PerspectiveRowRef(TABLE, overflow)], DateTimeOffset.UtcNow.AddHours(1), cancellationToken);
    await coordinator.ReapPerspectiveRowCapsAsync(cancellationToken: cancellationToken);
    await Assert.That(await _survivesAsync(conn, overflow)).IsTrue()
      .Because("stamping expires_at could never hold the cap sweep — the hold table must, on every eviction path");

    await coordinator.ReleasePerspectiveRowHoldsAsync([new PerspectiveRowRef(TABLE, overflow)], cancellationToken);
    await coordinator.ReapPerspectiveRowCapsAsync(cancellationToken: cancellationToken);
    await Assert.That(await _survivesAsync(conn, overflow)).IsFalse();
    await Assert.That(await _survivesAsync(conn, newest)).IsTrue();
  }

  [Test]
  [Timeout(60000)]
  public async Task Collect_OffersExactlyWhatTheSweepsWouldDestroyAsync(CancellationToken cancellationToken) {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync(cancellationToken);
    // TTL 2h so the 1h-idle cap-overflow row is INSIDE the window — cap is its only eviction path.
    await _arrangeAsync(conn, ttlSeconds: 7200, capPerScope: 1, acknowledged: true);
    var expiredOffered = Guid.NewGuid();
    var expiredHeld = Guid.NewGuid();
    var fresh = Guid.NewGuid();
    var capOverflow = Guid.NewGuid();
    await _seedRowAsync(conn, expiredOffered, "user-1", idleHours: 48);
    await _seedRowAsync(conn, expiredHeld, "user-2", idleHours: 48);
    await _seedRowAsync(conn, fresh, "user-3", idleHours: 0);
    await _seedRowAsync(conn, capOverflow, "user-3", idleHours: 1);
    await using var ctx = CreateDbContext();
    var coordinator = _coordinator(ctx);
    await coordinator.HoldPerspectiveRowDestructionAsync(
      [new PerspectiveRowRef(TABLE, expiredHeld)], DateTimeOffset.UtcNow.AddHours(1), cancellationToken);

    var targets = await coordinator.GetPerspectiveRowsAboutToReapAsync([CLR_TYPE], cancellationToken: cancellationToken);

    var ids = targets.Select(t => t.RowId).ToList();
    await Assert.That(ids).Contains(expiredOffered);
    await Assert.That(ids).DoesNotContain(expiredHeld)
      .Because("a held row is invisible to the sweeps, so the collect must not offer it either — predicate parity");
    var ttlTarget = targets.First(t => t.RowId == expiredOffered);
    await Assert.That(ttlTarget.Reason).IsEqualTo("ttl");
    await Assert.That(ttlTarget.Data.GetProperty("blobName").GetString()).IsEqualTo(expiredOffered.ToString())
      .Because("the payload rides the offering so a guard can find the external resource the row references");
    await Assert.That(ttlTarget.Scope!.Value.GetProperty("u").GetString()).IsEqualTo("user-1");

    // user-3 holds two rows against a cap of 1 — the older is cap overflow.
    var capTarget = targets.FirstOrDefault(t => t.RowId == capOverflow);
    await Assert.That(capTarget).IsNotNull();
    await Assert.That(capTarget!.Reason).IsEqualTo("cap");
    await Assert.That(ids).DoesNotContain(fresh)
      .Because("the newest row per scope is under the cap and inside the window — nothing would destroy it");
  }

  [Test]
  [Timeout(60000)]
  public async Task Collect_HonorsTheAcknowledgementGate_ExactlyLikeTheSweepsAsync(CancellationToken cancellationToken) {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync(cancellationToken);
    await _arrangeAsync(conn, ttlSeconds: 3600, capPerScope: 1, acknowledged: false);
    var expired = Guid.NewGuid();
    var capOverflow = Guid.NewGuid();
    var newest = Guid.NewGuid();
    await _seedRowAsync(conn, expired, "user-1", idleHours: 48);
    await _seedRowAsync(conn, capOverflow, "user-2", idleHours: 10);
    await _seedRowAsync(conn, newest, "user-2", idleHours: 1);
    await using var ctx = CreateDbContext();
    var coordinator = _coordinator(ctx);

    var beforeAck = await coordinator.GetPerspectiveRowsAboutToReapAsync([CLR_TYPE], cancellationToken: cancellationToken);
    await Assert.That(beforeAck).Count().IsEqualTo(0)
      .Because("unacknowledged enforcement destroys NOTHING — since 113 the cap sweep carries the "
             + "same gate as the expiry sweep, and the collect's parity reflects it");

    var backlog = await coordinator.CountPerspectiveRetentionBacklogAsync(CLR_TYPE, cancellationToken);
    await Assert.That(backlog).IsGreaterThanOrEqualTo(1L)
      .Because("the preview is the number an operator reads before acknowledging");

    await coordinator.AcknowledgeRetentionEnforcementAsync(CLR_TYPE, cancellationToken);
    var afterAck = await coordinator.GetPerspectiveRowsAboutToReapAsync([CLR_TYPE], cancellationToken: cancellationToken);
    await Assert.That(afterAck.Where(t => t.Reason == "ttl").Select(t => t.RowId)).Contains(expired)
      .Because("acknowledgement un-gates enforcement — the C# API this feature adds");
    await Assert.That(afterAck.Where(t => t.Reason == "cap").Select(t => t.RowId)).Contains(capOverflow)
      .Because("acknowledgement un-gates BOTH sides now");
  }

  [Test]
  [Timeout(60000)]
  public async Task FailureLadder_HoldsUnderTheCap_ThenPolicyDecidesAsync(CancellationToken cancellationToken) {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync(cancellationToken);
    await _arrangeAsync(conn, ttlSeconds: 3600, acknowledged: true);
    var retried = Guid.NewGuid();
    var kept = Guid.NewGuid();
    var forced = Guid.NewGuid();
    await _seedRowAsync(conn, retried, "user-1", idleHours: 48);
    await _seedRowAsync(conn, kept, "user-2", idleHours: 48);
    await _seedRowAsync(conn, forced, "user-3", idleHours: 48);
    await using var ctx = CreateDbContext();
    var coordinator = _coordinator(ctx);
    var backoff = TimeSpan.FromMinutes(5);

    // All three rows get their guard-failure bookkeeping BEFORE any sweep runs — an unheld
    // expired row is legitimately destroyed by the first sweep, which is not what this test is
    // probing. One failure under the cap; the cap exhausted with RetryThenKeep; one failure with
    // ForceDeleteImmediately.
    var attempt = await coordinator.RecordPerspectiveRowDestructionFailureAsync(
      [new PerspectiveRowRef(TABLE, retried)], backoff, maxRetries: 3, OnDestroyFailure.RetryThenForcedDelete, cancellationToken);
    await Assert.That(attempt).IsEqualTo(1);
    for (var i = 0; i < 5; i++) {
      await coordinator.RecordPerspectiveRowDestructionFailureAsync(
        [new PerspectiveRowRef(TABLE, kept)], backoff, maxRetries: 3, OnDestroyFailure.RetryThenKeep, cancellationToken);
    }
    await coordinator.RecordPerspectiveRowDestructionFailureAsync(
      [new PerspectiveRowRef(TABLE, forced)], backoff, maxRetries: 3, OnDestroyFailure.ForceDeleteImmediately, cancellationToken);

    await coordinator.ReapEnrolledPerspectiveRowsAsync(cancellationToken: cancellationToken);

    await Assert.That(await _survivesAsync(conn, retried)).IsTrue()
      .Because("a failing guard means retry, not fail-open into deleting a row whose resource wasn't cleaned");
    await Assert.That(await _survivesAsync(conn, kept)).IsTrue()
      .Because("RetryThenKeep past the cap is the developer's explicit leak-risk choice — keep forever");
    await Assert.That(await _survivesAsync(conn, forced)).IsFalse()
      .Because("ForceDeleteImmediately is the pre-seam behavior, chosen explicitly");
  }

  [Test]
  [Timeout(60000)]
  public async Task StaleHolds_SelfCleanWhenTheirRowIsGoneAsync(CancellationToken cancellationToken) {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync(cancellationToken);
    await _arrangeAsync(conn, ttlSeconds: 3600, acknowledged: true);
    var ghost = Guid.NewGuid();
    await using var ctx = CreateDbContext();
    var coordinator = _coordinator(ctx);
    await coordinator.HoldPerspectiveRowDestructionAsync(
      [new PerspectiveRowRef(TABLE, ghost)], DateTimeOffset.UtcNow.AddDays(365), cancellationToken);

    await coordinator.ReapEnrolledPerspectiveRowsAsync(cancellationToken: cancellationToken);

    await using var cmd = new NpgsqlCommand(
      "SELECT COUNT(*) FROM wh_perspective_row_hold WHERE table_name = @t AND row_id = @r", conn);
    cmd.Parameters.AddWithValue("t", TABLE);
    cmd.Parameters.AddWithValue("r", ghost);
    var remaining = Convert.ToInt64(
      await cmd.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture);
    await Assert.That(remaining).IsEqualTo(0L)
      .Because("a hold whose row no longer exists holds nothing — the sweep self-cleans it");
  }
}
