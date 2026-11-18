// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// The caller's half of the stored ceiling (#2288): what a decision reports is written to the store, and a failed write
/// never fails the pass.
/// </summary>
/// <remarks>
/// The coordinator decides under a lock and does no I/O there, so the change it reports is carried out here, by the
/// caller that measured the backlog. Recording is best-effort: the in-memory ceiling still holds when a write fails, so
/// a failure is logged as a warning and the pass goes on.
/// </remarks>
/// <code-under-test>src/Whizbang.Core/Workers/HousekeepingDeferralStore.cs</code-under-test>
/// <docs>operations/workers/housekeeping-arbitration#the-ceiling-survives-a-restart</docs>
[Category("Workers")]
public class HousekeepingDeferralStoreTests {

  private static readonly DateTimeOffset _at = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

  private static readonly HousekeepingCoordinator.Decision _refusedFirst =
    new(false, HousekeepingCoordinator.Verdict.ServiceBusy) {
      DeferralToRecord = new HousekeepingCoordinator.DeferralRecord("maintenance", _at),
    };

  private static readonly HousekeepingCoordinator.Decision _grantedWhileStored =
    new(true, HousekeepingCoordinator.Verdict.Proceed) { DeferralToClear = "maintenance" };

  private static readonly string[] _maintenanceOnly = ["maintenance"];

  private static readonly (string, DateTimeOffset)[] _maintenanceAt = [("maintenance", _at)];

  private static readonly HousekeepingCoordinator.Decision _plain = new(true, HousekeepingCoordinator.Verdict.Proceed);

  [Test]
  public async Task ARecordTheDecisionReports_IsWrittenAsync() {
    var store = new DeferralRecordingCoordinator();

    await HousekeepingDeferralStore.RecordAsync(store, _refusedFirst, new FakeLogger(), CancellationToken.None);

    await Assert.That(store.Recorded).IsEquivalentTo(_maintenanceAt);
  }

  [Test]
  public async Task AClearTheDecisionReports_IsWrittenAsync() {
    var store = new DeferralRecordingCoordinator();

    await HousekeepingDeferralStore.ClearAsync(store, _grantedWhileStored, new FakeLogger());

    await Assert.That(store.Cleared).IsEquivalentTo(_maintenanceOnly);
  }

  [Test]
  public async Task ADecisionThatReportsNothing_WritesNothingAsync() {
    var store = new DeferralRecordingCoordinator();

    await HousekeepingDeferralStore.RecordAsync(store, _plain, new FakeLogger(), CancellationToken.None);
    await HousekeepingDeferralStore.ClearAsync(store, _plain, new FakeLogger());
    await HousekeepingDeferralStore.RecordAsync(coordinator: null, _refusedFirst, new FakeLogger(), CancellationToken.None);
    await HousekeepingDeferralStore.ClearAsync(coordinator: null, _grantedWhileStored, new FakeLogger());

    await Assert.That(store.Recorded).IsEmpty();
    await Assert.That(store.Cleared).IsEmpty()
      .Because("a pass with nothing stored costs no write, and a host without a work coordinator has nowhere to write");
  }

  [Test]
  public async Task AFailedRecord_IsLogged_AndThePassGoesOnAsync() {
    var store = new DeferralRecordingCoordinator { Failure = new InvalidOperationException("store unavailable") };
    var logger = new FakeLogger();

    await HousekeepingDeferralStore.RecordAsync(store, _refusedFirst, logger, CancellationToken.None);

    var entry = logger.Collector.LatestRecord;
    await Assert.That(entry.Level).IsEqualTo(LogLevel.Warning);
    await Assert.That(entry.Message).Contains("maintenance")
      .Because("a non-rethrowing catch says WHAT failed: the budget whose first deferral was not recorded");
    await Assert.That(entry.Exception).IsSameReferenceAs(store.Failure);
  }

  [Test]
  public async Task AFailedClear_IsLogged_AndThePassGoesOnAsync() {
    var store = new DeferralRecordingCoordinator { Failure = new InvalidOperationException("store unavailable") };
    var logger = new FakeLogger();

    await HousekeepingDeferralStore.ClearAsync(store, _grantedWhileStored, logger);

    var entry = logger.Collector.LatestRecord;
    await Assert.That(entry.Level).IsEqualTo(LogLevel.Warning);
    await Assert.That(entry.Message).Contains("maintenance");
    await Assert.That(entry.Exception).IsSameReferenceAs(store.Failure);
  }

  [Test]
  public async Task ACanceledWrite_PropagatesAsync() {
    var store = new DeferralRecordingCoordinator { Failure = new OperationCanceledException() };

    await Assert.That(async () => await HousekeepingDeferralStore.RecordAsync(
      store, _refusedFirst, new FakeLogger(), CancellationToken.None)).Throws<OperationCanceledException>()
      .Because("a canceled write is a stopping host, not a store failure to log and absorb");
    await Assert.That(async () => await HousekeepingDeferralStore.ClearAsync(
      store, _grantedWhileStored, new FakeLogger())).Throws<OperationCanceledException>();
  }
}

/// <summary>A work coordinator that records the housekeeping-deferral writes it is asked for, or fails them.</summary>
internal sealed class DeferralRecordingCoordinator : IWorkCoordinator {
  public List<(string Key, DateTimeOffset At)> Recorded { get; } = [];
  public List<string> Cleared { get; } = [];
  public Exception? Failure { get; init; }
  public ServiceBacklog? Backlog { get; set; }

  public ValueTask<ServiceBacklog?> CountServiceBacklogAsync(CancellationToken cancellationToken = default) =>
    ValueTask.FromResult(Backlog);

  public ValueTask RecordHousekeepingDeferralAsync(string budgetKey, DateTimeOffset firstDeferredAt, CancellationToken cancellationToken = default) {
    if (Failure is not null) {
      throw Failure;
    }
    Recorded.Add((budgetKey, firstDeferredAt));
    return ValueTask.CompletedTask;
  }

  public ValueTask ClearHousekeepingDeferralAsync(string budgetKey, CancellationToken cancellationToken = default) {
    if (Failure is not null) {
      throw Failure;
    }
    Cleared.Add(budgetKey);
    return ValueTask.CompletedTask;
  }

  public Task<IReadOnlyList<MaintenanceResult>> PerformMaintenanceAsync(CancellationToken cancellationToken = default) =>
    Task.FromResult<IReadOnlyList<MaintenanceResult>>([]);
  public Task<WorkBatch> ClaimWorkAsync(ClaimWorkRequest request, CancellationToken cancellationToken = default) =>
    Task.FromResult(new WorkBatch { OutboxWork = [], InboxWork = [], PerspectiveWork = [] });
  public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default) => Task.CompletedTask;
  public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken cancellationToken = default) =>
    Task.FromResult(new WorkCoordinatorStatistics());
  public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount, CancellationToken cancellationToken = default) => Task.CompletedTask;
  public Task<PartitionRecomputeResult> RecomputePartitionNumbersAsync(int partitionCount, CancellationToken cancellationToken = default) =>
    Task.FromResult(new PartitionRecomputeResult());
  public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion completion, CancellationToken cancellationToken = default) => Task.CompletedTask;
  public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure failure, CancellationToken cancellationToken = default) => Task.CompletedTask;
  public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default) =>
    Task.FromResult<PerspectiveCursorInfo?>(null);
}
