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
/// Round-23 coverage: <see cref="HeartbeatWorker"/> lines the existing HeartbeatWorker*Tests.cs
/// files do not reach — the schema-gate cancellation return, the OperationCanceledException
/// loop-break distinct from a real shutdown, the generic-exception log-and-continue arm, and the
/// main loop actually iterating a second time after a tick.
/// </summary>
public class HeartbeatWorkerCoverageTests {
  private sealed class _instanceProvider(string serviceName) : IServiceInstanceProvider {
    public Guid InstanceId { get; } = TrackedGuid.NewMedo().Value;
    public string ServiceName => serviceName;
    public string HostName => "test-host";
    public int ProcessId => 1;
    public ServiceInstanceInfo ToInfo() => new() {
      InstanceId = InstanceId,
      ServiceName = ServiceName,
      HostName = HostName,
      ProcessId = ProcessId
    };
  }

  /// <summary>Counts calls; can throw a chosen exception shape on the first N calls and succeed
  /// afterward, so a test can prove the loop survives a failure and keeps ticking.</summary>
  private sealed class _throwingCoordinator : NoOpWorkCoordinator, IWorkCoordinator {
    private int _calls;
    public int Calls => Volatile.Read(ref _calls);
    public int ThrowOnCallsUpTo { get; init; }
    public Exception? ThrowWith { get; init; }
    public TaskCompletionSource<int> FirstAcceptedCall { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource<int> SecondAcceptedCall { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<bool> RecordHeartbeatAsync(HeartbeatRequest request, CancellationToken cancellationToken = default) {
      var n = Interlocked.Increment(ref _calls);
      if (ThrowWith is not null && n <= ThrowOnCallsUpTo) {
        throw ThrowWith;
      }
      if (n == 1) { FirstAcceptedCall.TrySetResult(n); } else if (n == 2) { SecondAcceptedCall.TrySetResult(n); }
      return Task.FromResult(true);
    }
  }

  private static HeartbeatWorker _buildWorker(
      IWorkCoordinator coordinator, ISchemaReadyGate gate, HeartbeatWorkerOptions options) {
    var services = new ServiceCollection();
    services.AddSingleton(coordinator);
    var sp = services.BuildServiceProvider();
    return new HeartbeatWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      new _instanceProvider("origin-svc"),
      gate,
      Options.Create(options),
      NullLogger<HeartbeatWorker>.Instance);
  }

  // Target: src/Whizbang.Core/Workers/HeartbeatWorker.cs:79 — `return;` in the
  // `catch (OperationCanceledException)` around `_schemaReadyGate.WaitForReadyAsync`. Without
  // this, a pod stopped while still waiting for the schema would fault its BackgroundService
  // instead of exiting quietly — turning a routine fast restart into a logged crash.
  [Test]
  [Timeout(30000)]
  public async Task ExecuteAsync_CanceledWhileWaitingForSchemaReady_ReturnsQuietlyAsync(
      CancellationToken testToken) {
    var coordinator = new _throwingCoordinator();
    var worker = _buildWorker(coordinator, new SchemaReadyGate(), new HeartbeatWorkerOptions { IntervalSeconds = 1 });
    // gate is never marked ready.

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    var executeTask = worker.ExecuteTask;
    await worker.StopAsync(CancellationToken.None);

    await Assert.That(executeTask!.IsCompleted).IsTrue();
    await Assert.That(executeTask.IsFaulted).IsFalse()
      .Because("waiting for the schema is not an error condition, so stopping mid-wait must read "
             + "as a clean exit rather than a crashed worker");
    await Assert.That(coordinator.Calls).IsEqualTo(0)
      .Because("nothing may heartbeat before the schema is ready");
  }

  // Target: line 87 — `break;` in the `catch (OperationCanceledException)` around
  // `_heartbeatOnceAsync` inside the main loop. A cancellation surfacing from the tick itself
  // (not necessarily stoppingToken) must end the loop immediately rather than being retried —
  // retrying a call whose own cancellation already fired can never succeed.
  [Test]
  [Timeout(30000)]
  public async Task ExecuteAsync_TickThrowsOperationCanceled_BreaksTheLoopWithoutRetryingAsync(
      CancellationToken testToken) {
    var coordinator = new _throwingCoordinator {
      ThrowOnCallsUpTo = int.MaxValue,
      ThrowWith = new OperationCanceledException("simulated tick-level cancellation"),
    };
    var worker = _buildWorker(coordinator, SchemaReadyGate.AlreadyReady(),
      new HeartbeatWorkerOptions { IntervalSeconds = 1 });

    await worker.StartAsync(testToken);
    var executeTask = worker.ExecuteTask;
    await executeTask!.WaitAsync(TimeSpan.FromSeconds(20), testToken)
      .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    await worker.StopAsync(CancellationToken.None);

    await Assert.That(executeTask.IsCompleted).IsTrue();
    await Assert.That(executeTask.IsFaulted).IsFalse();
    await Assert.That(coordinator.Calls).IsEqualTo(1)
      .Because("an OperationCanceledException from the tick must stop the loop on the spot — "
             + "looping again would just retry a call that is guaranteed to be canceled again");
  }

  // Target: lines 88, 91, 92 (the generic-Exception arm logging and continuing) and line 111
  // (the loop's closing brace — reached only when a tick completes and the loop goes around
  // again). A single failed tick must be non-fatal: peers correctly flag a stale instance from a
  // missed heartbeat, but the worker itself must keep trying on every subsequent cadence.
  [Test]
  [Timeout(30000)]
  public async Task ExecuteAsync_FirstTickThrowsGenericException_LogsAndKeepsTickingAsync(
      CancellationToken testToken) {
    var coordinator = new _throwingCoordinator {
      ThrowOnCallsUpTo = 1,
      ThrowWith = new InvalidOperationException("simulated transient heartbeat failure"),
    };
    var worker = _buildWorker(coordinator, SchemaReadyGate.AlreadyReady(),
      new HeartbeatWorkerOptions { IntervalSeconds = 1 });

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    try {
      // Waits on the SECOND accepted call actually happening -- proof the loop looped back
      // (line 111) after surviving the first tick's exception (lines 88/91/92), not merely that
      // time passed.
      await coordinator.SecondAcceptedCall.Task.WaitAsync(TimeSpan.FromSeconds(20), testToken);
    } finally {
      await cts.CancelAsync();
      await worker.StopAsync(CancellationToken.None);
    }

    await Assert.That(coordinator.Calls).IsGreaterThanOrEqualTo(2)
      .Because("a single failed tick is non-fatal — the worker must still be ticking afterward, "
             + "or a transient DB hiccup would permanently silence this instance's heartbeat");
  }
}
