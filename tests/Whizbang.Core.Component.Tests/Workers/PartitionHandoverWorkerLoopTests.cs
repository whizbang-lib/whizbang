// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives.Sync;
using Whizbang.Core.Startup;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// The handover worker as hosted (#2557): it looks for partitions to hand over once an interval, and a pass that fails
/// is reported and retried on the next, never stopping the host. Driven on a fake clock and the coordinator's own calls.
/// </summary>
/// <docs>fundamentals/work-coordinator/partition-assignment#handover</docs>
/// <code-under-test>src/Whizbang.Core/Workers/PartitionHandoverWorker.cs</code-under-test>
public sealed class PartitionHandoverWorkerLoopTests {
  private static readonly int[][] _askedThenReAsked = [[3, 5], [5]];

  [Test]
  [Timeout(30000)]
  public async Task AFailedPass_IsRetriedOnTheNextInterval_AndThenHandsOverAsync(CancellationToken cancellationToken) {
    var coordinator = new ScriptedCoordinator();
    var time = new FakeTimeProvider();
    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coordinator);
    using var worker = new PartitionHandoverWorker(
      services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(), new Instance(), new PartitionTenures(), [],
      NullLogger<PartitionHandoverWorker>.Instance, new PartitionHandoverWorkerSettings { Time = time });

    await worker.StartAsync(cancellationToken);
    await coordinator.FirstAsked.WaitAsync(cancellationToken);
    time.Advance(PartitionHandoverWorker.Interval);
    var acknowledged = await coordinator.Acknowledged.WaitAsync(cancellationToken);
    await worker.StopAsync(cancellationToken);

