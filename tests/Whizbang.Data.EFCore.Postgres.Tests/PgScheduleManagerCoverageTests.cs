#pragma warning disable CA1707

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Notifications;
using Whizbang.Core.Observability;
using Whizbang.Core.Temporal;
using Whizbang.Core.Workers;
using Whizbang.Data.Postgres.Notifications;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Coverage for <see cref="PgScheduleManager"/>'s "no connection is configured" paths and the
/// Cron-recurrence client-side guard on <c>UpdateAsync</c> —
/// <see cref="PgScheduleManagerIntegrationTests"/> exercises the DB-backed behavior against a real
/// Postgres container. Every case here builds the manager with no <c>DirectConnectionString</c>, no
/// <c>ConnectionStringKey</c>, no fallback, and no data source, so <c>_openAsync</c>'s
/// <c>NotificationConnectionPlan.IsAvailable</c> check is false before a socket is ever touched — no
/// database anywhere in this file.
/// </summary>
/// <docs>fundamentals/temporal/temporal-engine</docs>
[Category("Shard1")]
public class PgScheduleManagerCoverageTests {
  private static readonly Guid _authority = Guid.NewGuid();

  private static PgScheduleManager _managerWithNoConnectionConfigured() {
    var options = new WhizbangNotificationOptions();
    var configuration = new ConfigurationBuilder().Build();
    var instance = new ServiceInstanceProvider(Guid.NewGuid(), "coverage-svc", "coverage-host", processId: 1);
    return new PgScheduleManager(
      Options.Create(options), configuration, instance,
      Options.Create(new ClaimWorkerOptions()),
      Options.Create(new TemporalOptions()),
      NullLogger<PgScheduleManager>.Instance);
  }

  // A schedule manager decides when recurring work next runs. If CreateAsync swallowed an
  // unavailable connection and reported success anyway, the caller would believe a schedule exists
  // that was never persisted — a phantom schedule that silently never fires, which looks like an
  // idle system rather than an error.
  [Test]
  public async Task CreateAsync_WithNoConnectionConfigured_ThrowsInvalidOperationAsync() {
    var manager = _managerWithNoConnectionConfigured();

    await Assert.That(() => manager.CreateAsync(new ScheduleDefinition {
      EventType = "CoverageNoConnCreate",
      AuthorityPrincipalId = _authority,
      StreamId = Guid.NewGuid(),
      Kind = RecurrenceKind.OneShot
    })).Throws<InvalidOperationException>()
      .Because("with no connection available there is nothing to create against — reporting "
             + "success would fabricate a schedule that was never persisted");
  }

  // TriggerNowAsync fires an extra occurrence outside the normal cadence. With no connection there
  // is no schedule to trigger, so it must report "nothing happened" (null) rather than a fabricated
  // occurrence id a caller might go on to track.
  [Test]
  public async Task TriggerNowAsync_WithNoConnectionConfigured_ReturnsNullAsync() {
    var manager = _managerWithNoConnectionConfigured();

    var result = await manager.TriggerNowAsync(Guid.NewGuid());

    await Assert.That(result).IsNull();
  }

  // The Cron-recurrence guard on UpdateAsync mirrors CreateAsync's: a Cron update with no cron
  // expression cannot compute a next fire time. This runs entirely client-side, before _openAsync
  // is ever reached, so it must throw regardless of whether a connection is configured.
  [Test]
  public async Task UpdateAsync_CronWithoutCron_ThrowsAsync() {
    var manager = _managerWithNoConnectionConfigured();

    await Assert.That(() => manager.UpdateAsync(Guid.NewGuid(), new ScheduleUpdate {
      Kind = RecurrenceKind.Cron
    })).Throws<ArgumentException>()
      .Because("a Cron update with no Cron expression has no next fire time to compute, and that "
             + "must fail before ever reaching the database");
  }

  // Past the validation guards, UpdateAsync's "no connection" path must report null (no update
  // happened) rather than silently pretending the recomputed cadence was persisted.
  [Test]
  public async Task UpdateAsync_WithNoConnectionConfigured_ReturnsNullAsync() {
    var manager = _managerWithNoConnectionConfigured();

    var result = await manager.UpdateAsync(Guid.NewGuid(), new ScheduleUpdate {
      Kind = RecurrenceKind.OneShot
    });

    await Assert.That(result).IsNull();
  }

  // Pause/Resume/Cancel all funnel through the same _transitionAsync helper. Any one of them
  // silently reporting success with no connection would leave an operator believing a schedule was
  // paused (or canceled) when it is still running unattended — the opposite of what they asked for.
  [Test]
  public async Task PauseResumeCancel_WithNoConnectionConfigured_AllReturnFalseAsync() {
    var manager = _managerWithNoConnectionConfigured();
    var scheduleId = Guid.NewGuid();

    await Assert.That(await manager.PauseAsync(scheduleId)).IsFalse();
    await Assert.That(await manager.ResumeAsync(scheduleId)).IsFalse();
    await Assert.That(await manager.CancelAsync(scheduleId)).IsFalse();
  }
}
