// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Observability;
using Whizbang.Core.Startup;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Component.Tests;

/// <summary>
/// The partition assigner's hosted loop (#1254), on a real thread with in-memory fakes: how a tenure begins and ends,
/// and that nothing short of the role's loss ends it. Every wait is on the public signal for the exact transition
/// asserted; the deadline only bounds it.
/// </summary>
[Category("Component")]
public class PartitionAssignerWorkerLoopTests {
  private static readonly TimeSpan _deadline = TimeSpan.FromSeconds(30);
  private static readonly Guid _self = new("00000000-0000-0000-0000-000000000005");

  // --- fakes -----------------------------------------------------------------------------------

  private sealed class Instance : IServiceInstanceProvider {
    public Guid InstanceId => _self;
    public string ServiceName => "svc";
    public string HostName => "host";
    public int ProcessId => 1;
    public ServiceInstanceInfo ToInfo() => new() { InstanceId = _self, ServiceName = ServiceName, HostName = HostName, ProcessId = ProcessId };
  }

  private sealed class Grant(long? epoch) : IDutyGrant {
    private readonly TaskCompletionSource _disposed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public string Duty => PartitionAssignerOptions.ROLE;
    public DateTimeOffset AcquiredAt => DateTimeOffset.UnixEpoch;
    public long? Epoch => epoch;
    public volatile bool Held = true;
    public volatile bool Drain;
    public bool DrainRequested => Drain;
    public Task Disposed => _disposed.Task;
    public Task<bool> VerifyStillHeldAsync(CancellationToken cancellationToken) => Task.FromResult(Held);
    public ValueTask DisposeAsync() {
      _disposed.TrySetResult();
      return ValueTask.CompletedTask;
    }
  }

  /// <summary>Hands out the queued grants in order, then refuses; signals each vote.</summary>
  private sealed class Elector(params Func<DutyAttempt>[] attempts) : IDutyElector {
    private int _votes;
    private readonly TaskCompletionSource _secondVote = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task SecondVote => _secondVote.Task;
    public Task<DutyAttempt> TryAcquireAsync(string duty, CancellationToken cancellationToken) {
      var n = Interlocked.Increment(ref _votes);
      if (n == 2) {
        _secondVote.TrySetResult();
      }
      return Task.FromResult(n <= attempts.Length ? attempts[n - 1]() : DutyAttempt.Lost(DutyRefusal.Contended, "held elsewhere"));
    }
  }

  private sealed class Store(bool failFirstRead = false) : IPartitionAssignmentStore {
    private int _failFirstRead = failFirstRead ? 1 : 0;
    private readonly TaskCompletionSource _failed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    /// <summary>Completes when the one failing read has thrown.</summary>
    public Task Failed => _failed.Task;
    public Task<PartitionAssignmentRead?> ReadAsync(CancellationToken cancellationToken) => Task.FromResult<PartitionAssignmentRead?>(null);
    public Task<IReadOnlyList<PartitionAssignmentCandidate>> ReadCandidatesAsync(CancellationToken cancellationToken) {
      if (Interlocked.Exchange(ref _failFirstRead, 0) == 1) {
        _failed.TrySetResult();
        return Task.FromException<IReadOnlyList<PartitionAssignmentCandidate>>(new InvalidOperationException("transient"));
      }
      return Task.FromResult<IReadOnlyList<PartitionAssignmentCandidate>>([]);
    }
    public Task<PartitionAssignment?> PublishAsync(
        string role, Guid instanceId, long epoch, IReadOnlyList<Guid> members, TimeSpan lease, CancellationToken cancellationToken) =>
      Task.FromResult<PartitionAssignment?>(new PartitionAssignment(epoch, 1, instanceId, [.. members], DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch + lease));
    public Task<bool> RenewAsync(Guid instanceId, long epoch, CancellationToken cancellationToken) => Task.FromResult(true);
  }

  private sealed class FailingElector : IDutyElector {
    private int _votes;
    private readonly TaskCompletionSource _retried = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task Retried => _retried.Task;
    public Task<DutyAttempt> TryAcquireAsync(string duty, CancellationToken cancellationToken) {
      if (Interlocked.Increment(ref _votes) == 1) {
        return Task.FromException<DutyAttempt>(new InvalidOperationException("the database is unavailable"));
      }
      _retried.TrySetResult();
      return Task.FromResult(DutyAttempt.Lost(DutyRefusal.Unavailable, "still down"));
    }
  }

  private static PartitionAssignerWorker _worker(IDutyElector elector, IPartitionAssignmentStore? store = null) {
    var roles = new RoleAssignmentOptions { RenewInterval = TimeSpan.FromMilliseconds(10) };
    roles.Roles.Add(PartitionAssignerOptions.ROLE);
    return new PartitionAssignerWorker(elector, store ?? new Store(), NullInstanceConnectionModeSource.Instance, new Instance(),
      Options.Create(new PartitionAssignerOptions()), Options.Create(roles), NullLogger<PartitionAssignerWorker>.Instance);
  }

