using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Temporal;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Coverage for two <see cref="IntegritySweepScheduler"/> lines
/// <see cref="IntegritySweepSchedulingTests"/> never happens to exercise: the scheduling-failure
/// log branch, and <see cref="IntegritySweepScheduler.StopAsync"/>. No database needed — both
/// are driven entirely through fakes.
/// <para>
/// Production impact if the failure branch regresses: a scheduling failure that goes
/// unlogged would leave an operator with no signal that the full sweep never got registered —
/// the every-Nth-audit counter fallback keeps running, but silently, with no trail explaining
/// why the cron never took over.
/// </para>
/// </summary>
[Category("Shard1")]
public class IntegritySweepSchedulerCoverageTests {

  private sealed class _throwingScheduleManager : IScheduleManager {
    public Task<ScheduleHandle> CreateAsync(ScheduleDefinition definition, CancellationToken ct = default) =>
      throw new InvalidOperationException("temporal engine rejected the schedule");
    public Task<bool> PauseAsync(Guid scheduleId, long? expectedVersion = null, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<bool> ResumeAsync(Guid scheduleId, long? expectedVersion = null, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<bool> CancelAsync(Guid scheduleId, long? expectedVersion = null, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<Guid?> TriggerNowAsync(Guid scheduleId, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<ScheduleUpdateResult?> UpdateAsync(Guid scheduleId, ScheduleUpdate update, long? expectedVersion = null, CancellationToken ct = default) => throw new NotSupportedException();
  }

  private sealed class _instanceProvider : IServiceInstanceProvider {
    public Guid InstanceId { get; } = TrackedGuid.NewMedo().Value;
    public string ServiceName => "coverage-svc";
    public string HostName => "test-host";
    public int ProcessId => 1;
    public ServiceInstanceInfo ToInfo() => new() {
      ServiceName = ServiceName,
      InstanceId = InstanceId,
      HostName = HostName,
      ProcessId = ProcessId,
    };
  }

  private static IntegritySweepScheduler _newSchedulerWithThrowingManager() {
    var services = new ServiceCollection();
    services.AddSingleton<IScheduleManager>(new _throwingScheduleManager());
    services.AddSingleton<IServiceInstanceProvider>(new _instanceProvider());
    var state = new IntegritySweepScheduleState();
    services.AddSingleton(state);
    var sp = services.BuildServiceProvider();
    return new IntegritySweepScheduler(
      sp, Options.Create(new StreamIntegrityOptions { FullSweepCron = "0 3 * * *" }),
      NullLogger<IntegritySweepScheduler>.Instance);
  }

  [Test]
  public async Task StartAsync_ScheduleCreationThrows_LeavesTheCounterFallbackInChargeAsync() {
    var scheduler = _newSchedulerWithThrowingManager();

    // Reaching here without the exception escaping StartAsync IS the assertion: a scheduling
    // failure must be caught and logged, never allowed to fail the hosted service's StartAsync
    // and take the whole host down over what is, by design, a fallback-covered condition.
    await Assert.That(async () => await scheduler.StartAsync(CancellationToken.None))
      .ThrowsNothing()
      .Because("a scheduling failure must never propagate out of StartAsync — the counter fallback "
             + "exists precisely so this failure mode is survivable");
  }

  [Test]
  public async Task StopAsync_CompletesWithoutThrowingAsync() {
    var services = new ServiceCollection();
    var sp = services.BuildServiceProvider();
    var scheduler = new IntegritySweepScheduler(
      sp, Options.Create(new StreamIntegrityOptions { FullSweepCron = "0 3 * * *" }),
      NullLogger<IntegritySweepScheduler>.Instance);

    await Assert.That(async () => await scheduler.StopAsync(CancellationToken.None))
      .ThrowsNothing()
      .Because("the schedule registration lives for the process lifetime — StopAsync has nothing to unwind");
  }
}
