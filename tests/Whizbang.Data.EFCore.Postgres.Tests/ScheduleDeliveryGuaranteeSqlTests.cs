using Npgsql;
using NpgsqlTypes;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Locks the per-schedule delivery-guarantee invariant on the outbox failure path
/// (<c>process_outbox_failures</c>): an <b>at-most-once</b> occurrence is parked terminally on failure
/// (<c>scheduled_for = infinity</c> is never claimable, so it is never redelivered — redelivery is unsafe
/// for non-idempotent scheduled work) while an <b>at-least-once</b> occurrence keeps its normal
/// exponential-backoff retry. Both land a durable <c>wh_schedule_runs</c> Failed row. Plain non-schedule
/// messages must be completely unaffected.
/// </summary>
/// <docs>fundamentals/temporal/temporal-engine</docs>
[Category("Shard2")]
public class ScheduleDeliveryGuaranteeSqlTests : EFCoreTestBase {
  private async Task _insertScheduleAsync(NpgsqlConnection conn, Guid scheduleId, short deliveryGuarantee) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = @"
      INSERT INTO wh_schedules
        (schedule_id, stream_id, recurrence_kind, interval_ms, next_fire_at, status, event_type, delivery_guarantee)
      VALUES (@id, gen_random_uuid(), 1, 60000, NOW(), 0, 'DgOcc', @dg);";
    cmd.Parameters.AddWithValue("id", scheduleId);
    cmd.Parameters.Add(new NpgsqlParameter("dg", NpgsqlDbType.Smallint) { Value = deliveryGuarantee });
    await cmd.ExecuteNonQueryAsync();
  }

  private async Task _insertOutboxAsync(NpgsqlConnection conn, Guid messageId, string metadataJson) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = @"
      INSERT INTO wh_outbox (message_id, message_type, event_data, metadata, status, attempts, created_at)
      VALUES (@id, 'DgOcc', '{}'::jsonb, @md::jsonb, 1, 0, NOW());";
    cmd.Parameters.AddWithValue("id", messageId);
    cmd.Parameters.AddWithValue("md", metadataJson);
    await cmd.ExecuteNonQueryAsync();
  }

  private async Task _failAsync(NpgsqlConnection conn, Guid messageId, string error) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT process_outbox_failures(@f::jsonb, NOW())";
    cmd.Parameters.AddWithValue("f",
      $"[{{\"MessageId\":\"{messageId}\",\"CompletedStatus\":0,\"Error\":\"{error}\",\"FailureReason\":0}}]");
    await cmd.ExecuteNonQueryAsync();
  }

  private async Task<bool> _isClaimableAsync(NpgsqlConnection conn, Guid messageId) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = @"
      SELECT (scheduled_for IS NULL OR scheduled_for <= NOW()) FROM wh_outbox WHERE message_id = @p";
    cmd.Parameters.AddWithValue("p", messageId);
    return (bool)(await cmd.ExecuteScalarAsync() ?? false);
  }

  private async Task<long> _failedRunsAsync(NpgsqlConnection conn, Guid scheduleId) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT count(*) FROM wh_schedule_runs WHERE schedule_id = @p AND status = 1";
    cmd.Parameters.AddWithValue("p", scheduleId);
    return (long)(await cmd.ExecuteScalarAsync() ?? 0L);
  }

  [Test]
  public async Task AtMostOnce_Failure_IsParkedAndNeverRedeliveredAsync() {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    var schedule = Guid.NewGuid();
    var message = Guid.NewGuid();
    await _insertScheduleAsync(conn, schedule, deliveryGuarantee: 1);   // AtMostOnce
    await _insertOutboxAsync(conn, message,
      $"{{\"scheduleId\":\"{schedule}\",\"occurrence\":0,\"deliveryGuarantee\":1}}");

    await _failAsync(conn, message, "boom-amo");

    await Assert.That(await _isClaimableAsync(conn, message)).IsFalse()
      .Because("an at-most-once occurrence must never be redelivered after a failure");
    await Assert.That(await _failedRunsAsync(conn, schedule)).IsEqualTo(1L)
      .Because("the non-retried failure must be durably recorded — that is what makes at-most-once safe");
  }

  [Test]
  public async Task AtLeastOnce_Failure_IsStillRetriedAsync() {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    var schedule = Guid.NewGuid();
    var message = Guid.NewGuid();
    await _insertScheduleAsync(conn, schedule, deliveryGuarantee: 0);   // AtLeastOnce
    await _insertOutboxAsync(conn, message,
      $"{{\"scheduleId\":\"{schedule}\",\"occurrence\":0,\"deliveryGuarantee\":0}}");

    await _failAsync(conn, message, "boom-alo");

    // Backoff puts it in the future (so not claimable right now), but it is NOT parked at infinity.
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT scheduled_for < 'infinity'::timestamptz FROM wh_outbox WHERE message_id = @p";
    cmd.Parameters.AddWithValue("p", message);
    await Assert.That((bool)(await cmd.ExecuteScalarAsync() ?? false)).IsTrue()
      .Because("at-least-once keeps the normal exponential-backoff retry");
    await Assert.That(await _failedRunsAsync(conn, schedule)).IsEqualTo(1L);
  }

  [Test]
  public async Task PlainMessage_Failure_IsUnaffectedAsync() {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    var message = Guid.NewGuid();
    await _insertOutboxAsync(conn, message, $"{{\"id\":\"{message}\"}}");   // no scheduleId

    await _failAsync(conn, message, "boom-plain");

    await using var cmd = conn.CreateCommand();
    cmd.CommandText = @"
      SELECT scheduled_for > NOW() AND scheduled_for < 'infinity'::timestamptz
      FROM wh_outbox WHERE message_id = @p";
    cmd.Parameters.AddWithValue("p", message);
    await Assert.That((bool)(await cmd.ExecuteScalarAsync() ?? false)).IsTrue()
      .Because("non-schedule messages must keep their existing retry behaviour exactly");

    await using var runs = conn.CreateCommand();
    runs.CommandText = "SELECT count(*) FROM wh_schedule_runs WHERE occurrence_id = @p";
    runs.Parameters.AddWithValue("p", message);
    await Assert.That((long)(await runs.ExecuteScalarAsync() ?? 0L)).IsEqualTo(0L)
      .Because("a plain message is not a schedule occurrence and must not create a run row");
  }
}
