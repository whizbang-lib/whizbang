// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

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
using Whizbang.Data.Postgres.Notifications;

namespace Whizbang.Data.EFCore.Postgres.Tests.BranchCoverage.DataPostgres;

/// <summary>
/// The temporal stores' remaining decisions against a real server: the claimer and the manager built
/// without the claim-worker and temporal options (a host that never configured either), an interval
/// update that names neither a cron expression nor a start, and a run logged without a note.
/// </summary>
/// <code-under-test>src/Whizbang.Data.Postgres/Notifications/PgScheduleClaimer.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/Notifications/PgScheduleManager.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/Notifications/PgScheduleOccurrenceStore.cs</code-under-test>
[Category("Integration")]
[Category("Shard5")]
public class ScheduleStoreBranchTests : EFCoreTestBase {
  private static readonly Guid _authority = Guid.NewGuid();

  // The options are optional dependencies: a host that registered the temporal engine but never
  // configured the claim worker or the temporal options must still fire due schedules, on the
  // defaults, rather than fail to construct or claim nothing.
  [Test]
  [Timeout(60000)]
  public async Task Claimer_WithoutClaimOrTemporalOptions_FiresADueOwnedScheduleOnTheDefaultsAsync(CancellationToken cancellationToken) {
    var instance = _instance();
    var claimer = new PgScheduleClaimer(
      Options.Create(new WhizbangNotificationOptions { DirectConnectionString = ConnectionString }),
      new ConfigurationBuilder().AddInMemoryCollection([]).Build(),
      instance,
      claimWorkerOptions: null!,
      temporalOptions: null!,
      NullLogger<PgScheduleClaimer>.Instance);
    var stream = Guid.NewGuid();
    await _pinStreamAsync(stream, instance.InstanceId, cancellationToken);
    await _insertDueScheduleAsync(Guid.NewGuid(), stream, "BranchClaimDefaults", cancellationToken);

    var fired = await claimer.ClaimDueSchedulesAsync(100, cancellationToken);

    await Assert.That(fired).IsEqualTo(1);
    await Assert.That(await _countOutboxAsync("BranchClaimDefaults", cancellationToken)).IsEqualTo(1L);
  }

  [Test]
  [Timeout(60000)]
  public async Task Manager_WithoutClaimOrTemporalOptions_TriggersAnOccurrenceOnTheDefaultsAsync(CancellationToken cancellationToken) {
    var manager = _manager(useDefaults: true);
    var handle = await manager.CreateAsync(new ScheduleDefinition {
      EventType = "BranchTriggerDefaults",
      AuthorityPrincipalId = _authority,
      StreamId = Guid.NewGuid(),
      Kind = RecurrenceKind.Interval,
      Interval = TimeSpan.FromHours(1),
      StartAt = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero),
    }, cancellationToken);

    var occurrenceId = await manager.TriggerNowAsync(handle.ScheduleId, cancellationToken);

