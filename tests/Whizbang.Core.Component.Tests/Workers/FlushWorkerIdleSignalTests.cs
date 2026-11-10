// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Each of the five flush workers reports when the work-table writes queued to it have landed (#1899), so a caller that
/// waited for the drain workers to go idle can also wait for the completions, failures, lease renewals and handler
/// commits they queued. The worker's signal is its flusher's: an item offered through the worker's channel surface
/// is pending until the coordinator call carrying it returns.
/// </summary>
/// <remarks>
/// The coordinator holds every write open until the test releases it and says when a write has entered, so "not idle"
/// is asserted while the write is known to be in progress and "idle" only after the write returned.
/// </remarks>
/// <code-under-test>src/Whizbang.Core/Workers/OutboxCompletionFlushWorker.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Workers/PerspectiveCompletionFlushWorker.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Workers/FailureFlushWorker.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Workers/LeaseRenewalWorker.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Workers/InboxHandlerWorker.cs</code-under-test>
/// <docs>fundamentals/work-coordinator/batched-flushers#when-writes-have-landed</docs>
[Category("Workers")]
public sealed class FlushWorkerIdleSignalTests {
  private static BatchFlusherOptions _immediate() => new() { CoalesceWindowMs = 0, MaxBatchSize = 10, ImmediateFlushThreshold = 1 };

  /// <summary>Holds every write open until released, and says when one has entered.</summary>
  private sealed class HoldsEveryWrite : IWorkCoordinator {
    private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task Entered => _entered.Task;

    public void Release() => _release.TrySetResult();

    private async Task _holdAsync(CancellationToken ct) {
      _entered.TrySetResult();
      await _release.Task.WaitAsync(ct);
    }

