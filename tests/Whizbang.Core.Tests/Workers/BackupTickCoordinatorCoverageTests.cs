using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Coverage for three <see cref="BackupTickCoordinator"/> branches
/// <see cref="BackupTickCoordinatorTests"/> and <see cref="BackupTickCoordinatorStateMachineTests"/>
/// don't reach: a schema-gate wait canceled during startup, the ASLEEP state's real sleep-then-loop
/// cycle, and a registered tick whose <see cref="OperationCanceledException"/> races the
/// coordinator's own shutdown check. This coordinator is the zero-idle-polling backstop for every
/// registered periodic concern — a startup cancellation that faults instead of returning cleanly
/// would report a routine shutdown as a crash, and an ASLEEP loop that never actually re-checks
/// idle time would mean the backstop never wakes up.
/// </summary>
public class BackupTickCoordinatorCoverageTests {

  /// <summary>Always reports schema readiness as canceled, regardless of the caller's own token —
  /// simulates a host stopped mid-migration before the schema ever became ready. Signals
  /// <see cref="Entered"/> first, so a test can wait on evidence the coordinator's own body reached
  /// the barrier instead of on <c>StartAsync</c> returning (which, since .NET 10, only means the
  /// thread-pool work item was queued).</summary>
  private sealed class _canceledSchemaGate : ISchemaReadyGate {
    public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public bool IsReady => false;
    public void MarkReady() { }
    public Task WaitForReadyAsync(CancellationToken cancellationToken) {
      Entered.TrySetResult();
      return Task.FromException(new OperationCanceledException("schema never became ready"));
    }
  }

  /// <summary>Always reports a small, constant idle time so the ASLEEP branch never transitions
  /// to POLLING; signals once its getter has been read a second time, proving the sleep-then-loop
  /// cycle actually ran instead of the coordinator exiting or hanging on the first pass.</summary>
  private sealed class _alwaysIdleTracker : IIdleActivityTracker {
    private int _reads;
    public TaskCompletionSource SecondReadSignal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public void Touch(string source) { }
    public TimeSpan TimeSinceLastActivity {
      get {
        if (Interlocked.Increment(ref _reads) == 2) {
          SecondReadSignal.TrySetResult();
        }
        return TimeSpan.Zero;
      }
    }
    public DateTimeOffset LastActivityAt => DateTimeOffset.UtcNow;
    public string LastActivitySource => string.Empty;
  }

