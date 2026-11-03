// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// A coordinator that cannot record integrity deliveries (another engine, or one without the table) says so, and the
/// rest of the queue surface is inert on it (#2001). Callers read <see cref="IntegrityDeliveryEnqueueResult.Unsupported"/>
/// as "handle the delivery inline, as before".
/// </summary>
/// <code-under-test>src/Whizbang.Core/Messaging/IWorkCoordinator.cs</code-under-test>
public class IntegrityDeliveryCoordinatorDefaultsTests {
  [Test]
  public async Task ACoordinatorWithoutTheQueue_ReportsUnsupported_AndIsInertAsync() {
    IWorkCoordinator coordinator = new BareCoordinator();

    var enqueued = await coordinator.EnqueueIntegrityDeliveryAsync(Guid.NewGuid(), "t", "{}");
    var leased = await coordinator.LeaseIntegrityDeliveriesAsync(10, TimeSpan.FromMinutes(1));
    await coordinator.CompleteIntegrityDeliveryAsync(Guid.NewGuid());
    await coordinator.FailIntegrityDeliveryAsync(Guid.NewGuid(), "e", TimeSpan.FromSeconds(1));

    await Assert.That(enqueued).IsEqualTo(IntegrityDeliveryEnqueueResult.Unsupported);
    await Assert.That(leased).IsEmpty();
  }

  private sealed class BareCoordinator : IWorkCoordinator {
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
}
