// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Data.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Notifications;
using Whizbang.Core.Observability;
using Whizbang.Core.Signals;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// The claim never writes this instance's registration row (#1226). When a claim reports the row missing or stale,
/// the claim loop registers right after it, as a statement of its own on its pinned connection, and only then.
/// </summary>
/// <remarks>
/// A registration written inside the claim's transaction held the row until the claim committed, so a heartbeat for
/// the same instance waited for the whole claim. These tests pin the caller's half of the fix: registration follows a
/// claim that asked for it, never one that did not, and a failed registration never costs the claim's batch.
/// </remarks>
/// <docs>fundamentals/work-coordinator/claim-loop</docs>
[NotInParallel(Order = 100)]
public class ClaimWorkerRegistrationTests {
  private static readonly TimeSpan _wait = TimeSpan.FromSeconds(10);

  [Test]
  public async Task AClaimThatReportsAStaleRegistration_IsFollowedByARegistrationOnThePinnedConnectionAsync() {
    var coord = new SequencedCoordinator { RegistrationStaleOnClaim = n => n == 1 };
    var pinned = new StubDbConnection();
    var instance = new StubInstanceProvider();
    var (worker, cts, _) = _start(coord, new PinningPool(pinned), instance);

    await coord.WaitForHeartbeatsAsync(2, _wait);
    await coord.WaitForClaimsAsync(2, _wait);

    var registration = coord.Heartbeats[1];
    await Assert.That(registration.Order).IsGreaterThan(coord.ClaimOrders[0])
      .Because("the registration is sent once the claim that asked for it has returned, never inside it");
    await Assert.That(registration.Order).IsLessThan(coord.ClaimOrders[1])
      .Because("the claim loop registers before it claims again");
    await Assert.That(registration.Connection).IsSameReferenceAs(pinned)
      .Because("the registration goes over the claim worker's pinned connection, as the heartbeat's beats do");
    await Assert.That(registration.Request.InstanceId).IsEqualTo(instance.InstanceId);
    await Assert.That(registration.Request.ServiceName).IsEqualTo(instance.ServiceName);
    await Assert.That(registration.Request.HostName).IsEqualTo(instance.HostName);
    await Assert.That(registration.Request.ProcessId).IsEqualTo(instance.ProcessId);

    await _stopAsync(worker, cts);
  }

  [Test]
  public async Task AClaimWithAFreshRegistration_IsNotFollowedByOneAsync() {
    var coord = new SequencedCoordinator { RegistrationStaleOnClaim = _ => false };
    var (worker, cts, _) = _start(coord, NoOpPinnedConnectionPool.Instance, new StubInstanceProvider());

    await coord.WaitForClaimsAsync(3, _wait);

    // A registration after claim 1 or 2 would have been sent before claim 3 began.
    await Assert.That(coord.Heartbeats).Count().IsEqualTo(1)
      .Because("only the startup registration runs; a claim on a fresh row must not cause a write per claim");

    await _stopAsync(worker, cts);
  }

  [Test]
  public async Task AFailedRegistration_IsReportedAndTheLoopClaimsOnAsync() {
    var coord = new SequencedCoordinator {
      RegistrationStaleOnClaim = _ => true,
      HeartbeatFailure = n => n == 2 ? new InvalidOperationException("the registry refused the write") : null
    };
    var logger = new FakeLogger<ClaimWorker>();
    var (worker, cts, _) = _start(coord, NoOpPinnedConnectionPool.Instance, new StubInstanceProvider(), logger);

    await coord.WaitForClaimsAsync(3, _wait);

    var reported = logger.Collector.GetSnapshot().Where(e => e.Id.Id == 20).ToList();
    await Assert.That(reported).Count().IsEqualTo(1)
      .Because("a registration that failed is reported once, with what failed");
    await Assert.That(reported[0].Level).IsEqualTo(LogLevel.Warning);
    await Assert.That(reported[0].Exception).IsTypeOf<InvalidOperationException>();
    await Assert.That(coord.Heartbeats.Count).IsGreaterThanOrEqualTo(3)
      .Because("the next claim that still finds the row stale asks again, and the loop registers again");

    await _stopAsync(worker, cts);
  }

