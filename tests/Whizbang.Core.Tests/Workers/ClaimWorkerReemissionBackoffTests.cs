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
/// Locks <see cref="ClaimWorker"/>'s cadence against a RE-EMITTED work set.
///
/// <para>
/// <c>claim_work</c>'s eligible CTEs filter <c>instance_id = me AND lease_expiry &gt; NOW() AND
/// processed_at IS NULL</c>, so every leased-but-uncompleted row is re-emitted on EVERY poll —
/// deliberately, because the alternative (an in-memory in-flight filter) proved unrecoverable in
/// production when a drain died before clearing its flag. Emission must therefore stay unconditional.
/// </para>
///
/// <para>
/// The cost was never bounded, though. The claim loop treats "the batch was non-empty" as "there
/// was work", which is true but not useful: a re-offered row makes it non-empty forever. The
/// empty-poll streak never increments, the adaptive backoff never engages, and the loop re-claims
/// as fast as the database can answer — a rate set by query latency rather than by workload. A
/// nearly-empty store sustains it just as well as a large one, which is the signature that the
/// spin is structural rather than driven by real work.
/// </para>
///
/// <para>
/// The contract: re-offering the SAME work must back off like an idle poll. Emission is untouched
/// (every stream_id is still distributed every cycle, so nothing can wedge) — only the WAIT adapts.
/// </para>
/// </summary>
/// <docs>fundamentals/work-coordinator/claim-cadence</docs>
[NotInParallel(Order = 103)]
public class ClaimWorkerReemissionBackoffTests {

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

  /// <summary>
  /// Returns the IDENTICAL non-empty stream set on every claim — the shape a leased,
  /// not-yet-completed row produces cycle after cycle.
  /// </summary>
  private sealed class ReemittingCoordinator : IWorkCoordinator {
    private readonly Lock _lock = new();
    private readonly Guid _stuckStream = TrackedGuid.NewMedo();
    public List<DateTimeOffset> ClaimCallTimes { get; } = [];
    public TaskCompletionSource FirstCallSignal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource SecondCallSignal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource ThirdCallSignal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Fires after each claim. The test uses it to pull the worker's wake lever, standing in for
    /// the real feedback path: the drains this claim triggers publish, their completions signal,
    /// and the signal releases the wake permit.
    /// </summary>
    public Action? AfterClaim { get; set; }

    /// <summary>When true, every claim returns an EMPTY batch — the true-idle shape.</summary>
    public bool ReturnEmpty { get; set; }

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
      AfterClaim?.Invoke();
      // Same stream, every time. Nothing new ever appears.
      return Task.FromResult(ReturnEmpty
          ? new WorkBatch { OutboxWork = [], InboxWork = [], PerspectiveWork = [] }
          : new WorkBatch {
            OutboxWork = [],
            InboxWork = [],
            PerspectiveWork = [],
            OutboxStreamIds = [_stuckStream],
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

  private sealed class AvailableGate : INotifySignalingGate {
    public bool IsAvailable => true;
    public DateTimeOffset? LastVerifiedAt => null;
    public DateTimeOffset? LastFailureAt => null;
    public string? LastFailureReason => null;
    public event Action<bool>? OnAvailabilityChanged { add { } remove { } }
    public Task<bool> ProbeNowAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
  }

  [Test]
  public async Task RepeatedIdenticalWorkSet_BacksOffLikeAnIdlePollAsync() {
    var coord = new ReemittingCoordinator();
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
        NotifyHealthyPollingIntervalMilliseconds = null,
      }),
      NullLogger<ClaimWorker>.Instance,
      signalingGate: new AvailableGate());

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await coord.FirstCallSignal.Task.WaitAsync(TimeSpan.FromSeconds(5));
    await coord.SecondCallSignal.Task.WaitAsync(TimeSpan.FromSeconds(5));
    await coord.ThirdCallSignal.Task.WaitAsync(TimeSpan.FromSeconds(5));

