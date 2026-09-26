using Microsoft.EntityFrameworkCore;
using Npgsql;
using Whizbang.Core.Priority;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// A row the claim leases is a row the claim hands to the drain, whatever band it sits in.
/// </summary>
/// <remarks>
/// <para>
/// The idle band is withheld while a service is busy, and 167 decided that once per poll for both
/// halves of the claim: acquisition (<c>claim_orphaned_inbox</c>) and the re-offer of held streams to
/// the drain (<c>claim_work</c>). The two did not agree. Acquisition gates only unowned idle EVENTS;
/// an idle-band COMMAND, and any idle row whose lease lapsed, is acquired and charged an attempt
/// regardless. The re-offer skipped bucket three entirely unless the band was admitted, and the
/// admission probe looks at idle events only, so a service whose idle work is commands never admits it.
/// </para>
/// <para>
/// The result is a row that is leased, never handed to the drain, never dispatched, and so never
/// reaches the only place the attempt ceiling is enforced. Its lease lapses, the next claim takes it
/// again and charges another attempt, forever: observed as composite commands in the idle band past
/// three hundred attempts, re-leased every lease period, never dead-lettered, while the stuck-row
/// sentinel reported every one of them each maintenance cycle.
/// </para>
/// <para>
/// Each case drives <c>claim_work</c> directly, as <see cref="IdleBandDrainTests"/> does, and
/// simulates "leased but never dispatched" by lapsing the lease between cycles rather than waiting.
/// </para>
/// </remarks>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/170_HeldIdleRowsAreReOffered.sql</code-under-test>
/// <docs>fundamentals/messaging/message-priority#the-idle-band</docs>
[Category("Shard2")]
public class IdleBandHeldRowReOfferTests : EFCoreTestBase {

  /// <summary>The acquisition SQL's own casualty stamp, as a row carries it after a crash.</summary>
  private const string CASUALTY_STAMP =
    "Attempt 3 ended without a reported outcome: lease held by instance 00000000-0000-0000-0000-000000000001 "
    + "expired at 2026-01-01 00:00:00+00 (process terminated mid-dispatch, or the handler outran its lease). "
    + "No dispatch failure was recorded for that attempt.";

  private static async Task<NpgsqlConnection> _openAsync(DbContext ctx) {
    var connection = ctx.Database.GetDbConnection();
    if (connection.State != System.Data.ConnectionState.Open) {
      await connection.OpenAsync();
    }
    return (NpgsqlConnection)connection;
  }

