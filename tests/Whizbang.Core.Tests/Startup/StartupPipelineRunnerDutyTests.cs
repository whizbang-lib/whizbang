using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Startup;

namespace Whizbang.Core.Tests.Startup;

/// <summary>
/// Increment 7, the runner half: a step requiring a duty runs on the instance that wins it, and
/// non-holders do what the descriptor declares — <c>Skip</c> reports <c>capability not held</c>
/// and carries on; <c>Await</c> keeps re-attempting until the holder's release lets it win, where
/// the step's own idempotency makes the late winner find nothing to do. Without an elector a duty
/// degrades to a shared capability, exactly as documented.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Startup/StartupPipelineRunner.cs</code-under-test>
[Category("Startup")]
public class StartupPipelineRunnerDutyTests {

  private sealed class _grant(Action onRelease) : IDutyGrant {
    public string Duty => "test-duty";
    public DateTimeOffset AcquiredAt { get; } = DateTimeOffset.UtcNow;
    public bool Released { get; private set; }
    public Task<bool> VerifyStillHeldAsync(CancellationToken cancellationToken) => Task.FromResult(!Released);
    public ValueTask DisposeAsync() {
      Released = true;
      onRelease();
      return ValueTask.CompletedTask;
    }
  }

  /// <summary>Grants on the Nth attempt; refuses before. Counts every attempt.</summary>
  private sealed class _elector(int grantOnAttempt) : IDutyElector {
    private int _attempts;
    public int Attempts => Volatile.Read(ref _attempts);
    public _grant? Granted { get; private set; }
    public Task<DutyAttempt> TryAcquireAsync(string duty, CancellationToken cancellationToken) {
      var attempt = Interlocked.Increment(ref _attempts);
      if (attempt < grantOnAttempt) {
        return Task.FromResult(DutyAttempt.Lost(DutyRefusal.Contended, "held by a peer"));
      }
      Granted = new _grant(() => { });
      return Task.FromResult(DutyAttempt.Granted(Granted));
    }
  }

  private sealed class _countingStep(string name, string capability, NonHolderBehavior nonHolder = NonHolderBehavior.Await)
      : IStartupStep {
    private int _executions;
    public int Executions => Volatile.Read(ref _executions);
    public StartupStepDescriptor Descriptor { get; } = new() {
      Name = name,
      RequiredCapability = capability,
      NonHolderBehavior = nonHolder,
    };
    public ValueTask<StartupStepReport> ExecuteAsync(CancellationToken cancellationToken) {
      Interlocked.Increment(ref _executions);
      return new(new StartupStepReport(StartupStepOutcome.Completed));
    }
  }

  [Test]
  public async Task DutyStep_WhenTheElectorGrants_RunsAndReleasesAsync() {
    var elector = new _elector(grantOnAttempt: 1);
    var step = new _countingStep("Rewrite", "maintainer");
    var runner = new StartupPipelineRunner([step], dutyElector: elector);

    var results = await runner.RunAsync(CancellationToken.None);

    await Assert.That(step.Executions).IsEqualTo(1);
    await Assert.That(results[0].Outcome).IsEqualTo(StartupStepOutcome.Completed);
    await Assert.That(elector.Granted!.Released).IsTrue()
      .Because("the duty is held for the step's tenure, not the process's — release on completion");
  }

  [Test]
  public async Task DutyStep_NonHolderWithSkip_ReportsCapabilityNotHeldAndCarriesOnAsync() {
    var elector = new _elector(grantOnAttempt: int.MaxValue);   // never wins
    var step = new _countingStep("Rewrite", "maintainer", NonHolderBehavior.Skip);
    var runner = new StartupPipelineRunner([step], dutyElector: elector);

    var results = await runner.RunAsync(CancellationToken.None);

    await Assert.That(step.Executions).IsEqualTo(0)
      .Because("nobody blocks on a VACUUM FULL — that is the entire reason Skip exists");
    await Assert.That(results[0].Outcome).IsEqualTo(StartupStepOutcome.Skipped);
    await Assert.That(results[0].Reason).IsEqualTo("capability not held")
      .Because("'lost the race' and 'found nothing to do' are different facts an operator must "
             + "be able to tell apart");
    await Assert.That(elector.Attempts).IsEqualTo(1)
      .Because("Skip is a single non-blocking attempt, never a wait");
  }

