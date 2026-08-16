using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Startup;

namespace Whizbang.Core.Tests.Startup;

/// <summary>
/// Increment 9: <c>Assess</c> decides where this instance stands before anything changes, on
/// EVERY instance — the ones that will never win the migrator duty are exactly the ones at risk
/// of being obsolete. A StandDown verdict reports as a failed blocking step: the pipeline's
/// fail-closed posture IS not-ready-while-alive, which is precisely what tells an orchestrator to
/// replace the instance and a load balancer to stop sending it traffic.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Startup/AssessStartupStep.cs</code-under-test>
[Category("Startup")]
public class AssessStartupStepTests {

  private sealed class _fixedAssessor(StartupAssessment assessment) : IStartupAssessor {
    public Task<StartupAssessment> AssessAsync(CancellationToken cancellationToken) =>
      Task.FromResult(assessment);
  }

  [Test]
  public async Task Descriptor_RunsOnEveryInstance_BeforeTheMigrationBarrierAsync() {
    var step = new AssessStartupStep();

    await Assert.That(step.Descriptor.Name).IsEqualTo(FrameworkStartupSteps.ASSESS);
    await Assert.That(step.Descriptor.RequiredCapability).IsEqualTo(StartupCapabilities.EVERY_INSTANCE)
      .Because("assessment is universal — an instance that will never migrate still needs to "
             + "know whether it is obsolete");
    await Assert.That(step.Descriptor.Blocking).IsTrue();

    var migrate = new MigrateStartupStep(new Whizbang.Core.Workers.SchemaReadyGate());
    await Assert.That(migrate.Descriptor.DependsOn).Contains(FrameworkStartupSteps.ASSESS)
      .Because("an instance cleared only to serve — or standing down — must know it BEFORE the "
             + "migration barrier");
  }

  [Test]
  public async Task WithoutAnAssessor_SkipsWithTheStatedReasonAsync() {
    var report = await new AssessStartupStep().ExecuteAsync(CancellationToken.None);

    await Assert.That(report.Outcome).IsEqualTo(StartupStepOutcome.Skipped);
    await Assert.That(report.Reason).Contains("no assessor registered");
  }

  [Test]
  [Arguments(StartupVerdict.Serve)]
  [Arguments(StartupVerdict.Migrate)]
  public async Task ServeAndMigrateVerdicts_CompleteWithTheReasonAsync(StartupVerdict verdict) {
    var step = new AssessStartupStep(new _fixedAssessor(new StartupAssessment(verdict, "clear to proceed")));

    var report = await step.ExecuteAsync(CancellationToken.None);

    await Assert.That(report.Outcome).IsEqualTo(StartupStepOutcome.Completed);
    await Assert.That(report.Reason).IsEqualTo("clear to proceed");
  }

  [Test]
  public async Task StandDownVerdict_FailsTheBlockingStep_WhichIsNotReadyWhileAliveAsync() {
    var step = new AssessStartupStep(new _fixedAssessor(
      new StartupAssessment(StartupVerdict.StandDown, "the ledger records a newer version")));

    var report = await step.ExecuteAsync(CancellationToken.None);

    await Assert.That(report.Outcome).IsEqualTo(StartupStepOutcome.Failed)
      .Because("fail-closed readiness IS the stand-down posture: the composite never signals, "
             + "health reports Faulted with the step's name, and the instance stays alive");
    await Assert.That(report.Reason).Contains("newer version");
  }

  [Test]
  public async Task StandDown_ThroughTheRealPipeline_KeepsReadinessPendingForeverAsync() {
    var state = new StartupPipelineState();
    var assess = new AssessStartupStep(new _fixedAssessor(
      new StartupAssessment(StartupVerdict.StandDown, "newer version recorded")));
    var runner = new StartupPipelineRunner([assess], [state]);

    await runner.RunAsync(CancellationToken.None);

    await Assert.That(state.IsComplete).IsTrue();
    await Assert.That(state.IsReady).IsFalse()
      .Because("a standing-down instance must never report ready — releasing capabilities and "
             + "holding the data plane compose on top of exactly this signal");
  }
}
