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
using Whizbang.Core.Workers;
using Whizbang.Data.Postgres.Notifications;

namespace Whizbang.Data.EFCore.Postgres.Tests.BranchCoverage.SecondPass;

/// <summary>
/// Every outcome of the schedule manager's connection and value decisions in one class, so that one
/// coverage report holds all of them: each operation with no connection configured and with a real
/// database, an update that names a cron expression and a start and one that names neither, and a
/// transition the database accepts and one it refuses.
/// </summary>
/// <code-under-test>src/Whizbang.Data.Postgres/Notifications/PgScheduleManager.cs</code-under-test>
[Category("Integration")]
[Category("Shard5")]
public class PgScheduleManagerAllOutcomesTests : EFCoreTestBase {
  private static readonly Guid _authority = Guid.NewGuid();

  // With nothing configured there is no connection to open: each operation reports "nothing done"
  // in its own way rather than throwing out of a background caller.
  [Test]
  [Timeout(60000)]
  public async Task Operations_WithNoConnectionConfigured_ReportNothingDoneAsync(CancellationToken cancellationToken) {
    var manager = _manager(connectionString: null);
    var scheduleId = Guid.NewGuid();

    await Assert.That(await manager.TriggerNowAsync(scheduleId, cancellationToken)).IsNull();
    await Assert.That(await manager.UpdateAsync(scheduleId, new ScheduleUpdate {
      Kind = RecurrenceKind.Interval,
      Interval = TimeSpan.FromMinutes(5),
    }, cancellationToken: cancellationToken)).IsNull();
    await Assert.That(await manager.PauseAsync(scheduleId, cancellationToken: cancellationToken)).IsFalse();
  }

  // With a database, the same three operations reach it: the trigger spawns an occurrence and the
  // transition is applied.
  [Test]
  [Timeout(60000)]
  public async Task Operations_WithADatabase_ReachItAsync(CancellationToken cancellationToken) {
    var manager = _manager(ConnectionString);
    var handle = await _createIntervalScheduleAsync(manager, "AllOutcomesConnected", cancellationToken);

    var occurrence = await manager.TriggerNowAsync(handle.ScheduleId, cancellationToken);
    var paused = await manager.PauseAsync(handle.ScheduleId, cancellationToken: cancellationToken);

    await Assert.That(occurrence).IsNotNull();
    await Assert.That(paused).IsTrue().Because("an active schedule can be paused");
    await Assert.That(await _statusAsync(handle.ScheduleId, cancellationToken)).IsEqualTo((short)1);
  }

  // A transition the database does not apply (no such schedule) answers false and changes nothing.
  [Test]
  [Timeout(60000)]
  public async Task Transition_OfAnUnknownSchedule_IsRefusedAsync(CancellationToken cancellationToken) {
    var manager = _manager(ConnectionString);

    var canceled = await manager.CancelAsync(Guid.NewGuid(), cancellationToken: cancellationToken);

    await Assert.That(canceled).IsFalse();
  }

  // An update that names a cron expression and a start sends both: the schedule keeps the new
  // expression and its next fire is not before the start.
  [Test]
  [Timeout(60000)]
  public async Task Update_WithCronAndStart_SendsBothAsync(CancellationToken cancellationToken) {
    var manager = _manager(ConnectionString);
    var handle = await _createIntervalScheduleAsync(manager, "AllOutcomesCron", cancellationToken);
    var start = new DateTimeOffset(2031, 3, 1, 0, 0, 0, TimeSpan.Zero);

    var result = await manager.UpdateAsync(handle.ScheduleId, new ScheduleUpdate {
      Kind = RecurrenceKind.Cron,
      Cron = "0 9 * * *",
      StartAt = start,
    }, cancellationToken: cancellationToken);

    await Assert.That(result).IsNotNull();
    await Assert.That(result!.Value.NextFireAt).IsGreaterThanOrEqualTo(start)
      .Because("the start the caller named is the earliest the schedule may fire");
    await Assert.That(await _cronAsync(handle.ScheduleId, cancellationToken)).IsEqualTo("0 9 * * *");
  }