  private static async Task _heartbeatAsync(NpgsqlConnection conn, Guid instance) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
      INSERT INTO wh_service_instances
        (instance_id, service_name, host_name, process_id, last_heartbeat_at, started_at, metadata)
      VALUES (@i, 'HeldReOfferSvc', 'host', 1, NOW(), NOW(), '{}'::jsonb)
      """;
    cmd.Parameters.AddWithValue("i", instance);
    await cmd.ExecuteNonQueryAsync();
  }

  /// <summary>
  /// Seeds one pending COMMAND (is_event = false) and returns its stream id. A non-null
  /// <paramref name="heldBy"/> leaves it leased to that instance with a lease that has already lapsed.
  /// </summary>
  private static async Task<(Guid MessageId, Guid StreamId)> _seedCommandAsync(
      NpgsqlConnection conn, int priority, int attempts, int ageMinutes, string? error, Guid? heldBy) {
    var messageId = Guid.CreateVersion7();
    var streamId = Guid.CreateVersion7();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
      WITH m AS (
        INSERT INTO wh_inbox
          (message_id, handler_name, message_type, event_data, metadata, received_at,
           stream_id, is_event, priority)
        VALUES (@msg, 'TestHandler', 'TestComposite', '{"p": {}}', '{}',
                NOW() - (@age * INTERVAL '1 minute'), @stream, FALSE, @priority)
        RETURNING message_id, stream_id, received_at, priority, is_event
      )
      INSERT INTO wh_inbox_state
        (message_id, stream_id, received_at, priority, is_event, status, attempts,
         partition_number, instance_id, lease_expiry, error, failure_reason)
      SELECT message_id, stream_id, received_at, priority, is_event, 1, @attempts, 0,
             @heldBy, CASE WHEN @heldBy::uuid IS NULL THEN NULL ELSE NOW() - INTERVAL '1 minute' END,
             @error, CASE WHEN @error::text IS NULL THEN 99 ELSE 6 END
      FROM m
      """;
    cmd.Parameters.AddWithValue("msg", messageId);
    cmd.Parameters.AddWithValue("stream", streamId);
    cmd.Parameters.AddWithValue("age", ageMinutes);
    cmd.Parameters.AddWithValue(nameof(priority), priority);
    cmd.Parameters.AddWithValue(nameof(attempts), attempts);
    cmd.Parameters.Add(new NpgsqlParameter("heldBy", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = (object?)heldBy ?? DBNull.Value });
    cmd.Parameters.Add(new NpgsqlParameter("error", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)error ?? DBNull.Value });
    await cmd.ExecuteNonQueryAsync();
    return (messageId, streamId);
  }

  /// <summary>Runs one claim on a BUSY service with the production idle bounds; returns the inbox streams it re-offered.</summary>
  private static async Task<HashSet<Guid>> _claimBusyAsync(NpgsqlConnection conn, Guid instance) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
      SELECT work_stream_id FROM claim_work(
        p_instance_id => @i,
        p_service_name => 'HeldReOfferSvc',
        p_host_name => 'host',
        p_process_id => 1,
        p_max_streams => 100,
        p_partition_count => 10000,
        p_lease_seconds => 300,
        p_max_rows => 100,
        p_idle_settled => FALSE,
        p_idle_trickle_after => INTERVAL '30 minutes',
        p_idle_trickle_slice => 10,
        p_idle_force_after => INTERVAL '4 hours')
      WHERE source = 'inbox' AND work_stream_id IS NOT NULL
      """;
    cmd.Parameters.AddWithValue("i", instance);
    cmd.CommandTimeout = 120;
    var offered = new HashSet<Guid>();
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync()) {
      offered.Add(reader.GetGuid(0));
    }
    return offered;
  }

  /// <summary>What the drain would receive for this stream: the attempts of every row fetch_inbox_batch returns.</summary>
  private static async Task<List<int>> _fetchAttemptsAsync(NpgsqlConnection conn, Guid streamId, Guid instance) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT attempts FROM fetch_inbox_batch(@s, @i, 100, NULL)";
    cmd.Parameters.AddWithValue("s", new[] { streamId });
    cmd.Parameters.AddWithValue("i", instance);
    var attempts = new List<int>();
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync()) {
      attempts.Add(reader.GetInt32(0));
    }
    return attempts;
  }

  /// <summary>Whether <paramref name="instance"/> holds a live lease on the row, and the row's attempts.</summary>
  private static async Task<(bool HeldLive, int Attempts)> _stateAsync(NpgsqlConnection conn, Guid messageId, Guid instance) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
      SELECT COALESCE(instance_id = @i AND lease_expiry > NOW(), FALSE), attempts
      FROM wh_inbox_state WHERE message_id = @m
      """;
    cmd.Parameters.AddWithValue("m", messageId);
    cmd.Parameters.AddWithValue("i", instance);
    await using var reader = await cmd.ExecuteReaderAsync();
    await reader.ReadAsync();
    return (reader.GetBoolean(0), reader.GetInt32(1));
  }

  private static async Task _lapseLeaseAsync(NpgsqlConnection conn, Guid messageId) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "UPDATE wh_inbox_state SET lease_expiry = NOW() - INTERVAL '1 minute' WHERE message_id = @m";
    cmd.Parameters.AddWithValue("m", messageId);
    await cmd.ExecuteNonQueryAsync();
  }

  /// <summary>
  /// The invariant, on the smallest case: a fresh idle-band command on a busy service. Whether or not
  /// the claim should have taken it, once it holds the lease it must hand the stream to the drain.
  /// </summary>
  [Test]
  public async Task WhileBusy_AnIdleBandCommandTheClaimLeases_IsReOfferedToTheDrainAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    var instance = Guid.NewGuid();
    await _heartbeatAsync(conn, instance);
    var (messageId, streamId) = await _seedCommandAsync(
      conn, WorkPriority.IDLE, attempts: 0, ageMinutes: 1, error: null, heldBy: null);

    var offered = await _claimBusyAsync(conn, instance);
    var state = await _stateAsync(conn, messageId, instance);

    var held = state.HeldLive;
    await Assert.That(offered.Contains(streamId)).IsEqualTo(held)
      .Because($"held={held}, attempts={state.Attempts}: a row the claim leases, and charges an attempt, must be "
        + "handed to the drain on the same poll; a leased row that is not re-offered is never fetched, never "
        + "dispatched, and is charged again when its lease lapses");
  }

  /// <summary>
  /// The reported failure, reproduced: an idle-band command already past the default ceiling of ten
  /// attempts, its last attempt ended by a lease lapse (a crash), held by an instance that never
  /// dispatched it. Every cycle the claim takes it again; the drain must receive it every cycle so the
  /// dispatcher can dead-letter it, instead of the row climbing attempts for ever.
  /// </summary>
  [Test]
  public async Task AnIdleBandCommandPastMaxAttempts_ReclaimedAfterItsLeaseLapses_ReachesTheDrainEveryCycleAsync() {
    const int maxInboxAttempts = 10;
    const int cycles = 3;
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    var instance = Guid.NewGuid();
    await _heartbeatAsync(conn, instance);
    // A day old: older than every idle bound, so no reading of the band's rules can justify withholding it.
    var (messageId, streamId) = await _seedCommandAsync(
      conn, WorkPriority.IDLE, attempts: maxInboxAttempts + 1, ageMinutes: 24 * 60, error: CASUALTY_STAMP,
      heldBy: Guid.NewGuid());

    var offeredCycles = 0;
    var fetchedPastCeiling = 0;
    for (var cycle = 0; cycle < cycles; cycle++) {
      var offered = await _claimBusyAsync(conn, instance);
      if (offered.Contains(streamId)) {
        offeredCycles++;
        var fetched = await _fetchAttemptsAsync(conn, streamId, instance);
        if (fetched.Count == 1 && fetched[0] > maxInboxAttempts) {
          fetchedPastCeiling++;
        }
      }
      // The drain never dispatched it (or the process died): the lease lapses, as it does live.
      await _lapseLeaseAsync(conn, messageId);
    }

    var final = await _stateAsync(conn, messageId, instance);
    await Assert.That(offeredCycles).IsEqualTo(cycles)
      .Because($"the claim re-leased this row on every cycle (attempts climbed from {maxInboxAttempts + 1} to "
        + $"{final.Attempts}), so every cycle must hand its stream to the drain; a row leased and never offered "
        + "never reaches the dispatcher's attempt ceiling and is re-leased for ever");
    await Assert.That(fetchedPastCeiling).IsEqualTo(cycles)
      .Because("the drain's fetch must return the row with its attempts past the ceiling, which is the input "
        + "the dispatcher dead-letters on");
  }
}
