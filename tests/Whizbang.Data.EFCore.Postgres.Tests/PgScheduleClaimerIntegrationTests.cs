using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Notifications;
using Whizbang.Core.Observability;
using Whizbang.Core.Temporal;
using Whizbang.Core.Workers;
using Whizbang.Data.Postgres.Notifications;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Integration tests for <see cref="PgScheduleClaimer"/> — the C# adapter over
/// <c>wh_claim_due_schedules</c>: it fires due owned schedules (spawning the occurrence) and returns the
/// count, using the DB clock for the due decision.
/// </summary>
/// <docs>fundamentals/temporal/temporal-engine</docs>
[Category("Shard3")]
public class PgScheduleClaimerIntegrationTests : EFCoreTestBase {
  private (PgScheduleClaimer Claimer, IServiceInstanceProvider Instance) _create() {
    var opts = new WhizbangNotificationOptions { DirectConnectionString = ConnectionString };
    var cfg = new ConfigurationBuilder().AddInMemoryCollection([]).Build();
    var instance = new ServiceInstanceProvider(Guid.NewGuid(), "utest-svc", "utest-host", processId: 1);
    var claimer = new PgScheduleClaimer(
      Options.Create(opts), cfg, instance,
      Options.Create(new ClaimWorkerOptions()),
      Options.Create(new TemporalOptions()),
      NullLogger<PgScheduleClaimer>.Instance);
    return (claimer, instance);
  }

