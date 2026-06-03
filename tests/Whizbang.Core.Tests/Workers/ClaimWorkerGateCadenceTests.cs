using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Notifications;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Slice 33.6 — locks <see cref="ClaimWorker"/>'s polling-cadence response to
/// <see cref="INotifySignalingGate.IsAvailable"/> transitions:
/// <list type="bullet">
/// <item><description>When the gate is unavailable, the adaptive backoff CANNOT stretch past the
/// base interval — NOTIFY isn't going to wake us, so we MUST keep polling tight.</description></item>
/// <item><description>When the gate flips available, an immediate poll fires so any work that
/// accumulated during the unavailable window doesn't wait out the current backoff.</description></item>
/// </list>
/// </summary>
/// <docs>fundamentals/work-coordinator/notifications-and-pgbouncer</docs>
[NotInParallel(Order = 101)]
public class ClaimWorkerGateCadenceTests {

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

  private sealed class TickRecordingCoordinator : IWorkCoordinator {
    private readonly Lock _lock = new();
    public List<DateTimeOffset> ClaimCallTimes { get; } = [];
    public TaskCompletionSource FirstCallSignal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource SecondCallSignal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource ThirdCallSignal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task<WorkBatch> ClaimWorkAsync(ClaimWorkRequest request, CancellationToken cancellationToken = default) {
      lock (_lock) {
        ClaimCallTimes.Add(DateTimeOffset.UtcNow);
        if (ClaimCallTimes.Count == 1) {
          FirstCallSignal.TrySetResult();
        } else if (ClaimCallTimes.Count == 2) {
          SecondCallSignal.TrySetResult();
        } else if (ClaimCallTimes.Count == 3) {
          ThirdCallSignal.TrySetResult();
        }
      }
      return Task.FromResult(new WorkBatch {
        OutboxWork = [],
        InboxWork = [],
        PerspectiveWork = [],
      });
    }
    public Task RecordHeartbeatAsync(HeartbeatRequest request, CancellationToken cancellationToken = default) => Task.CompletedTask;
    // Stubs for IWorkCoordinator members without default implementations. ClaimWorker
    // doesn't exercise these in cadence tests; throwing if hit gives a fast-failing signal
    // if the worker's surface area expands unexpectedly.
    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task StoreOutboxMessagesAsync(OutboxMessage[] messages, int partitionCount, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion completion, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure failure, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default) => throw new NotImplementedException();
  }

  private sealed class FakeGate : INotifySignalingGate {
    public bool IsAvailable { get; private set; }
    public DateTimeOffset? LastVerifiedAt => null;
    public DateTimeOffset? LastFailureAt => null;
    public string? LastFailureReason => null;
    public event Action<bool>? OnAvailabilityChanged;
    public Task<bool> ProbeNowAsync(CancellationToken cancellationToken = default) => Task.FromResult(IsAvailable);
    public void Set(bool available) {
      if (IsAvailable == available) { return; }
      IsAvailable = available;
      OnAvailabilityChanged?.Invoke(available);
    }
  }

