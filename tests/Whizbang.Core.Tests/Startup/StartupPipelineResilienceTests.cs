using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Startup;

namespace Whizbang.Core.Tests.Startup;

/// <summary>
/// The startup pipeline must not be able to terminate the host.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="StartupPipelineWorker"/> is a <c>BackgroundService</c>, and .NET's default
/// <c>HostOptions.BackgroundServiceExceptionBehavior</c> is <c>StopHost</c>: an exception that
/// escapes <c>ExecuteAsync</c> stops the entire host. The runner already understood half of
/// this — it wraps each step's body precisely so that "an exception that unwinds the runner
/// destroys exactly that record" — but the duty-acquisition calls that BRACKET that protected
/// body were themselves unprotected, so a transient failure reaching the elector unwound
/// everything the guard existed to preserve.
/// </para>
/// <para>
/// The elector talks to a coordination primitive over a network. A read timeout there is an
/// ordinary transient condition that the surrounding loop already knows how to wait out — the
/// same condition every other worker in the framework treats as a retryable tick. Turning it
/// into host termination discards a healthy process and every piece of work in flight on it.
/// </para>
/// <para>
/// What made this pathological rather than merely wrong is how it FAILED: the host stops
/// gracefully, so the process exits ZERO and logs an orderly shutdown. To anything watching for
/// crashes — exit codes, restart reasons, OOM kills — a host destroyed this way is
/// indistinguishable from one asked politely to stop. It can recur indefinitely while every
/// health signal reports normal.
/// </para>
/// <para>
/// Fail-closed is preserved throughout, and is what makes non-fatal handling safe: a pipeline
/// that does not complete leaves the availability filter refusing writes. Staying up and
/// unready is strictly more informative than exiting zero.
/// </para>
/// </remarks>
/// <code-under-test>src/Whizbang.Core/Startup/StartupPipelineRunner.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Startup/StartupPipelineHosting.cs</code-under-test>
[Category("Startup")]
public class StartupPipelineResilienceTests {

  private sealed class _grant : IDutyGrant {
    public string Duty => "test-duty";
    public DateTimeOffset AcquiredAt { get; } = DateTimeOffset.UtcNow;
    public bool Released { get; private set; }
    public Task<bool> VerifyStillHeldAsync(CancellationToken cancellationToken) => Task.FromResult(!Released);
    public ValueTask DisposeAsync() {
      Released = true;
      return ValueTask.CompletedTask;
    }
  }

  private sealed class _step(string name, string capability, NonHolderBehavior nonHolder = NonHolderBehavior.Await)
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

  /// <summary>Throws a transient failure on the first N attempts, then grants — a blip, not an outage.</summary>
  private sealed class _throwsThenGrantsElector(int throwCount) : IDutyElector {
    private int _attempts;
    public int Attempts => Volatile.Read(ref _attempts);
    public Task<DutyAttempt> TryAcquireAsync(string duty, CancellationToken cancellationToken) {
      var attempt = Interlocked.Increment(ref _attempts);
      if (attempt <= throwCount) {
        throw new TimeoutException("Timeout during reading attempt");
      }
      return Task.FromResult(DutyAttempt.Granted(new _grant()));
    }
  }

  /// <summary>Throws forever — a standing outage, not a blip.</summary>
  private sealed class _alwaysThrowsElector : IDutyElector {
    private int _attempts;
    public int Attempts => Volatile.Read(ref _attempts);
    public Task<DutyAttempt> TryAcquireAsync(string duty, CancellationToken cancellationToken) {
      Interlocked.Increment(ref _attempts);
      throw new TimeoutException("Timeout during reading attempt");
    }
  }

  [Test]
  public async Task DutyAcquisition_ThatFailsTransiently_IsRetriedRatherThanUnwindingTheRunAsync() {
    var elector = new _throwsThenGrantsElector(throwCount: 2);
    var step = new _step("Migrate", "migrator");
    var runner = new StartupPipelineRunner([step], dutyElector: elector) {
      DutyRetryInterval = TimeSpan.FromMilliseconds(10),
    };

    var results = await runner.RunAsync(CancellationToken.None);

    await Assert.That(step.Executions).IsEqualTo(1)
      .Because("a read timeout while asking who holds the duty says nothing about whether the "
             + "step should run — the surrounding loop already waits out exactly this condition");
    await Assert.That(results[0].Outcome).IsEqualTo(StartupStepOutcome.Completed);
    await Assert.That(elector.Attempts).IsEqualTo(3)
      .Because("the two failures must be re-attempted, not swallowed into a skip and not "
             + "escalated into termination");
  }

