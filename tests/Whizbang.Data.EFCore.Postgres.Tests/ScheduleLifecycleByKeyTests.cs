// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Notifications;
using Whizbang.Core.Observability;
using Whizbang.Core.Temporal;
using Whizbang.Core.Workers;
using Whizbang.Data.Postgres.Notifications;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// #2586: a keyed schedule's whole lifecycle on Postgres, through the real <see cref="PgScheduleManager"/> and the
/// integrity scheduler's restarts. Create-or-update by key (<c>wh_create_schedule</c>) refreshes every field of the
/// definition and re-activates a canceled row, so a check turned off and on again, or moved to a new cron, ends with one
/// Active row on the newest definition, and the claim fires it at the new time and not the old one.
/// </summary>
/// <code-under-test>src/Whizbang.Data.Postgres/Notifications/PgScheduleManager.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/IntegritySweepScheduler.cs</code-under-test>
/// <docs>resilience/stream-integrity#integrity-check-schedules</docs>
[Category("Integration")]
[Category("Shard2")]
public class ScheduleLifecycleByKeyTests : EFCoreTestBase {
  private const string COMPARE_KEY = "wh-integrity-sweep";
  private const string RECHECK_KEY = "wh-seal-recheck";
  private const string SERVICE_NAME = "lifecycle-svc";
  private const short ACTIVE = 0;
  private const short CANCELED = 3;
  private const int PARTITION_COUNT = 16;

  /// <summary>The integrity scheduler's fixed control stream (<c>IntegritySweepScheduler._sweepStreamId</c>).</summary>
  private static readonly Guid _sweepStreamId = Guid.Parse("0f80d000-5eeb-4a9e-8c01-000000000001");

  private readonly Guid _instanceId = Guid.NewGuid();

  private sealed record StoredSchedule(
    Guid Id, short Status, string? Cron, DateTimeOffset NextFireAt, Guid? Authority, string? EventData, long Version);

  [Test]
  [Timeout(60000)]
  public async Task CancelByKey_ThenCreateWithTheSameKey_IsActiveAgain_OnTheNewDefinitionAsync(CancellationToken cancellationToken) {
    var manager = _manager();
    var key = "lifecycle-" + Guid.NewGuid().ToString("N");
    var first = await manager.CreateAsync(_definition(key, "0 9 * * *", Guid.NewGuid(), """{"v":1}"""), cancellationToken);
    await Assert.That(await manager.CancelByKeyAsync(key, cancellationToken)).IsTrue();
    await Assert.That((await _readAsync(key, cancellationToken)).Status).IsEqualTo(CANCELED);

    var authority = Guid.NewGuid();
    var before = await _dbNowAsync(cancellationToken);
    var second = await manager.CreateAsync(_definition(key, "30 14 * * *", authority, """{"v":2}"""), cancellationToken);
    var stored = await _readAsync(key, cancellationToken);

    await Assert.That(await _countAsync(key, cancellationToken)).IsEqualTo(1)
      .Because("create-or-update by key keeps one row for the key");
    await Assert.That(second.ScheduleId).IsEqualTo(first.ScheduleId);
    await Assert.That(second.WasCreated).IsFalse();
    await Assert.That(stored.Status).IsEqualTo(ACTIVE).Because("a create by key re-activates a canceled schedule");
    await Assert.That(stored.Cron).IsEqualTo("30 14 * * *");
    await Assert.That(stored.Authority).IsEqualTo(authority);
    await Assert.That(stored.EventData).IsEqualTo("""{"v": 2}""");
    await _assertNextFireIsAsync(stored.NextFireAt, hour: 14, minute: 30, after: before);
    await Assert.That(second.NextFireAt).IsEqualTo(stored.NextFireAt);
  }