  [Test]
  public async Task DutyStep_NonHolderWithAwait_ReAttemptsUntilTheHoldersReleaseLetsItWinAsync() {
    var elector = new _elector(grantOnAttempt: 3);   // wins on the third attempt
    var step = new _countingStep("Migrate2", "migrator", NonHolderBehavior.Await);
    var runner = new StartupPipelineRunner([step], dutyElector: elector) {
      DutyRetryInterval = TimeSpan.FromMilliseconds(10),
    };

    var results = await runner.RunAsync(CancellationToken.None);

    await Assert.That(elector.Attempts).IsEqualTo(3)
      .Because("the holder's release is the completion signal, and re-attempting IS how a "
             + "waiter learns of it — exactly what the migration advisory lock produces today");
    await Assert.That(step.Executions).IsEqualTo(1)
      .Because("the late winner runs the step; its own idempotency makes it find nothing to do");
    await Assert.That(results[0].Outcome).IsEqualTo(StartupStepOutcome.Completed);
  }

  [Test]
  public async Task DutyStep_WithNoElector_DegradesToASharedCapabilityAsync() {
    var step = new _countingStep("Rewrite", "maintainer", NonHolderBehavior.Skip);
    var runner = new StartupPipelineRunner([step]);

    var results = await runner.RunAsync(CancellationToken.None);

    await Assert.That(step.Executions).IsEqualTo(1)
      .Because("without an elector a duty degrades to a shared capability — every instance runs "
             + "the step, survivable only because the exclusive steps are individually idempotent "
             + "and separately guarded");
    await Assert.That(results[0].Outcome).IsEqualTo(StartupStepOutcome.Completed);
  }

  [Test]
  public async Task SharedStep_NeverConsultsTheElectorAsync() {
    var elector = new _elector(grantOnAttempt: 1);
    var step = new _countingStep("Reconcile", StartupCapabilities.EVERY_INSTANCE);
    var runner = new StartupPipelineRunner([step], dutyElector: elector);

    await runner.RunAsync(CancellationToken.None);

    await Assert.That(step.Executions).IsEqualTo(1);
    await Assert.That(elector.Attempts).IsEqualTo(0)
      .Because("a step requiring a capability every instance holds has no election to run");
  }

  [Test]
  public async Task DutyStep_WhoseBodyThrows_StillReleasesTheGrantAsync() {
    var elector = new _elector(grantOnAttempt: 1);
    var step = new _throwingStep();
    var runner = new StartupPipelineRunner([step], dutyElector: elector);

    var results = await runner.RunAsync(CancellationToken.None);

    await Assert.That(results[0].Outcome).IsEqualTo(StartupStepOutcome.Failed);
    await Assert.That(elector.Granted!.Released).IsTrue()
      .Because("a failed holder must not strand the duty — release rides the same path as success");
  }

  private sealed class _throwingStep : IStartupStep {
    public StartupStepDescriptor Descriptor { get; } = new() {
      Name = "Broken",
      RequiredCapability = "maintainer",
    };
    public ValueTask<StartupStepReport> ExecuteAsync(CancellationToken cancellationToken) =>
      throw new InvalidOperationException("boom");
  }

  private sealed class _neverGrantingElector : IDutyElector {
    public Task<DutyAttempt> TryAcquireAsync(string duty, CancellationToken cancellationToken)
      => Task.FromResult(DutyAttempt.Lost(DutyRefusal.Unavailable,
        "no coordination connection is configured"));   // standing — retrying can never succeed
  }

  private sealed class _awaitDutyStep : IStartupStep {
    public StartupStepDescriptor Descriptor { get; } = new() {
      Name = "AwaitDuty",
      RequiredCapability = "never-grantable",
      NonHolderBehavior = NonHolderBehavior.Await,   // the DEFAULT behaviour
    };
    public ValueTask<StartupStepReport> ExecuteAsync(CancellationToken cancellationToken)
      => new(new StartupStepReport(StartupStepOutcome.Completed));
  }

