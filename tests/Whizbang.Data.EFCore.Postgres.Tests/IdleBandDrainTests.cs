using Microsoft.EntityFrameworkCore;
using Npgsql;
using Whizbang.Core.Priority;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// The idle band's four behaviours, measured against the deployed claim rather than described.
/// </summary>
/// <remarks>
/// <para>
/// The band exists because some work is work nobody waits for, and the service should not spend a
/// busy moment on it. A band that is withheld is a band that can starve, so withholding is bounded
/// by TIME and never by the service happening to go quiet: past the trickle bound a busy service
/// takes a slice anyway, and past the force bound it drains the band at full width however busy it
/// is. Those two bounds, the quiet drain, and the withholding itself are the four cases here.
/// </para>
/// <para>
/// Each case drives <c>claim_work</c> directly with explicit bounds rather than waiting out the
/// real thirty minutes and four hours: the ages are made by writing <c>received_at</c> into the
/// past, which is what the function measures.
/// </para>
/// </remarks>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/167_IdleBandIsWithheldWhileBusy.sql</code-under-test>
/// <docs>fundamentals/messaging/message-priority#the-idle-band</docs>
[Category("Shard2")]
public class IdleBandDrainTests : EFCoreTestBase {

  private static async Task<NpgsqlConnection> _openAsync(DbContext ctx) {
    var connection = ctx.Database.GetDbConnection();
    if (connection.State != System.Data.ConnectionState.Open) {
      await connection.OpenAsync();
    }
    return (NpgsqlConnection)connection;
  }