  [Test]
  [Timeout(60000)]
  public async Task Restarts_OnWithCronA_ThenOff_ThenOnWithCronB_LeaveOneActiveRowOnCronBAsync(CancellationToken cancellationToken) {
    var cronA = "0 2 * * *";
    var cronB = "0 4 * * *";
    var splayedB = IntegritySweepScheduler.SplayCron(cronB, SERVICE_NAME);

    await _startAsync(new StreamIntegrityOptions { FullHistoryCompare = { Enabled = true, Schedule = cronA } }, cancellationToken);
    await Assert.That((await _readAsync(COMPARE_KEY, cancellationToken)).Cron).IsEqualTo(IntegritySweepScheduler.SplayCron(cronA, SERVICE_NAME));

    var offState = await _startAsync(new StreamIntegrityOptions { FullHistoryCompare = { Enabled = false, Schedule = cronA } }, cancellationToken);
    await Assert.That((await _readAsync(COMPARE_KEY, cancellationToken)).Status).IsEqualTo(CANCELED)
      .Because("a check turned off has its stored schedule canceled");
    await Assert.That(offState.CronActive).IsFalse();

    var before = await _dbNowAsync(cancellationToken);
    var onState = await _startAsync(new StreamIntegrityOptions { FullHistoryCompare = { Enabled = true, Schedule = cronB } }, cancellationToken);
    var stored = await _readAsync(COMPARE_KEY, cancellationToken);

    await Assert.That(await _countAsync(COMPARE_KEY, cancellationToken)).IsEqualTo(1);
    await Assert.That(stored.Status).IsEqualTo(ACTIVE).Because("turning the check back on re-activates its schedule");
    await Assert.That(stored.Cron).IsEqualTo(splayedB);
    await _assertNextFireIsAsync(stored.NextFireAt, hour: 4, minute: _minuteOf(splayedB), after: before);
    await Assert.That(onState.CronActive).IsTrue();
  }

  [Test]
  [Timeout(60000)]
  public async Task Restarts_WithTheCronChanged_MoveTheStoredSchedule_AndTheClaimFiresAtTheNewTimeOnlyAsync(
      CancellationToken cancellationToken) {
    // Cron A fires every minute, so its next fire is always before B's, which is twelve hours out: a row left on A's
    // next fire would be claimed one second before B's.
    var dbNow = await _dbNowAsync(cancellationToken);
    var hourB = (dbNow.UtcDateTime.Hour + 12) % 24;
    var cronB = string.Create(CultureInfo.InvariantCulture, $"17 {hourB} * * *");
    await _startAsync(new StreamIntegrityOptions { SealRecheck = { Enabled = true, Schedule = "* * * * *" } }, cancellationToken);
    var onA = await _readAsync(RECHECK_KEY, cancellationToken);

    var state = await _startAsync(new StreamIntegrityOptions { SealRecheck = { Enabled = true, Schedule = cronB } }, cancellationToken);
    var onB = await _readAsync(RECHECK_KEY, cancellationToken);

    await Assert.That(await _countAsync(RECHECK_KEY, cancellationToken)).IsEqualTo(1);
    await Assert.That(onB.Id).IsEqualTo(onA.Id);
    await Assert.That(onB.Status).IsEqualTo(ACTIVE);
    await Assert.That(onB.Cron).IsEqualTo(cronB).Because("an explicit minute is not splayed");
    await _assertNextFireIsAsync(onB.NextFireAt, hourB, minute: 17, after: dbNow);
    await Assert.That(onB.NextFireAt).IsGreaterThan(onA.NextFireAt);
    await Assert.That(state.SealRecheckCronActive).IsTrue();

    await _pinSweepStreamAsync(cancellationToken);
    await Assert.That(await _claimAsync(onA.NextFireAt, cancellationToken)).IsEqualTo(0)
      .Because("the old cron's fire time is gone from the row");
    await Assert.That(await _claimAsync(onB.NextFireAt.AddSeconds(-1), cancellationToken)).IsEqualTo(0);
    await Assert.That(await _claimAsync(onB.NextFireAt, cancellationToken)).IsEqualTo(1)
      .Because("the claim fires the schedule at the new cron's time");
  }

