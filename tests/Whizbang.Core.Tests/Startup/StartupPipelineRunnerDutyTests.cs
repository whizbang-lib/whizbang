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
    public Task<IDutyGrant?> TryAcquireAsync(string duty, CancellationToken cancellationToken) {
      var attempt = Interlocked.Increment(ref _attempts);
      if (attempt < grantOnAttempt) {
        return Task.FromResult<IDutyGrant?>(null);
      }
      Granted = new _grant(() => { });
      return Task.FromResult<IDutyGrant?>(Granted);
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
}
