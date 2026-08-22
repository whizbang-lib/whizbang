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
/// Integration tests for <see cref="PgScheduleManager"/> — the C# management API over
/// <c>wh_create_schedule</c> / <c>wh_transition_schedule</c>: create (with id generation + idempotent
/// create-or-update by key), the pause/resume/cancel transitions with optimistic concurrency, and the
/// client-side argument guards.
/// </summary>
/// <docs>fundamentals/temporal/temporal-engine</docs>
[Category("Shard3")]
public class PgScheduleManagerIntegrationTests : EFCoreTestBase {
  private static DateTimeOffset _utc(int y, int mo, int d, int h, int mi) =>
    new(y, mo, d, h, mi, 0, TimeSpan.Zero);

  // Every schedule must name the principal its occurrences run as (explicit, no implicit creator-authority).
  private static readonly Guid _authority = Guid.NewGuid();

  private PgScheduleManager _manager() {
    var opts = new WhizbangNotificationOptions { DirectConnectionString = ConnectionString };
    var cfg = new ConfigurationBuilder().AddInMemoryCollection([]).Build();
    var instance = new ServiceInstanceProvider(Guid.NewGuid(), "utest-svc", "utest-host", processId: 1);
    return new PgScheduleManager(
      Options.Create(opts), cfg, instance,
      Options.Create(new ClaimWorkerOptions()),
      Options.Create(new TemporalOptions()),
      NullLogger<PgScheduleManager>.Instance);
  }

  private async Task<short> _statusAsync(Guid id) {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    await using var cmd = new NpgsqlCommand("SELECT status FROM wh_schedules WHERE schedule_id = @p", conn);
    cmd.Parameters.AddWithValue("p", id);
    return (short)(await cmd.ExecuteScalarAsync() ?? (short)-1);
  }

  // ---- client-side guards (no DB dependency) ----

  [Test]
  public async Task Create_IntervalWithoutInterval_ThrowsAsync() {
    var mgr = _manager();
    await Assert.That(async () => await mgr.CreateAsync(new ScheduleDefinition {
      EventType = "E",
      AuthorityPrincipalId = _authority,
      StreamId = Guid.NewGuid(),
      Kind = RecurrenceKind.Interval
    })).Throws<ArgumentException>();
  }

  [Test]
  public async Task Create_CronWithoutCron_ThrowsAsync() {
    var mgr = _manager();
    await Assert.That(async () => await mgr.CreateAsync(new ScheduleDefinition {
      EventType = "E",
      AuthorityPrincipalId = _authority,
      StreamId = Guid.NewGuid(),
      Kind = RecurrenceKind.Cron
    })).Throws<ArgumentException>();
  }

  [Test]
  public async Task Create_EmptyEventType_ThrowsAsync() {
    var mgr = _manager();
    await Assert.That(async () => await mgr.CreateAsync(new ScheduleDefinition {
      EventType = "  ",
      AuthorityPrincipalId = _authority,
      StreamId = Guid.NewGuid(),
      Kind = RecurrenceKind.OneShot
    })).Throws<ArgumentException>();
  }

  [Test]
  public async Task Create_MissingAuthority_ThrowsAsync() {
    var mgr = _manager();
    await Assert.That(async () => await mgr.CreateAsync(new ScheduleDefinition {
      EventType = "E",
      AuthorityPrincipalId = Guid.Empty,   // no implicit creator-authority fallback
      StreamId = Guid.NewGuid(),
      Kind = RecurrenceKind.OneShot,
      StartAt = _utc(2026, 07, 13, 09, 00)
    })).Throws<ArgumentException>()
      .Because("an occurrence fires with no interactive user, so the run-as principal must be explicit");
  }

  // ---- DB-backed behavior ----

