using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Startup;

namespace Whizbang.Core.Tests.Startup;

/// <summary>
/// A duty-bound startup step run as owed work (#966, requirement 7): deferred while this
/// instance's own pipeline is still running, done when the step completes or finds nothing to do,
/// not done (and so still owed) when it fails.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Startup/StartupStepDutyWork.cs</code-under-test>
[Category("Startup")]
public class StartupStepDutyWorkTests {

  private sealed class Step(StartupStepReport report, bool enabled = true) : IStartupStep {
    public int Runs { get; private set; }
    public StartupStepDescriptor Descriptor { get; } = new() {
      Name = "Rewrite",
      RequiredCapability = StartupDuties.MAINTAINER,
      NonHolderBehavior = NonHolderBehavior.Skip,
      Enabled = enabled,
    };
    public ValueTask<StartupStepReport> ExecuteAsync(CancellationToken cancellationToken) {
      Runs++;
      return ValueTask.FromResult(report);
    }
  }

  private sealed class Grant : IDutyGrant {
    public string Duty => StartupDuties.MAINTAINER;
    public DateTimeOffset AcquiredAt => DateTimeOffset.UnixEpoch;
    public long? Epoch => 3;
    public Task<bool> VerifyStillHeldAsync(CancellationToken cancellationToken) => Task.FromResult(true);
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
  }

  private static async Task<StartupPipelineState> _stateAfterRunAsync(bool complete) {
    var state = new StartupPipelineState();
    if (complete) {
      await state.OnPipelineCompletedAsync(new StartupSummary([]), CancellationToken.None);
    }
    return state;
  }

  [Test]
  public async Task RoleAndKey_AreTheStepsDutyAndNameAsync() {
    var work = new StartupStepDutyWork(new Step(new(StartupStepOutcome.Completed)), null);

    await Assert.That(work.Role).IsEqualTo(StartupDuties.MAINTAINER);
    await Assert.That(work.WorkKey).IsEqualTo("Rewrite");
  }

  [Test]
  [Arguments(StartupStepOutcome.Completed)]
  [Arguments(StartupStepOutcome.Skipped)]
  public async Task AStepThatCompletesOrFindsNothingToDo_IsDoneAsync(StartupStepOutcome outcome) {
    var step = new Step(new(outcome, "nothing owed"));

    var result = await new StartupStepDutyWork(step, await _stateAfterRunAsync(complete: true)).RunAsync(new Grant(), CancellationToken.None);

    await Assert.That(result.Status).IsEqualTo(DutyWorkStatus.Done);
    await Assert.That(step.Runs).IsEqualTo(1);
  }

  [Test]
  public async Task AStepThatFails_IsNotDone_WithItsReasonAsync() {
    var result = await new StartupStepDutyWork(new Step(new(StartupStepOutcome.Failed, "table locked")), null)
      .RunAsync(new Grant(), CancellationToken.None);
    var noReason = await new StartupStepDutyWork(new Step(new(StartupStepOutcome.Failed)), null)
      .RunAsync(new Grant(), CancellationToken.None);

    await Assert.That(result).IsEqualTo(DutyWorkResult.NotDone("table locked"));
    await Assert.That(noReason.Detail).IsEqualTo("the step failed");
  }

  [Test]
  public async Task WhileThisInstancesPipelineIsRunning_TheStepIsDeferred_NotRunAsync() {
    var step = new Step(new(StartupStepOutcome.Completed));

    var result = await new StartupStepDutyWork(step, await _stateAfterRunAsync(complete: false)).RunAsync(new Grant(), CancellationToken.None);

    await Assert.That(result.Status).IsEqualTo(DutyWorkStatus.Deferred)
      .Because("the local run may be executing the same step, and its dependencies are not met yet");
    await Assert.That(step.Runs).IsEqualTo(0);
  }

  [Test]
  public async Task ADisabledStep_OwesNothing_AndDoesNotRunAsync() {
    var step = new Step(new(StartupStepOutcome.Completed), enabled: false);

    var result = await new StartupStepDutyWork(step, null).RunAsync(new Grant(), CancellationToken.None);

    await Assert.That(result.Status).IsEqualTo(DutyWorkStatus.Done);
    await Assert.That(step.Runs).IsEqualTo(0);
  }

  [Test]
  public async Task Constructor_RefusesANullStepAsync() {
    await Assert.That(() => new StartupStepDutyWork(null!, null)).Throws<ArgumentNullException>();
  }
}