    // Mirrors GateAvailable_AdaptiveBackoffMayStretchToMaxAsync, which asserts this same growth
    // for EMPTY polls. Re-offering an identical set is idle in every sense that matters to
    // cadence, so it must behave the same. Without the fix the streak never increments and the
    // gap pins to the 50ms base — the hot loop.
    var gap2to3 = coord.ClaimCallTimes[2] - coord.ClaimCallTimes[1];
    await Assert.That(gap2to3).IsGreaterThan(TimeSpan.FromMilliseconds(70))
      .Because(
        "re-offering the SAME work set must back off like an idle poll; treating a re-emitted "
        + "row as 'there was work' pins the claim loop at its tightest cadence forever");

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);
  }

  /// <summary>
  /// The production half. The wake permit short-circuits the loop's wait, and the system's own
  /// completion traffic keeps setting it — publishes complete, completions signal, the permit is
  /// released. So an empty-poll streak alone cannot slow the loop down when signals keep
  /// arriving: the streak stretches the timeout, but a pending permit means the wait returns
  /// immediately anyway.
  ///
  /// Here every claim pulls the wake lever, standing in for that feedback path. With the same
  /// work re-offered each time, the loop must STILL space its claims out. Without the pre-wait
  /// spacing this pins to back-to-back claims regardless of the streak, which is why the streak
  /// increment needed to be verified separately from the spacing that acts on it.
  /// </summary>
  [Test]
  public async Task RepeatedWorkSet_UnderConstantWakeSignals_StillSpacesClaimsAsync() {
    var coord = new ReemittingCoordinator();
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
        NotifyHealthyPollingIntervalMilliseconds = null,
      }),
      NullLogger<ClaimWorker>.Instance,
      signalingGate: new AvailableGate());

    // Every claim immediately re-arms the wake, as our own completion signals do in production.
    coord.AfterClaim = worker.RequestImmediatePoll;

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await coord.FirstCallSignal.Task.WaitAsync(TimeSpan.FromSeconds(5));
    await coord.SecondCallSignal.Task.WaitAsync(TimeSpan.FromSeconds(5));
    await coord.ThirdCallSignal.Task.WaitAsync(TimeSpan.FromSeconds(5));

    var gap2to3 = coord.ClaimCallTimes[2] - coord.ClaimCallTimes[1];
    await Assert.That(gap2to3).IsGreaterThan(TimeSpan.FromMilliseconds(40))
      .Because(
        "a permanently-armed wake permit must not let the loop re-claim the same work set "
        + "back-to-back; the spacing has to apply before the permit is consumed");

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);
  }

  /// <summary>
  /// The other side of the spacing coin (live forensic: v7-timestamp deltas of 8.4s/5.3s on an
  /// interactive workload, interleaved with sub-second samples). The spacing nap runs as an
  /// uninterruptible delay BEFORE the wake-permit wait, so a NEW-WORK doorbell (store-level
  /// NOTIFY → bus → SignalNewWork) that lands mid-nap is swallowed: the permit is released but
  /// the nap keeps sleeping, and the fresh row waits out the remainder — up to the full
  /// notify-healthy floor (5s in production) per hop. New work must interrupt the nap; ONLY
  /// completion-feedback wakes (RequestImmediatePoll) may not.
  /// </summary>
  [Test]
  public async Task RepeatedWorkSet_NewWorkDoorbellDuringSpacing_ClaimsPromptlyAsync() {
    var coord = new ReemittingCoordinator();
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
        PollingMaxIntervalMilliseconds = 10_000,
        // The gate reports available, so this IS the spacing nap length — production's 5s
        // floor scaled to 3s so the RED failure (claim 3 waits out the nap) is unmistakable
        // against the 1s promptness bound without slowing the suite unduly.
        NotifyHealthyPollingIntervalMilliseconds = 3_000,
      }),
      NullLogger<ClaimWorker>.Instance,
      signalingGate: new AvailableGate());

    // Drive the first two claims promptly (completion-feedback shape), so claim 2 is the
    // re-offer that engages the spacing. Stop pulling the lever after that: the nap that
    // follows claim 2 must be interrupted by the DOORBELL below, not by feedback wakes.
    var fed = 0;
    coord.AfterClaim = () => {
      if (Interlocked.Increment(ref fed) <= 1) {
        worker.RequestImmediatePoll();
      }
    };

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await coord.FirstCallSignal.Task.WaitAsync(TimeSpan.FromSeconds(5));
    await coord.SecondCallSignal.Task.WaitAsync(TimeSpan.FromSeconds(5));

    // Give the loop a moment to enter the spacing nap that follows the re-offer claim, then
    // ring the new-work doorbell mid-nap.
    await Task.Delay(300);
    var doorbellAt = DateTimeOffset.UtcNow;
    worker.SignalNewWork();

    await coord.ThirdCallSignal.Task.WaitAsync(TimeSpan.FromSeconds(8));
    var wakeLatency = coord.ClaimCallTimes[2] - doorbellAt;

    await Assert.That(wakeLatency).IsLessThan(TimeSpan.FromSeconds(1))
      .Because(
        "a NEW-WORK doorbell must interrupt the repeat-claim spacing nap — otherwise a fresh row "
        + "that arrives mid-nap waits out the remainder of the notify-healthy floor (0-5s per hop "
        + "in production; two unlucky hops measured at 8.4s end-to-end on an interactive workload)");

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);
  }

  /// <summary>
  /// The guard that keeps the fix honest: completion-feedback wakes must STILL not defeat the
  /// spacing at nap scale. Same shape as the doorbell test, but the mid-nap wake is
  /// RequestImmediatePoll — claim 3 must wait out the nap.
  /// </summary>
  [Test]
  public async Task RepeatedWorkSet_CompletionFeedbackDuringSpacing_StillWaitsOutTheNapAsync() {
    var coord = new ReemittingCoordinator();
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
        PollingMaxIntervalMilliseconds = 10_000,
        NotifyHealthyPollingIntervalMilliseconds = 3_000,
      }),
      NullLogger<ClaimWorker>.Instance,
      signalingGate: new AvailableGate());

    var fed = 0;
    coord.AfterClaim = () => {
      if (Interlocked.Increment(ref fed) <= 1) {
        worker.RequestImmediatePoll();
      }
    };

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await coord.FirstCallSignal.Task.WaitAsync(TimeSpan.FromSeconds(5));
    await coord.SecondCallSignal.Task.WaitAsync(TimeSpan.FromSeconds(5));

    await Task.Delay(300);
    worker.RequestImmediatePoll();

    await coord.ThirdCallSignal.Task.WaitAsync(TimeSpan.FromSeconds(8));
    var gap2to3 = coord.ClaimCallTimes[2] - coord.ClaimCallTimes[1];

    await Assert.That(gap2to3).IsGreaterThan(TimeSpan.FromSeconds(2))
      .Because(
        "completion-feedback wakes exist precisely so the loop's own traffic re-arms the permit; "
        + "letting them interrupt the spacing nap would reintroduce the re-offer spin loop the "
        + "spacing was built to damp. Only NEW-WORK doorbells may cut the nap short.");

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);
  }

  /// <summary>
  /// The TRUE-IDLE twin of the re-offer spacing (#635). An empty store under constant
  /// completion-feedback wakes ran the claim cycle at permit-arrival rate: the empty streak
  /// stretched the WAIT timeout, but a pending permit returns immediately, and the spacing nap
  /// engaged only on re-offers. Measured fleet-wide as a ~27/s claim metronome on a deployment
  /// with zero application traffic. Idle must space like idle regardless of what keeps ringing
  /// the completion bell.
  /// </summary>
  [Test]
  public async Task EmptyStore_UnderConstantCompletionFeedback_StillSpacesClaimsAsync() {
    var coord = new ReemittingCoordinator { ReturnEmpty = true };
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
        PollingMaxIntervalMilliseconds = 10_000,
        NotifyHealthyPollingIntervalMilliseconds = 3_000,
      }),
      NullLogger<ClaimWorker>.Instance,
      signalingGate: new AvailableGate());

    // Constant feedback: every claim pulls the wake lever, the shape a chatty fleet produces.
    coord.AfterClaim = worker.RequestImmediatePoll;

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await coord.FirstCallSignal.Task.WaitAsync(TimeSpan.FromSeconds(5));
    await coord.SecondCallSignal.Task.WaitAsync(TimeSpan.FromSeconds(8));
    await coord.ThirdCallSignal.Task.WaitAsync(TimeSpan.FromSeconds(8));

    var gap2to3 = coord.ClaimCallTimes[2] - coord.ClaimCallTimes[1];
    await Assert.That(gap2to3).IsGreaterThan(TimeSpan.FromSeconds(2))
      .Because(
        "an EMPTY claim under healthy notify must space out even while completion-feedback "
        + "permits keep arriving; otherwise idle cadence is set by whoever rings the bell, and "
        + "a whole fleet of quiet services claims at tens of cycles per second forever");

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);
  }

  /// <summary>
  /// The responsiveness guard on the idle nap: a NEW-WORK doorbell must cut it short exactly as
  /// it cuts the re-offer nap, so the spacing never taxes a genuinely fresh row.
  /// </summary>
  [Test]
  public async Task EmptyStore_NewWorkDoorbellInterruptsTheIdleSpacingAsync() {
    var coord = new ReemittingCoordinator { ReturnEmpty = true };
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
        PollingMaxIntervalMilliseconds = 10_000,
        NotifyHealthyPollingIntervalMilliseconds = 3_000,
      }),
      NullLogger<ClaimWorker>.Instance,
      signalingGate: new AvailableGate());

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await coord.FirstCallSignal.Task.WaitAsync(TimeSpan.FromSeconds(5));

    // The loop is inside the idle spacing nap that follows the empty claim. Ring the doorbell.
    await Task.Delay(300);
    var doorbellAt = DateTimeOffset.UtcNow;
    worker.SignalNewWork();

    await coord.SecondCallSignal.Task.WaitAsync(TimeSpan.FromSeconds(8));
    var wakeLatency = coord.ClaimCallTimes[1] - doorbellAt;

    await Assert.That(wakeLatency).IsLessThan(TimeSpan.FromSeconds(1))
      .Because("idle spacing must never tax a genuinely fresh row: the doorbell cancels the nap");

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);
  }

}