    await Assert.That(occurrenceId).IsNotNull()
      .Because("the trigger leases its occurrence for the default lease and partitions on the default count");
  }

  // An interval update carries no cron expression and, here, no start: the schedule's next fire is
  // recomputed from the database clock plus the new interval. Sending either as anything but NULL
  // would make the database read a cron expression or a fixed start that the caller never gave.
  [Test]
  [Timeout(60000)]
  public async Task Update_IntervalWithoutCronOrStart_RecomputesFromNowPlusTheIntervalAsync(CancellationToken cancellationToken) {
    var manager = _manager(useDefaults: false);
    var handle = await manager.CreateAsync(new ScheduleDefinition {
      EventType = "BranchUpdateInterval",
      AuthorityPrincipalId = _authority,
      StreamId = Guid.NewGuid(),
      Kind = RecurrenceKind.Cron,
      Cron = "0 9 * * *",
      StartAt = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero),
    }, cancellationToken);

    var result = await manager.UpdateAsync(handle.ScheduleId, new ScheduleUpdate {
      Kind = RecurrenceKind.Interval,
      Interval = TimeSpan.FromHours(2),
    }, cancellationToken: cancellationToken);
    var databaseNow = await _databaseNowAsync(cancellationToken);

    await Assert.That(result).IsNotNull();
    await Assert.That(result!.Value.NextFireAt).IsEqualTo(databaseNow.AddHours(2)).Within(TimeSpan.FromMinutes(1))
      .Because("with no start given, the next fire is the database's now plus the new interval");
    await Assert.That(await _cronAsync(handle.ScheduleId, cancellationToken)).IsNull()
      .Because("an interval schedule carries no cron expression once updated");
  }

  // A run outcome without a note (a plain success) is logged with a NULL message, not an empty one.
  [Test]
  [Timeout(60000)]
  public async Task LogRun_WithoutANote_StoresNoMessageAsync(CancellationToken cancellationToken) {
    var schedule = Guid.NewGuid();
    var occurrence = Guid.NewGuid();
    await _insertDueScheduleAsync(schedule, Guid.NewGuid(), "BranchLogRun", cancellationToken);
    var store = new PgScheduleOccurrenceStore(
      Options.Create(new WhizbangNotificationOptions { DirectConnectionString = ConnectionString }),
      new ConfigurationBuilder().AddInMemoryCollection([]).Build());

    await store.LogRunAsync(schedule, occurrence, status: 1, note: null, cancellationToken);

    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync(cancellationToken);
    await using var cmd = new NpgsqlCommand(
      "SELECT count(*), bool_and(error_message IS NULL) FROM wh_schedule_runs WHERE schedule_id = @p AND occurrence_id = @o", conn);
    cmd.Parameters.AddWithValue("p", schedule);
    cmd.Parameters.AddWithValue("o", occurrence);
    await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
    _ = await reader.ReadAsync(cancellationToken);

    await Assert.That(reader.GetInt64(0)).IsEqualTo(1L);
    await Assert.That(reader.GetBoolean(1)).IsTrue();
  }

  private static ServiceInstanceProvider _instance() =>
    new(Guid.NewGuid(), "branch-temporal-svc", "branch-temporal-host", processId: 1);

  private PgScheduleManager _manager(bool useDefaults) => new(
    Options.Create(new WhizbangNotificationOptions { DirectConnectionString = ConnectionString }),
    new ConfigurationBuilder().AddInMemoryCollection([]).Build(),
    _instance(),
    useDefaults ? null! : Options.Create(new Whizbang.Core.Workers.ClaimWorkerOptions()),
    useDefaults ? null! : Options.Create(new TemporalOptions()),
    NullLogger<PgScheduleManager>.Instance);

  private async Task _pinStreamAsync(Guid streamId, Guid instanceId, CancellationToken cancellationToken) {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync(cancellationToken);
    await using var cmd = new NpgsqlCommand(@"
      INSERT INTO wh_active_streams (stream_id, partition_number, assigned_instance_id, created_at, last_activity_at)
      VALUES (@s, 0, @i, NOW(), NOW())
      ON CONFLICT (stream_id) DO UPDATE SET assigned_instance_id = EXCLUDED.assigned_instance_id;", conn);
    cmd.Parameters.AddWithValue("s", streamId);
    cmd.Parameters.AddWithValue("i", instanceId);
    await cmd.ExecuteNonQueryAsync(cancellationToken);
  }

  // Seeded on the database clock: the due decision compares against NOW() there, not the host clock.
  private async Task _insertDueScheduleAsync(Guid scheduleId, Guid streamId, string eventType, CancellationToken cancellationToken) {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync(cancellationToken);
    await using var cmd = new NpgsqlCommand(@"
      INSERT INTO wh_schedules
        (schedule_id, stream_id, partition_number, recurrence_kind, interval_ms, timezone,
         next_fire_at, occurrence_count, status, event_type, event_data)
      VALUES (@id, @stream, 0, 1, 60000, 'UTC', NOW() - INTERVAL '1 minute', 0, 0, @etype, '{}'::jsonb);", conn);
    cmd.Parameters.AddWithValue("id", scheduleId);
    cmd.Parameters.AddWithValue("stream", streamId);
    cmd.Parameters.AddWithValue("etype", eventType);
    await cmd.ExecuteNonQueryAsync(cancellationToken);
  }

  private async Task<long> _countOutboxAsync(string eventType, CancellationToken cancellationToken) {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync(cancellationToken);
    await using var cmd = new NpgsqlCommand("SELECT count(*) FROM wh_outbox WHERE message_type = @t", conn);
    cmd.Parameters.AddWithValue("t", eventType);
    return (long)(await cmd.ExecuteScalarAsync(cancellationToken))!;
  }

  private async Task<DateTimeOffset> _databaseNowAsync(CancellationToken cancellationToken) {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync(cancellationToken);
    await using var cmd = new NpgsqlCommand("SELECT NOW()", conn);
    var now = (DateTime)(await cmd.ExecuteScalarAsync(cancellationToken))!;
    return new DateTimeOffset(DateTime.SpecifyKind(now, DateTimeKind.Utc), TimeSpan.Zero);
  }

  private async Task<string?> _cronAsync(Guid scheduleId, CancellationToken cancellationToken) {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync(cancellationToken);
    await using var cmd = new NpgsqlCommand("SELECT cron FROM wh_schedules WHERE schedule_id = @p", conn);
    cmd.Parameters.AddWithValue("p", scheduleId);
    var value = await cmd.ExecuteScalarAsync(cancellationToken);
    return value is DBNull ? null : (string?)value;
  }
}