  [Test]
  public async Task ARegistrationCanceledMidCall_EndsTheLoopAsACanceledClaimWouldAsync() {
    var coord = new SequencedCoordinator {
      RegistrationStaleOnClaim = _ => true,
      HeartbeatFailure = n => n == 2 ? new OperationCanceledException() : null
    };
    var logger = new FakeLogger<ClaimWorker>();
    var (worker, cts, _) = _start(coord, NoOpPinnedConnectionPool.Instance, new StubInstanceProvider(), logger);

    await coord.WaitForHeartbeatsAsync(2, _wait);
    await worker.ExecuteTask!.WaitAsync(_wait).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

    await Assert.That(worker.ExecuteTask.IsCompleted).IsTrue()
      .Because("a cancellation is not a registration failure to report and claim past; the claim loop treats it the "
        + "way it treats a canceled claim");
    await Assert.That(coord.ClaimOrders).Count().IsEqualTo(1);
    await Assert.That(logger.Collector.GetSnapshot().Any(e => e.Id.Id == 20)).IsFalse();

    await cts.CancelAsync();
    cts.Dispose();
  }

  // ------------------------------------------------------------------

  private static (ClaimWorker Worker, CancellationTokenSource Cts, IServiceProvider Services) _start(
      SequencedCoordinator coord, IPinnedConnectionPool pool, IServiceInstanceProvider instance,
      ILogger<ClaimWorker>? logger = null) {
    var services = new ServiceCollection();
    services.TryAddWhizbangDefaults();
    services.AddSingleton<IWorkCoordinator>(coord);
    var sp = services.BuildServiceProvider();
    var gate = new SchemaReadyGate();
    gate.MarkReady();
    var worker = new ClaimWorker(
      scopeFactory: sp.GetRequiredService<IServiceScopeFactory>(),
      instanceProvider: instance,
      notificationListener: new NoOpWorkNotificationListener(),
      schemaReadyGate: gate,
      options: Options.Create(new ClaimWorkerOptions { PollingIntervalMilliseconds = 10, PollingMaxIntervalMilliseconds = 50 }),
      logger: logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<ClaimWorker>.Instance,
      outboxChannel: new WorkChannelWriter(),
      inboxChannel: new InboxChannelWriter(),
      perspectiveChannel: new PerspectiveChannelWriter(),
      perspectiveDrainChannel: new PerspectiveDrainChannel(),
      outboxDrainChannel: new OutboxDrainChannel(),
      inboxDrainChannel: new InboxDrainChannel(),
      signalingGate: NullNotifySignalingGate.Instance,
      pinnedPool: pool,
      signalBus: NullSignalBus.Instance);
    var cts = new CancellationTokenSource();
    worker.StartAsync(cts.Token).GetAwaiter().GetResult();
    return (worker, cts, sp);
  }