  // An update that names neither sends NULL for both: the stored cron is cleared.
  [Test]
  [Timeout(60000)]
  public async Task Update_WithNeitherCronNorStart_SendsNullForBothAsync(CancellationToken cancellationToken) {
    var manager = _manager(ConnectionString);
    var handle = await _createCronScheduleAsync(manager, "AllOutcomesInterval", cancellationToken);

    var result = await manager.UpdateAsync(handle.ScheduleId, new ScheduleUpdate {
      Kind = RecurrenceKind.Interval,
      Interval = TimeSpan.FromHours(3),
    }, cancellationToken: cancellationToken);

    await Assert.That(result).IsNotNull();
    await Assert.That(await _cronAsync(handle.ScheduleId, cancellationToken)).IsNull();
  }

  // A caller that names the schedule's id gets that id: it is how a caller makes creation idempotent
  // across retries, and a generated id in its place would create a second schedule on every retry.
  [Test]
  [Timeout(60000)]
  public async Task Create_WithACallerChosenId_UsesThatIdAsync(CancellationToken cancellationToken) {
    var manager = _manager(ConnectionString);
    var chosen = Guid.NewGuid();

    var handle = await manager.CreateAsync(new ScheduleDefinition {
      ScheduleId = chosen,
      EventType = "AllOutcomesChosenId",
      AuthorityPrincipalId = _authority,
      StreamId = Guid.NewGuid(),
      Kind = RecurrenceKind.Interval,
      Interval = TimeSpan.FromHours(1),
      CatchUpLookback = TimeSpan.FromMinutes(30),
      StartAt = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero),
    }, cancellationToken);

    await Assert.That(handle.ScheduleId).IsEqualTo(chosen);
    await Assert.That(await _statusAsync(chosen, cancellationToken)).IsEqualTo((short)0)
      .Because("the row is stored under the caller's id, active");
  }

  private static Task<ScheduleHandle> _createIntervalScheduleAsync(
      PgScheduleManager manager, string eventType, CancellationToken cancellationToken) =>
    manager.CreateAsync(new ScheduleDefinition {
      EventType = eventType,
      AuthorityPrincipalId = _authority,
      StreamId = Guid.NewGuid(),
      Kind = RecurrenceKind.Interval,
      Interval = TimeSpan.FromHours(1),
      StartAt = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero),
    }, cancellationToken);

  private static Task<ScheduleHandle> _createCronScheduleAsync(
      PgScheduleManager manager, string eventType, CancellationToken cancellationToken) =>
    manager.CreateAsync(new ScheduleDefinition {
      EventType = eventType,
      AuthorityPrincipalId = _authority,
      StreamId = Guid.NewGuid(),
      Kind = RecurrenceKind.Cron,
      Cron = "0 9 * * *",
      StartAt = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero),
    }, cancellationToken);

  private static PgScheduleManager _manager(string? connectionString) => new(
    Options.Create(new WhizbangNotificationOptions { DirectConnectionString = connectionString }),
    new ConfigurationBuilder().Build(),
    new ServiceInstanceProvider(Guid.NewGuid(), "all-outcomes-svc", "all-outcomes-host", processId: 1),
    Options.Create(new ClaimWorkerOptions()),
    Options.Create(new TemporalOptions()),
    NullLogger<PgScheduleManager>.Instance);

  private async Task<short> _statusAsync(Guid scheduleId, CancellationToken cancellationToken) {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync(cancellationToken);
    await using var cmd = new NpgsqlCommand("SELECT status FROM wh_schedules WHERE schedule_id = @p", conn);
    cmd.Parameters.AddWithValue("p", scheduleId);
    return Convert.ToInt16(await cmd.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture);
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