  private async Task _pinStreamAsync(Guid streamId, Guid instanceId) {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    await using var cmd = new NpgsqlCommand(@"
      INSERT INTO wh_active_streams (stream_id, partition_number, assigned_instance_id, created_at, last_activity_at)
      VALUES (@s, 0, @i, NOW(), NOW())
      ON CONFLICT (stream_id) DO UPDATE SET assigned_instance_id = EXCLUDED.assigned_instance_id;", conn);
    cmd.Parameters.AddWithValue("s", streamId);
    cmd.Parameters.AddWithValue("i", instanceId);
    await cmd.ExecuteNonQueryAsync();
  }

  // next_fire_at is computed relative to the DB clock (NOW() + the given interval), NOT the C# host clock.
  // The due decision in wh_claim_due_schedules is `next_fire_at <= NOW()` on the DB clock, so seeding from the
  // host clock would make the test hostage to host↔container clock skew — under sustained load a Docker VM
  // clock can drift minutes behind the host, turning a "1 minute ago (host)" seed into a future time (DB) and
  // the schedule spuriously not-due. Seeding on the same clock the engine compares against is drift-proof.
  private async Task _insertScheduleAsync(Guid streamId, string fireOffset, string eventType) {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    await using var cmd = new NpgsqlCommand(@"
      INSERT INTO wh_schedules
        (schedule_id, stream_id, partition_number, recurrence_kind, interval_ms, timezone,
         next_fire_at, occurrence_count, status, event_type, event_data)
      VALUES (gen_random_uuid(), @stream, 0, 1, 60000, 'UTC', NOW() + @off::interval, 0, 0, @etype, '{}'::jsonb);", conn);
    cmd.Parameters.AddWithValue("stream", streamId);
    cmd.Parameters.AddWithValue("off", fireOffset);
    cmd.Parameters.AddWithValue("etype", eventType);
    await cmd.ExecuteNonQueryAsync();
  }

  private async Task<long> _countOutboxAsync(string eventType) {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    await using var cmd = new NpgsqlCommand("SELECT count(*) FROM wh_outbox WHERE message_type = @t", conn);
    cmd.Parameters.AddWithValue("t", eventType);
    return Convert.ToInt64(await cmd.ExecuteScalarAsync() ?? 0L, CultureInfo.InvariantCulture);
  }

  [Test]
  public async Task ClaimDueSchedules_FiresDueOwnedScheduleAsync() {
    var (claimer, instance) = _create();
    var stream = Guid.NewGuid();
    await _pinStreamAsync(stream, instance.InstanceId);
    await _insertScheduleAsync(stream, "-1 minute", "ClaimerOccA");

    var fired = await claimer.ClaimDueSchedulesAsync(100);

    await Assert.That(fired).IsEqualTo(1);
    await Assert.That(await _countOutboxAsync("ClaimerOccA")).IsEqualTo(1L);
  }

  [Test]
  public async Task ClaimDueSchedules_NothingDue_ReturnsZeroAsync() {
    var (claimer, instance) = _create();
    var stream = Guid.NewGuid();
    await _pinStreamAsync(stream, instance.InstanceId);
    await _insertScheduleAsync(stream, "1 hour", "ClaimerOccFuture");

    var fired = await claimer.ClaimDueSchedulesAsync(100);

    await Assert.That(fired).IsEqualTo(0);
    await Assert.That(await _countOutboxAsync("ClaimerOccFuture")).IsEqualTo(0L);
  }

  /// <summary>
  /// The schedule's fire time as the database holds it, shaped exactly as the claimer returns it.
  /// Read back rather than computed from the host clock: the rows are seeded with NOW() on the DB
  /// clock, and a container VM under load can sit minutes behind the host.
  /// </summary>
  private async Task<DateTimeOffset?> _fireAtForStreamAsync(Guid streamId) {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    await using var cmd = new NpgsqlCommand(
      "SELECT MIN(next_fire_at) FROM wh_schedules WHERE stream_id = @s AND status = 0", conn);
    cmd.Parameters.AddWithValue("s", streamId);
    var value = await cmd.ExecuteScalarAsync();
    return value is null or DBNull
      ? null
      : new DateTimeOffset(DateTime.SpecifyKind((DateTime)value, DateTimeKind.Utc), TimeSpan.Zero);
  }

  [Test]
  public async Task GetNextFireTime_ReturnsTheEarliestOwnedScheduleAsUtcAsync() {
    // This is what arms the precise wake. The worker sleeps until the moment this returns, so a
    // value that is late, or carries the wrong offset, does not fail — it just means the schedule
    // fires whenever the backstop next comes round instead of when it was due.
    var (claimer, instance) = _create();
    var stream = Guid.NewGuid();
    await _pinStreamAsync(stream, instance.InstanceId);
    await _insertScheduleAsync(stream, "1 hour", "NextFireLate");
    await _insertScheduleAsync(stream, "10 minutes", "NextFireSoon");

    var next = await claimer.GetNextFireTimeAsync();

    await Assert.That(next).IsNotNull();
    await Assert.That(next!.Value).IsEqualTo((await _fireAtForStreamAsync(stream))!.Value)
      .Because("arming to anything later than the earliest pending schedule leaves that one to be "
             + "picked up by the backstop, which is the latency this path exists to avoid");
    await Assert.That(next.Value.Offset).IsEqualTo(TimeSpan.Zero)
      .Because("the column is timestamptz and the caller treats the result as an absolute instant; "
             + "a local-kind value would arm the timer off by the host's offset");
  }

  [Test]
  public async Task GetNextFireTime_IgnoresSchedulesOwnedByAnotherInstanceAsync() {
    // Streams are assigned to instances, and a schedule on someone else's stream is not this pod's
    // to fire. Arming for it wakes this pod for work it cannot claim, and it does so EARLIER than
    // its own next schedule — so the wake it really needed gets replaced by one that does nothing.
    var (claimer, instance) = _create();
    var mine = Guid.NewGuid();
    var theirs = Guid.NewGuid();
    await _pinStreamAsync(mine, instance.InstanceId);
    await _pinStreamAsync(theirs, Guid.NewGuid());
    await _insertScheduleAsync(mine, "30 minutes", "NextFireMine");
    await _insertScheduleAsync(theirs, "1 minute", "NextFireTheirs");

    var next = await claimer.GetNextFireTimeAsync();

    await Assert.That(next).IsNotNull();
    await Assert.That(next!.Value).IsEqualTo((await _fireAtForStreamAsync(mine))!.Value)
      .Because("the sooner schedule belongs to another instance; answering with it would arm a "
             + "wake this pod cannot act on and drop the one it could");
  }

  [Test]
  public async Task GetNextFireTime_WithNothingOwned_ReturnsNullAsync() {
    // No owned schedules means no wake to arm, and the worker falls back to its backstop cadence.
    // Returning a value here would arm a timer for a schedule that will never be claimed.
    var (claimer, instance) = _create();
    var theirs = Guid.NewGuid();
    await _pinStreamAsync(theirs, Guid.NewGuid());
    await _insertScheduleAsync(theirs, "5 minutes", "NextFireNotMine");

    await Assert.That(await claimer.GetNextFireTimeAsync()).IsNull()
      .Because("nothing is assigned to this instance, so there is no moment worth waking for");
  }

  [Test]
  public async Task BeforeTheDatabaseIsReachable_BothEntryPointsReportNothingToDoAsync() {
    // A pod can start before its database is resolvable — no connection string yet, no registered
    // data source. Neither entry point may throw: the temporal engine is driven by the doorbell
    // and the backstop, both of which come back on their own once the connection exists. Throwing
    // here would take the host down over a condition that resolves itself.
    var claimer = new PgScheduleClaimer(
      Options.Create(new WhizbangNotificationOptions()),
      new ConfigurationBuilder().AddInMemoryCollection([]).Build(),
      new ServiceInstanceProvider(Guid.NewGuid(), "unwired-svc", "unwired-host", processId: 1),
      Options.Create(new ClaimWorkerOptions()),
      Options.Create(new TemporalOptions()),
      NullLogger<PgScheduleClaimer>.Instance);

    await Assert.That(await claimer.ClaimDueSchedulesAsync(100)).IsEqualTo(0)
      .Because("claiming nothing is the honest answer when there is nowhere to claim from");
    await Assert.That(await claimer.GetNextFireTimeAsync()).IsNull()
      .Because("there is no schedule table to read a fire time out of yet");
  }
}