  [Test]
  [Timeout(60000)]
  public async Task Restarts_OnWithACron_ThenOnWithAnEmptySchedule_CancelTheSchedule_AndLeaveTheCounterInChargeAsync(
      CancellationToken cancellationToken) {
    var first = await _startAsync(new StreamIntegrityOptions {
      FullHistoryCompare = { Enabled = true, Schedule = "0 2 * * *" },
      SealRecheck = { Enabled = true, Schedule = "0 3 * * *" },
    }, cancellationToken);
    await Assert.That(first.CronActive).IsTrue();
    await Assert.That(first.SealRecheckCronActive).IsTrue();

    var second = await _startAsync(new StreamIntegrityOptions {
      FullHistoryCompare = { Enabled = true, Schedule = "" },
      SealRecheck = { Enabled = true, Schedule = null },
    }, cancellationToken);

    await Assert.That((await _readAsync(COMPARE_KEY, cancellationToken)).Status).IsEqualTo(CANCELED);
    await Assert.That((await _readAsync(RECHECK_KEY, cancellationToken)).Status).IsEqualTo(CANCELED)
      .Because("a check on with no Schedule runs on the counter alone, so its schedule must not fire as well");
    await Assert.That(second.CronActive).IsFalse();
    await Assert.That(second.SealRecheckCronActive).IsFalse()
      .Because("with no schedule the every-Nth-audit counter runs each check");
  }

  // ── helpers ──────────────────────────────────────────────────────────────────────────────

  /// <summary>One process start: a fresh scheduler and schedule state over the same database and instance.</summary>
  private async Task<IntegritySweepScheduleState> _startAsync(StreamIntegrityOptions options, CancellationToken cancellationToken) {
    var services = new ServiceCollection();
    services.AddSingleton<IScheduleManager>(_manager());
    services.AddSingleton<IServiceInstanceProvider>(_instance());
    var state = new IntegritySweepScheduleState();
    services.AddSingleton(state);
    var scheduler = new IntegritySweepScheduler(
      services.BuildServiceProvider(), Options.Create(options), NullLogger<IntegritySweepScheduler>.Instance);
    await scheduler.StartAsync(cancellationToken);
    return state;
  }

  private ServiceInstanceProvider _instance() => new(_instanceId, SERVICE_NAME, "lifecycle-host", processId: 1);

  private PgScheduleManager _manager() => new(
    Options.Create(new WhizbangNotificationOptions { DirectConnectionString = ConnectionString }),
    new ConfigurationBuilder().Build(),
    _instance(),
    Options.Create(new ClaimWorkerOptions()),
    Options.Create(new TemporalOptions()),
    NullLogger<PgScheduleManager>.Instance);

  private static ScheduleDefinition _definition(string key, string cron, Guid authority, string eventData) => new() {
    Key = key,
    EventType = "LifecycleByKey",
    EventDataJson = eventData,
    AuthorityPrincipalId = authority,
    StreamId = Guid.NewGuid(),
    Kind = RecurrenceKind.Cron,
    Cron = cron,
  };

  private static int _minuteOf(string cron) => int.Parse(cron.Split(' ')[0], CultureInfo.InvariantCulture);

  /// <summary>A daily cron's next fire is the one time at its hour and minute in the day after <paramref name="after"/>.</summary>
  private static async Task _assertNextFireIsAsync(DateTimeOffset next, int hour, int minute, DateTimeOffset after) {
    await Assert.That(next.UtcDateTime.Hour).IsEqualTo(hour);
    await Assert.That(next.UtcDateTime.Minute).IsEqualTo(minute);
    await Assert.That(next.UtcDateTime.Second).IsEqualTo(0);
    await Assert.That(next).IsGreaterThan(after).Because("the next fire is computed afresh from now, not kept from before");
    await Assert.That(next).IsLessThanOrEqualTo(after.AddDays(1).AddMinutes(1));
  }