  [Test]
  public async Task Create_AuthorityIsCapturedOnTheScheduleAsync() {
    var mgr = _manager();
    var handle = await mgr.CreateAsync(new ScheduleDefinition {
      EventType = "MgrAuth",
      AuthorityPrincipalId = _authority,
      AuthorityClaimsJson = """{"roles":["billing"]}""",
      StreamId = Guid.NewGuid(),
      Kind = RecurrenceKind.OneShot,
      StartAt = _utc(2026, 07, 13, 09, 00)
    });

    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    await using var cmd = new NpgsqlCommand(
      "SELECT authority_principal_id, authority_claims->'roles'->>0 FROM wh_schedules WHERE schedule_id = @p", conn);
    cmd.Parameters.AddWithValue("p", handle.ScheduleId);
    await using var r = await cmd.ExecuteReaderAsync();
    _ = await r.ReadAsync();

    await Assert.That(r.GetGuid(0)).IsEqualTo(_authority);      // run-as principal captured
    await Assert.That(r.GetString(1)).IsEqualTo("billing");      // claims snapshotted at create
  }

  [Test]
  public async Task Create_Cron_ReturnsHandleAndActivatesAsync() {
    var mgr = _manager();
    var handle = await mgr.CreateAsync(new ScheduleDefinition {
      EventType = "MgrCron",
      AuthorityPrincipalId = _authority,
      StreamId = Guid.NewGuid(),
      Kind = RecurrenceKind.Cron,
      Cron = "0 9 * * *",
      StartAt = _utc(2026, 07, 13, 08, 00)
    });

    await Assert.That(handle.WasCreated).IsTrue();
    await Assert.That(handle.ScheduleId).IsNotEqualTo(Guid.Empty);
    await Assert.That(handle.NextFireAt).IsEqualTo(_utc(2026, 07, 13, 09, 00));
    await Assert.That(await _statusAsync(handle.ScheduleId)).IsEqualTo((short)0);   // Active
  }

  [Test]
  public async Task Create_Interval_GeneratesIdAndComputesNextFireAsync() {
    var mgr = _manager();
    var start = _utc(2026, 07, 13, 09, 00);
    var handle = await mgr.CreateAsync(new ScheduleDefinition {
      EventType = "MgrInt",
      AuthorityPrincipalId = _authority,
      StreamId = Guid.NewGuid(),
      Kind = RecurrenceKind.Interval,
      Interval = TimeSpan.FromMinutes(15),
      StartAt = start
    });

    await Assert.That(handle.ScheduleId).IsNotEqualTo(Guid.Empty);   // id generated
    await Assert.That(handle.NextFireAt).IsEqualTo(start);           // start given => first fire at start
  }

  [Test]
  public async Task Create_IdempotentByKey_UpdatesInPlaceAsync() {
    var mgr = _manager();
    var first = await mgr.CreateAsync(new ScheduleDefinition {
      EventType = "MgrKey",
      AuthorityPrincipalId = _authority,
      StreamId = Guid.NewGuid(),
      Kind = RecurrenceKind.Cron,
      Cron = "0 9 * * *",
      StartAt = _utc(2026, 07, 13, 08, 00),
      Key = "mgr-daily"
    });
    var second = await mgr.CreateAsync(new ScheduleDefinition {
      EventType = "MgrKey",
      AuthorityPrincipalId = _authority,
      StreamId = Guid.NewGuid(),
      Kind = RecurrenceKind.Cron,
      Cron = "0 17 * * *",
      StartAt = _utc(2026, 07, 13, 08, 00),
      Key = "mgr-daily"
    });

    await Assert.That(first.WasCreated).IsTrue();
    await Assert.That(second.WasCreated).IsFalse();
    await Assert.That(second.ScheduleId).IsEqualTo(first.ScheduleId);
    await Assert.That(second.NextFireAt).IsEqualTo(_utc(2026, 07, 13, 17, 00));   // new cron applied
  }

