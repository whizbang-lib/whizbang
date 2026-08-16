using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Startup;

namespace Whizbang.Core.Tests.Startup;

/// <summary>
/// The runner executes what the resolver ordered, and records what each step actually did. The
/// outcome is the load-bearing part: it is what makes "this step found nothing to do" distinguishable
/// from "this step could not run", which today it is not — rewind repair skipped by a cold-database
/// catch-all reports exactly what rewind repair that found nothing reports.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Startup/StartupPipelineRunner.cs</code-under-test>
[Category("Startup")]
public class StartupPipelineRunnerTests {

  private sealed class _recordingStep(
      string name, List<string> log, string[]? dependsOn = null,
      StartupStepOutcome outcome = StartupStepOutcome.Completed,
      string? reason = null, Exception? throws = null, bool enabled = true) : IStartupStep {

    public StartupStepDescriptor Descriptor { get; } = new() {
      Name = name,
      DependsOn = dependsOn ?? [],
      Enabled = enabled,
    };

    public int Runs { get; private set; }

    public ValueTask<StartupStepReport> ExecuteAsync(CancellationToken cancellationToken) {
      Runs++;
      log.Add(name);
      if (throws is not null) {
        throw throws;
      }
      return ValueTask.FromResult(new StartupStepReport(outcome, reason));
    }
  }

  // ── ordering and execution ──────────────────────────────────────────────

  [Test]
  public async Task RunAsync_ExecutesInResolvedOrderNotRegistrationOrderAsync() {
    var log = new List<string>();
    var runner = new StartupPipelineRunner([
      new _recordingStep("Ready", log, ["Migrate"]),
      new _recordingStep("Migrate", log),
    ]);

    await runner.RunAsync(CancellationToken.None);

    await Assert.That(string.Join(" → ", log)).IsEqualTo("Migrate → Ready")
      .Because("the runner executes the RESOLVED sequence, not the order steps were registered in");
  }

  [Test]
  public async Task RunAsync_ReportsEveryStepsOutcomeAsync() {
    var log = new List<string>();
    var runner = new StartupPipelineRunner([
      new _recordingStep("Migrate", log),
      new _recordingStep("Repair", log, ["Migrate"], StartupStepOutcome.Skipped, "nothing to repair"),
    ]);

    var results = await runner.RunAsync(CancellationToken.None);

    await Assert.That(results.Count).IsEqualTo(2);
    await Assert.That(results[0].Outcome).IsEqualTo(StartupStepOutcome.Completed);
    await Assert.That(results[1].Outcome).IsEqualTo(StartupStepOutcome.Skipped);
    await Assert.That(results[1].Reason).IsEqualTo("nothing to repair");
  }

  // A skipped step and a completed one must not serialize identically — that ambiguity is the whole
  // reason the outcome field exists.
  [Test]
  public async Task RunAsync_SkippedAndCompleted_AreDistinguishableAsync() {
    var log = new List<string>();
    var runner = new StartupPipelineRunner([
      new _recordingStep("Completed", log),
      new _recordingStep("Skipped", log, null, StartupStepOutcome.Skipped, "no origins known yet"),
    ]);

    var results = await runner.RunAsync(CancellationToken.None);
    var completed = results.Single(r => r.Name == "Completed");
    var skipped = results.Single(r => r.Name == "Skipped");

    await Assert.That(completed.Outcome).IsNotEqualTo(skipped.Outcome);
    await Assert.That(skipped.Reason).IsEqualTo("no origins known yet");
  }

  [Test]
  public async Task RunAsync_RecordsDurationForEachStepAsync() {
    var log = new List<string>();
    var runner = new StartupPipelineRunner([new _recordingStep("Migrate", log)]);

    var results = await runner.RunAsync(CancellationToken.None);

    await Assert.That(results[0].Duration).IsGreaterThanOrEqualTo(TimeSpan.Zero);
  }

  [Test]
  public async Task RunAsync_OmitsDisabledStepsAsync() {
    var log = new List<string>();
    var disabled = new _recordingStep("Disabled", log, null, enabled: false);
    var runner = new StartupPipelineRunner([new _recordingStep("Migrate", log), disabled]);

    var results = await runner.RunAsync(CancellationToken.None);

    await Assert.That(disabled.Runs).IsEqualTo(0);
    await Assert.That(results.Any(r => r.Name == "Disabled")).IsFalse();
  }

  // ── failure ─────────────────────────────────────────────────────────────

  // A step that throws is reported as Failed rather than taking the whole pipeline down with an
  // unhandled exception — the report is how anything downstream learns what happened.
  [Test]
  public async Task RunAsync_WhenAStepThrows_ReportsFailedWithTheReasonAsync() {
    var log = new List<string>();
    var runner = new StartupPipelineRunner([
      new _recordingStep("Migrate", log, null, throws: new InvalidOperationException("schema unreachable")),
    ]);

    var results = await runner.RunAsync(CancellationToken.None);

    await Assert.That(results[0].Outcome).IsEqualTo(StartupStepOutcome.Failed);
    await Assert.That(results[0].Reason).Contains("schema unreachable");
  }

  // ── re-entrancy ─────────────────────────────────────────────────────────

  // Revival from standby re-enters the pipeline rather than running a second, separate one. Nothing
  // re-enters it yet, but a runner that quietly refuses a second run would have to be rebuilt to
  // allow it later, so it is built that way now.
  [Test]
  public async Task RunAsync_RunTwice_ExecutesEveryStepAgainAsync() {
    var log = new List<string>();
    var migrate = new _recordingStep("Migrate", log);
    var runner = new StartupPipelineRunner([migrate]);

    await runner.RunAsync(CancellationToken.None);
    var second = await runner.RunAsync(CancellationToken.None);

    await Assert.That(migrate.Runs).IsEqualTo(2)
      .Because("re-entering the pipeline is how an instance revives from standby");
    await Assert.That(second.Count).IsEqualTo(1);
  }

  [Test]
  public async Task RunAsync_RunTwice_ReportsOnlyTheLatestRunAsync() {
    var log = new List<string>();
    var runner = new StartupPipelineRunner([new _recordingStep("Migrate", log)]);

    await runner.RunAsync(CancellationToken.None);
    var second = await runner.RunAsync(CancellationToken.None);

    await Assert.That(second.Count).IsEqualTo(1)
      .Because("results accumulating across runs would make the second run's report unreadable");
  }

  // ── edges ───────────────────────────────────────────────────────────────

  [Test]
  public async Task RunAsync_WithNoSteps_ReturnsEmptyAsync() {
    var runner = new StartupPipelineRunner([]);
    await Assert.That(await runner.RunAsync(CancellationToken.None)).IsEmpty();
  }

  [Test]
  public async Task Constructor_WithNullSteps_ThrowsAsync() {
    await Assert.That(() => new StartupPipelineRunner(null!)).Throws<ArgumentNullException>();
  }
}
