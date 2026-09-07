using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Startup;

namespace Whizbang.Core.Tests.Startup;

/// <summary>
/// Covers <see cref="IStartupStepObserver.OnStepWaitingAsync"/>'s default no-op implementation.
/// Every observer used anywhere else in the suite overrides it (or is never put through a wait
/// long enough to be narrated); this exercises an observer that implements only the REQUIRED
/// members and relies on the interface's default for the backoff notification, run through a
/// scenario that actually reaches the narration point (three failed duty attempts).
/// </summary>
public class IStartupStepObserverCoverageTests {

  /// <summary>Implements only the members without a default — OnRunStartingAsync and
  /// OnStepWaitingAsync fall through to the interface's no-op defaults.</summary>
  private sealed class MinimalObserver : IStartupStepObserver {
    public List<string> Completed { get; } = [];
    public ValueTask OnStepStartingAsync(StartupStepContext context, CancellationToken cancellationToken) =>
      ValueTask.CompletedTask;
    public ValueTask OnStepCompletedAsync(StartupStepResult result, CancellationToken cancellationToken) {
      Completed.Add(result.Name);
      return ValueTask.CompletedTask;
    }
    public ValueTask OnPipelineCompletedAsync(StartupSummary summary, CancellationToken cancellationToken) =>
      ValueTask.CompletedTask;
  }

  private sealed class ContendedThenGrantedElector : IDutyElector {
    private int _calls;
    public int Calls => Volatile.Read(ref _calls);

    public Task<DutyAttempt> TryAcquireAsync(string duty, CancellationToken cancellationToken) {
      var call = Interlocked.Increment(ref _calls);
      return Task.FromResult(call <= 3
        ? DutyAttempt.Lost(DutyRefusal.Contended, "held by another instance")
        : DutyAttempt.Granted(new FakeGrant(duty)));
    }
  }

  private sealed class FakeGrant(string duty) : IDutyGrant {
    public string Duty { get; } = duty;
    public DateTimeOffset AcquiredAt { get; } = DateTimeOffset.UnixEpoch;
    public Task<bool> VerifyStillHeldAsync(CancellationToken cancellationToken) => Task.FromResult(true);
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
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
  /// The backoff notification is diagnostic-only, narrated to whoever is listening. If it were
  /// treated as required — the runner assuming every observer implements it, or a call against
  /// the interface default throwing — an operator wiring only the mandatory observer surface (the
  /// documented minimum) would find their boot hang or crash on the very first long duty wait,
  /// instead of simply not being narrated.
  /// </summary>
  [Test]
  [Timeout(30000)]
  public async Task StepWaitingNotification_ObserverWithoutOverride_DoesNotStallTheDutyWaitAsync(CancellationToken testToken) {
    var elector = new ContendedThenGrantedElector();
    var observer = new MinimalObserver();
    var runner = new StartupPipelineRunner([new ExclusiveStep()], [observer], elector) {
      DutyRetryInterval = TimeSpan.FromMilliseconds(1),
    };

    var results = await runner.RunAsync(CancellationToken.None);

    await Assert.That(elector.Calls).IsGreaterThanOrEqualTo(4)
      .Because("the backoff narration fires once attempts reach 3 — reaching a 4th (winning) "
             + "attempt proves the wait loop kept running through the notification instead of "
             + "stalling on an observer that only implements the interface's required members");
    await Assert.That(results.Single().Outcome).IsEqualTo(StartupStepOutcome.Completed)
      .Because("a diagnostic-only notification path must never be load-bearing for whether a step "
             + "reports success once it actually won the duty");
    await Assert.That(observer.Completed).Contains("exclusive-step");
  }
}
