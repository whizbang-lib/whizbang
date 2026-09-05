using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Workers;

#pragma warning disable CA1707 // test method underscores

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// <para>Locks three production findings from the first observed run of idle-arbitrated recovery,
/// all found on the same day, all silent:</para>
/// <list type="number">
///   <item>A second, open <c>TryAddSingleton&lt;HousekeepingCoordinator&gt;()</c> in the core
///   registration ran before the worker pipeline's metrics-attached factory, so the factory was a
///   no-op fleet-wide: the coordinator arbitrated with the parameterless test constructor and not
///   one decision was ever counted. Verified in production as a metric that existed in code and
///   never once appeared in telemetry.</item>
///   <item><see cref="MessageFailureReason.PoisonRedeliveryLoop"/> had no recovery policy, so it
///   fell through to Unknown's OneShotThenHold — and since every requarantine mints a NEW row
///   with a fresh one-shot budget, recovery and the observation bound played ping-pong at
///   ~190 rows/minute, throttled only by the loop breaker.</item>
///   <item>The worker returned silently when <c>IDeadLetterRecoveryService</c> resolved null —
///   from the generation-replay block that return killed the whole worker, from the scan it
///   no-opped every cycle — either way indistinguishable from working, forever.</item>
/// </list>
/// </summary>
/// <code-under-test>src/Whizbang.Core/ServiceCollectionExtensions.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Messaging/DeadLetterRecoveryTypes.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Workers/DeadLetterRecoveryWorker.cs</code-under-test>
[Category("Shard2")]
public sealed class RecoveryLifecycleHardeningTests {

  // ==========================================================================
  // 1. The turnkey coordinator must be the metrics-attached one, in the REAL
  //    bootstrap order (AddWhizbang first, AddWhizbangWorkers second).
  // ==========================================================================
  [Test]
  public async Task TurnkeyBootstrap_CoordinatorRecordsDecisions_OnTheHousekeepingMeterAsync() {
    var services = new ServiceCollection();
    services.AddWhizbang();
    services.AddWhizbangWorkers();
    await using var provider = services.BuildServiceProvider();

    var recorded = new List<(string Instrument, long Value)>();
    using var listener = new MeterListener();
    listener.InstrumentPublished = (instrument, l) => {
      if (instrument.Meter.Name == "Whizbang.Housekeeping") {
        l.EnableMeasurementEvents(instrument);
      }
    };
    listener.SetMeasurementEventCallback<long>((instrument, value, _, _) => {
      lock (recorded) { recorded.Add((instrument.Name, value)); }
    });
    listener.Start();

    var coordinator = provider.GetRequiredService<HousekeepingCoordinator>();
    var decision = coordinator.TryBegin(
      HousekeepingCoordinator.Activity.DeadLetterRecovery,
      new ServiceBacklog { UnprocessedInboxRows = 0, ActiveLeasedRows = 0 });
    if (decision.Granted) {
      coordinator.End(HousekeepingCoordinator.Activity.DeadLetterRecovery);
    }

    List<(string, long)> snapshot;
    lock (recorded) { snapshot = [.. recorded]; }
    await Assert.That(snapshot.Any(r => r.Item1 == "whizbang.housekeeping.decisions")).IsTrue()
      .Because("the coordinator the REAL bootstrap order resolves must be the metrics-attached "
             + "one — a second open registration made every arbitration decision invisible in "
             + "production while the dashboards said the feature did not run");
  }

  // ==========================================================================
  // 2. The observation-bound quarantine class must never be auto-re-driven.
  // ==========================================================================
  [Test]
  public async Task PoisonRedeliveryLoop_DefaultsToHoldForReview_NeverAutoRedriveAsync() {
    var policy = new DefaultDeadLetterRecoveryPolicy(
      Options.Create(new DeadLetterRecoveryOptions()));

    var entry = new DeadLetterEntry(
      DeadLetterId: Guid.NewGuid(),
      SourceTable: "wh_inbox",
      SourceId: Guid.NewGuid(),
      StreamId: null,
      MessageType: "Whizbang.Core.Messaging.IntegrityCheckpoint",
      FailureReason: MessageFailureReason.PoisonRedeliveryLoop,
      AttemptsWhenDlq: 1,
      DeadLetteredAt: DateTimeOffset.UtcNow,
      RecoveryStatus: DeadLetterRecoveryStatus.Pending,
      RecoveryAttempts: 0,
      Generation: "test/1");

    var p = policy.GetPolicy(entry);
    await Assert.That(p.MaxRecoveryAttempts).IsEqualTo(0)
      .Because("this reason means the observation counter proved redelivery is not making "
             + "progress; re-driving it mints a fresh dead letter and the two mechanisms "
             + "ping-pong forever — the loop breaker was the only thing throttling it in "
             + "production");
    await Assert.That(p.Name).IsEqualTo("HoldForReview")
      .Because("held rows stay visible for an operator instead of feeding the loop");
  }

