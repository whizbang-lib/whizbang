using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Startup;

namespace Whizbang.Core.Tests.Startup;

/// <summary>
/// Increment 2 of the startup pipeline: the two seams consumers get. <see cref="IStartupStepObserver"/>
/// watches transitions; <see cref="IStartupPipelineState"/> answers questions at any moment. Observers
/// are advisory — one that throws is recorded, never allowed to fail a boot — and the state is what
/// lets a consumer's own service say "after Migrate" instead of guessing at registration order.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Startup/IStartupStepObserver.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Startup/StartupPipelineState.cs</code-under-test>
[Category("Startup")]
public class StartupPipelineHooksTests {

  private sealed class _step(string name, List<string>? log = null,
      StartupStepOutcome outcome = StartupStepOutcome.Completed,
      string? reason = null, string[]? dependsOn = null) : IStartupStep {
    public StartupStepDescriptor Descriptor { get; } = new() { Name = name, DependsOn = dependsOn ?? [] };
    public ValueTask<StartupStepReport> ExecuteAsync(CancellationToken cancellationToken) {
      log?.Add(name);
      return ValueTask.FromResult(new StartupStepReport(outcome, reason));
    }
  }

  private sealed class _recordingObserver : IStartupStepObserver {
    public List<string> Events { get; } = [];
    public ValueTask OnStepStartingAsync(StartupStepContext context, CancellationToken ct) {
      Events.Add($"starting:{context.Descriptor.Name}");
      return ValueTask.CompletedTask;
    }
    public ValueTask OnStepCompletedAsync(StartupStepResult result, CancellationToken ct) {
      Events.Add($"completed:{result.Name}:{result.Outcome}");
      return ValueTask.CompletedTask;
    }
    public ValueTask OnPipelineCompletedAsync(StartupSummary summary, CancellationToken ct) {
      Events.Add($"pipeline:{summary.Results.Count}");
      return ValueTask.CompletedTask;
    }
  }

  private sealed class _throwingObserver : IStartupStepObserver {
    public ValueTask OnStepStartingAsync(StartupStepContext context, CancellationToken ct)
      => throw new InvalidOperationException("diagnostic exploded");
    public ValueTask OnStepCompletedAsync(StartupStepResult result, CancellationToken ct)
      => throw new InvalidOperationException("diagnostic exploded");
    public ValueTask OnPipelineCompletedAsync(StartupSummary summary, CancellationToken ct)
      => throw new InvalidOperationException("diagnostic exploded");
  }

  // ── observation ─────────────────────────────────────────────────────────

  [Test]
  public async Task RunAsync_NotifiesStartingAndCompletedForEachStepInOrderAsync() {
    var observer = new _recordingObserver();
    var runner = new StartupPipelineRunner(
      [new _step("Ready", dependsOn: ["Migrate"]), new _step("Migrate")],
      [observer]);

    await runner.RunAsync(CancellationToken.None);

    await Assert.That(string.Join(" ", observer.Events)).IsEqualTo(
      "starting:Migrate completed:Migrate:Completed starting:Ready completed:Ready:Completed pipeline:2")
      .Because("observers see each step bracketed, in the resolved order, then the pipeline summary");
  }

  [Test]
  public async Task RunAsync_ObserverSeesSkipOutcomeAndReasonAsync() {
    var observer = new _recordingObserver();
    var runner = new StartupPipelineRunner(
      [new _step("Repair", outcome: StartupStepOutcome.Skipped, reason: "nothing to repair")],
      [observer]);

    var results = await runner.RunAsync(CancellationToken.None);

    await Assert.That(observer.Events).Contains("completed:Repair:Skipped");
    await Assert.That(results[0].Reason).IsEqualTo("nothing to repair");
  }

  // The load-bearing safety property: a diagnostic must not be able to break a boot.
  [Test]
  public async Task RunAsync_ThrowingObserver_DoesNotFailTheStepOrThePipelineAsync() {
    var log = new List<string>();
    var runner = new StartupPipelineRunner(
      [new _step("Migrate", log)],
      [new _throwingObserver()]);

    var results = await runner.RunAsync(CancellationToken.None);

    await Assert.That(log).Contains("Migrate")
      .Because("the step must run despite the observer throwing on OnStepStarting");
    await Assert.That(results[0].Outcome).IsEqualTo(StartupStepOutcome.Completed)
      .Because("an observer failure is the observer's problem, never the step's");
  }

