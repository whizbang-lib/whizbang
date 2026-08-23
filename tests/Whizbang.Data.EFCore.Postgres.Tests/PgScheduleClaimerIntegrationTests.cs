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
}
