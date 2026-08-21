using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Notifications;
using Whizbang.Data.Postgres.Notifications;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Integration tests for <see cref="PgScheduleOccurrenceStore"/> (migration 071) — the occurrence-level
/// operations the pre-fire gate needs: defer the SAME in-flight occurrence, log a gate outcome, and write
/// back a refreshed authority snapshot.
/// </summary>
/// <docs>fundamentals/temporal/pre-fire-hook</docs>
[Category("Shard1")]
public class PgScheduleOccurrenceStoreIntegrationTests : EFCoreTestBase {
  private PgScheduleOccurrenceStore _store() {
    var opts = new WhizbangNotificationOptions { DirectConnectionString = ConnectionString };
    var cfg = new ConfigurationBuilder().AddInMemoryCollection([]).Build();
    return new PgScheduleOccurrenceStore(
      Options.Create(opts), cfg, NullLogger<PgScheduleOccurrenceStore>.Instance);
  }

  private async Task _insertOutboxAsync(NpgsqlConnection conn, Guid messageId, Guid instanceId) {
    await using var cmd = new NpgsqlCommand(@"
      INSERT INTO wh_outbox (message_id, message_type, event_data, metadata, status, attempts, created_at,
                             instance_id, lease_expiry)
      VALUES (@id, 'Occ', '{}'::jsonb, '{}'::jsonb, 1, 0, NOW(), @inst, NOW() + INTERVAL '5 min');", conn);
    cmd.Parameters.AddWithValue("id", messageId);
    cmd.Parameters.AddWithValue("inst", instanceId);
    await cmd.ExecuteNonQueryAsync();
  }

  private async Task _insertScheduleAsync(NpgsqlConnection conn, Guid scheduleId, string claims) {
    await using var cmd = new NpgsqlCommand(@"
      INSERT INTO wh_schedules
        (schedule_id, stream_id, recurrence_kind, interval_ms, next_fire_at, status, event_type,
         authority_principal_id, authority_claims)
      VALUES (@id, gen_random_uuid(), 1, 60000, NOW(), 0, 'Occ', gen_random_uuid(), @claims::jsonb);", conn);
    cmd.Parameters.AddWithValue("id", scheduleId);
    cmd.Parameters.AddWithValue("claims", claims);
    await cmd.ExecuteNonQueryAsync();
  }

  [Test]
  public async Task Defer_ReschedulesTheSameMessageAndReleasesLeaseAsync() {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    var message = Guid.NewGuid();
    await _insertOutboxAsync(conn, message, Guid.NewGuid());
    var until = DateTimeOffset.UtcNow.AddHours(2);

    await _store().DeferAsync(message, until);

    await using var cmd = new NpgsqlCommand(
      "SELECT scheduled_for, instance_id IS NULL, lease_expiry IS NULL FROM wh_outbox WHERE message_id = @p", conn);
    cmd.Parameters.AddWithValue("p", message);
    await using var r = await cmd.ExecuteReaderAsync();
    _ = await r.ReadAsync();
    var scheduledFor = new DateTimeOffset(DateTime.SpecifyKind(r.GetDateTime(0), DateTimeKind.Utc), TimeSpan.Zero);

    await Assert.That(scheduledFor).IsEqualTo(until).Within(TimeSpan.FromSeconds(1))
      .Because("defer retries the SAME occurrence later — it is not dropped and not re-created");
    await Assert.That(r.GetBoolean(1)).IsTrue();   // lease released
    await Assert.That(r.GetBoolean(2)).IsTrue();
  }

  [Test]
  public async Task LogRun_AppendsRunRowAsync() {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    var schedule = Guid.NewGuid();
    var occurrence = Guid.NewGuid();
    await _insertScheduleAsync(conn, schedule, """{"roles":["billing"]}""");

    await _store().LogRunAsync(schedule, occurrence, status: 2, note: "skipped by pre-fire hook");

    await using var cmd = new NpgsqlCommand(
      "SELECT status, error_message FROM wh_schedule_runs WHERE schedule_id = @p AND occurrence_id = @o", conn);
    cmd.Parameters.AddWithValue("p", schedule);
    cmd.Parameters.AddWithValue("o", occurrence);
    await using var r = await cmd.ExecuteReaderAsync();
    _ = await r.ReadAsync();

    await Assert.That(r.GetInt16(0)).IsEqualTo((short)2);
    await Assert.That(r.GetString(1)).IsEqualTo("skipped by pre-fire hook");
  }

  [Test]
  public async Task RefreshAuthorityClaims_WritesSnapshotBackAsync() {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    var schedule = Guid.NewGuid();
    await _insertScheduleAsync(conn, schedule, """{"roles":["billing"]}""");

    await _store().RefreshAuthorityClaimsAsync(schedule, """{"roles":["reduced"]}""");

    await using var cmd = new NpgsqlCommand(
      "SELECT authority_claims->'roles'->>0 FROM wh_schedules WHERE schedule_id = @p", conn);
    cmd.Parameters.AddWithValue("p", schedule);
    await Assert.That((string?)await cmd.ExecuteScalarAsync()).IsEqualTo("reduced")
      .Because("subsequent fires must start from the refreshed snapshot, not the stale create-time one");
  }
}
