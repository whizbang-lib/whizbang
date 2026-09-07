using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Startup;

namespace Whizbang.Core.Tests.Startup;

/// <summary>
/// Covers <see cref="StartupPipelineRunner"/>'s duty-acquisition cancellation re-throw: when the
/// elector's <c>TryAcquireAsync</c> observes cancellation on the SAME token the caller is shutting
/// down with, that must propagate immediately rather than being folded into the generic
/// "transient elector failure" branch beneath it — no existing test in
/// <c>StartupPipelineRunnerTests</c> cancels the caller's own token while the elector call is in
/// flight.
/// </summary>
public class StartupPipelineRunnerCoverageTests {

  /// <summary>Blocks inside TryAcquireAsync until the CALLER's own cancellationToken cancels.</summary>
  private sealed class CancelDuringAcquireElector : IDutyElector {
    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task<DutyAttempt> TryAcquireAsync(string duty, CancellationToken cancellationToken) {
      Started.TrySetResult();
      await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); // throws once the caller's token cancels
      throw new InvalidOperationException("unreachable — the delay above always throws first");
    }
  }

  private sealed class ExclusiveStep : IStartupStep {
    public StartupStepDescriptor Descriptor { get; } = new() {
      Name = "exclusive-step",
      RequiredCapability = "coverage-duty",
      NonHolderBehavior = NonHolderBehavior.Await,
    };

    public ValueTask<StartupStepReport> ExecuteAsync(CancellationToken cancellationToken) =>
      new(new StartupStepReport(StartupStepOutcome.Completed));
  }

  /// <summary>
  /// If this re-throw were instead folded into the generic "elector failed, count it as
  /// transient" branch, a boot racing shutdown mid-acquisition would count the cancellation
  /// toward <c>MaxTransientDutyFailures</c> (or just retry) instead of unwinding immediately the
  /// way issue #494 requires — turning an ordinary shutdown into either a slow, noisy retry storm
  /// or, worse, a step that reports Failed instead of the run simply stopping.
  /// </summary>
  [Test]
  [Timeout(30000)]
  public async Task DutyElectorCanceledDuringAcquire_RethrowsRatherThanBeingTreatedAsTransientAsync(CancellationToken testToken) {
    var elector = new CancelDuringAcquireElector();
    var runner = new StartupPipelineRunner([new ExclusiveStep()], dutyElector: elector);

    using var cts = CancellationTokenSource.CreateLinkedTokenSource(testToken);
    var run = runner.RunAsync(cts.Token);

    await elector.Started.Task.WaitAsync(TimeSpan.FromSeconds(10), testToken);
    await cts.CancelAsync();

    await Assert.That(async () => await run)
      .Throws<OperationCanceledException>()
      .Because("a cancellation observed while ASKING for the duty is a shutdown-in-progress "
             + "signal, not a transient elector failure, and must unwind the run immediately");
  }
}