  private async Task<NpgsqlConnection> _openAsync(CancellationToken cancellationToken) {
    var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync(cancellationToken);
    return conn;
  }

  private async Task<DateTimeOffset> _dbNowAsync(CancellationToken cancellationToken) {
    await using var conn = await _openAsync(cancellationToken);
    await using var cmd = new NpgsqlCommand("SELECT date_trunc('second', NOW())", conn);
    return new DateTimeOffset(DateTime.SpecifyKind((DateTime)(await cmd.ExecuteScalarAsync(cancellationToken))!, DateTimeKind.Utc));
  }

  private async Task<long> _countAsync(string key, CancellationToken cancellationToken) {
    await using var conn = await _openAsync(cancellationToken);
    await using var cmd = new NpgsqlCommand("SELECT count(*) FROM wh_schedules WHERE schedule_key = @k", conn);
    cmd.Parameters.AddWithValue("k", key);
    return (long)(await cmd.ExecuteScalarAsync(cancellationToken))!;
  }

  private async Task<StoredSchedule> _readAsync(string key, CancellationToken cancellationToken) {
    await using var conn = await _openAsync(cancellationToken);
    await using var cmd = new NpgsqlCommand(@"
      SELECT schedule_id, status, cron, next_fire_at, authority_principal_id, event_data::text, version
      FROM wh_schedules WHERE schedule_key = @k", conn);
    cmd.Parameters.AddWithValue("k", key);
    await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
    if (!await reader.ReadAsync(cancellationToken)) {
      throw new InvalidOperationException($"No schedule is stored under '{key}'.");
    }
    return new StoredSchedule(
      reader.GetGuid(0),
      reader.GetInt16(1),
      reader.IsDBNull(2) ? null : reader.GetString(2),
      new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(3), DateTimeKind.Utc)),
      reader.IsDBNull(4) ? null : reader.GetGuid(4),
      reader.IsDBNull(5) ? null : reader.GetString(5),
      reader.GetInt64(6));
  }

  private async Task _pinSweepStreamAsync(CancellationToken cancellationToken) {
    await using var conn = await _openAsync(cancellationToken);
    await using var cmd = new NpgsqlCommand(@"
      INSERT INTO wh_active_streams (stream_id, partition_number, assigned_instance_id, created_at, last_activity_at)
      VALUES (@s, 0, @i, NOW(), NOW())
      ON CONFLICT (stream_id) DO UPDATE SET assigned_instance_id = EXCLUDED.assigned_instance_id", conn);
    cmd.Parameters.AddWithValue("s", _sweepStreamId);
    cmd.Parameters.AddWithValue("i", _instanceId);
    await cmd.ExecuteNonQueryAsync(cancellationToken);
  }

  private async Task<int> _claimAsync(DateTimeOffset now, CancellationToken cancellationToken) {
    await using var conn = await _openAsync(cancellationToken);
    await using var cmd = new NpgsqlCommand(@"
      SELECT count(*) FROM wh_claim_due_schedules(
        p_instance_id => @i, p_lease_expiry => @lease, p_partition_count => @pc, p_limit => 100, p_now => @now)", conn);
    cmd.Parameters.AddWithValue("i", _instanceId);
    cmd.Parameters.Add(new NpgsqlParameter(nameof(now), NpgsqlDbType.TimestampTz) { Value = now });
    cmd.Parameters.Add(new NpgsqlParameter("lease", NpgsqlDbType.TimestampTz) { Value = now.AddMinutes(5) });
    cmd.Parameters.Add(new NpgsqlParameter("pc", NpgsqlDbType.Integer) { Value = PARTITION_COUNT });
    return Convert.ToInt32(await cmd.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
  }
}