  [Test]
  [Timeout(30_000)]
  public async Task DutyAcquisition_ThatFailsPersistently_FailsTheStepBoundedlyInsteadOfHangingAsync(
      CancellationToken cancellationToken) {
    var elector = new _alwaysThrowsElector();
    var step = new _step("Migrate", "migrator");
    var runner = new StartupPipelineRunner([step], dutyElector: elector) {
      DutyRetryInterval = TimeSpan.FromMilliseconds(5),
    };

    var results = await runner.RunAsync(cancellationToken);

    await Assert.That(results[0].Outcome).IsEqualTo(StartupStepOutcome.Failed)
      .Because("retrying a standing outage forever is the OTHER way to be silently broken — "
             + "issue #494's rule is that boot failing loudly beats boot hanging quietly");
    await Assert.That(results[0].Reason).IsNotNull();
    await Assert.That(results[0].Reason!).Contains("Timeout during reading attempt")
      .Because("the operator needs the underlying failure, not just the fact that a bound was "
             + "reached — the report is the only record that survives");
    await Assert.That(step.Executions).IsEqualTo(0)
      .Because("the duty was never held, so running the exclusive body anyway would defeat the "
             + "exclusion the step asked for");
  }

  [Test]
  public async Task DutyAcquisition_ThatFailsTransientlyUnderSkip_DoesNotBlockAsync() {
    var elector = new _alwaysThrowsElector();
    var step = new _step("Rewrite", "maintainer", NonHolderBehavior.Skip);
    var runner = new StartupPipelineRunner([step], dutyElector: elector) {
      DutyRetryInterval = TimeSpan.FromMilliseconds(10),
    };

    var results = await runner.RunAsync(CancellationToken.None);

    await Assert.That(results[0].Outcome).IsEqualTo(StartupStepOutcome.Skipped);
    await Assert.That(elector.Attempts).IsEqualTo(1)
      .Because("Skip exists so that nobody blocks on this class of step; a transient failure "
             + "must not quietly convert it into a waiter");
    await Assert.That(step.Executions).IsEqualTo(0);
  }

  /// <summary>
  /// The worker-level backstop. Distinct from the runner fix above: that one makes duty
  /// acquisition behave CORRECTLY, this one guarantees that nothing reaching the worker can stop
  /// the host regardless of where it came from. An unorderable step graph is used because it
  /// throws from order resolution, before any step runs — a path the runner's own guards
  /// deliberately do not cover.
  /// </summary>
  [Test]
  public async Task Worker_WhenTheRunThrows_DoesNotPropagateAndStopTheHostAsync() {
    // A step depending on a name nothing declares cannot be ordered.
    var unorderable = new _unorderableStep();
    var runner = new StartupPipelineRunner([unorderable]);
    var worker = new StartupPipelineWorker(runner);

    // Captured explicitly rather than through a throws-nothing assertion over an async lambda:
    // BackgroundService.StartAsync only hands back the execute task when it has ALREADY faulted,
    // so whether the failure is observable here at all depends on timing. Catching it directly
    // is the only form that cannot pass by accident.
    Exception? escaped = null;
    try {
      await worker.StartAsync(CancellationToken.None);
      if (worker.ExecuteTask is not null) {
        await worker.ExecuteTask;
      }
    } catch (Exception ex) {
      escaped = ex;
    }

    await Assert.That(escaped).IsNull()
      .Because("an exception escaping ExecuteAsync stops the whole host under the default "
             + "BackgroundServiceExceptionBehavior, and it does so by exiting ZERO — the least "
             + "visible possible way for a service to die");

    await worker.StopAsync(CancellationToken.None);
  }

  private sealed class _unorderableStep : IStartupStep {
    public StartupStepDescriptor Descriptor { get; } = new() {
      Name = "Dependent",
      DependsOn = ["NoSuchStepWasEverDeclared"],
    };
    public ValueTask<StartupStepReport> ExecuteAsync(CancellationToken cancellationToken)
      => new(new StartupStepReport(StartupStepOutcome.Completed));
  }
}