  [Test]
  public async Task Pause_Resume_CancelAsync() {
    var mgr = _manager();
    var handle = await mgr.CreateAsync(new ScheduleDefinition {
      EventType = "MgrTrans",
      AuthorityPrincipalId = _authority,
      StreamId = Guid.NewGuid(),
      Kind = RecurrenceKind.Interval,
      Interval = TimeSpan.FromMinutes(1),
      StartAt = _utc(2026, 07, 13, 09, 00)
    });

    await Assert.That(await mgr.PauseAsync(handle.ScheduleId)).IsTrue();
    await Assert.That(await _statusAsync(handle.ScheduleId)).IsEqualTo((short)1);   // Paused
    await Assert.That(await mgr.ResumeAsync(handle.ScheduleId)).IsTrue();
    await Assert.That(await _statusAsync(handle.ScheduleId)).IsEqualTo((short)0);   // Active
    await Assert.That(await mgr.CancelAsync(handle.ScheduleId)).IsTrue();
    await Assert.That(await _statusAsync(handle.ScheduleId)).IsEqualTo((short)3);   // Cancelled
    await Assert.That(await mgr.PauseAsync(handle.ScheduleId)).IsFalse();           // terminal
  }

  [Test]
  public async Task Pause_WrongVersion_ReturnsFalseAsync() {
    var mgr = _manager();
    var handle = await mgr.CreateAsync(new ScheduleDefinition {
      EventType = "MgrVer",
      AuthorityPrincipalId = _authority,
      StreamId = Guid.NewGuid(),
      Kind = RecurrenceKind.Interval,
      Interval = TimeSpan.FromMinutes(1),
      StartAt = _utc(2026, 07, 13, 09, 00)
    });

    await Assert.That(await mgr.PauseAsync(handle.ScheduleId, expectedVersion: 999)).IsFalse();
    await Assert.That(await _statusAsync(handle.ScheduleId)).IsEqualTo((short)0);   // unchanged
  }

  // ---- trigger-now ----

  private async Task<(DateTimeOffset NextFire, long Count)> _cadenceAsync(Guid id) {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    await using var cmd = new NpgsqlCommand(
      "SELECT next_fire_at, occurrence_count FROM wh_schedules WHERE schedule_id = @p", conn);
    cmd.Parameters.AddWithValue("p", id);
    await using var r = await cmd.ExecuteReaderAsync();
    _ = await r.ReadAsync();
    var next = new DateTimeOffset(DateTime.SpecifyKind(r.GetDateTime(0), DateTimeKind.Utc), TimeSpan.Zero);
    return (next, r.GetInt64(1));
  }

  private async Task<long> _runCountAsync(Guid id, short status) {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    await using var cmd = new NpgsqlCommand(
      "SELECT count(*) FROM wh_schedule_runs WHERE schedule_id = @p AND status = @s", conn);
    cmd.Parameters.AddWithValue("p", id);
    cmd.Parameters.Add(new NpgsqlParameter("s", NpgsqlTypes.NpgsqlDbType.Smallint) { Value = status });
    return (long)(await cmd.ExecuteScalarAsync() ?? 0L);
  }

  [Test]
  public async Task TriggerNow_FiresExtraOccurrence_CadenceUntouchedAsync() {
    var mgr = _manager();
    var future = _utc(2027, 01, 01, 00, 00);
    var handle = await mgr.CreateAsync(new ScheduleDefinition {
      EventType = "MgrTrig",
      AuthorityPrincipalId = _authority,
      StreamId = Guid.NewGuid(),
      Kind = RecurrenceKind.Interval,
      Interval = TimeSpan.FromHours(1),
      StartAt = future
    });

    var occurrenceId = await mgr.TriggerNowAsync(handle.ScheduleId);

    await Assert.That(occurrenceId).IsNotNull();
    await Assert.That(await _runCountAsync(handle.ScheduleId, 3)).IsEqualTo(1L);   // TriggeredEarly run
    var (nextFire, count) = await _cadenceAsync(handle.ScheduleId);
    await Assert.That(nextFire).IsEqualTo(future);   // cadence untouched
    await Assert.That(count).IsEqualTo(0L);          // did not consume an occurrence slot
  }

