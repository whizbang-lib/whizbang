using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// What the adaptive doorbell debounce actually decides, asserted from the state it records rather
/// than by waiting out its windows.
/// </summary>
/// <remarks>
/// <para>
/// The debounce has two axes (137). The TIME axis is a window: while a target instance's found-work
/// watermark is fresher than the window, a ring toward it is suppressed. The VOLUME axis chooses
/// which window: the floor (<c>notify_debounce_floor_ms</c>, 50 ms) normally, escalating to the
/// ceiling (<c>notify_debounce_seconds</c>, 7 s) once <c>rapid_run</c> reaches
/// <c>notify_churn_run</c> (5), where the run advances only while consecutive rings toward the same
/// instance and kind arrive closer together than <c>notify_rapid_gap_ms</c> (100 ms).
/// </para>
/// <para>
/// Measured on a deployed fleet, the ceiling had never engaged once: <c>effective_window_ms</c> sat
/// at the 50 ms floor for every kind and <c>rapid_run</c> peaked at 1 for outbox and inbox and 3 for
/// perspective. The reading was that escalation cannot fire because it needs five rings inside
/// 100 ms while the real gap per target was around twenty seconds, two hundred times wider. These
/// tests separate the two things that reading conflates: whether the mechanism is capable of
/// escalating at all, and whether the traffic it sees ever lets it.
/// </para>
/// <para>
/// Nothing here sleeps and nothing polls. The mechanism reads its own recorded
/// <c>last_attempt_at</c> to compute the gap, so a test can present any gap it likes by writing
/// that column, which is driving the input rather than racing a clock. Every assertion is on what
/// the function wrote to <c>wh_notify_state</c>: the run, the window it chose, and whether it
/// counted the ring as fired or suppressed.
/// </para>
/// </remarks>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/141_NotifyKindSeparateFromPayload.sql</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/137_AdaptiveNotifyDebounce.sql</code-under-test>
[Category("Shard1")]
[Category("Integration")]
public class NotifyDebounceEscalationSqlTests : EFCoreTestBase {
  private const string KIND = "outbox";
  /// <summary>The ceiling the debounce escalates to, in seconds, as the caller passes it.</summary>
  private const int WINDOW_SECONDS = 7;
  /// <summary>Consecutive rapid rings the volume axis needs before it escalates.</summary>
  private const int CHURN_RUN = 5;

  /// <summary>
  /// The mechanism can escalate: presented with five consecutive rapid gaps it reaches the ceiling.
  /// </summary>
  /// <remarks>
  /// This is the half of the production reading that needed checking. If this failed, the debounce
  /// would be broken rather than merely idle, and the conclusion would be a defect instead of a
  /// calibration question.
  /// </remarks>
  [Test]
  [Timeout(600000)]
  public async Task Debounce_FiveConsecutiveRapidGaps_EscalatesToTheCeilingAsync(
      CancellationToken cancellationToken) {
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext, cancellationToken);
    var instance = await _registerLiveInstanceAsync(conn);

    // The first ring creates the row; each later one is presented with a gap inside the rapid
    // threshold by writing last_attempt_at, so the run advances regardless of how fast this runs.
    await _ringAsync(conn, instance);
    for (var i = 0; i < CHURN_RUN; i++) {
      await _setLastAttemptGapAsync(conn, instance, milliseconds: 10);
      await _ringAsync(conn, instance);
    }

