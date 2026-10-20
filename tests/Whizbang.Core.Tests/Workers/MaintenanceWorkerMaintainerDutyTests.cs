// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Startup;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// #1377: the periodic maintenance sweep runs on the instance holding the maintainer duty, as the vacuum
/// already does. Before, every instance ran its own full cycle against the same shared tables, so a service
/// scaled out on its backlog ran N overlapping sweeps at the moment it could least absorb them.
/// </summary>
/// <remarks>
/// When role assignment does not manage the maintainer duty (no duty loop, or a loop without that role),
/// every instance still sweeps, as before: there is no holder to defer to.
/// </remarks>
/// <code-under-test>src/Whizbang.Core/Workers/MaintenanceWorker.cs</code-under-test>
[NotInParallel("WhizbangBackgroundServiceTests")]
public class MaintenanceWorkerMaintainerDutyTests {
  [Test]
  [Arguments(true)]
  [Arguments(false)]
  public async Task WithTheMaintainerDutyAssigned_OnlyItsHolderSweepsAsync(bool holds) {
    var holder = _holder(grants: holds, role: StartupDuties.MAINTAINER);
    await holder.RunOnceAsync(CancellationToken.None);
    var (worker, coordinator) = _build(holder);

    await worker.RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(holder.Holds(StartupDuties.MAINTAINER)).IsEqualTo(holds);
    await Assert.That(coordinator.SweepCount).IsEqualTo(holds ? 1 : 0)
      .Because("the sweep is fleet-wide work on shared tables: one holder does it once");
  }

  [Test]
  public async Task WithoutRoleAssignment_EveryInstanceSweepsAsync() {
    var (worker, coordinator) = _build(holder: null);

    await worker.RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(coordinator.SweepCount).IsEqualTo(1).Because("with no duty to defer to, the instance sweeps");
  }

  [Test]
  public async Task WithADutyLoopThatDoesNotManageTheMaintainerDuty_EveryInstanceSweepsAsync() {
    var (worker, coordinator) = _build(_holder(grants: false, role: StartupDuties.MIGRATOR));

    await worker.RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(coordinator.SweepCount).IsEqualTo(1).Because("the maintainer duty is not assigned, so no instance holds it");
  }

  private static (MaintenanceWorker Worker, SweepCountingCoordinator Coordinator) _build(DutyHolderWorker? holder) {
    var coordinator = new SweepCountingCoordinator();
    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coordinator);
    if (holder is not null) {
      services.AddSingleton(holder);
    }
    var provider = services.BuildServiceProvider();
    var worker = new MaintenanceWorker(
      provider.GetRequiredService<IServiceScopeFactory>(),
      new SchemaReadyGate(),
      Options.Create(new MaintenanceWorkerOptions { IntervalMinutes = 1 }),
      NullLogger<MaintenanceWorker>.Instance,
      metrics: null,
      housekeeping: null);
    return (worker, coordinator);
  }

  private static DutyHolderWorker _holder(bool grants, string role) =>
    new(new Elector(grants), new NoOwedWork(), [new Handler(role)], [],
      Options.Create(new RoleAssignmentOptions { Roles = { StartupDuties.MIGRATOR } }),
      NullLogger<DutyHolderWorker>.Instance, notify: null);

  private sealed class SweepCountingCoordinator : IWorkCoordinator {
    public int SweepCount { get; private set; }
    public Task<IReadOnlyList<MaintenanceResult>> PerformMaintenanceAsync(CancellationToken cancellationToken = default) {
      SweepCount++;
      return Task.FromResult<IReadOnlyList<MaintenanceResult>>([]);
    }
    public Task<WorkBatch> ClaimWorkAsync(ClaimWorkRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new WorkCoordinatorStatistics());
    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<PartitionRecomputeResult> RecomputePartitionNumbersAsync(int partitionCount, CancellationToken cancellationToken = default) => Task.FromResult(new PartitionRecomputeResult());
    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion completion, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure failure, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default) => Task.FromResult<PerspectiveCursorInfo?>(null);
  }

  private sealed class Grant : IDutyGrant {
    public string Duty => StartupDuties.MAINTAINER;
    public DateTimeOffset AcquiredAt => DateTimeOffset.UnixEpoch;
    public Task<bool> VerifyStillHeldAsync(CancellationToken cancellationToken) => Task.FromResult(true);
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
  }

  private sealed class Elector(bool grants) : IDutyElector {
    public Task<DutyAttempt> TryAcquireAsync(string duty, CancellationToken cancellationToken) =>
      Task.FromResult(grants ? DutyAttempt.Granted(new Grant()) : DutyAttempt.Lost(DutyRefusal.Contended, "held elsewhere"));
  }

  private sealed class NoOwedWork : IPendingDutyWorkStore {
    public Task OweAsync(string role, string workKey, CancellationToken cancellationToken) => Task.CompletedTask;
    public Task<IReadOnlyList<PendingDutyWork>> ListOwedAsync(string role, CancellationToken cancellationToken) =>
      Task.FromResult<IReadOnlyList<PendingDutyWork>>([]);
    public Task<DutyWorkCompletion> CompleteAsync(PendingDutyWork work, IDutyGrant grant, CancellationToken cancellationToken) =>
      Task.FromResult(DutyWorkCompletion.Completed);
    public Task<bool> RecordFailureAsync(PendingDutyWork work, IDutyGrant grant, string failure, CancellationToken cancellationToken) =>
      Task.FromResult(true);
  }

  private sealed class Handler(string role) : IDutyWorkHandler {
    public string Role => role;
    public string WorkKey => "MaintenanceWorkerMaintainerDutyTest";
    public ValueTask<DutyWorkResult> RunAsync(IDutyGrant grant, CancellationToken cancellationToken) =>
      ValueTask.FromResult(DutyWorkResult.Done());
  }
}