    public async Task<int> CompleteOutboxPublishedAsync(IReadOnlyList<Guid> ids, bool debugMode, CancellationToken cancellationToken = default) {
      await _holdAsync(cancellationToken);
      return ids.Count;
    }
    public Task CompletePerspectiveAsync(IReadOnlyList<PerspectiveCursorCompletion> cursors, IReadOnlyList<Guid> eventWorkIds, bool debugMode, CancellationToken cancellationToken = default)
      => _holdAsync(cancellationToken);
    public async Task<IReadOnlyList<Guid>> RenewHeldLeasesAsync(WorkCategory category, Guid instanceId, IReadOnlyList<Guid> ids, int leaseSeconds = 300, CancellationToken cancellationToken = default) {
      await _holdAsync(cancellationToken);
      return [];
    }
    public Task ReportFailuresAsync(WorkCategory category, IReadOnlyList<MessageFailure> failures, CancellationToken cancellationToken = default)
      => _holdAsync(cancellationToken);
    public async Task<IReadOnlyList<HandlerBatchResult>> CommitHandlerBatchAsync(IReadOnlyList<HandlerCommitRequest> requests, CancellationToken cancellationToken = default) {
      await _holdAsync(cancellationToken);
      return [];
    }
    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new WorkCoordinatorStatistics());
    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task StoreOutboxMessagesAsync(OutboxMessage[] messages, int partitionCount, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion completion, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure failure, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default) => Task.FromResult<PerspectiveCursorInfo?>(null);
  }

  private sealed class NoFailures : IFailureChannel {
    public ValueTask EnqueueAsync(WorkCategory category, MessageFailure failure, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
  }

  private static ServiceProvider _services(HoldsEveryWrite coordinator) =>
    new ServiceCollection().AddSingleton<IWorkCoordinator>(coordinator).BuildServiceProvider();

  /// <summary>
  /// Offers one item, holds its write, and asserts the worker reports it pending until the write returns, then idle
  /// with the idle event raised once.
  /// </summary>
  private static async Task _assertReportsTheWriteUntilItLandsAsync(
      IQueuedWriteFlusher worker, HoldsEveryWrite coordinator, Func<ValueTask> offer, CancellationToken ct) {
    var idleRaised = 0;
    void onIdle() => Interlocked.Increment(ref idleRaised);
    worker.OnWorkProcessingIdle += onIdle;

    await Assert.That(worker.IsIdle).IsTrue();
    await offer();
    var wait = worker.WaitForIdleAsync(ct);
    await coordinator.Entered.WaitAsync(ct);

    await Assert.That(worker.IsIdle).IsFalse();
    await Assert.That(worker.Activity).IsEqualTo(new QueuedWriteActivity(Version: 1, Pending: 1));
    await Assert.That(wait.IsCompleted).IsFalse()
      .Because("the write the worker was given is still in progress (#1899)");

    coordinator.Release();
    await wait;

    await Assert.That(worker.IsIdle).IsTrue();
    await Assert.That(worker.Activity).IsEqualTo(new QueuedWriteActivity(Version: 1, Pending: 0));
    await Assert.That(Volatile.Read(ref idleRaised)).IsEqualTo(1);

    worker.OnWorkProcessingIdle -= onIdle;
  }

  [Test]
  [Timeout(30000)]
  public async Task OutboxCompletionFlushWorker_ReportsAQueuedCompletionUntilItLandsAsync(CancellationToken cancellationToken) {
    var coordinator = new HoldsEveryWrite();
    await using var sp = _services(coordinator);
    var worker = new OutboxCompletionFlushWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      Options.Create(new OutboxCompletionFlushWorkerOptions { Flusher = _immediate() }),
      Options.Create(new WorkCoordinatorOptions()),
      NullLogger<OutboxCompletionFlushWorker>.Instance,
      NoOpPinnedConnectionPool.Instance);
    await worker.StartAsync(cancellationToken);

    await _assertReportsTheWriteUntilItLandsAsync(worker, coordinator,
      () => worker.EnqueueAsync(Guid.CreateVersion7(), cancellationToken), cancellationToken);

    await worker.StopAsync(CancellationToken.None);
  }

  [Test]
  [Timeout(30000)]
  public async Task PerspectiveCompletionFlushWorker_ReportsAQueuedCompletionUntilItLandsAsync(CancellationToken cancellationToken) {
    var coordinator = new HoldsEveryWrite();
    await using var sp = _services(coordinator);
    var worker = new PerspectiveCompletionFlushWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      Options.Create(new PerspectiveCompletionFlushWorkerOptions { Flusher = _immediate() }),
      Options.Create(new WorkCoordinatorOptions()),
      NullLogger<PerspectiveCompletionFlushWorker>.Instance,
      NoOpPinnedConnectionPool.Instance);
    await worker.StartAsync(cancellationToken);

    await _assertReportsTheWriteUntilItLandsAsync(worker, coordinator,
      () => worker.EnqueueEventWorkIdAsync(Guid.CreateVersion7(), cancellationToken), cancellationToken);

    await worker.StopAsync(CancellationToken.None);
  }

  [Test]
  [Timeout(30000)]
  public async Task FailureFlushWorker_ReportsAQueuedFailureUntilItLandsAsync(CancellationToken cancellationToken) {
    var coordinator = new HoldsEveryWrite();
    await using var sp = _services(coordinator);
    var worker = new FailureFlushWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      Options.Create(new FailureFlushWorkerOptions { Flusher = _immediate() }),
      NullLogger<FailureFlushWorker>.Instance,
      NoOpPinnedConnectionPool.Instance);
    await worker.StartAsync(cancellationToken);

    await _assertReportsTheWriteUntilItLandsAsync(worker, coordinator,
      () => worker.EnqueueAsync(WorkCategory.Outbox, new MessageFailure {
        MessageId = Guid.CreateVersion7(),
        CompletedStatus = MessageProcessingStatus.None,
        Error = "failed",
      }, cancellationToken), cancellationToken);

    await worker.StopAsync(CancellationToken.None);
  }

  [Test]
  [Timeout(30000)]
  public async Task LeaseRenewalWorker_ReportsAQueuedRenewalUntilItLandsAsync(CancellationToken cancellationToken) {
    var coordinator = new HoldsEveryWrite();
    await using var sp = _services(coordinator);
    var worker = new LeaseRenewalWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      Options.Create(new LeaseRenewalWorkerOptions { Flusher = _immediate() }),
      NullLogger<LeaseRenewalWorker>.Instance,
      NoOpPinnedConnectionPool.Instance,
      new ServiceInstanceProvider((Guid)TrackedGuid.New(), "svc", "host", 1));
    await worker.StartAsync(cancellationToken);

    await _assertReportsTheWriteUntilItLandsAsync(worker, coordinator,
      () => worker.EnqueueAsync(WorkCategory.Inbox, Guid.CreateVersion7(), cancellationToken), cancellationToken);

    await worker.StopAsync(CancellationToken.None);
  }

  [Test]
  [Timeout(30000)]
  public async Task InboxHandlerWorker_ReportsAQueuedCommitUntilItLandsAsync(CancellationToken cancellationToken) {
    var coordinator = new HoldsEveryWrite();
    await using var sp = _services(coordinator);
    var worker = new InboxHandlerWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      new NoFailures(),
      Options.Create(new InboxHandlerWorkerOptions { Flusher = _immediate() }),
      NullLogger<InboxHandlerWorker>.Instance,
      NoOpPinnedConnectionPool.Instance,
      new InboxChannelWriter());
    await worker.StartAsync(cancellationToken);

    await _assertReportsTheWriteUntilItLandsAsync(worker, coordinator,
      () => worker.EnqueueAsync(new HandlerCommitRequest(
        HandlerId: Guid.CreateVersion7(),
        InstanceId: Guid.CreateVersion7(),
        ServiceName: "test",
        HostName: "test-host",
        ProcessId: 1,
        PartitionCount: 10_000,
        InboxCompletion: new HandlerInboxCompletion(Guid.CreateVersion7(), 2)), cancellationToken), cancellationToken);

    await worker.StopAsync(CancellationToken.None);
  }
}