  private static async Task _stopAsync(ClaimWorker worker, CancellationTokenSource cts) {
    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);
    cts.Dispose();
  }

  private sealed record HeartbeatCall(int Order, HeartbeatRequest Request, DbConnection? Connection);

  /// <summary>
  /// Records claims and heartbeats in one order, with the pinned connection each heartbeat ran on, and signals by count.
  /// </summary>
  private sealed class SequencedCoordinator : IWorkCoordinator {
    private readonly Lock _lock = new();
    private readonly List<int> _claimOrders = [];
    private readonly List<HeartbeatCall> _heartbeats = [];
    private readonly List<(int Count, TaskCompletionSource Signal)> _claimWaiters = [];
    private readonly List<(int Count, TaskCompletionSource Signal)> _heartbeatWaiters = [];
    private int _order;

    public Func<int, bool> RegistrationStaleOnClaim { get; init; } = _ => false;
    public Func<int, Exception?> HeartbeatFailure { get; init; } = _ => null;

    public IReadOnlyList<int> ClaimOrders { get { lock (_lock) { return [.. _claimOrders]; } } }
    public IReadOnlyList<HeartbeatCall> Heartbeats { get { lock (_lock) { return [.. _heartbeats]; } } }

    public Task WaitForClaimsAsync(int count, TimeSpan timeout) => _waitAsync(_claimWaiters, () => _claimOrders.Count, count, timeout);
    public Task WaitForHeartbeatsAsync(int count, TimeSpan timeout) => _waitAsync(_heartbeatWaiters, () => _heartbeats.Count, count, timeout);

    private Task _waitAsync(List<(int, TaskCompletionSource)> waiters, Func<int> current, int count, TimeSpan timeout) {
      lock (_lock) {
        if (current() >= count) {
          return Task.CompletedTask;
        }
        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        waiters.Add((count, signal));
        return signal.Task.WaitAsync(timeout);
      }
    }

    private static void _release(List<(int Count, TaskCompletionSource Signal)> waiters, int reached) {
      foreach (var (count, signal) in waiters.Where(w => w.Count <= reached).ToList()) {
        signal.TrySetResult();
        waiters.Remove((count, signal));
      }
    }

    public Task<WorkBatch> ClaimWorkAsync(ClaimWorkRequest request, CancellationToken cancellationToken = default) {
      int n;
      lock (_lock) {
        _claimOrders.Add(++_order);
        n = _claimOrders.Count;
        _release(_claimWaiters, n);
      }
      return Task.FromResult(new WorkBatch {
        OutboxWork = [],
        InboxWork = [],
        PerspectiveWork = [],
        InstanceRegistrationStale = RegistrationStaleOnClaim(n)
      });
    }

    public Task<bool> RecordHeartbeatAsync(HeartbeatRequest request, CancellationToken cancellationToken = default) {
      int n;
      lock (_lock) {
        _heartbeats.Add(new HeartbeatCall(++_order, request, PinnedConnectionContext.Current));
        n = _heartbeats.Count;
        _release(_heartbeatWaiters, n);
      }
      return HeartbeatFailure(n) is { } failure ? Task.FromException<bool>(failure) : Task.FromResult(true);
    }

    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new WorkCoordinatorStatistics());
    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<PartitionRecomputeResult> RecomputePartitionNumbersAsync(int partitionCount, CancellationToken cancellationToken = default) => Task.FromResult(new PartitionRecomputeResult());
    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion completion, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure failure, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default) => Task.FromResult<PerspectiveCursorInfo?>(null);
  }

  /// <summary>A pool that hands every eligible worker the same pinned connection.</summary>
  private sealed class PinningPool(DbConnection connection) : IPinnedConnectionPool {
    public ValueTask<IBorrowedConnection> TryPinForAsync(Type workerType, CancellationToken cancellationToken) =>
      new(new Borrowed(connection));

    private sealed class Borrowed(DbConnection connection) : IBorrowedConnection {
      public DbConnection? Connection => connection;
      public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
  }

  private sealed class StubInstanceProvider : IServiceInstanceProvider {
    public Guid InstanceId { get; } = TrackedGuid.New();
    public string ServiceName => "registration-test";
    public string HostName => "registration-host";
    public int ProcessId => 4242;
    public ServiceInstanceInfo ToInfo() => new() {
      InstanceId = InstanceId,
      ServiceName = ServiceName,
      HostName = HostName,
      ProcessId = ProcessId
    };
  }

  /// <summary>Never opened: it only has to be the same object the registration sees.</summary>
  private sealed class StubDbConnection : DbConnection {
#pragma warning disable CS8765
    public override string ConnectionString { get; set; } = "";
#pragma warning restore CS8765
    public override string Database => "pinned";
    public override string DataSource => "pinned";
    public override string ServerVersion => "0";
    public override System.Data.ConnectionState State => System.Data.ConnectionState.Closed;
    public override void ChangeDatabase(string databaseName) => throw new NotSupportedException();
    public override void Close() { }
    public override void Open() => throw new NotSupportedException();
    protected override DbTransaction BeginDbTransaction(System.Data.IsolationLevel isolationLevel) => throw new NotSupportedException();
    protected override DbCommand CreateDbCommand() => throw new NotSupportedException();
  }
}