  /// <summary>
  /// Issue #494: an Await step whose duty can never be granted must FAIL, loudly and boundedly —
  /// not retry forever with a once-a-second Warning as the only evidence. Boot failing loudly
  /// beats boot hanging silently.
  /// </summary>
  [Test]
  [Timeout(30000)]
  public async Task AwaitDutyStep_WhoseDutyIsNeverGrantable_FailsBoundedlyInsteadOfHangingAsync(CancellationToken cancellationToken) {
    var runner = new StartupPipelineRunner([new _awaitDutyStep()], dutyElector: new _neverGrantingElector());

    var run = runner.RunAsync(cancellationToken);
    var winner = await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(10), cancellationToken));

    await Assert.That(ReferenceEquals(winner, run)).IsTrue()
      .Because("issue #494: the run must conclude — a duty the elector reports as unacquirable "
             + "must not spin the boot forever");
    var result = (await run).Single();
    await Assert.That(result.Outcome).IsEqualTo(StartupStepOutcome.Failed);
    await Assert.That(result.Reason).Contains("never-grantable")
      .Because("the failure must NAME the duty — a five-minute diagnosis instead of an open-ended one");
  }

  private sealed class _contendedForeverElector : IDutyElector {
    public Task<DutyAttempt> TryAcquireAsync(string duty, CancellationToken cancellationToken)
      => Task.FromResult(DutyAttempt.Lost(DutyRefusal.Contended, "held by a very slow peer"));
  }

  private sealed class _waitRecordingObserver : IStartupStepObserver {
    private readonly List<StartupStepWaitContext> _waits = [];
    private readonly Lock _lock = new();
    public IReadOnlyList<StartupStepWaitContext> Waits {
      get {
        lock (_lock) {
          return [.. _waits];
        }
      }
    }
    public ValueTask OnStepStartingAsync(StartupStepContext context, CancellationToken ct) => default;
    public ValueTask OnStepCompletedAsync(StartupStepResult result, CancellationToken ct) => default;
    public ValueTask OnPipelineCompletedAsync(StartupSummary summary, CancellationToken ct) => default;
    public ValueTask OnStepWaitingAsync(StartupStepWaitContext context, CancellationToken ct) {
      lock (_lock) {
        _waits.Add(context);
      }
      return default;
    }
  }

  /// <summary>
  /// Issue #493/#494's diagnosability half: a LEGITIMATE wait (contended duty, Await) must narrate
  /// itself through the observer seam on a backoff — a hang with no output gives a consumer
  /// nothing to diagnose.
  /// </summary>
  [Test]
  [Timeout(30000)]
  public async Task AwaitDutyStep_WhileContended_NarratesTheWaitThroughObserversAsync(CancellationToken cancellationToken) {
    var observer = new _waitRecordingObserver();
    var runner = new StartupPipelineRunner(
      [new _awaitDutyStep()], [observer], new _contendedForeverElector()) {
      DutyRetryInterval = TimeSpan.FromMilliseconds(15),
    };
    using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

    var run = runner.RunAsync(cts.Token);
    while (observer.Waits.Count < 2) {
      cancellationToken.ThrowIfCancellationRequested();
      await Task.Delay(15, cancellationToken);
    }
    await cts.CancelAsync();
    try { await run; } catch (OperationCanceledException) { /* the wait was legitimately unbounded */ }

    var waits = observer.Waits;
    await Assert.That(waits[0].Duty).IsEqualTo("never-grantable");
    await Assert.That(waits[0].LastRefusalDetail).IsEqualTo("held by a very slow peer")
      .Because("the narration must carry the elector's own words — that detail is the diagnosis");
    await Assert.That(waits[1].Waited > waits[0].Waited).IsTrue()
      .Because("each narration reports cumulative wait, on a backoff — never a per-tick drumbeat");
  }
}