    var state = await _readStateAsync(conn, instance);
    await Assert.That(state.RapidRun).IsGreaterThanOrEqualTo(CHURN_RUN)
      .Because($"{CHURN_RUN} consecutive rings inside the rapid gap must advance the run to the churn "
        + "threshold; a run that cannot reach it means the volume axis is unreachable by construction "
        + "rather than merely unused");
    await Assert.That(state.EffectiveWindowMs).IsEqualTo(WINDOW_SECONDS * 1000)
      .Because("at the churn threshold the window has to become the ceiling the caller passed, which is "
        + "the whole purpose of the volume axis: the escalation is what makes suppression wide enough to "
        + "matter under a burst");
  }

  /// <summary>
  /// The traffic a deployed fleet actually produces keeps it at the floor.
  /// </summary>
  /// <remarks>
  /// The other half of the reading, pinned as a test rather than left as an observation. A gap of
  /// twenty seconds is what a fleet was measured at per target; against a 100 ms threshold the run
  /// resets on every ring, so the window never leaves the floor no matter how many rings arrive.
  /// </remarks>
  [Test]
  [Timeout(600000)]
  public async Task Debounce_GapsWiderThanTheRapidThreshold_StayAtTheFloorAsync(
      CancellationToken cancellationToken) {
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext, cancellationToken);
    var instance = await _registerLiveInstanceAsync(conn);

    await _ringAsync(conn, instance);
    for (var i = 0; i < CHURN_RUN * 2; i++) {
      // Twenty seconds: the inter-ring gap per target a fleet was measured at.
      await _setLastAttemptGapAsync(conn, instance, milliseconds: 20_000);
      await _ringAsync(conn, instance);
    }

    var state = await _readStateAsync(conn, instance);
    await Assert.That(state.RapidRun).IsEqualTo(0)
      .Because($"{CHURN_RUN * 2} rings arrived, twice the churn threshold, and the run is still zero "
        + "because each gap exceeded the rapid threshold and reset it; the run counts consecutive rapid "
        + "rings, not rings");
    await Assert.That(state.EffectiveWindowMs).IsLessThan(WINDOW_SECONDS * 1000)
      .Because("the window stays at the floor, which is why a deployed fleet never observed the ceiling: "
        + "the threshold is two hundred times tighter than the traffic, so no number of rings escalates");
  }

  /// <summary>
  /// A ring toward an instance whose watermark was never armed is always fired, never suppressed.
  /// </summary>
  /// <remarks>
  /// "A fire never arms suppression" (131). The watermark is stamped only by <c>claim_work</c> when
  /// an instance finds work, so an instance that has not been draining cannot have a ring swallowed,
  /// however wide the window is. This is what keeps suppression from stranding work, and it is the
  /// second reason the measured suppression rate is low.
  /// </remarks>
  [Test]
  [Timeout(600000)]
  public async Task Debounce_WatermarkNeverArmed_FiresEvenAtTheCeilingAsync(
      CancellationToken cancellationToken) {
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext, cancellationToken);
    var instance = await _registerLiveInstanceAsync(conn);

    // Escalate first, so the window is at its widest and cannot be the reason nothing is suppressed.
    await _ringAsync(conn, instance);
    for (var i = 0; i < CHURN_RUN; i++) {
      await _setLastAttemptGapAsync(conn, instance, milliseconds: 10);
      await _ringAsync(conn, instance);
    }
    await _clearWatermarkAsync(conn, instance);
    var before = await _readStateAsync(conn, instance);

    await _setLastAttemptGapAsync(conn, instance, milliseconds: 10);
    await _ringAsync(conn, instance);

    var after = await _readStateAsync(conn, instance);
    await Assert.That(after.SuppressedCount).IsEqualTo(before.SuppressedCount)
      .Because("an instance whose found-work watermark is null has never reported draining, so its ring "
        + "must fire however wide the window is; suppressing it would strand the work behind a drainer "
        + "that is not running");
    await Assert.That(after.FiredCount).IsGreaterThan(before.FiredCount)
      .Because("the ring has to be counted as fired, or the counters cannot distinguish a suppressed "
        + "ring from one the mechanism never saw");
  }

  /// <summary>
  /// Suppression is reachable: an escalated window plus a fresh watermark swallows a ring.
  /// </summary>
  /// <remarks>
  /// Without this the two tests above would be consistent with a mechanism that never suppresses at
  /// all, and "the debounce is inert" would be unfalsifiable. This is the positive case that makes
  /// the other results mean what they say.
  /// </remarks>
  [Test]
  [Timeout(600000)]
  public async Task Debounce_EscalatedWindowAndFreshWatermark_SuppressesAsync(
      CancellationToken cancellationToken) {
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext, cancellationToken);
    var instance = await _registerLiveInstanceAsync(conn);

    await _ringAsync(conn, instance);
    for (var i = 0; i < CHURN_RUN; i++) {
      await _setLastAttemptGapAsync(conn, instance, milliseconds: 10);
      await _ringAsync(conn, instance);
    }
    await _armWatermarkAsync(conn, instance);
    var before = await _readStateAsync(conn, instance);

    await _setLastAttemptGapAsync(conn, instance, milliseconds: 10);
    await _ringAsync(conn, instance);

    var after = await _readStateAsync(conn, instance);
    await Assert.That(after.SuppressedCount).IsGreaterThan(before.SuppressedCount)
      .Because("a live instance that reported finding work inside the effective window is draining "
        + "already, so the ring is redundant and gets swallowed; if this never happened the debounce "
        + "would not be idle, it would be inoperative, and the two readings above would prove nothing");
  }

  // --- helpers ---

  private readonly record struct DebounceState(
    int RapidRun, int EffectiveWindowMs, long FiredCount, long SuppressedCount);

  private static async Task<NpgsqlConnection> _openAsync(
      WorkCoordinationDbContext dbContext, CancellationToken cancellationToken) {
    var conn = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync(cancellationToken);
    }
    return conn;
  }

  /// <summary>A live target: suppression is only ever considered toward one (130).</summary>
  private static async Task<Guid> _registerLiveInstanceAsync(NpgsqlConnection conn) {
    var instance = Guid.NewGuid();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = @"
      INSERT INTO wh_service_instances
        (instance_id, service_name, host_name, process_id, last_heartbeat_at, started_at, metadata)
      VALUES (@id, 'test-svc', 'test-host', 1, NOW(), NOW(), '{}'::jsonb)";
    cmd.Parameters.AddWithValue("id", instance);
    await cmd.ExecuteNonQueryAsync();
    return instance;
  }

  private static async Task _ringAsync(NpgsqlConnection conn, Guid instance) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT _notify_debounced(@id, @kind, @kind, @window)";
    cmd.Parameters.AddWithValue("id", instance);
    cmd.Parameters.AddWithValue("kind", KIND);
    cmd.Parameters.AddWithValue("window", WINDOW_SECONDS);
    await cmd.ExecuteNonQueryAsync();
  }

  /// <summary>
  /// Presents the next ring with a chosen gap by writing the column the mechanism computes it from.
  /// This is why nothing here sleeps: the gap is an input, so it can be stated rather than waited for.
  /// </summary>
  private static async Task _setLastAttemptGapAsync(
      NpgsqlConnection conn, Guid instance, int milliseconds) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = @"
      UPDATE wh_notify_state
      SET last_attempt_at = NOW() - (@ms * INTERVAL '1 millisecond')
      WHERE instance_id = @id AND payload_kind = @kind";
    cmd.Parameters.AddWithValue("id", instance);
    cmd.Parameters.AddWithValue("kind", KIND);
    cmd.Parameters.AddWithValue("ms", milliseconds);
    await cmd.ExecuteNonQueryAsync();
  }

  /// <summary>The state claim_work leaves when the instance has just found work.</summary>
  private static async Task _armWatermarkAsync(NpgsqlConnection conn, Guid instance) =>
    await _setWatermarkAsync(conn, instance, armed: true);

  /// <summary>The state an instance that has never drained is in.</summary>
  private static async Task _clearWatermarkAsync(NpgsqlConnection conn, Guid instance) =>
    await _setWatermarkAsync(conn, instance, armed: false);

  private static async Task _setWatermarkAsync(NpgsqlConnection conn, Guid instance, bool armed) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = @"
      UPDATE wh_notify_state
      SET last_work_at = CASE WHEN @armed THEN NOW() ELSE NULL END
      WHERE instance_id = @id AND payload_kind = @kind";
    cmd.Parameters.AddWithValue("id", instance);
    cmd.Parameters.AddWithValue("kind", KIND);
    cmd.Parameters.Add(new NpgsqlParameter(nameof(armed), NpgsqlDbType.Boolean) { Value = armed });
    await cmd.ExecuteNonQueryAsync();
  }

  private static async Task<DebounceState> _readStateAsync(NpgsqlConnection conn, Guid instance) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = @"
      SELECT rapid_run, effective_window_ms, fired_count, suppressed_count
      FROM wh_notify_state WHERE instance_id = @id AND payload_kind = @kind";
    cmd.Parameters.AddWithValue("id", instance);
    cmd.Parameters.AddWithValue("kind", KIND);
    await using var reader = await cmd.ExecuteReaderAsync();
    if (!await reader.ReadAsync()) {
      throw new InvalidOperationException(
        "The debounce recorded no state for this target, so there is nothing to assert on. A ring "
        + "must create the row even when it fires.");
    }
    return new DebounceState(
      reader.GetInt32(0), reader.GetInt32(1), reader.GetInt64(2), reader.GetInt64(3));
  }
}