  /// <summary>What breaks: a host stopped before its schema exists must shut down cleanly. If the
  /// schema-gate cancellation escaped instead of returning, a routine "stopped during migration"
  /// would report as a hosted-service crash — and if the coordinator swallowed it and carried on,
  /// it would start polling the database the migration has not created yet.</summary>
  /// <remarks>
  /// The stopping token is deliberately never canceled here. That is what makes the "ExecuteAsync
  /// completed" assertion mean something: the only way out of this method without the schema-gate
  /// catch returning is the polling loop, which would run until shutdown. The wait is on a signal
  /// the coordinator's own body emits — since .NET 10 <c>BackgroundService.StartAsync</c> queues
  /// <c>ExecuteAsync</c> to the thread pool, so StartAsync returning proves nothing ran.
  /// </remarks>
  [Test]
  [Timeout(30000)]
  public async Task ExecuteAsync_SchemaGateCanceledDuringStartup_ReturnsWithoutFaultingAsync(CancellationToken testToken) {
    var gate = new _canceledSchemaGate();
    var registry = new BackupTickRegistry();
    var ticked = 0;
    registry.Register("backstop", _ => { Interlocked.Increment(ref ticked); return Task.CompletedTask; }, () => true);

    var coordinator = new BackupTickCoordinator(
      new IdleActivityTracker(TimeProvider.System),
      registry,
      // Zero idle threshold: if the gate cancellation were ignored, the coordinator would go
      // straight to POLLING and fire the registration above.
      Options.Create(new BackupTickCoordinatorOptions { IdleThreshold = TimeSpan.Zero }),
      NullLogger<BackupTickCoordinator>.Instance,
      schemaReadyGate: gate);

    await coordinator.StartAsync(CancellationToken.None);
    await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), testToken);
    var executeTask = coordinator.ExecuteTask;

    await executeTask!.WaitAsync(TimeSpan.FromSeconds(10), testToken)
      .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

    await Assert.That(executeTask.IsCompleted).IsTrue()
      .Because("with the stopping token still live, the only way out of ExecuteAsync is the "
             + "schema-gate catch returning — the polling loop would run indefinitely");
    await Assert.That(executeTask.IsFaulted).IsFalse()
      .Because("a host stopped before the schema exists must shut down cleanly, not report a crash");
    await Assert.That(Volatile.Read(ref ticked)).IsEqualTo(0)
      .Because("returning at the gate means no registered backstop tick ever queries a database "
             + "whose migration has not run");

    await coordinator.StopAsync(CancellationToken.None);
  }

  /// <summary>What breaks: the ASLEEP branch's whole purpose is zero DB calls while idle — if it
  /// never actually slept and re-checked, the backstop would either busy-loop or never wake up to
  /// transition to POLLING once real idle time elapses.</summary>
  [Test]
  [Timeout(30000)]
  public async Task ExecuteAsync_Asleep_SleepsThenReChecksIdleTimeAsync(CancellationToken testToken) {
    var tracker = new _alwaysIdleTracker();
    var coordinator = new BackupTickCoordinator(
      tracker,
      new BackupTickRegistry(),
      Options.Create(new BackupTickCoordinatorOptions { IdleThreshold = TimeSpan.FromMilliseconds(5) }),
      NullLogger<BackupTickCoordinator>.Instance,
      schemaReadyGate: Whizbang.Core.Workers.SchemaReadyGate.AlreadyReady());

    using var cts = new CancellationTokenSource();
    await coordinator.StartAsync(cts.Token);
    await tracker.SecondReadSignal.Task.WaitAsync(TimeSpan.FromSeconds(5), testToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    await coordinator.StopAsync(CancellationToken.None);

    await Assert.That(tracker.SecondReadSignal.Task.IsCompletedSuccessfully).IsTrue()
      .Because("a second read of idle time proves the ASLEEP branch actually slept and looped back to re-check, instead of exiting or hanging on the first pass");
  }

  /// <summary>What breaks: a registered tick canceling in lockstep with the coordinator's own
  /// shutdown token must stop the cycle without being counted as a delegate failure — counting it
  /// would page an operator for an ordinary shutdown race.</summary>
  [Test]
  public async Task FireOneCycleAsync_TickCancelsDuringShutdown_BreaksWithoutCountingAFailureAsync() {
    var registry = new BackupTickRegistry();
    using var cts = new CancellationTokenSource();
    var ranSecond = false;
    registry.Register("shutdown-racer", _ => {
      cts.Cancel();
      throw new OperationCanceledException("racing the coordinator's own shutdown");
    }, () => true);
    registry.Register("second", _ => { ranSecond = true; return Task.CompletedTask; }, () => true);
    var coordinator = new BackupTickCoordinator(
      new IdleActivityTracker(TimeProvider.System),
      registry,
      Options.Create(new BackupTickCoordinatorOptions()),
      NullLogger<BackupTickCoordinator>.Instance,
      schemaReadyGate: Whizbang.Core.Workers.SchemaReadyGate.AlreadyReady());

    var invoked = await coordinator.FireOneCycleAsync(cts.Token);

    await Assert.That(ranSecond).IsFalse()
      .Because("a cancellation racing the coordinator's own token must halt the cycle immediately");
    await Assert.That(invoked).IsEqualTo(1)
      .Because("the racing tick was still invoked before it canceled");
    await Assert.That(coordinator.TotalDelegateFailures).IsEqualTo(0L)
      .Because("shutdown propagation is not a delegate failure — counting it would alert on an ordinary stop");
  }
}