  [Test]
  public async Task TriggerNow_TerminalSchedule_ReturnsNullAsync() {
    var mgr = _manager();
    var handle = await mgr.CreateAsync(new ScheduleDefinition {
      EventType = "MgrTrigTerm",
      AuthorityPrincipalId = _authority,
      StreamId = Guid.NewGuid(),
      Kind = RecurrenceKind.Interval,
      Interval = TimeSpan.FromHours(1),
      StartAt = _utc(2027, 01, 01, 00, 00)
    });
    _ = await mgr.CancelAsync(handle.ScheduleId);

    await Assert.That(await mgr.TriggerNowAsync(handle.ScheduleId)).IsNull();
  }

  // ---- update ----

  [Test]
  public async Task Update_RecomputesNextFireAsync() {
    var mgr = _manager();
    var handle = await mgr.CreateAsync(new ScheduleDefinition {
      EventType = "MgrUpd",
      AuthorityPrincipalId = _authority,
      StreamId = Guid.NewGuid(),
      Kind = RecurrenceKind.Cron,
      Cron = "0 9 * * *",
      StartAt = _utc(2026, 07, 13, 08, 00)
    });

    var result = await mgr.UpdateAsync(handle.ScheduleId, new ScheduleUpdate {
      Kind = RecurrenceKind.Cron,
      Cron = "0 17 * * *",
      StartAt = _utc(2026, 07, 13, 08, 00)
    });

    await Assert.That(result).IsNotNull();
    await Assert.That(result!.Value.NextFireAt).IsEqualTo(_utc(2026, 07, 13, 17, 00));
    var (nextFire, _) = await _cadenceAsync(handle.ScheduleId);
    await Assert.That(nextFire).IsEqualTo(_utc(2026, 07, 13, 17, 00));   // persisted
  }

  [Test]
  public async Task Update_WrongVersion_ReturnsNullAsync() {
    var mgr = _manager();
    var handle = await mgr.CreateAsync(new ScheduleDefinition {
      EventType = "MgrUpdVer",
      AuthorityPrincipalId = _authority,
      StreamId = Guid.NewGuid(),
      Kind = RecurrenceKind.Cron,
      Cron = "0 9 * * *",
      StartAt = _utc(2026, 07, 13, 08, 00)
    });

    var result = await mgr.UpdateAsync(handle.ScheduleId, new ScheduleUpdate {
      Kind = RecurrenceKind.Cron,
      Cron = "0 17 * * *",
      StartAt = _utc(2026, 07, 13, 08, 00)
    }, expectedVersion: 999);

    await Assert.That(result).IsNull();
    var (nextFire, _) = await _cadenceAsync(handle.ScheduleId);
    await Assert.That(nextFire).IsEqualTo(_utc(2026, 07, 13, 09, 00));   // unchanged
  }

  [Test]
  public async Task Update_TerminalSchedule_ReturnsNullAsync() {
    var mgr = _manager();
    var handle = await mgr.CreateAsync(new ScheduleDefinition {
      EventType = "MgrUpdTerm",
      AuthorityPrincipalId = _authority,
      StreamId = Guid.NewGuid(),
      Kind = RecurrenceKind.Cron,
      Cron = "0 9 * * *",
      StartAt = _utc(2026, 07, 13, 08, 00)
    });
    _ = await mgr.CancelAsync(handle.ScheduleId);

    var result = await mgr.UpdateAsync(handle.ScheduleId, new ScheduleUpdate {
      Kind = RecurrenceKind.Cron,
      Cron = "0 17 * * *",
      StartAt = _utc(2026, 07, 13, 08, 00)
    });

    await Assert.That(result).IsNull();
  }

  [Test]
  public async Task Update_IntervalWithoutInterval_ThrowsAsync() {
    var mgr = _manager();
    await Assert.That(async () => await mgr.UpdateAsync(Guid.NewGuid(), new ScheduleUpdate {
      Kind = RecurrenceKind.Interval
    })).Throws<ArgumentException>();
  }
}