  private static (ClaimWorker Worker, TickRecordingCoordinator Coord, FakeGate Gate) _newWorker(
      int pollingIntervalMilliseconds,
      int pollingMaxIntervalMilliseconds,
      bool gateAvailable) {
    var coord = new TickRecordingCoordinator();
    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coord);
    var sp = services.BuildServiceProvider();
    var schemaGate = new SchemaReadyGate();
    schemaGate.MarkReady();
    var gate = new FakeGate();
    if (gateAvailable) { gate.Set(true); }
    var worker = new ClaimWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      new StubInstanceProvider(),
      new NoOpWorkNotificationListener(),
      schemaGate,
      Options.Create(new ClaimWorkerOptions {
        PollingIntervalMilliseconds = pollingIntervalMilliseconds,
        PollingMaxIntervalMilliseconds = pollingMaxIntervalMilliseconds,
      }),
      NullLogger<ClaimWorker>.Instance,
      signalingGate: gate);
    return (worker, coord, gate);
  }

  [Test]
  public async Task GateAvailable_AdaptiveBackoffMayStretchToMaxAsync() {
    // When the gate is healthy, an empty-poll streak is allowed to push the wait up
    // toward PollingMaxIntervalMilliseconds (10s default in prod). We use a 50ms base
    // / 2 s max here to keep the test fast, then assert the 2nd→3rd interval grew past
    // the base — proves the adaptive path isn't being clamped.
    var (worker, coord, _) = _newWorker(
      pollingIntervalMilliseconds: 50,
      pollingMaxIntervalMilliseconds: 2_000,
      gateAvailable: true);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await coord.FirstCallSignal.Task.WaitAsync(TimeSpan.FromSeconds(5));
    await coord.SecondCallSignal.Task.WaitAsync(TimeSpan.FromSeconds(5));
    await coord.ThirdCallSignal.Task.WaitAsync(TimeSpan.FromSeconds(5));

    // All three calls came in. The gap between call 2 and 3 should exceed base (50ms)
    // because the backoff doubled after empty-poll #1.
    var gap2to3 = coord.ClaimCallTimes[2] - coord.ClaimCallTimes[1];
    await Assert.That(gap2to3).IsGreaterThan(TimeSpan.FromMilliseconds(70))
      .Because("with gate available the adaptive backoff doubles past the 50ms base");

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);
  }

  [Test]
  public async Task GateUnavailable_PollIntervalStaysAtBaseEvenAfterEmptyStreakAsync() {
    // When the gate reports unavailable, NOTIFY isn't going to wake us, so we MUST keep
    // polling at the base interval — the adaptive backoff cannot push the wait out to
    // PollingMaxIntervalMilliseconds (would silently increase work-pickup latency).
    var (worker, coord, _) = _newWorker(
      pollingIntervalMilliseconds: 100,
      pollingMaxIntervalMilliseconds: 5_000,
      gateAvailable: false);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await coord.FirstCallSignal.Task.WaitAsync(TimeSpan.FromSeconds(5));
    await coord.SecondCallSignal.Task.WaitAsync(TimeSpan.FromSeconds(5));
    await coord.ThirdCallSignal.Task.WaitAsync(TimeSpan.FromSeconds(5));

    // Every gap must stay near the base (100ms), even though 3 consecutive empty polls
    // have happened. Allow up to 2× base for scheduling jitter.
    var gap1to2 = coord.ClaimCallTimes[1] - coord.ClaimCallTimes[0];
    var gap2to3 = coord.ClaimCallTimes[2] - coord.ClaimCallTimes[1];
    await Assert.That(gap2to3).IsLessThan(TimeSpan.FromMilliseconds(300))
      .Because("with gate unavailable the cadence MUST stay tight — adaptive backoff is disabled");
    await Assert.That(gap1to2).IsLessThan(TimeSpan.FromMilliseconds(300));

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);
  }

  [Test]
  public async Task GateFlipsToAvailable_TriggersImmediatePollAsync() {
    // When the gate transitions false → true (e.g., the network came back), a poll
    // should fire RIGHT AWAY so any work that accumulated during the unavailable window
    // doesn't wait out the next adaptive-backoff tick.
    var (worker, coord, gate) = _newWorker(
      pollingIntervalMilliseconds: 50,
      pollingMaxIntervalMilliseconds: 5_000,
      gateAvailable: true);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await coord.FirstCallSignal.Task.WaitAsync(TimeSpan.FromSeconds(5));
    await coord.SecondCallSignal.Task.WaitAsync(TimeSpan.FromSeconds(5));

    // Force a long-ish wait that would otherwise apply: drive several empty polls so
    // the backoff stretches, then flip the gate. The flip should wake us within ~base
    // rather than after the full backoff.
    await coord.ThirdCallSignal.Task.WaitAsync(TimeSpan.FromSeconds(5));
    gate.Set(false);
    var fourthSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    coord.ClaimCallTimes.Clear();
    coord.FirstCallSignal.TrySetResult();
    coord.SecondCallSignal.TrySetResult();
    coord.ThirdCallSignal.TrySetResult();

    // Trigger the transition + measure wake latency.
    var beforeFlip = DateTimeOffset.UtcNow;
    gate.Set(true);
    while (coord.ClaimCallTimes.Count == 0) { await Task.Delay(20); }
    var afterFlip = coord.ClaimCallTimes[0];
    var wakeLatency = afterFlip - beforeFlip;

    await Assert.That(wakeLatency).IsLessThan(TimeSpan.FromMilliseconds(500))
      .Because("gate available transition should RequestImmediatePoll, waking the worker promptly");

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);
  }

  [Test]
  public async Task GateNotInjected_PreservesPreSliceBehaviorAsync() {
    // The signalingGate parameter is optional. When null (pre-slice-33 caller, e.g., an
    // existing test that hasn't been migrated), behavior matches the old adaptive-backoff
    // logic — no clamping based on a gate that isn't there.
    var coord = new TickRecordingCoordinator();
    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coord);
    var sp = services.BuildServiceProvider();
    var schemaGate = new SchemaReadyGate();
    schemaGate.MarkReady();
    var worker = new ClaimWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      new StubInstanceProvider(),
      new NoOpWorkNotificationListener(),
      schemaGate,
      Options.Create(new ClaimWorkerOptions {
        PollingIntervalMilliseconds = 50,
        PollingMaxIntervalMilliseconds = 2_000,
      }),
      NullLogger<ClaimWorker>.Instance);
    // No signalingGate.

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await coord.FirstCallSignal.Task.WaitAsync(TimeSpan.FromSeconds(5));
    await coord.SecondCallSignal.Task.WaitAsync(TimeSpan.FromSeconds(5));
    await coord.ThirdCallSignal.Task.WaitAsync(TimeSpan.FromSeconds(5));

    // With no gate, the adaptive path runs as before — gap should grow past base.
    var gap2to3 = coord.ClaimCallTimes[2] - coord.ClaimCallTimes[1];
    await Assert.That(gap2to3).IsGreaterThan(TimeSpan.FromMilliseconds(70));

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);
  }

  [Test]
  public async Task NotifyHealthyOverride_GateAvailable_UsesRelaxedBaselineAsync() {
    // PR #227 — when LISTEN/NOTIFY is verified healthy AND an operator has set
    // NotifyHealthyPollingIntervalMilliseconds to a value larger than the tight base,
    // that becomes the effective base cadence. Gives multi-pod deployments a way to
    // relieve wh_active_streams unique-index pressure without disabling polling entirely.
    var coord = new TickRecordingCoordinator();
    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coord);
    var sp = services.BuildServiceProvider();
    var schemaGate = new SchemaReadyGate();
    schemaGate.MarkReady();
    var gate = new FakeGate();
    gate.Set(true);
    var worker = new ClaimWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      new StubInstanceProvider(),
      new NoOpWorkNotificationListener(),
      schemaGate,
      Options.Create(new ClaimWorkerOptions {
        PollingIntervalMilliseconds = 50,
        PollingMaxIntervalMilliseconds = 5_000,
        NotifyHealthyPollingIntervalMilliseconds = 400,  // 8× the tight base
      }),
      NullLogger<ClaimWorker>.Instance,
      signalingGate: gate);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await coord.FirstCallSignal.Task.WaitAsync(TimeSpan.FromSeconds(5));
    await coord.SecondCallSignal.Task.WaitAsync(TimeSpan.FromSeconds(5));

    // Gap between ticks should be at least the relaxed base (400ms), not the tight 50ms.
    var gap1to2 = coord.ClaimCallTimes[1] - coord.ClaimCallTimes[0];
    await Assert.That(gap1to2).IsGreaterThan(TimeSpan.FromMilliseconds(300))
      .Because("relaxed baseline must replace the tight 50ms when NOTIFY is healthy");

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);
  }

  [Test]
  public async Task NotifyHealthyOverride_GateUnavailable_FallsBackToTightBaseAsync() {
    // The relaxed baseline MUST NOT apply when the gate reports NOTIFY unavailable.
    // Without NOTIFY waking us, the relaxed cadence would silently slow work pickup to
    // the relaxed value (could be seconds) on every Azure pod whose listener went down.
    // The behavior must mirror the no-override case: tight base, no backoff stretch.
    var coord = new TickRecordingCoordinator();
    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coord);
    var sp = services.BuildServiceProvider();
    var schemaGate = new SchemaReadyGate();
    schemaGate.MarkReady();
    var gate = new FakeGate();
    gate.Set(false);
    var worker = new ClaimWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      new StubInstanceProvider(),
      new NoOpWorkNotificationListener(),
      schemaGate,
      Options.Create(new ClaimWorkerOptions {
        PollingIntervalMilliseconds = 100,
        PollingMaxIntervalMilliseconds = 5_000,
        NotifyHealthyPollingIntervalMilliseconds = 1_000,  // would be 10× if respected
      }),
      NullLogger<ClaimWorker>.Instance,
      signalingGate: gate);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await coord.FirstCallSignal.Task.WaitAsync(TimeSpan.FromSeconds(5));
    await coord.SecondCallSignal.Task.WaitAsync(TimeSpan.FromSeconds(5));
    await coord.ThirdCallSignal.Task.WaitAsync(TimeSpan.FromSeconds(5));

    var gap1to2 = coord.ClaimCallTimes[1] - coord.ClaimCallTimes[0];
    var gap2to3 = coord.ClaimCallTimes[2] - coord.ClaimCallTimes[1];
    await Assert.That(gap1to2).IsLessThan(TimeSpan.FromMilliseconds(300))
      .Because("relaxed override MUST be ignored when NOTIFY is unhealthy — tight base only");
    await Assert.That(gap2to3).IsLessThan(TimeSpan.FromMilliseconds(300));

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);
  }

  [Test]
  public async Task ReconnectCatchUp_GateUnavailableThenAvailable_IncrementsCounterAndFiresImmediatePollAsync() {
    // v0.502 slice B.1: when the NOTIFY gate transitions unavailable→available, the worker
    // should run a catch-up claim (semaphore-wakeup → next loop iteration → claim_orphaned_*).
    // The catch-up is implicit in the existing wake-up chain; this test locks the observable
    // counter so a future refactor that drops the OnAvailabilityChanged handler is caught.
    var (worker, coord, gate) = _newWorker(
      pollingIntervalMilliseconds: 50,
      pollingMaxIntervalMilliseconds: 2_000,
      gateAvailable: true);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await coord.FirstCallSignal.Task.WaitAsync(TimeSpan.FromSeconds(5));

    var initialCount = worker.ReconnectCatchUpCount;
    gate.Set(false);
    await Task.Delay(20);  // Brief unavailable window
    gate.Set(true);

    // Allow the OnAvailabilityChanged → RequestImmediatePoll → wake path a moment to fire.
    await Task.Delay(100);

    await Assert.That(worker.ReconnectCatchUpCount).IsEqualTo(initialCount + 1)
      .Because("unavailable→available transition must increment the catch-up counter exactly once");

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);
  }

  [Test]
  public async Task ReconnectCatchUp_NoTransition_DoesNotIncrementCounterAsync() {
    // If the gate flips false then back to true within the same OnAvailabilityChanged batch
    // OR the gate stays in one state, no catch-up fires. Confirms we count actual reconnects,
    // not phantom transitions.
    var (worker, coord, gate) = _newWorker(
      pollingIntervalMilliseconds: 50,
      pollingMaxIntervalMilliseconds: 2_000,
      gateAvailable: true);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await coord.FirstCallSignal.Task.WaitAsync(TimeSpan.FromSeconds(5));

    // Same state set — FakeGate.Set is a no-op when IsAvailable already matches.
    gate.Set(true);
    await Task.Delay(50);

    await Assert.That(worker.ReconnectCatchUpCount).IsEqualTo(0)
      .Because("re-setting the gate to its current state must NOT register as a reconnect");

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);
  }
}
