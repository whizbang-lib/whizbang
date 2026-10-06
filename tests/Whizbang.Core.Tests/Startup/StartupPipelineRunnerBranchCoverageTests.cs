// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Startup;

namespace Whizbang.Core.Tests.Startup;

/// <summary>
/// Branch coverage for the exclusive-step wait narration in <see cref="StartupPipelineRunner"/>:
/// the steady-state "every 60 attempts" arm of the backoff, and a narration whose most recent
/// attempt was a transient elector failure rather than a clean refusal.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Startup/StartupPipelineRunner.cs</code-under-test>
[Category("Startup")]
public class StartupPipelineRunnerBranchCoverageTests {

  private const string DUTY = "scripted-duty";
  private const string TRANSIENT_MESSAGE = "coordination read timed out";

  private sealed class Grant : IDutyGrant {
    public string Duty => DUTY;
    public DateTimeOffset AcquiredAt { get; } = DateTimeOffset.UtcNow;
    public bool Released { get; private set; }
    public Task<bool> VerifyStillHeldAsync(CancellationToken cancellationToken) => Task.FromResult(!Released);
    public ValueTask DisposeAsync() {
      Released = true;
      return ValueTask.CompletedTask;
    }
  }

  /// <summary>
  /// Throws a transient failure on calls 3 and 30, refuses as contended on every other call
  /// before <c>grantOnCall</c>, and grants from then on.
  /// </summary>
  private sealed class ScriptedElector(int grantOnCall) : IDutyElector {
    private int _calls;
    public int Calls => Volatile.Read(ref _calls);
    public Grant? Granted { get; private set; }
    public Task<DutyAttempt> TryAcquireAsync(string duty, CancellationToken cancellationToken) {
      var call = Interlocked.Increment(ref _calls);
      if (call is 3 or 30) {
        throw new InvalidOperationException(TRANSIENT_MESSAGE);
      }
      if (call < grantOnCall) {
        return Task.FromResult(DutyAttempt.Lost(DutyRefusal.Contended, "held by a peer"));
      }
      Granted = new Grant();
      return Task.FromResult(DutyAttempt.Granted(Granted));
    }
  }

  private sealed class AwaitDutyStep : IStartupStep {
    private int _executions;
    public int Executions => Volatile.Read(ref _executions);
    public StartupStepDescriptor Descriptor { get; } = new() {
      Name = "AwaitScripted",
      RequiredCapability = DUTY,
      NonHolderBehavior = NonHolderBehavior.Await,
    };
    public ValueTask<StartupStepReport> ExecuteAsync(CancellationToken cancellationToken) {
      Interlocked.Increment(ref _executions);
      return new(new StartupStepReport(StartupStepOutcome.Completed));
    }
  }

  private sealed class WaitRecordingObserver : IStartupStepObserver {
    private readonly List<StartupStepWaitContext> _waits = [];
    private readonly Lock _lock = new();
    public IReadOnlyList<StartupStepWaitContext> Waits {
      get {
        lock (_lock) {
          return [.. _waits];
        }
      }
    }
    public ValueTask OnStepStartingAsync(StartupStepContext context, CancellationToken cancellationToken) => default;
    public ValueTask OnStepCompletedAsync(StartupStepResult result, CancellationToken cancellationToken) => default;
    public ValueTask OnPipelineCompletedAsync(StartupSummary summary, CancellationToken cancellationToken) => default;
    public ValueTask OnStepWaitingAsync(StartupStepWaitContext context, CancellationToken cancellationToken) {
      lock (_lock) {
        _waits.Add(context);
      }
      return default;
    }
  }

  /// <summary>
  /// A wait that outlives the first two narrations (attempts 3 and 10) is narrated again at 30
  /// and then only every 60 attempts after that, so 39 failed attempts produce exactly three
  /// narrations. When the attempt a narration reports was a transient elector failure, the
  /// narration carries that failure's type and message, because there is no refusal detail to
  /// report and the failure is the diagnosis.
  /// </summary>
  [Test]
  [Timeout(30000)]
  public async Task AwaitDutyStep_LongWaitWithTransientFailures_NarratesOnTheBackoffWithTheFailureAsDetailAsync(
      CancellationToken cancellationToken) {
    var elector = new ScriptedElector(grantOnCall: 40);
    var step = new AwaitDutyStep();
    var observer = new WaitRecordingObserver();
    var runner = new StartupPipelineRunner([step], [observer], elector) {
      // Zero keeps the retry loop free of wall-clock time; the backoff counts attempts, not time.
      DutyRetryInterval = TimeSpan.Zero,
    };

    var results = await runner.RunAsync(cancellationToken);

    await Assert.That(results[0].Outcome).IsEqualTo(StartupStepOutcome.Completed)
      .Because("isolated transient failures between clean refusals reset the failure budget, and the step still runs once granted");
    await Assert.That(step.Executions).IsEqualTo(1);
    await Assert.That(elector.Calls).IsEqualTo(40);
    await Assert.That(elector.Granted!.Released).IsTrue();

    var waits = observer.Waits;
    await Assert.That(waits.Count).IsEqualTo(3)
      .Because("narrations fall at attempts 3, 10 and 30; after 30 the next is 60 attempts later, at 90, "
             + "so 39 failed attempts must not produce a fourth");
    await Assert.That(waits[0].LastRefusalDetail).IsEqualTo($"InvalidOperationException: {TRANSIENT_MESSAGE}")
      .Because("attempt 3 failed transiently, so the narration reports the failure instead of a refusal detail");
    await Assert.That(waits[1].LastRefusalDetail).IsEqualTo("held by a peer")
      .Because("attempt 10 was a clean refusal, so the narration carries the elector's own words");
    await Assert.That(waits[2].LastRefusalDetail).IsEqualTo($"InvalidOperationException: {TRANSIENT_MESSAGE}")
      .Because("attempt 30 failed transiently and is the narration reached through the steady-state backoff arm");
    await Assert.That(waits.All(w => w.Duty == DUTY)).IsTrue();
  }
}
