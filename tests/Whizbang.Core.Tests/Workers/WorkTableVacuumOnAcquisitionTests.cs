// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Startup;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// #1351: the work tables are vacuumed when an instance acquires the maintainer duty, never merely
/// because it started. A first assignment always vacuums, once per process; a reassignment (the duty
/// came from another instance) vacuums again unless <see cref="MaintenanceWorkerOptions.MaintainOnReassignment"/>
/// is off. A table the connection cannot vacuum is named in a warning, never passed off as done.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Workers/WorkTableVacuumOnAcquisition.cs</code-under-test>
[Category("Workers")]
public class WorkTableVacuumOnAcquisitionTests {
  private static readonly Guid _self = Guid.CreateVersion7();
  private static readonly Guid _other = Guid.CreateVersion7();

  private sealed class Grant(Guid? previous, bool held = true) : IDutyGrant {
    public string Duty => StartupDuties.MAINTAINER;
    public DateTimeOffset AcquiredAt => DateTimeOffset.UnixEpoch;
    public Guid? PreviousHolderInstanceId => previous;
    public Task<bool> VerifyStillHeldAsync(CancellationToken cancellationToken) => Task.FromResult(held);
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
  }

  private sealed class Instance : IServiceInstanceProvider {
    public Guid InstanceId => _self;
    public string ServiceName => "svc";
    public string HostName => "host";
    public int ProcessId => 1;
    public ServiceInstanceInfo ToInfo() => new() { InstanceId = InstanceId, ServiceName = ServiceName, HostName = HostName, ProcessId = ProcessId };
  }

  private sealed class VacuumCoordinator : IWorkCoordinator {
    public int Vacuums { get; private set; }
    public IReadOnlyList<WorkTableVacuum> Outcome { get; init; } = [new("wh_outbox", true, null)];

    public Task<IReadOnlyList<WorkTableVacuum>> VacuumWorkTablesAsync(CancellationToken cancellationToken = default) {
      Vacuums++;
      return Task.FromResult(Outcome);
    }

