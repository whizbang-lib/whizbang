using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
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
/// Locks the runtime doorbell-liveness accounting (issue #505 layer 3): when a claim discovers
/// fresh work on the empty→non-empty edge — where the store guarantees a doorbell rings — and no
/// doorbell preceded it, the claim loop records a missed doorbell on
/// <see cref="SignalBusLivenessState"/>; a doorbell-preceded discovery resets the streak. This is
/// the empirical runtime check the wire-route probe cannot provide: it catches NOTIFY delivery
/// dying mid-run, from real traffic, with no extra queries.
/// </summary>
/// <docs>fundamentals/signal-bus/signal-bus</docs>
[NotInParallel(Order = 104)]
public class ClaimWorkerDoorbellLivenessTests {

  private sealed class StubInstanceProvider : IServiceInstanceProvider {
    public Guid InstanceId { get; } = TrackedGuid.NewMedo();
    public string ServiceName => "test";
    public string HostName => "test-host";
    public int ProcessId => 1;
    public ServiceInstanceInfo ToInfo() => new() {
      InstanceId = InstanceId,
      ServiceName = ServiceName,
      HostName = HostName,
      ProcessId = ProcessId
    };
  }

  private sealed class AvailableGate : INotifySignalingGate {
    public bool IsAvailable => true;
    public DateTimeOffset? LastVerifiedAt => null;
    public DateTimeOffset? LastFailureAt => null;
    public string? LastFailureReason => null;
    public event Action<bool>? OnAvailabilityChanged { add { } remove { } }
    public Task<bool> ProbeNowAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
  }

  /// <summary>
  /// First claim returns EMPTY, the second returns fresh work (the empty→non-empty edge), later
  /// claims return empty again — the exact shape where the store's edge doorbell must have rung.
  /// </summary>
  private sealed class EdgeCoordinator : IWorkCoordinator {
    private readonly Lock _lock = new();
    private readonly Guid _freshStream = TrackedGuid.NewMedo();
    private int _calls;
    public TaskCompletionSource FirstCallSignal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource SecondCallSignal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource ThirdCallSignal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<WorkBatch> ClaimWorkAsync(ClaimWorkRequest request, CancellationToken cancellationToken = default) {
      int call;
      lock (_lock) {
        call = ++_calls;
        if (call == 1) {
          FirstCallSignal.TrySetResult();
        } else if (call == 2) {
          SecondCallSignal.TrySetResult();
        } else if (call == 3) {
          ThirdCallSignal.TrySetResult();
        }
      }
      return Task.FromResult(new WorkBatch {
        OutboxWork = [],
        InboxWork = [],
        PerspectiveWork = [],
        OutboxStreamIds = call == 2 ? [_freshStream] : [],
      });
    }

    public Task<bool> RecordHeartbeatAsync(HeartbeatRequest request, CancellationToken cancellationToken = default) => Task.FromResult(true);
    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task StoreOutboxMessagesAsync(OutboxMessage[] messages, int partitionCount, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion completion, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure failure, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default) => throw new NotImplementedException();
  }

  private static ClaimWorker _buildWorker(EdgeCoordinator coord, SignalBusLivenessState liveness, int pollingIntervalMs = 50) {
    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coord);
    var sp = services.BuildServiceProvider();
    var schemaGate = new SchemaReadyGate();
    schemaGate.MarkReady();
    return new ClaimWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      new StubInstanceProvider(),
      new NoOpWorkNotificationListener(),
      schemaGate,
      Options.Create(new ClaimWorkerOptions {
        PollingIntervalMilliseconds = pollingIntervalMs,
        PollingMaxIntervalMilliseconds = Math.Max(2_000, pollingIntervalMs),
        NotifyHealthyPollingIntervalMilliseconds = null,
      }),
      NullLogger<ClaimWorker>.Instance,
      signalingGate: new AvailableGate(),
      busLiveness: liveness);
  }

  [Test]
  public async Task FreshWorkOnEmptyEdge_NoDoorbell_RecordsMissedDoorbellAsync() {
    var coord = new EdgeCoordinator();
    var liveness = new SignalBusLivenessState();
    var worker = _buildWorker(coord, liveness);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await coord.FirstCallSignal.Task.WaitAsync(TimeSpan.FromSeconds(30));
    await coord.SecondCallSignal.Task.WaitAsync(TimeSpan.FromSeconds(30));
    await coord.ThirdCallSignal.Task.WaitAsync(TimeSpan.FromSeconds(30));

    await Assert.That(liveness.ConsecutiveMissedDoorbells)
      .IsEqualTo(1)
      .Because("fresh work appeared on the empty→non-empty edge with no doorbell — the store " +
               "guarantees one rings there, so its absence must be recorded");

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);
  }

  [Test]
  public async Task FreshWorkOnEmptyEdge_DoorbellPreceded_NoMissRecordedAsync() {
    var coord = new EdgeCoordinator();
    var liveness = new SignalBusLivenessState();
    // Polling parked far out: every claim past the first must be doorbell-driven. With a tight
    // poll, a saturated test host can slip a poll-fired claim in BETWEEN call #1 and the test
    // thread's SignalNewWork below — discovering the fresh work without a doorbell and
    // recording a miss this test asserts against.
    var worker = _buildWorker(coord, liveness, pollingIntervalMs: 60_000);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await coord.FirstCallSignal.Task.WaitAsync(TimeSpan.FromSeconds(30));
    worker.SignalNewWork();
    await coord.SecondCallSignal.Task.WaitAsync(TimeSpan.FromSeconds(30));
    worker.SignalNewWork();
    await coord.ThirdCallSignal.Task.WaitAsync(TimeSpan.FromSeconds(30));

    await Assert.That(liveness.ConsecutiveMissedDoorbells)
      .IsEqualTo(0)
      .Because("the discovery was doorbell-preceded — the route is alive, nothing to record");

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);
  }
}