  /// <summary>Registers the instance, so the claim ranks it rather than refusing to lease.</summary>
  private static async Task _heartbeatAsync(NpgsqlConnection conn, Guid instance) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
      INSERT INTO wh_service_instances
        (instance_id, service_name, host_name, process_id, last_heartbeat_at, started_at, metadata)
      VALUES (@i, 'IdleBandSvc', 'host', 1, NOW(), NOW(), '{}'::jsonb)
      """;
    cmd.Parameters.AddWithValue("i", instance);
    await cmd.ExecuteNonQueryAsync();
  }

  /// <summary>
  /// Seeds unowned pending inbox work at one priority, aged <paramref name="ageMinutes"/> minutes.
  /// </summary>
  private static async Task _seedAsync(NpgsqlConnection conn, int priority, int rows, int ageMinutes) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
      WITH m AS (
        INSERT INTO wh_inbox
          (message_id, handler_name, message_type, event_data, metadata, received_at,
           stream_id, is_event, priority)
        SELECT gen_random_uuid(), 'TestHandler', 'TestEvent', '{"p": {}}', '{}',
               NOW() - (@age * INTERVAL '1 minute'), gen_random_uuid(), TRUE, @priority
        FROM generate_series(1, @rows) g
        RETURNING message_id, stream_id, received_at, priority, is_event
      )
      INSERT INTO wh_inbox_state
        (message_id, stream_id, received_at, priority, is_event, status, attempts,
         partition_number, instance_id, lease_expiry)
      SELECT message_id, stream_id, received_at, priority, is_event, 0, 0, 0, NULL, NULL
      FROM m
      """;
    cmd.Parameters.AddWithValue(nameof(priority), priority);
    cmd.Parameters.AddWithValue(nameof(rows), rows);
    cmd.Parameters.AddWithValue("age", ageMinutes);
    await cmd.ExecuteNonQueryAsync();
    await using var analyze = conn.CreateCommand();
    analyze.CommandText = "ANALYZE wh_inbox, wh_inbox_state";
    await analyze.ExecuteNonQueryAsync();
  }

  /// <summary>How many idle-band rows this instance now holds a live lease on.</summary>
  private static async Task<long> _idleHeldAsync(NpgsqlConnection conn, Guid instance) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = $"""
      SELECT count(*) FROM wh_inbox_state
      WHERE instance_id = @i AND lease_expiry > NOW() AND priority > {WorkPriority.BACKGROUND_BAND_END}
      """;
    cmd.Parameters.AddWithValue("i", instance);
    return (long)(await cmd.ExecuteScalarAsync() ?? 0L);
  }

  /// <summary>Runs one claim with the idle bounds stated explicitly.</summary>
  private static async Task _claimAsync(
    NpgsqlConnection conn, Guid instance, bool settled, string trickleAfter, int slice, string forceAfter) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = $"""
      SELECT count(*) FROM claim_work(
        p_instance_id => @i,
        p_service_name => 'IdleBandSvc',
        p_host_name => 'host',
        p_process_id => 1,
        p_max_streams => 100,
        p_partition_count => 10000,
        p_lease_seconds => 300,
        p_max_rows => 100,
        p_idle_settled => @settled,
        p_idle_trickle_after => INTERVAL '{trickleAfter}',
        p_idle_trickle_slice => @slice,
        p_idle_force_after => INTERVAL '{forceAfter}')
      """;
    cmd.Parameters.AddWithValue("i", instance);
    cmd.Parameters.AddWithValue(nameof(settled), settled);
    cmd.Parameters.AddWithValue(nameof(slice), slice);
    cmd.CommandTimeout = 120;
    await cmd.ExecuteScalarAsync();
  }

  /// <summary>
  /// A busy service leaves fresh idle work alone. This is the band's whole purpose, and the case
  /// every other one here is a bounded exception to.
  /// </summary>
  [Test]
  public async Task WhileBusy_FreshIdleWork_IsNotClaimedAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    var instance = Guid.NewGuid();
    await _heartbeatAsync(conn, instance);
    await _seedAsync(conn, WorkPriority.IDLE, rows: 40, ageMinutes: 1);

    await _claimAsync(conn, instance, settled: false, trickleAfter: "30 minutes", slice: 10, forceAfter: "4 hours");

    await Assert.That(await _idleHeldAsync(conn, instance)).IsEqualTo(0)
      .Because("the service is busy and this work is one minute old: nothing is waiting on it, so it waits");
  }

  /// <summary>A settled service drains the band at full width, which is the point of the band.</summary>
  [Test]
  public async Task WhenSettled_TheBandDrainsAtFullWidthAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    var instance = Guid.NewGuid();
    await _heartbeatAsync(conn, instance);
    await _seedAsync(conn, WorkPriority.IDLE, rows: 40, ageMinutes: 1);

    await _claimAsync(conn, instance, settled: true, trickleAfter: "30 minutes", slice: 10, forceAfter: "4 hours");

    await Assert.That(await _idleHeldAsync(conn, instance)).IsGreaterThan(10)
      .Because("a settled service drains the band at full width, not at the trickle slice");
  }

  /// <summary>
  /// Past the trickle bound a busy service takes a slice, and only a slice. "Never more than this
  /// long without progress" is the guarantee; a burst would be a different one.
  /// </summary>
  [Test]
  public async Task PastTheTrickleBound_ABusyServiceTakesASliceAndOnlyASliceAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    var instance = Guid.NewGuid();
    await _heartbeatAsync(conn, instance);
    await _seedAsync(conn, WorkPriority.IDLE, rows: 40, ageMinutes: 45);

    await _claimAsync(conn, instance, settled: false, trickleAfter: "30 minutes", slice: 10, forceAfter: "4 hours");

    var held = await _idleHeldAsync(conn, instance);
    await Assert.That(held).IsGreaterThan(0)
      .Because("45 minutes is past the 30-minute trickle bound, so a busy service must make some progress");
    await Assert.That(held).IsLessThanOrEqualTo(10)
      .Because("a trickle is bounded by its slice; taking the backlog would make the bound a burst");
  }

  /// <summary>
  /// Past the force bound the band drains at full width however busy the service is. This is the
  /// floor that makes withholding safe: a service that never goes quiet still empties the band.
  /// </summary>
  [Test]
  public async Task PastTheForceBound_ABusyServiceDrainsAtFullWidthAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    var instance = Guid.NewGuid();
    await _heartbeatAsync(conn, instance);
    await _seedAsync(conn, WorkPriority.IDLE, rows: 40, ageMinutes: 5 * 60);

    await _claimAsync(conn, instance, settled: false, trickleAfter: "30 minutes", slice: 10, forceAfter: "4 hours");

    await Assert.That(await _idleHeldAsync(conn, instance)).IsGreaterThan(10)
      .Because("five hours is past the four-hour force bound: the band drains at full width however busy the "
        + "service is, or a service that never goes quiet never runs its audit at all");
  }

  /// <summary>
  /// Urgent work is never withheld by any of this. The gate applies to bucket three alone, and a
  /// regression that gated the wrong bucket would be the worst defect this change could cause.
  /// </summary>
  [Test]
  public async Task TheGate_AppliesToTheIdleBandAlone_NotToOrdinaryWorkAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    var instance = Guid.NewGuid();
    await _heartbeatAsync(conn, instance);
    await _seedAsync(conn, WorkPriority.INTERACTIVE, rows: 5, ageMinutes: 0);
    await _seedAsync(conn, WorkPriority.BACKGROUND, rows: 5, ageMinutes: 0);
    await _seedAsync(conn, WorkPriority.IDLE, rows: 5, ageMinutes: 0);

    await _claimAsync(conn, instance, settled: false, trickleAfter: "30 minutes", slice: 10, forceAfter: "4 hours");

    await using var cmd = conn.CreateCommand();
    cmd.CommandText = $"""
      SELECT count(*) FROM wh_inbox_state
      WHERE instance_id = @i AND lease_expiry > NOW() AND priority <= {WorkPriority.BACKGROUND_BAND_END}
      """;
    cmd.Parameters.AddWithValue("i", instance);
    var ordinaryHeld = (long)(await cmd.ExecuteScalarAsync() ?? 0L);

    await Assert.That(ordinaryHeld).IsEqualTo(10)
      .Because("interactive and background work is claimed exactly as before; only the idle band is withheld");
    await Assert.That(await _idleHeldAsync(conn, instance)).IsEqualTo(0)
      .Because("and the idle band, fresh and on a busy service, is the one thing left behind");
  }
}