    // Unused surface for this test.
    public Task<WorkBatch> ClaimWorkAsync(ClaimWorkRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new WorkCoordinatorStatistics());
    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<PartitionRecomputeResult> RecomputePartitionNumbersAsync(int partitionCount, CancellationToken cancellationToken = default) => Task.FromResult(new PartitionRecomputeResult());
    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion completion, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure failure, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default) => Task.FromResult<PerspectiveCursorInfo?>(null);
    public Task<IReadOnlyList<MaintenanceResult>> PerformMaintenanceAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<MaintenanceResult>>([]);
  }

  private static (WorkTableVacuumOnAcquisition Handler, FakeLogger<WorkTableVacuumOnAcquisition> Logger) _handlerOver(
      VacuumCoordinator? coordinator, bool maintainOnReassignment = true) {
    var services = new ServiceCollection();
    if (coordinator is not null) {
      services.AddSingleton<IWorkCoordinator>(coordinator);
    }
    var provider = services.BuildServiceProvider();
    var logger = new FakeLogger<WorkTableVacuumOnAcquisition>();
    var handler = new WorkTableVacuumOnAcquisition(
      provider.GetRequiredService<IServiceScopeFactory>(),
      Options.Create(new MaintenanceWorkerOptions { MaintainOnReassignment = maintainOnReassignment }),
      new Instance(),
      logger);
    return (handler, logger);
  }

  [Test]
  public async Task Role_IsTheMaintainerDutyAsync() {
    var (handler, _) = _handlerOver(new VacuumCoordinator());

    await Assert.That(handler.Role).IsEqualTo(StartupDuties.MAINTAINER);
  }

  [Test]
  public async Task MaintainOnReassignment_DefaultsToOnAsync() =>
    await Assert.That(new MaintenanceWorkerOptions().MaintainOnReassignment).IsTrue();

  [Test]
  public async Task AFirstAssignment_Vacuums_AndOnlyOncePerProcessAsync() {
    // No previous holder, then this instance's own lapse and recovery: neither is a reassignment, so the
    // second acquisition does not vacuum again.
    var coordinator = new VacuumCoordinator();
    var (handler, logger) = _handlerOver(coordinator);

    await handler.OnAcquiredAsync(new Grant(previous: null), CancellationToken.None);
    await handler.OnAcquiredAsync(new Grant(previous: _self), CancellationToken.None);

    await Assert.That(coordinator.Vacuums).IsEqualTo(1);
    await Assert.That(logger.Collector.GetSnapshot().Any(r => r.Level == LogLevel.Information && r.Message.Contains("1 of 1"))).IsTrue();
  }

  [Test]
  public async Task AReassignment_Vacuums_EvenAfterAFirstAssignmentDidAsync() {
    var coordinator = new VacuumCoordinator();
    var (handler, _) = _handlerOver(coordinator);

    await handler.OnAcquiredAsync(new Grant(previous: null), CancellationToken.None);
    await handler.OnAcquiredAsync(new Grant(previous: _other), CancellationToken.None);

    await Assert.That(coordinator.Vacuums).IsEqualTo(2).Because("the duty came from another instance");
  }

  [Test]
  public async Task AReassignment_WithMaintainOnReassignmentOff_DoesNotVacuum_AndSaysWhyAsync() {
    var coordinator = new VacuumCoordinator();
    var (handler, logger) = _handlerOver(coordinator, maintainOnReassignment: false);

    await handler.OnAcquiredAsync(new Grant(previous: _other), CancellationToken.None);

    await Assert.That(coordinator.Vacuums).IsEqualTo(0);
    var record = logger.Collector.GetSnapshot().Single();
    await Assert.That(record.Message).Contains(nameof(MaintenanceWorkerOptions.MaintainOnReassignment));
    await Assert.That(record.Message).Contains(_other.ToString());
  }

  [Test]
  public async Task AReassignment_WithTheFlagOff_StillLeavesTheFirstAssignmentOwedAsync() {
    // Skipping a hand-off is not the process's vacuum: a later first assignment still runs.
    var coordinator = new VacuumCoordinator();
    var (handler, _) = _handlerOver(coordinator, maintainOnReassignment: false);

    await handler.OnAcquiredAsync(new Grant(previous: _other), CancellationToken.None);
    await handler.OnAcquiredAsync(new Grant(previous: null), CancellationToken.None);

    await Assert.That(coordinator.Vacuums).IsEqualTo(1);
  }

  [Test]
  public async Task ATableTheConnectionCannotVacuum_IsNamedInAWarningAsync() {
    var coordinator = new VacuumCoordinator {
      Outcome = [
        new("wh_outbox", true, null),
        new("wh_inbox", false, "role 'app' does not own wh_inbox and no schema-initialization connection is configured"),
      ],
    };
    var (handler, logger) = _handlerOver(coordinator);

    await handler.OnAcquiredAsync(new Grant(previous: null), CancellationToken.None);

    var warning = logger.Collector.GetSnapshot().Single(r => r.Level == LogLevel.Warning);
    await Assert.That(warning.Message).Contains("wh_inbox");
    await Assert.That(warning.Message).Contains("role 'app' does not own wh_inbox");
    await Assert.That(logger.Collector.GetSnapshot().Any(r => r.Level == LogLevel.Information && r.Message.Contains("1 of 2"))).IsTrue();
  }

  [Test]
  public async Task AGrantLostBeforeTheVacuum_DoesNotVacuum_AndLeavesTheFirstAssignmentOwedAsync() {
    var coordinator = new VacuumCoordinator();
    var (handler, _) = _handlerOver(coordinator);

    await handler.OnAcquiredAsync(new Grant(previous: null, held: false), CancellationToken.None);
    await Assert.That(coordinator.Vacuums).IsEqualTo(0);

    await handler.OnAcquiredAsync(new Grant(previous: null), CancellationToken.None);
    await Assert.That(coordinator.Vacuums).IsEqualTo(1);
  }

  [Test]
  public async Task WithNoWorkCoordinator_ThereIsNothingToVacuumAsync() {
    var (handler, logger) = _handlerOver(coordinator: null);

    await handler.OnAcquiredAsync(new Grant(previous: null), CancellationToken.None);

    await Assert.That(logger.Collector.GetSnapshot().Any(r => r.Level >= LogLevel.Warning)).IsFalse();
  }

  [Test]
  public async Task Constructor_RefusesNullsAsync() {
    var scopes = new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    var options = Options.Create(new MaintenanceWorkerOptions());
    var logger = new FakeLogger<WorkTableVacuumOnAcquisition>();
    await Assert.That(() => new WorkTableVacuumOnAcquisition(null!, options, new Instance(), logger)).Throws<ArgumentNullException>();
    await Assert.That(() => new WorkTableVacuumOnAcquisition(scopes, null!, new Instance(), logger)).Throws<ArgumentNullException>();
    await Assert.That(() => new WorkTableVacuumOnAcquisition(scopes, options, null!, logger)).Throws<ArgumentNullException>();
    await Assert.That(() => new WorkTableVacuumOnAcquisition(scopes, options, new Instance(), null!)).Throws<ArgumentNullException>();
  }

  [Test]
  public async Task OnAcquiredAsync_RefusesANullGrantAsync() {
    var (handler, _) = _handlerOver(new VacuumCoordinator());

    await Assert.That(async () => await handler.OnAcquiredAsync(null!, CancellationToken.None)).Throws<ArgumentNullException>();
  }
}
