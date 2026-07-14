using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Notifications;
using Whizbang.Core.Temporal;
using Whizbang.Data.Postgres.Notifications;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Integration tests for <see cref="PgScheduleManager"/> — the C# management API over
/// <c>wh_create_schedule</c> / <c>wh_transition_schedule</c>: create (with id generation + idempotent
/// create-or-update by key), the pause/resume/cancel transitions with optimistic concurrency, and the
/// client-side argument guards.
/// </summary>
/// <docs>fundamentals/temporal/temporal-engine</docs>
public class PgScheduleManagerIntegrationTests : EFCoreTestBase {
  private static DateTimeOffset _utc(int y, int mo, int d, int h, int mi) =>
    new(y, mo, d, h, mi, 0, TimeSpan.Zero);

  private PgScheduleManager _manager() {
    var opts = new WhizbangNotificationOptions { DirectConnectionString = ConnectionString };
    var cfg = new ConfigurationBuilder().AddInMemoryCollection([]).Build();
    return new PgScheduleManager(Options.Create(opts), cfg, NullLogger<PgScheduleManager>.Instance);
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
      StreamId = Guid.NewGuid(),
      Kind = RecurrenceKind.Interval
    })).Throws<ArgumentException>();
  }

  [Test]
  public async Task Create_CronWithoutCron_ThrowsAsync() {
    var mgr = _manager();
    await Assert.That(async () => await mgr.CreateAsync(new ScheduleDefinition {
      EventType = "E",
      StreamId = Guid.NewGuid(),
      Kind = RecurrenceKind.Cron
    })).Throws<ArgumentException>();
  }

  [Test]
  public async Task Create_EmptyEventType_ThrowsAsync() {
    var mgr = _manager();
    await Assert.That(async () => await mgr.CreateAsync(new ScheduleDefinition {
      EventType = "  ",
      StreamId = Guid.NewGuid(),
      Kind = RecurrenceKind.OneShot
    })).Throws<ArgumentException>();
  }

  // ---- DB-backed behavior ----

  [Test]
  public async Task Create_Cron_ReturnsHandleAndActivatesAsync() {
    var mgr = _manager();
    var handle = await mgr.CreateAsync(new ScheduleDefinition {
      EventType = "MgrCron",
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
      StreamId = Guid.NewGuid(),
      Kind = RecurrenceKind.Cron,
      Cron = "0 9 * * *",
      StartAt = _utc(2026, 07, 13, 08, 00),
      Key = "mgr-daily"
    });
    var second = await mgr.CreateAsync(new ScheduleDefinition {
      EventType = "MgrKey",
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
      StreamId = Guid.NewGuid(),
      Kind = RecurrenceKind.Interval,
      Interval = TimeSpan.FromMinutes(1),
      StartAt = _utc(2026, 07, 13, 09, 00)
    });

    await Assert.That(await mgr.PauseAsync(handle.ScheduleId, expectedVersion: 999)).IsFalse();
    await Assert.That(await _statusAsync(handle.ScheduleId)).IsEqualTo((short)0);   // unchanged
  }
}