    await Assert.That(acknowledged).IsEquivalentTo([new PartitionHandover(3, 5, Guid.Empty)])
      .Because("the pass after the failed one hands the partition over");
  }

  [Test]
  [Timeout(30000)]
  public async Task AnAcknowledgementThatFlipsPartOfWhatItNamed_IsReAskedOnTheNextInterval_UntilTheRestMoveAsync(
      CancellationToken cancellationToken) {
    // #2577: the database flips only the partitions no writer holds and leaves the rest handing over.
    var coordinator = new PartialCoordinator([3, 5]) { HeldOnFirstAsk = 5 };
    var time = new FakeTimeProvider();
    var worker = _worker(coordinator, time);

    await worker.StartAsync(cancellationToken);
    await coordinator.FirstAcknowledged.WaitAsync(cancellationToken);
    time.Advance(PartitionHandoverWorker.Interval);
    await coordinator.NothingLeft.WaitAsync(cancellationToken);
    await worker.StopAsync(cancellationToken);

    await Assert.That(coordinator.Asked).IsEquivalentTo(_askedThenReAsked)
      .Because("the pass after a partial acknowledgement asks again for exactly what did not move");
  }

  [Test]
  [Timeout(30000)]
  public async Task Leaving_HandsOverUntilNothingIsOwned_ThenReturnsAsync(CancellationToken cancellationToken) {
    var coordinator = new ScriptedCoordinator { FailFirstAsk = false };
    coordinator.Statuses.Enqueue(new PartitionLeaveStatus(1, 1));
    coordinator.Statuses.Enqueue(new PartitionLeaveStatus(0, 1));
    var time = new FakeTimeProvider();
    var worker = _worker(coordinator, time);

    var leaving = worker.ReleaseHeldWorkAsync(cancellationToken);
    var acknowledged = await coordinator.Acknowledged.WaitAsync(cancellationToken);
    time.Advance(PartitionHandoverWorker.Interval);
    await leaving.WaitAsync(cancellationToken);

    await Assert.That(acknowledged).Count().IsEqualTo(1).Because("a stopping instance hands its partitions over before it deregisters");
    await Assert.That(coordinator.StatusReads).IsEqualTo(2);
  }

  [Test]
  public async Task Leaving_BeforeTheHeartbeatsStartPoint_AsksTheDatabaseNothingAsync() {
    var coordinator = new ScriptedCoordinator { FailFirstAsk = false };
    var stages = new Stages(reached: false);
    var worker = _worker(coordinator, new FakeTimeProvider(), stages: stages);

    await worker.ReleaseHeldWorkAsync(CancellationToken.None);

    await Assert.That(coordinator.StatusReads).IsEqualTo(0)
      .Because("an instance stopped before it was ever live was never given a partition, and its database may not be ready");
    await Assert.That(stages.Asked).IsEqualTo(HeartbeatWorker.StartsAt);
  }

  [Test]
  public async Task Leaving_PastTheHeartbeatsStartPoint_ReadsWhatItOwnsAsync() {
    var coordinator = new ScriptedCoordinator { FailFirstAsk = false };
    coordinator.Statuses.Enqueue(new PartitionLeaveStatus(0, 1));
    var worker = _worker(coordinator, new FakeTimeProvider(), stages: new Stages(reached: true));

    await worker.ReleaseHeldWorkAsync(CancellationToken.None);

    await Assert.That(coordinator.StatusReads).IsEqualTo(1);
  }

  [Test]
  public async Task Leaving_WithNoOtherLiveInstance_ReturnsAtOnceAsync() {
    var coordinator = new ScriptedCoordinator { FailFirstAsk = false };
    coordinator.Statuses.Enqueue(new PartitionLeaveStatus(10_000, 0));
    var worker = _worker(coordinator, new FakeTimeProvider());

    await worker.ReleaseHeldWorkAsync(CancellationToken.None);

    await Assert.That(coordinator.Asks).IsEqualTo(0).Because("nobody could take the partitions, so there is nothing to wait for");
  }

  [Test]
  [Timeout(30000)]
  public async Task Leaving_GivesUpAfterTheHandoverDeadline_AndAStopEndsTheWaitAsync(CancellationToken cancellationToken) {
    var coordinator = new ScriptedCoordinator { FailFirstAsk = false, HandOverNothing = true };
    coordinator.Statuses.Enqueue(new PartitionLeaveStatus(3, 1));
    var time = new FakeTimeProvider();
    var worker = _worker(coordinator, time, leaveBudget: PartitionHandoverWorker.Interval);

    var leaving = worker.ReleaseHeldWorkAsync(cancellationToken);
    await coordinator.FirstAsked.WaitAsync(cancellationToken);
    time.Advance(PartitionHandoverWorker.Interval);
    await leaving.WaitAsync(cancellationToken);
    using var stopped = new CancellationTokenSource();
    var second = worker.ReleaseHeldWorkAsync(stopped.Token);
    await coordinator.SecondAsked.WaitAsync(cancellationToken);
    await stopped.CancelAsync();
    await second.WaitAsync(cancellationToken);

    await Assert.That(coordinator.Asks).IsEqualTo(3).Because("the first leave gave up after its budget; the second ended at the stop");
  }

  [Test]
  public async Task TheWorker_RefusesMissingCollaboratorsAsync() {
    var scopes = new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    await Assert.That(() => new PartitionHandoverWorker(null!, new Instance(), new PartitionTenures(), [], NullLogger<PartitionHandoverWorker>.Instance))
      .Throws<ArgumentNullException>();
    await Assert.That(() => new PartitionHandoverWorker(scopes, null!, new PartitionTenures(), [], NullLogger<PartitionHandoverWorker>.Instance))
      .Throws<ArgumentNullException>();
    await Assert.That(() => new PartitionHandoverWorker(scopes, new Instance(), null!, [], NullLogger<PartitionHandoverWorker>.Instance))
      .Throws<ArgumentNullException>();
    await Assert.That(() => new PartitionHandoverWorker(scopes, new Instance(), new PartitionTenures(), null!, NullLogger<PartitionHandoverWorker>.Instance))
      .Throws<ArgumentNullException>();
    await Assert.That(() => new PartitionHandoverWorker(scopes, new Instance(), new PartitionTenures(), [], null!))
      .Throws<ArgumentNullException>();
  }

  private static PartitionHandoverWorker _worker(
      IWorkCoordinator coordinator, FakeTimeProvider time, TimeSpan? leaveBudget = null, IStartupStageGate? stages = null) {
    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coordinator);
    return new PartitionHandoverWorker(
      services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(), new Instance(), new PartitionTenures(), [],
      NullLogger<PartitionHandoverWorker>.Instance,
      new PartitionHandoverWorkerSettings { Time = time, LeaveBudget = leaveBudget, Stages = stages });
  }

  private sealed class Stages(bool reached) : IStartupStageGate {
    public StartupStage? Asked { get; private set; }
    public bool HasReached(StartupStage stage) {
      Asked = stage;
      return reached;
    }
    public Task WaitForAsync(StartupStage stage, CancellationToken cancellationToken) => Task.CompletedTask;
  }

  private sealed class Instance : IServiceInstanceProvider {
    public Guid InstanceId { get; } = Guid.Empty;
    public string ServiceName => "test";
    public string HostName => "test-host";
    public int ProcessId => 1;
    public ServiceInstanceInfo ToInfo() => new() { InstanceId = InstanceId, ServiceName = "test", HostName = "test-host", ProcessId = 1 };
  }

  /// <summary>A database whose first acknowledgement leaves one partition handing over, as a writer holding it would.</summary>
  private sealed class PartialCoordinator(IEnumerable<int> handingOver) : IWorkCoordinator {
    private readonly HashSet<int> _handingOver = [.. handingOver];
    private readonly TaskCompletionSource _firstAcknowledged = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _nothingLeft = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public int? HeldOnFirstAsk { get; init; }
    public List<int[]> Asked { get; } = [];
    public Task FirstAcknowledged => _firstAcknowledged.Task;
    public Task NothingLeft => _nothingLeft.Task;

    public Task<IReadOnlyList<PartitionHandover>> GetPartitionsHandingOverAsync(Guid instanceId, CancellationToken cancellationToken = default) {
      lock (_handingOver) {
        return Task.FromResult<IReadOnlyList<PartitionHandover>>(
          [.. _handingOver.Order().Select(p => new PartitionHandover(p, 1, Guid.Empty))]);
      }
    }

    public Task<IReadOnlyList<int>> AcknowledgeHandoverAsync(
        Guid instanceId, IReadOnlyList<PartitionHandover> handovers, CancellationToken cancellationToken = default) {
      lock (_handingOver) {
        var first = Asked.Count == 0;
        Asked.Add([.. handovers.Select(h => h.Partition)]);
        var flipped = handovers.Select(h => h.Partition).Where(p => !(first && p == HeldOnFirstAsk)).ToList();
        _handingOver.ExceptWith(flipped);
        if (first) {
          _firstAcknowledged.TrySetResult();
        }
        if (_handingOver.Count == 0) {
          _nothingLeft.TrySetResult();
        }
        return Task.FromResult<IReadOnlyList<int>>(flipped);
      }
    }

    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken cancellationToken = default) =>
      Task.FromResult(new WorkCoordinatorStatistics());
    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount, CancellationToken cancellationToken = default) =>
      Task.CompletedTask;
    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion completion, CancellationToken cancellationToken = default) =>
      Task.CompletedTask;
    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure failure, CancellationToken cancellationToken = default) =>
      Task.CompletedTask;
    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default) =>
      Task.FromResult<PerspectiveCursorInfo?>(null);
  }

  private sealed class ScriptedCoordinator : IWorkCoordinator {
    private int _asks;
    private int _statusReads;
    private PartitionLeaveStatus _last;
    private readonly TaskCompletionSource _firstAsked = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _secondAsked = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public bool FailFirstAsk { get; init; } = true;
    public bool HandOverNothing { get; init; }
    public Queue<PartitionLeaveStatus> Statuses { get; } = new();
    public int Asks => Volatile.Read(ref _asks);
    public int StatusReads => Volatile.Read(ref _statusReads);
    public Task SecondAsked => _secondAsked.Task;

    public Task<PartitionLeaveStatus> GetLeaveStatusAsync(Guid instanceId, CancellationToken cancellationToken = default) {
      Interlocked.Increment(ref _statusReads);
      _last = Statuses.Count > 0 ? Statuses.Dequeue() : _last;
      return Task.FromResult(_last);
    }
    private readonly TaskCompletionSource<IReadOnlyList<PartitionHandover>> _acknowledged = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task FirstAsked => _firstAsked.Task;
    public Task<IReadOnlyList<PartitionHandover>> Acknowledged => _acknowledged.Task;

    public Task<IReadOnlyList<PartitionHandover>> GetPartitionsHandingOverAsync(Guid instanceId, CancellationToken cancellationToken = default) {
      var ask = Interlocked.Increment(ref _asks);
      if (ask == 1) {
        _firstAsked.TrySetResult();
        if (FailFirstAsk) {
          throw new InvalidOperationException("the database is unavailable");
        }
      }
      if (ask == 3) {
        _secondAsked.TrySetResult();
      }
      return Task.FromResult<IReadOnlyList<PartitionHandover>>(HandOverNothing ? [] : [new PartitionHandover(3, 5, Guid.Empty)]);
    }

    public Task<IReadOnlyList<int>> AcknowledgeHandoverAsync(
        Guid instanceId, IReadOnlyList<PartitionHandover> handovers, CancellationToken cancellationToken = default) {
      _acknowledged.TrySetResult(handovers);
      return Task.FromResult<IReadOnlyList<int>>([.. handovers.Select(h => h.Partition)]);
    }

    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken cancellationToken = default) =>
      Task.FromResult(new WorkCoordinatorStatistics());
    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount, CancellationToken cancellationToken = default) =>
      Task.CompletedTask;
    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion completion, CancellationToken cancellationToken = default) =>
      Task.CompletedTask;
    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure failure, CancellationToken cancellationToken = default) =>
      Task.CompletedTask;
    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default) =>
      Task.FromResult<PerspectiveCursorInfo?>(null);
  }
}
