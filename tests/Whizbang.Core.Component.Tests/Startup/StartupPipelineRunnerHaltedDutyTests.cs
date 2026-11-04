// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.RunControl;
using Whizbang.Core.Startup;

namespace Whizbang.Core.Tests.Startup;

/// <summary>
/// A duty refused because the instance's run permit is closed (<see cref="DutyRefusal.Halted"/>) is not permanent
/// (#2045): a resume reopens the permit and the next vote is answered as usual. An awaited duty step therefore waits for
/// the permit to reopen, on the permit's own signal, and votes again; the run's cancellation bounds the wait, and a
/// stopped permit fails the step, since a stop is final.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Startup/StartupPipelineRunner.cs</code-under-test>
[Category("Startup")]
[Category("RunControl")]
public class StartupPipelineRunnerHaltedDutyTests {
  private static readonly TimeSpan _deadline = TimeSpan.FromSeconds(30);

  private sealed class Grant : IDutyGrant {
    public string Duty => "migrator";
    public DateTimeOffset AcquiredAt { get; } = DateTimeOffset.UnixEpoch;
    public Task<bool> VerifyStillHeldAsync(CancellationToken cancellationToken) => Task.FromResult(true);
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
  }

  /// <summary>Refuses as halted while the permit is closed, and grants once it is open, as the role elector does.</summary>
  private sealed class PermitElector(WhizbangRunPermit permit) : IDutyElector, IObeysRunPermit {
    private readonly TaskCompletionSource _firstAttempt = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _attempts;
    public WhizbangRunPermit RunPermit => permit;
    public Task FirstAttempt => _firstAttempt.Task;
    public int Attempts => Volatile.Read(ref _attempts);
    public Task<DutyAttempt> TryAcquireAsync(string duty, CancellationToken cancellationToken) {
      Interlocked.Increment(ref _attempts);
      _firstAttempt.TrySetResult();
      return Task.FromResult(permit.IsRunning
        ? DutyAttempt.Granted(new Grant())
        : DutyAttempt.Lost(DutyRefusal.Halted, "the run permit is closed"));
    }
  }

  /// <summary>Refuses as halted a fixed number of times, with no permit to wait on.</summary>
  private sealed class PermitlessElector(int haltedAttempts) : IDutyElector {
    private int _attempts;
    public int Attempts => Volatile.Read(ref _attempts);
    public Task<DutyAttempt> TryAcquireAsync(string duty, CancellationToken cancellationToken) =>
      Task.FromResult(Interlocked.Increment(ref _attempts) <= haltedAttempts
        ? DutyAttempt.Lost(DutyRefusal.Halted, "the run permit is closed")
        : DutyAttempt.Granted(new Grant()));
  }

  private sealed class Step : IStartupStep {
    private int _executions;
    public int Executions => Volatile.Read(ref _executions);
    public StartupStepDescriptor Descriptor { get; } = new() {
      Name = "Migrate2", RequiredCapability = "migrator", NonHolderBehavior = NonHolderBehavior.Await,
    };
    public ValueTask<StartupStepReport> ExecuteAsync(CancellationToken cancellationToken) {
      Interlocked.Increment(ref _executions);
      return new(new StartupStepReport(StartupStepOutcome.Completed));
    }
  }

  private sealed class WaitingObserver : IStartupStepObserver {
    public List<string> Waits { get; } = [];
    public ValueTask OnStepStartingAsync(StartupStepContext context, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    public ValueTask OnStepCompletedAsync(StartupStepResult result, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    public ValueTask OnPipelineCompletedAsync(StartupSummary summary, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    public ValueTask OnStepWaitingAsync(StartupStepWaitContext context, CancellationToken cancellationToken) {
      Waits.Add(context.LastRefusalDetail ?? "");
      return ValueTask.CompletedTask;
    }
  }

  [Test]
  public async Task AnAwaitedStepRefusedAsHalted_WaitsForThePermitToReopen_ThenRunsAsync() {
    var permit = new WhizbangRunPermit();
    await permit.ChangeAsync(RunState.Paused, CancellationToken.None);
    var elector = new PermitElector(permit);
    var step = new Step();
    var observer = new WaitingObserver();
    var runner = new StartupPipelineRunner(steps: [step], observers: [observer], dutyElector: elector);

    var run = runner.RunAsync(CancellationToken.None);
    await elector.FirstAttempt.WaitAsync(_deadline);
    await Assert.That(run.IsCompleted).IsFalse().Because("a halted instance holds no duty until it resumes");

    await permit.ChangeAsync(RunState.Running, CancellationToken.None);
    var results = await run.WaitAsync(_deadline);

    await Assert.That(results[0].Outcome).IsEqualTo(StartupStepOutcome.Completed);
    await Assert.That(step.Executions).IsEqualTo(1);
    await Assert.That(elector.Attempts).IsEqualTo(2).Because("one refused vote, then one vote once the permit reopened");
    await Assert.That(observer.Waits).IsEquivalentTo(["the run permit is closed"])
      .Because("the wait is narrated once, naming why");
  }

  [Test]
  public async Task AnAwaitedStepRefusedAsHalted_WhenThePermitIsStopped_FailsAsync() {
    var permit = new WhizbangRunPermit();
    await permit.ChangeAsync(RunState.Paused, CancellationToken.None);
    var elector = new PermitElector(permit);
    var runner = new StartupPipelineRunner(steps: [new Step()], observers: [], dutyElector: elector);

    var run = runner.RunAsync(CancellationToken.None);
    await elector.FirstAttempt.WaitAsync(_deadline);
    await permit.ChangeAsync(RunState.Stopped, CancellationToken.None);
    var results = await run.WaitAsync(_deadline);

    await Assert.That(results[0].Outcome).IsEqualTo(StartupStepOutcome.Failed);
    await Assert.That(results[0].Reason).Contains("stopped");
  }

  [Test]
  public async Task AnAwaitedStepRefusedAsHalted_TheRunsCancellation_EndsTheWaitAsync() {
    var permit = new WhizbangRunPermit();
    await permit.ChangeAsync(RunState.Paused, CancellationToken.None);
    var elector = new PermitElector(permit);
    var runner = new StartupPipelineRunner(steps: [new Step()], observers: [], dutyElector: elector);
    using var cts = new CancellationTokenSource();

    var run = runner.RunAsync(cts.Token);
    await elector.FirstAttempt.WaitAsync(_deadline);
    await cts.CancelAsync();

    await Assert.That(async () => await run.WaitAsync(_deadline)).Throws<OperationCanceledException>();
  }

  [Test]
  public async Task AnAwaitedStepRefusedAsHalted_ByAnElectorWithNoPermit_IsRetriedLikeAContentionAsync() {
    var elector = new PermitlessElector(haltedAttempts: 2);
    var step = new Step();
    var runner = new StartupPipelineRunner(steps: [step], observers: [], dutyElector: elector) {
      DutyRetryInterval = TimeSpan.Zero,
    };

    var results = await runner.RunAsync(CancellationToken.None);

    await Assert.That(results[0].Outcome).IsEqualTo(StartupStepOutcome.Completed);
    await Assert.That(elector.Attempts).IsEqualTo(3);
  }
}