  private static Task<T> _signal<T>(Action<Action<T>> subscribe, Func<T, bool>? when = null) {
    var signal = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
    subscribe(value => {
      if (when is null || when(value)) {
        signal.TrySetResult(value);
      }
    });
    return signal.Task;
  }

  // --- tests -----------------------------------------------------------------------------------

  [Test]
  public async Task AGrant_BeginsATenureThatPublishes_AndTheRolesLoss_EndsItAsync() {
    var grant = new Grant(3);
    var worker = _worker(new Elector(() => DutyAttempt.Granted(grant)));
    var published = _signal<PartitionAssignment>(h => worker.OnPublished += h);
    var stopped = _signal<long>(h => worker.OnStoppedAssigning += h);

    await worker.StartAsync(CancellationToken.None);
    try {
      var assignment = await published.WaitAsync(_deadline);
      await Assert.That(assignment.Members).IsEquivalentTo([_self]);
      await Assert.That(worker.IsAssigner).IsTrue();

      grant.Held = false;
      await Assert.That(await stopped.WaitAsync(_deadline)).IsEqualTo(3);
      await grant.Disposed.WaitAsync(_deadline);
      await Assert.That(worker.IsAssigner).IsFalse();
    } finally {
      await worker.StopAsync(CancellationToken.None);
    }
  }

  [Test]
  public async Task ADrainRequest_EndsTheTenure_AndGivesTheRoleBackAsync() {
    var grant = new Grant(4);
    var worker = _worker(new Elector(() => DutyAttempt.Granted(grant)));
    var published = _signal<PartitionAssignment>(h => worker.OnPublished += h);
    var stopped = _signal<long>(h => worker.OnStoppedAssigning += h);

    await worker.StartAsync(CancellationToken.None);
    try {
      _ = await published.WaitAsync(_deadline);
      grant.Drain = true;

      await Assert.That(await stopped.WaitAsync(_deadline)).IsEqualTo(4);
      await grant.Disposed.WaitAsync(_deadline);
    } finally {
      await worker.StopAsync(CancellationToken.None);
    }
  }

  [Test]
  public async Task AGrantWithoutAnEpoch_IsGivenBack_WithoutATenureAsync() {
    var grant = new Grant(null);
    var elector = new Elector(() => DutyAttempt.Granted(grant));
    var worker = _worker(elector);
    var began = false;
    worker.OnBecameAssigner += _ => began = true;

    await worker.StartAsync(CancellationToken.None);
    try {
      await grant.Disposed.WaitAsync(_deadline);
      await elector.SecondVote.WaitAsync(_deadline);

      await Assert.That(began).IsFalse().Because("nothing can be fenced without an epoch");
    } finally {
      await worker.StopAsync(CancellationToken.None);
    }
  }

  [Test]
  public async Task AFailedEvaluation_KeepsTheTenure_AndTheNextOnePublishesAsync() {
    var grant = new Grant(5);
    var store = new Store(failFirstRead: true);
    var worker = _worker(new Elector(() => DutyAttempt.Granted(grant)), store);
    var published = _signal<PartitionAssignment>(h => worker.OnPublished += h);

    await worker.StartAsync(CancellationToken.None);
    try {
      await store.Failed.WaitAsync(_deadline);

      var assignment = await published.WaitAsync(_deadline);
      await Assert.That(assignment.Epoch).IsEqualTo(5).Because("a transient failure is no reason to give the role back");
      await Assert.That(grant.Disposed.IsCompleted).IsFalse();
    } finally {
      await worker.StopAsync(CancellationToken.None);
    }
  }

  [Test]
  public async Task AFailedVote_IsLogged_AndTheWorkerVotesAgainAsync() {
    var elector = new FailingElector();
    var worker = _worker(elector);

    await worker.StartAsync(CancellationToken.None);
    try {
      await elector.Retried.WaitAsync(_deadline);

      await Assert.That(worker.ExecuteTask!.IsCompleted).IsFalse().Because("a failed vote never stops the worker");
    } finally {
      await worker.StopAsync(CancellationToken.None);
    }
  }

  [Test]
  public async Task Stopping_EndsTheTenure_AndTheWorkerAsync() {
    var grant = new Grant(6);
    var worker = _worker(new Elector(() => DutyAttempt.Granted(grant)));
    var published = _signal<PartitionAssignment>(h => worker.OnPublished += h);
    var stopped = _signal<long>(h => worker.OnStoppedAssigning += h);
    await worker.StartAsync(CancellationToken.None);
    _ = await published.WaitAsync(_deadline);

    await worker.StopAsync(CancellationToken.None);

    await Assert.That(await stopped.WaitAsync(_deadline)).IsEqualTo(6);
    await grant.Disposed.WaitAsync(_deadline);
    await worker.ExecuteTask!.WaitAsync(_deadline);
  }
}
