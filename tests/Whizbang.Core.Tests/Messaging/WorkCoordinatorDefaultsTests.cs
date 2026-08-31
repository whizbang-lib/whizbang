using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Covers the default interface method bodies on <see cref="IWorkCoordinator"/>.
/// </summary>
/// <remarks>
/// IWorkCoordinator is a capability surface: a store provider implements the six
/// members it must, and inherits a safe no-op for every optional capability it does
/// not support. Those inherited defaults are only reachable through an implementation
/// that leaves them alone, so a Postgres or Dapper coordinator — which overrides
/// nearly all of them — never exercises one. Each test below pins the documented
/// fallback: an empty result, a zero count, or a completed task, never a throw, so a
/// provider missing a capability degrades rather than breaks the caller.
/// </remarks>
[Category("Core")]
[Category("Messaging")]
public class WorkCoordinatorDefaultsTests {

  /// <summary>
  /// Implements only the six abstract members so every optional capability keeps its
  /// inherited default. The abstract members throw, so a test that strays into one
  /// fails loudly instead of passing for the wrong reason.
  /// </summary>
  private sealed class MinimalWorkCoordinator : IWorkCoordinator {
    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(
        Guid streamId, string perspectiveName, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public Task ReportPerspectiveCompletionAsync(
        PerspectiveCursorCompletion completion, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public Task ReportPerspectiveFailureAsync(
        PerspectiveCursorFailure failure, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public Task StoreInboxMessagesAsync(
        InboxMessage[] messages, int partitionCount, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();
  }

  [Test]
  public async Task ReclassifyEventsEphemeralAsync_WithoutProviderSupport_UsesInheritedDefaultAsync() {
    IWorkCoordinator coordinator = new MinimalWorkCoordinator();

    var result = await coordinator.ReclassifyEventsEphemeralAsync([]);

    await Assert.That(result).IsNotNull();
  }

  [Test]
  public async Task CountSourcedEventsForTypesAsync_WithoutProviderSupport_UsesInheritedDefaultAsync() {
    IWorkCoordinator coordinator = new MinimalWorkCoordinator();

    var result = await coordinator.CountSourcedEventsForTypesAsync([]);

    await Assert.That(result).IsEqualTo(0);
  }

  [Test]
  public async Task GetTypeDefinitionsAsync_WithoutProviderSupport_UsesInheritedDefaultAsync() {
    IWorkCoordinator coordinator = new MinimalWorkCoordinator();

    var result = await coordinator.GetTypeDefinitionsAsync();

    await Assert.That(result).IsEmpty();
  }

  [Test]
  public async Task RegisterTypeDefinitionAsync_WithoutProviderSupport_UsesInheritedDefaultAsync() {
    IWorkCoordinator coordinator = new MinimalWorkCoordinator();

    var result = await coordinator.RegisterTypeDefinitionAsync("x", "x", "x", 0);

    await Assert.That(result).IsNotNull();
  }

  [Test]
  public async Task GetStateBasedStreamIdsAsync_WithoutProviderSupport_UsesInheritedDefaultAsync() {
    IWorkCoordinator coordinator = new MinimalWorkCoordinator();

    var result = await coordinator.GetStateBasedStreamIdsAsync([]);

    await Assert.That(result).IsEmpty();
  }

  [Test]
  public async Task SyncEphemeralTypeGraceAsync_WithoutProviderSupport_UsesInheritedDefaultAsync() {
    IWorkCoordinator coordinator = new MinimalWorkCoordinator();

    var task = coordinator.SyncEphemeralTypeGraceAsync([]);

    await Assert.That(task.IsCompletedSuccessfully).IsTrue();
    await task;
  }

  [Test]
  public async Task SyncPerspectiveRetentionAsync_WithoutProviderSupport_UsesInheritedDefaultAsync() {
    IWorkCoordinator coordinator = new MinimalWorkCoordinator();

    var task = coordinator.SyncPerspectiveRetentionAsync([]);

    await Assert.That(task.IsCompletedSuccessfully).IsTrue();
    await task;
  }

  [Test]
  public async Task GetEphemeralPairsNeedingSnapshotAsync_WithoutProviderSupport_UsesInheritedDefaultAsync() {
    IWorkCoordinator coordinator = new MinimalWorkCoordinator();

    var result = await coordinator.GetEphemeralPairsNeedingSnapshotAsync();

    await Assert.That(result).IsEmpty();
  }

  [Test]
  public async Task AdvanceIntegrityCheckpointAsync_WithoutProviderSupport_UsesInheritedDefaultAsync() {
    IWorkCoordinator coordinator = new MinimalWorkCoordinator();

    var result = await coordinator.AdvanceIntegrityCheckpointAsync();

    await Assert.That(result).IsNull();
  }

  [Test]
  public async Task CountReceivedFromOriginAsync_WithoutProviderSupport_UsesInheritedDefaultAsync() {
    IWorkCoordinator coordinator = new MinimalWorkCoordinator();

    var result = await coordinator.CountReceivedFromOriginAsync(Guid.Empty, 0L, 0L);

    await Assert.That(result).IsEmpty();
  }

  [Test]
  public async Task GetConsumedTypeRegistrationsAsync_WithoutProviderSupport_UsesInheritedDefaultAsync() {
    IWorkCoordinator coordinator = new MinimalWorkCoordinator();

    var result = await coordinator.GetConsumedTypeRegistrationsAsync();

    await Assert.That(result).IsEmpty();
  }

  [Test]
  public async Task RegisterConsumedTypesAsync_WithoutProviderSupport_UsesInheritedDefaultAsync() {
    IWorkCoordinator coordinator = new MinimalWorkCoordinator();

    var task = coordinator.RegisterConsumedTypesAsync([], false);

    await Assert.That(task.IsCompletedSuccessfully).IsTrue();
    await task;
  }

  [Test]
  public async Task MarkConsumedTypeBackfillRequestedAsync_WithoutProviderSupport_UsesInheritedDefaultAsync() {
    IWorkCoordinator coordinator = new MinimalWorkCoordinator();

    var task = coordinator.MarkConsumedTypeBackfillRequestedAsync([]);

    await Assert.That(task.IsCompletedSuccessfully).IsTrue();
    await task;
  }

  [Test]
  public async Task ComputeStreamDigestsAsync_WithoutProviderSupport_UsesInheritedDefaultAsync() {
    IWorkCoordinator coordinator = new MinimalWorkCoordinator();

    var result = await coordinator.ComputeStreamDigestsAsync(null, [], TimeSpan.Zero);

    await Assert.That(result).IsEmpty();
  }

  [Test]
  public async Task ComputeTypeDigestsAsync_WithoutProviderSupport_UsesInheritedDefaultAsync() {
    IWorkCoordinator coordinator = new MinimalWorkCoordinator();

    var result = await coordinator.ComputeTypeDigestsAsync(null, [], TimeSpan.Zero);

    await Assert.That(result).IsNotNull();
  }

  [Test]
  public async Task GetPerspectiveCoverageGapsAsync_WithoutProviderSupport_UsesInheritedDefaultAsync() {
    IWorkCoordinator coordinator = new MinimalWorkCoordinator();

    var result = await coordinator.GetPerspectiveCoverageGapsAsync(TimeSpan.Zero, 0);

    await Assert.That(result).IsEmpty();
  }

  [Test]
  public async Task TryClaimIntegrityAuditCycleAsync_WithoutProviderSupport_UsesInheritedDefaultAsync() {
    IWorkCoordinator coordinator = new MinimalWorkCoordinator();

    var result = await coordinator.TryClaimIntegrityAuditCycleAsync(TimeSpan.Zero);

    await Assert.That(result).IsTrue();
  }

  [Test]
  public async Task TryClaimTypeDefinitionReconcileAsync_WithoutProviderSupport_UsesInheritedDefaultAsync() {
    IWorkCoordinator coordinator = new MinimalWorkCoordinator();

    var result = await coordinator.TryClaimTypeDefinitionReconcileAsync(TimeSpan.Zero);

    await Assert.That(result).IsTrue();
  }

  [Test]
  public async Task RecordOffloadClaimAsync_WithoutProviderSupport_UsesInheritedDefaultAsync() {
    IWorkCoordinator coordinator = new MinimalWorkCoordinator();

    var task = coordinator.RecordOffloadClaimAsync("x", "x");

    await Assert.That(task.IsCompletedSuccessfully).IsTrue();
    await task;
  }

  [Test]
  public async Task GetExpiredOffloadClaimsAsync_WithoutProviderSupport_UsesInheritedDefaultAsync() {
    IWorkCoordinator coordinator = new MinimalWorkCoordinator();

    var result = await coordinator.GetExpiredOffloadClaimsAsync(TimeSpan.Zero, 0);

    await Assert.That(result).IsEmpty();
  }

  [Test]
  public async Task RemoveOffloadClaimsAsync_WithoutProviderSupport_UsesInheritedDefaultAsync() {
    IWorkCoordinator coordinator = new MinimalWorkCoordinator();

    var task = coordinator.RemoveOffloadClaimsAsync([]);

    await Assert.That(task.IsCompletedSuccessfully).IsTrue();
    await task;
  }

  [Test]
  public async Task TryClaimOffloadSweepAsync_WithoutProviderSupport_UsesInheritedDefaultAsync() {
    IWorkCoordinator coordinator = new MinimalWorkCoordinator();

    var result = await coordinator.TryClaimOffloadSweepAsync(TimeSpan.Zero);

    await Assert.That(result).IsTrue();
  }

  [Test]
  public async Task GetPerspectiveRowsAboutToReapAsync_WithoutProviderSupport_UsesInheritedDefaultAsync() {
    IWorkCoordinator coordinator = new MinimalWorkCoordinator();

    var result = await coordinator.GetPerspectiveRowsAboutToReapAsync([], 0);

    await Assert.That(result).IsEmpty();
  }

  [Test]
  public async Task HoldPerspectiveRowDestructionAsync_WithoutProviderSupport_UsesInheritedDefaultAsync() {
    IWorkCoordinator coordinator = new MinimalWorkCoordinator();

    var task = coordinator.HoldPerspectiveRowDestructionAsync([], default);

    await Assert.That(task.IsCompletedSuccessfully).IsTrue();
    await task;
  }

  [Test]
  public async Task ReleasePerspectiveRowHoldsAsync_WithoutProviderSupport_UsesInheritedDefaultAsync() {
    IWorkCoordinator coordinator = new MinimalWorkCoordinator();

    var task = coordinator.ReleasePerspectiveRowHoldsAsync([]);

    await Assert.That(task.IsCompletedSuccessfully).IsTrue();
    await task;
  }

  [Test]
  public async Task AcknowledgeRetentionEnforcementAsync_WithoutProviderSupport_UsesInheritedDefaultAsync() {
    IWorkCoordinator coordinator = new MinimalWorkCoordinator();

    var task = coordinator.AcknowledgeRetentionEnforcementAsync("x");

    await Assert.That(task.IsCompletedSuccessfully).IsTrue();
    await task;
  }

  [Test]
  public async Task CountPerspectiveRetentionBacklogAsync_WithoutProviderSupport_UsesInheritedDefaultAsync() {
    IWorkCoordinator coordinator = new MinimalWorkCoordinator();

    var result = await coordinator.CountPerspectiveRetentionBacklogAsync("x");

    await Assert.That(result).IsEqualTo(0);
  }

  [Test]
  public async Task RequeueRowEvictionsAsync_WithoutProviderSupport_UsesInheritedDefaultAsync() {
    IWorkCoordinator coordinator = new MinimalWorkCoordinator();

    var task = coordinator.RequeueRowEvictionsAsync([]);

    await Assert.That(task.IsCompletedSuccessfully).IsTrue();
    await task;
  }

  [Test]
  public async Task GetPerspectiveTableNamesAsync_WithoutProviderSupport_UsesInheritedDefaultAsync() {
    IWorkCoordinator coordinator = new MinimalWorkCoordinator();

    var result = await coordinator.GetPerspectiveTableNamesAsync([]);

    await Assert.That(result).IsEmpty();
  }

  [Test]
  public async Task GetPerspectiveRowsByIdsAsync_WithoutProviderSupport_UsesInheritedDefaultAsync() {
    IWorkCoordinator coordinator = new MinimalWorkCoordinator();

    var result = await coordinator.GetPerspectiveRowsByIdsAsync("x", "x", []);

    await Assert.That(result).IsEmpty();
  }

  [Test]
  public async Task CascadeDeletePerspectiveRowsAsync_WithoutProviderSupport_UsesInheritedDefaultAsync() {
    IWorkCoordinator coordinator = new MinimalWorkCoordinator();

    var result = await coordinator.CascadeDeletePerspectiveRowsAsync("x", []);

    await Assert.That(result).IsEqualTo(0);
  }

  [Test]
  public async Task ReconcileFollowerPresenceAsync_WithoutProviderSupport_UsesInheritedDefaultAsync() {
    IWorkCoordinator coordinator = new MinimalWorkCoordinator();

    var result = await coordinator.ReconcileFollowerPresenceAsync("x", []);

    await Assert.That(result).IsEqualTo(0);
  }

  [Test]
  public async Task GetOwnAuditedEventTypesAsync_WithoutProviderSupport_UsesInheritedDefaultAsync() {
    IWorkCoordinator coordinator = new MinimalWorkCoordinator();

    var result = await coordinator.GetOwnAuditedEventTypesAsync();

    await Assert.That(result).IsEmpty();
  }

  [Test]
  public async Task GetStreamDigestsAsync_WithoutProviderSupport_UsesInheritedDefaultAsync() {
    IWorkCoordinator coordinator = new MinimalWorkCoordinator();

    var result = await coordinator.GetStreamDigestsAsync(null, []);

    await Assert.That(result).IsEmpty();
  }

  [Test]
  public async Task GetTypeDigestsAsync_WithoutProviderSupport_UsesInheritedDefaultAsync() {
    IWorkCoordinator coordinator = new MinimalWorkCoordinator();

    var result = await coordinator.GetTypeDigestsAsync(null, []);

    await Assert.That(result).IsEmpty();
  }

  [Test]
  public async Task VerifyDigestTableAsync_WithoutProviderSupport_UsesInheritedDefaultAsync() {
    IWorkCoordinator coordinator = new MinimalWorkCoordinator();

    var result = await coordinator.VerifyDigestTableAsync(TimeSpan.Zero);

    await Assert.That(result).IsNotNull();
  }

  [Test]
  public async Task RewriteTableAsync_WithoutProviderSupport_UsesInheritedDefaultAsync() {
    IWorkCoordinator coordinator = new MinimalWorkCoordinator();

    var result = await coordinator.RewriteTableAsync("x");

    await Assert.That(result).IsNull();
  }

  [Test]
  public async Task ClearTableRewriteRequestAsync_WithoutProviderSupport_UsesInheritedDefaultAsync() {
    IWorkCoordinator coordinator = new MinimalWorkCoordinator();

    var task = coordinator.ClearTableRewriteRequestAsync("x");

    await Assert.That(task.IsCompletedSuccessfully).IsTrue();
    await task;
  }

  [Test]
  public async Task RequestTableRewriteAsync_WithoutProviderSupport_UsesInheritedDefaultAsync() {
    IWorkCoordinator coordinator = new MinimalWorkCoordinator();

    var task = coordinator.RequestTableRewriteAsync("x");

    await Assert.That(task.IsCompletedSuccessfully).IsTrue();
    await task;
  }

  [Test]
  public async Task RecordInstanceStateAsync_WithoutProviderSupport_UsesInheritedDefaultAsync() {
    IWorkCoordinator coordinator = new MinimalWorkCoordinator();

    var result = await coordinator.RecordInstanceStateAsync(Guid.Empty, "x", null);

    await Assert.That(result).IsFalse();
  }

  [Test]
  public async Task RequestStandbyAsync_WithoutProviderSupport_UsesInheritedDefaultAsync() {
    IWorkCoordinator coordinator = new MinimalWorkCoordinator();

    var result = await coordinator.RequestStandbyAsync(Guid.Empty, "x");

    await Assert.That(result).IsFalse();
  }

  [Test]
  public async Task ClearStandbyRequestAsync_WithoutProviderSupport_UsesInheritedDefaultAsync() {
    IWorkCoordinator coordinator = new MinimalWorkCoordinator();

    var result = await coordinator.ClearStandbyRequestAsync(Guid.Empty);

    await Assert.That(result).IsFalse();
  }

  [Test]
  public async Task GetStandbyRequestAsync_WithoutProviderSupport_UsesInheritedDefaultAsync() {
    IWorkCoordinator coordinator = new MinimalWorkCoordinator();

    var result = await coordinator.GetStandbyRequestAsync();

    await Assert.That(result).IsNull();
  }

  [Test]
  public async Task EvictInstanceAsync_WithoutProviderSupport_UsesInheritedDefaultAsync() {
    IWorkCoordinator coordinator = new MinimalWorkCoordinator();

    var task = coordinator.EvictInstanceAsync(Guid.Empty, Guid.Empty, "x");

    await Assert.That(task.IsCompletedSuccessfully).IsTrue();
    await task;
  }

  [Test]
  public async Task IntegrityTryBeginReportBatchAsync_WithoutProviderSupport_UsesInheritedDefaultAsync() {
    IWorkCoordinator coordinator = new MinimalWorkCoordinator();

    var result = await coordinator.IntegrityTryBeginReportBatchAsync(Guid.Empty, [], default, TimeSpan.Zero);

    await Assert.That(result).IsNull();
  }

  [Test]
  public async Task IntegrityTryBeginRepairBatchAsync_WithoutProviderSupport_UsesInheritedDefaultAsync() {
    IWorkCoordinator coordinator = new MinimalWorkCoordinator();

    var result = await coordinator.IntegrityTryBeginRepairBatchAsync(Guid.Empty, [], default, TimeSpan.Zero, 0, 0);

    await Assert.That(result).IsNull();
  }

  [Test]
  public async Task IntegrityStampRepairWindowsAsync_WithoutProviderSupport_UsesInheritedDefaultAsync() {
    IWorkCoordinator coordinator = new MinimalWorkCoordinator();

    var task = coordinator.IntegrityStampRepairWindowsAsync(Guid.Empty, [], 0L, 0L);

    await Assert.That(task.IsCompletedSuccessfully).IsTrue();
    await task;
  }

  [Test]
  public async Task IntegrityClaimRepairDrainAsync_WithoutProviderSupport_UsesInheritedDefaultAsync() {
    IWorkCoordinator coordinator = new MinimalWorkCoordinator();

    var result = await coordinator.IntegrityClaimRepairDrainAsync([], default, TimeSpan.Zero, 0, 0);

    await Assert.That(result).IsEmpty();
  }

  [Test]
  public async Task IntegrityMarkHealedBatchAsync_WithoutProviderSupport_UsesInheritedDefaultAsync() {
    IWorkCoordinator coordinator = new MinimalWorkCoordinator();

    var result = await coordinator.IntegrityMarkHealedBatchAsync(Guid.Empty, []);

    await Assert.That(result).IsFalse();
  }

  [Test]
  public async Task IntegrityMarkHealedBatchWithAgesAsync_WithoutProviderSupport_UsesInheritedDefaultAsync() {
    IWorkCoordinator coordinator = new MinimalWorkCoordinator();

    var result = await coordinator.IntegrityMarkHealedBatchWithAgesAsync(Guid.Empty, []);

    await Assert.That(result).IsNull();
  }

  [Test]
  public async Task GetIntegrityLedgerSummaryAsync_WithoutProviderSupport_UsesInheritedDefaultAsync() {
    IWorkCoordinator coordinator = new MinimalWorkCoordinator();

    var result = await coordinator.GetIntegrityLedgerSummaryAsync(0);

    await Assert.That(result).IsNotNull();
  }

  [Test]
  public async Task GetIntegritySettledMaxAsync_WithoutProviderSupport_UsesInheritedDefaultAsync() {
    IWorkCoordinator coordinator = new MinimalWorkCoordinator();

    var result = await coordinator.GetIntegritySettledMaxAsync(null, TimeSpan.Zero);

    await Assert.That(result).IsNull();
  }

  [Test]
  public async Task ComputeTypeDigestsWindowedAsync_WithoutProviderSupport_UsesInheritedDefaultAsync() {
    IWorkCoordinator coordinator = new MinimalWorkCoordinator();

    var result = await coordinator.ComputeTypeDigestsWindowedAsync(null, [], 0L, null, TimeSpan.Zero);

    await Assert.That(result).IsNull();
  }

  [Test]
  public async Task ComputeStreamDigestsWindowedAsync_WithoutProviderSupport_UsesInheritedDefaultAsync() {
    IWorkCoordinator coordinator = new MinimalWorkCoordinator();

    var result = await coordinator.ComputeStreamDigestsWindowedAsync(null, [], 0L, null, null, 0, TimeSpan.Zero);

    await Assert.That(result).IsNull();
  }

  [Test]
  public async Task ComputeStreamDigestsForChunkAsync_WithoutProviderSupport_UsesInheritedDefaultAsync() {
    IWorkCoordinator coordinator = new MinimalWorkCoordinator();

    var result = await coordinator.ComputeStreamDigestsForChunkAsync(Guid.Empty, [], null, null, TimeSpan.Zero);

    await Assert.That(result).IsNull();
  }

  [Test]
  public async Task GetIntegritySealAsync_WithoutProviderSupport_UsesInheritedDefaultAsync() {
    IWorkCoordinator coordinator = new MinimalWorkCoordinator();

    var result = await coordinator.GetIntegritySealAsync(Guid.Empty);

    await Assert.That(result).IsEqualTo(0);
  }

  [Test]
  public async Task AdvanceIntegritySealAsync_WithoutProviderSupport_UsesInheritedDefaultAsync() {
    IWorkCoordinator coordinator = new MinimalWorkCoordinator();

    var task = coordinator.AdvanceIntegritySealAsync(Guid.Empty, 0L);

    await Assert.That(task.IsCompletedSuccessfully).IsTrue();
    await task;
  }

  [Test]
  public async Task VerifyDigestEpochsAsync_WithoutProviderSupport_UsesInheritedDefaultAsync() {
    IWorkCoordinator coordinator = new MinimalWorkCoordinator();

    var result = await coordinator.VerifyDigestEpochsAsync(TimeSpan.Zero, 0);

    await Assert.That(result).IsNotNull();
  }

  [Test]
  public async Task GetIntegrityOriginGenerationAsync_WithoutProviderSupport_UsesInheritedDefaultAsync() {
    IWorkCoordinator coordinator = new MinimalWorkCoordinator();

    var result = await coordinator.GetIntegrityOriginGenerationAsync();

    await Assert.That(result).IsEqualTo(0);
  }

  [Test]
  public async Task EnsureIntegritySealGenerationAsync_WithoutProviderSupport_UsesInheritedDefaultAsync() {
    IWorkCoordinator coordinator = new MinimalWorkCoordinator();

    var result = await coordinator.EnsureIntegritySealGenerationAsync(Guid.Empty, 0L);

    await Assert.That(result).IsTrue();
  }

  [Test]
  public async Task CloseStreamAsync_WithoutProviderSupport_UsesInheritedDefaultAsync() {
    IWorkCoordinator coordinator = new MinimalWorkCoordinator();

    var result = await coordinator.CloseStreamAsync(Guid.Empty, 0L, false);

    await Assert.That(result).IsNotNull();
  }

  [Test]
  public async Task GetArchivedEventsAsync_WithoutProviderSupport_UsesInheritedDefaultAsync() {
    IWorkCoordinator coordinator = new MinimalWorkCoordinator();

    var result = await coordinator.GetArchivedEventsAsync(Guid.Empty);

    await Assert.That(result).IsEmpty();
  }

  [Test]
  public async Task GetEventVersionAsync_WithoutProviderSupport_UsesInheritedDefaultAsync() {
    IWorkCoordinator coordinator = new MinimalWorkCoordinator();

    var result = await coordinator.GetEventVersionAsync(Guid.Empty);

    await Assert.That(result).IsNull();
  }

  [Test]
  public async Task GetEphemeralBodiesAboutToReapAsync_WithoutProviderSupport_UsesInheritedDefaultAsync() {
    IWorkCoordinator coordinator = new MinimalWorkCoordinator();

    var result = await coordinator.GetEphemeralBodiesAboutToReapAsync();

    await Assert.That(result).IsEmpty();
  }

  [Test]
  public async Task HoldEphemeralDestructionAsync_WithoutProviderSupport_UsesInheritedDefaultAsync() {
    IWorkCoordinator coordinator = new MinimalWorkCoordinator();

    var task = coordinator.HoldEphemeralDestructionAsync([], default);

    await Assert.That(task.IsCompletedSuccessfully).IsTrue();
    await task;
  }

  [Test]
  public async Task GetPendingCoalesceGroupStatsAsync_WithoutProviderSupport_UsesInheritedDefaultAsync() {
    IWorkCoordinator coordinator = new MinimalWorkCoordinator();

    var result = await coordinator.GetPendingCoalesceGroupStatsAsync();

    await Assert.That(result).IsEmpty();
  }

  [Test]
  public async Task FetchPendingCoalesceAsync_WithoutProviderSupport_UsesInheritedDefaultAsync() {
    IWorkCoordinator coordinator = new MinimalWorkCoordinator();

    var result = await coordinator.FetchPendingCoalesceAsync("x", 0);

    await Assert.That(result).IsEmpty();
  }

  [Test]
  public async Task CompleteCoalesceFoldAsync_WithoutProviderSupport_UsesInheritedDefaultAsync() {
    IWorkCoordinator coordinator = new MinimalWorkCoordinator();

    var task = coordinator.CompleteCoalesceFoldAsync([], [], 0);

    await Assert.That(task.IsCompletedSuccessfully).IsTrue();
    await task;
  }

  [Test]
  public async Task ReleaseMaturedCoalesceAsync_WithoutProviderSupport_UsesInheritedDefaultAsync() {
    IWorkCoordinator coordinator = new MinimalWorkCoordinator();

    var result = await coordinator.ReleaseMaturedCoalesceAsync("x");

    await Assert.That(result).IsEqualTo(0);
  }

  // --- Defaults whose parameters need a real instance ------------------------

  [Test]
  public async Task RecordDefinitionLineageAsync_WithoutProviderSupport_UsesInheritedDefaultAsync() {
    IWorkCoordinator coordinator = new MinimalWorkCoordinator();

    var task = coordinator.RecordDefinitionLineageAsync(
        fromDefinitionId: 1,
        toDefinitionId: 2,
        DefinitionRelationship.SchemaUpgradedTo,
        migrationRef: null);

    await Assert.That(task.IsCompletedSuccessfully).IsTrue();
    await task;
  }

  [Test]
  public async Task SelectRedeliveryEventsAsync_WithoutProviderSupport_ReturnsNoEventsAsync() {
    IWorkCoordinator coordinator = new MinimalWorkCoordinator();

    var result = await coordinator.SelectRedeliveryEventsAsync(new RedeliveryRequest());

    await Assert.That(result).IsEmpty();
  }

  [Test]
  public async Task RecordPerspectiveRowDestructionFailureAsync_WithoutProviderSupport_ReportsExhaustedAsync() {
    // The default returns int.MaxValue, not zero: a provider that cannot record the
    // failure reports the retry budget as spent so the caller stops retrying rather
    // than looping forever against a store that will never persist the attempt.
    IWorkCoordinator coordinator = new MinimalWorkCoordinator();

    var result = await coordinator.RecordPerspectiveRowDestructionFailureAsync(
        [new Whizbang.Core.Lifecycle.PerspectiveRowRef("orders", Guid.Empty)],
        TimeSpan.Zero,
        maxRetries: 3,
        Whizbang.Core.Lifecycle.OnDestroyFailure.RetryThenForcedDelete);

    await Assert.That(result).IsEqualTo(int.MaxValue);
  }

  [Test]
  public async Task RecordDestructionFailureAsync_WithoutProviderSupport_ReportsExhaustedAsync() {
    IWorkCoordinator coordinator = new MinimalWorkCoordinator();

    var result = await coordinator.RecordDestructionFailureAsync(
        [Guid.Empty],
        retryHoldUntil: default,
        maxRetries: 3,
        Whizbang.Core.Lifecycle.OnDestroyFailure.RetryThenForcedDelete);

    await Assert.That(result).IsEqualTo(int.MaxValue);
  }

  [Test]
  public async Task ImportBrokerDeadLetterAsync_WithoutProviderSupport_RefusesLoudlyAsync() {
    // The one capability whose default throws instead of degrading quietly. Returning
    // false here would read as "nothing to import" and let the caller move on, while
    // the message is still sitting on the broker DLQ — so it refuses instead.
    IWorkCoordinator coordinator = new MinimalWorkCoordinator();

    var import = new Whizbang.Core.Transports.BrokerDeadLetterImport(
        MessageId: Guid.Empty,
        StreamId: null,
        MessageType: null,
        Destination: "orders",
        EnvelopeJson: "{}",
        BrokerReason: null,
        BrokerDescription: null,
        EnqueuedAt: null,
        DeliveryCount: null);

    await Assert.That(async () => await coordinator.ImportBrokerDeadLetterAsync(import))
        .ThrowsExactly<NotSupportedException>();
  }
}
