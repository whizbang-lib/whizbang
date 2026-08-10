using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Temporal;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// #80-D: the full sweep moves off the every-Nth-audit counter onto the temporal engine's clock —
/// a configured IDLE hour, splayed per service so a fleet sharing one database server does not
/// run its heaviest verification in unison. The counter remains only as the fallback for hosts
/// without the engine (the scheduler leaves <see cref="IntegritySweepScheduleState.CronActive"/>
/// false, and the audit worker keeps counting).
/// </summary>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/IntegritySweepScheduler.cs</code-under-test>
/// <docs>resilience/stream-integrity</docs>
public class IntegritySweepSchedulingTests {

  private sealed class _captureScheduleManager : IScheduleManager {
    public ScheduleDefinition? Created;
    public Task<ScheduleHandle> CreateAsync(ScheduleDefinition definition, CancellationToken ct = default) {
      Created = definition;
      return Task.FromResult(new ScheduleHandle(TrackedGuid.NewMedo().Value, DateTimeOffset.UtcNow, WasCreated: true));
    }
    public Task<bool> PauseAsync(Guid scheduleId, long? expectedVersion = null, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<bool> ResumeAsync(Guid scheduleId, long? expectedVersion = null, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<bool> CancelAsync(Guid scheduleId, long? expectedVersion = null, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<Guid?> TriggerNowAsync(Guid scheduleId, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<ScheduleUpdateResult?> UpdateAsync(Guid scheduleId, ScheduleUpdate update, long? expectedVersion = null, CancellationToken ct = default) => throw new NotSupportedException();
  }

  private sealed class _runner : IIntegritySweepRunner {
    public int Runs;
    public Task RunSweepOnceAsync(CancellationToken cancellationToken) {
      Runs++;
      return Task.CompletedTask;
    }
  }

  private sealed class _instanceProvider(string name) : IServiceInstanceProvider {
    public Guid InstanceId { get; } = TrackedGuid.NewMedo().Value;
    public string ServiceName => name;
    public string HostName => "test-host";
    public int ProcessId => 1;
    public ServiceInstanceInfo ToInfo() => new() {
      ServiceName = name,
      InstanceId = InstanceId,
      HostName = HostName,
      ProcessId = ProcessId,
    };
  }

  private static (IntegritySweepScheduler Scheduler, IntegritySweepScheduleState State, _captureScheduleManager Manager)
      _build(string? cron, bool withManager = true, string serviceName = "auditor-svc") {
    var services = new ServiceCollection();
    var manager = new _captureScheduleManager();
    if (withManager) {
      services.AddSingleton<IScheduleManager>(manager);
    }
    services.AddSingleton<IServiceInstanceProvider>(new _instanceProvider(serviceName));
    var state = new IntegritySweepScheduleState();
    services.AddSingleton(state);
    var sp = services.BuildServiceProvider();
    var scheduler = new IntegritySweepScheduler(
      sp, Options.Create(new StreamIntegrityOptions { FullSweepCron = cron }),
      NullLogger<IntegritySweepScheduler>.Instance);
    return (scheduler, state, manager);
  }

  [Test]
  public async Task Start_RegistersTheSweepSchedule_AndActivatesTheCronAsync() {
    var (scheduler, state, manager) = _build("0 3 * * *");

    await scheduler.StartAsync(CancellationToken.None);

    await Assert.That(manager.Created).IsNotNull()
      .Because("the sweep rides the temporal engine — idempotent create-or-update by key");
    await Assert.That(manager.Created!.Key).IsEqualTo("wh-integrity-sweep");
    await Assert.That(manager.Created.Kind).IsEqualTo(RecurrenceKind.Cron);
    await Assert.That(manager.Created.EventType)
      .IsEqualTo(TypeNameFormatter.Format(typeof(ScheduledIntegritySweep)));
    await Assert.That(state.CronActive).IsTrue()
      .Because("the audit worker's counter stands down only once the cron actually owns the sweep");
  }

  [Test]
  public async Task DefaultMinute_IsSplayedPerService_DeterministicallyAsync() {
    // A fleet sharing one database server must not run its heaviest verification in unison.
    // The splay must be STABLE per service — a restart that moved the minute would re-randomize
    // the very collisions the splay exists to prevent.
    var (s1, _, m1) = _build("0 3 * * *", serviceName: "service-alpha");
    var (s2, _, m2) = _build("0 3 * * *", serviceName: "service-alpha");
    await s1.StartAsync(CancellationToken.None);
    await s2.StartAsync(CancellationToken.None);

    var parts1 = m1.Created!.Cron!.Split(' ');
    var parts2 = m2.Created!.Cron!.Split(' ');
    await Assert.That(parts1[0]).IsEqualTo(parts2[0])
      .Because("the splay minute is a stable function of the service name");
    await Assert.That(int.Parse(parts1[0], System.Globalization.CultureInfo.InvariantCulture))
      .IsGreaterThanOrEqualTo(0);
    await Assert.That(int.Parse(parts1[0], System.Globalization.CultureInfo.InvariantCulture))
      .IsLessThan(60);
    await Assert.That(parts1[1]).IsEqualTo("3")
      .Because("only the minute splays — the operator's chosen hour is the idle-time contract");
  }

  [Test]
  public async Task ExplicitMinute_IsHonoredVerbatimAsync() {
    var (scheduler, _, manager) = _build("17 2 * * *");

    await scheduler.StartAsync(CancellationToken.None);

    await Assert.That(manager.Created!.Cron).IsEqualTo("17 2 * * *")
      .Because("an operator who chose an exact minute meant it — splay only replaces the default 0");
  }

  [Test]
  public async Task NoTemporalEngine_LeavesTheCounterFallbackInChargeAsync() {
    var (scheduler, state, manager) = _build("0 3 * * *", withManager: false);

    await scheduler.StartAsync(CancellationToken.None);

    await Assert.That(manager.Created).IsNull();
    await Assert.That(state.CronActive).IsFalse()
      .Because("without the engine the every-Nth-audit counter must keep sweeping — no silent loss of the sweep");
  }

  [Test]
  public async Task CronDisabled_RegistersNothingAsync() {
    var (scheduler, state, manager) = _build(cron: null);

    await scheduler.StartAsync(CancellationToken.None);

    await Assert.That(manager.Created).IsNull();
    await Assert.That(state.CronActive).IsFalse();
  }

  [Test]
  public async Task SweepReceptor_RunsTheSweep_WhenTheOccurrenceFiresAsync() {
    var runner = new _runner();
    var services = new ServiceCollection();
    services.AddSingleton<IIntegritySweepRunner>(runner);
    var sp = services.BuildServiceProvider();
    var receptor = new ScheduledIntegritySweepReceptor(
      sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<ScheduledIntegritySweepReceptor>.Instance);

    await receptor.HandleAsync(new ScheduledIntegritySweep());

    await Assert.That(runner.Runs).IsEqualTo(1)
      .Because("the occurrence at the idle hour IS the sweep trigger — an inert receptor would quietly end all sweeping");
  }

  [Test]
  public async Task SweepReceptor_NoRunnerRegistered_IsANoOpAsync() {
    var sp = new ServiceCollection().BuildServiceProvider();
    var receptor = new ScheduledIntegritySweepReceptor(
      sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<ScheduledIntegritySweepReceptor>.Instance);

    await receptor.HandleAsync(new ScheduledIntegritySweep());
    // Reaching here without throwing is the assertion — schema-only hosts still boot and dispatch.
  }
}