  // ==========================================================================
  // 3. A missing recovery service must be SAID, not silently absorbed.
  // ==========================================================================
  [Test]
  public async Task MissingRecoveryService_IsLoggedOnce_AndTheWorkerKeepsRunningAsync() {
    var services = new ServiceCollection();
    services.AddFakeLogging();
    await using var provider = services.BuildServiceProvider();
    var collector = provider.GetFakeLogCollector();

    var worker = new DeadLetterRecoveryWorker(
      provider.GetRequiredService<IServiceScopeFactory>(),
      new ImmediateGate(),
      Options.Create(new DeadLetterRecoveryOptions {
        ScanIntervalMinutes = 1,
        WaitForIdle = false,
        EnableGenerationReplay = true
      }),
      Options.Create(new Whizbang.Core.Messaging.StreamIntegrityOptions()),
      new FixedGeneration(),
      provider.GetRequiredService<ILogger<DeadLetterRecoveryWorker>>());

    using var cts = new CancellationTokenSource();
    var run = worker.StartAsync(cts.Token);
    // The warning is emitted on the first pass that discovers the absence — either the
    // generation-replay sweep or the first scan; both happen before the first idle wait.
    var deadline = Task.Delay(TimeSpan.FromSeconds(5), cts.Token);
    while (!collector.GetSnapshot().Any(r => r.Level == LogLevel.Warning
             && r.Message.Contains("IDeadLetterRecoveryService", StringComparison.Ordinal))) {
      if (deadline.IsCompleted) { break; }
      await Task.Yield();
    }
    cts.Cancel();
    try { await run; } catch (OperationCanceledException) { }

    var warnings = collector.GetSnapshot()
      .Where(r => r.Level == LogLevel.Warning
               && r.Message.Contains("IDeadLetterRecoveryService", StringComparison.Ordinal))
      .ToList();
    await Assert.That(warnings.Count).IsEqualTo(1)
      .Because("a worker that cannot do its job must say WHAT is missing and WHAT stops "
             + "happening, exactly once — in production this state was indistinguishable "
             + "from a healthy quiet worker for an entire day, and 20,000 rows sat parked "
             + "behind the silence");
  }

  // ==========================================================================
  // 4. Recovery must have the same bounded-deferral escape maintenance has.
  //    A service with a permanent trickle of work never reads settled at scan
  //    time, and without a floor its dead letters defer FOREVER — observed in
  //    production as 20,000 due rows behind a service whose backlog never quite
  //    touched zero.
  // ==========================================================================
  private static ServiceBacklog _busy() => new() { UnprocessedInboxRows = 500, ActiveLeasedRows = 3 };

  [Test]
  public async Task Recovery_DeferredPastTheLimit_ForcesThroughAsync() {
    var coordinator = new HousekeepingCoordinator(
      new HousekeepingCoordinator.Settings { MaxConsecutiveDeferrals = 3 });

    for (var i = 0; i < 3; i++) {
      var deferred = coordinator.TryBegin(HousekeepingCoordinator.Activity.DeadLetterRecovery, _busy());
      await Assert.That(deferred.Granted).IsFalse();
    }

    var forced = coordinator.TryBegin(HousekeepingCoordinator.Activity.DeadLetterRecovery, _busy());
    await Assert.That(forced.Granted).IsTrue()
      .Because("a permanently-busy service must still recover its dead letters eventually — "
             + "unbounded deferral is starvation with a politer name");
    await Assert.That(forced.Reason).IsEqualTo(HousekeepingCoordinator.Verdict.ProceedDeferralLimit)
      .Because("the forced pass is reported distinctly so a dashboard can tell 'ran because idle' "
             + "from 'ran because it was never idle once all hour'");
  }

  [Test]
  public async Task Recovery_CompletedForcedRun_ReArmsTheDeferralBudgetAsync() {
    var coordinator = new HousekeepingCoordinator(
      new HousekeepingCoordinator.Settings { MaxConsecutiveDeferrals = 2 });
    for (var i = 0; i < 2; i++) {
      _ = coordinator.TryBegin(HousekeepingCoordinator.Activity.DeadLetterRecovery, _busy());
    }
    var forced = coordinator.TryBegin(HousekeepingCoordinator.Activity.DeadLetterRecovery, _busy());
    await Assert.That(forced.Reason).IsEqualTo(HousekeepingCoordinator.Verdict.ProceedDeferralLimit);
    coordinator.End(HousekeepingCoordinator.Activity.DeadLetterRecovery);

    var next = coordinator.TryBegin(HousekeepingCoordinator.Activity.DeadLetterRecovery, _busy());
    await Assert.That(next.Granted).IsFalse()
      .Because("the escape is a bounded trickle, not a switch that stays open: after the forced "
             + "run completes, busy deferrals count from zero again");
  }

  [Test]
  public async Task RecoveryAndMaintenance_DeferralBudgets_AreIndependentAsync() {
    var coordinator = new HousekeepingCoordinator(
      new HousekeepingCoordinator.Settings { MaxConsecutiveDeferrals = 2 });
    for (var i = 0; i < 2; i++) {
      _ = coordinator.TryBegin(HousekeepingCoordinator.Activity.DeadLetterRecovery, _busy());
    }

    var maintenance = coordinator.TryBegin(HousekeepingCoordinator.Activity.Maintenance, _busy());
    await Assert.That(maintenance.Granted).IsFalse()
      .Because("recovery's spent deferrals must not open maintenance's escape — shared counters "
             + "let the lowest rank ride the highest rank's starvation");
  }

  private sealed class ImmediateGate : ISchemaReadyGate {
    public Task WaitForReadyAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public void MarkReady() { }
    public bool IsReady => true;
  }

  private sealed class FixedGeneration : IGenerationProvider {
    public string GetGeneration() => "test/1";
  }
}