  [Test]
  public async Task RunAsync_ThrowingObserver_DoesNotStarveOtherObserversAsync() {
    var healthy = new _recordingObserver();
    var runner = new StartupPipelineRunner(
      [new _step("Migrate")],
      [new _throwingObserver(), healthy]);

    await runner.RunAsync(CancellationToken.None);

    await Assert.That(healthy.Events).Contains("starting:Migrate")
      .Because("one broken diagnostic must not silence the rest");
  }

  // ── interrogation ───────────────────────────────────────────────────────

  [Test]
  public async Task State_BeforeAnyRun_ReportsNotCompleteAndPendingAsync() {
    var state = new StartupPipelineState();

    await Assert.That(state.IsComplete).IsFalse();
    await Assert.That(state.StatusOf("Migrate")).IsEqualTo(StartupStepStatus.Pending);
    await Assert.That(state.Completed).IsEmpty();
  }

  [Test]
  public async Task State_AfterARun_ReportsPerStepStatusAndCompletionAsync() {
    var state = new StartupPipelineState();
    var runner = new StartupPipelineRunner(
      [new _step("Migrate"), new _step("Repair", outcome: StartupStepOutcome.Skipped, reason: "cold")],
      [state]);

    await runner.RunAsync(CancellationToken.None);

    await Assert.That(state.IsComplete).IsTrue();
    await Assert.That(state.StatusOf("Migrate")).IsEqualTo(StartupStepStatus.Completed);
    await Assert.That(state.StatusOf("Repair")).IsEqualTo(StartupStepStatus.Skipped);
    await Assert.That(state.Completed.Count).IsEqualTo(2);
  }

  [Test]
  public async Task State_WaitForAsync_ReleasesWhenTheStepCompletesAsync() {
    var state = new StartupPipelineState();
    var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

    var blocked = new _gatedStep("Migrate", gate.Task);
    var runner = new StartupPipelineRunner([blocked], [state]);

    var run = runner.RunAsync(CancellationToken.None);
    var waiter = state.WaitForAsync("Migrate", CancellationToken.None);

    await Assert.That(waiter.IsCompleted).IsFalse()
      .Because("the step has not finished, so a dependent must still be held");

    gate.SetResult();
    await waiter.WaitAsync(TimeSpan.FromSeconds(5));
    await run;

    await Assert.That(state.StatusOf("Migrate")).IsEqualTo(StartupStepStatus.Completed);
  }

  [Test]
  public async Task State_WaitForAsync_AfterTheStepAlreadyCompleted_ReturnsImmediatelyAsync() {
    var state = new StartupPipelineState();
    var runner = new StartupPipelineRunner([new _step("Migrate")], [state]);
    await runner.RunAsync(CancellationToken.None);

    var waiter = state.WaitForAsync("Migrate", CancellationToken.None);

    await Assert.That(waiter.IsCompletedSuccessfully).IsTrue()
      .Because("a late subscriber to an already-completed step must not wait forever");
  }

  // Revival from standby re-enters the pipeline; the state must describe the run in progress,
  // not accumulate history from the previous one.
  [Test]
  public async Task State_OnReentry_ResetsToTheNewRunAsync() {
    var state = new StartupPipelineState();
    var runner = new StartupPipelineRunner([new _step("Migrate")], [state]);

    await runner.RunAsync(CancellationToken.None);
    await Assert.That(state.IsComplete).IsTrue();

    var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var runner2 = new StartupPipelineRunner([new _gatedStep("Migrate", gate.Task)], [state]);
    var run2 = runner2.RunAsync(CancellationToken.None);

    // Poll briefly: the reset happens when the new run begins.
    var deadline = DateTime.UtcNow.AddSeconds(5);
    while (state.IsComplete && DateTime.UtcNow < deadline) {
      await Task.Delay(10);
    }
    await Assert.That(state.IsComplete).IsFalse()
      .Because("a re-entered pipeline is running again; reporting the OLD run as complete would tell "
             + "a reviving instance it is ready when it is not");

    gate.SetResult();
    await run2;
    await Assert.That(state.IsComplete).IsTrue();
  }

  private sealed class _gatedStep(string name, Task gate) : IStartupStep {
    public StartupStepDescriptor Descriptor { get; } = new() { Name = name };
    public async ValueTask<StartupStepReport> ExecuteAsync(CancellationToken cancellationToken) {
      await gate.WaitAsync(cancellationToken);
      return new StartupStepReport(StartupStepOutcome.Completed);
    }
  }
}
