using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Whizbang.Core.Startup;

/// <summary>What a step did, as opposed to merely that it returned.</summary>
/// <remarks>
/// The distinction between <see cref="Completed"/> and <see cref="Skipped"/> is the reason this
/// exists. Today a step that silently does nothing is indistinguishable from one that succeeded:
/// rewind repair skipped by a cold-database catch-all reports exactly what rewind repair that found
/// nothing to do reports.
/// </remarks>
/// <docs>proposals/startup-pipeline</docs>
public enum StartupStepOutcome {
  /// <summary>The step did its work.</summary>
  Completed,

  /// <summary>
  /// The step ran and deliberately did nothing — no work to do, not this instance's to do, or
  /// already done elsewhere. Always accompanied by a reason, because "found nothing" and "could not
  /// look" are different facts.
  /// </summary>
  Skipped,

  /// <summary>The step could not complete. The reason carries what went wrong.</summary>
  Failed,
}

/// <summary>What a step reports about its own execution.</summary>
/// <param name="Outcome">Whether it did the work, deliberately did not, or could not.</param>
/// <param name="Reason">
/// Why — required in practice for <see cref="StartupStepOutcome.Skipped"/> and
/// <see cref="StartupStepOutcome.Failed"/>, since an outcome without a reason cannot be acted on.
/// </param>
/// <docs>proposals/startup-pipeline</docs>
public readonly record struct StartupStepReport(StartupStepOutcome Outcome, string? Reason = null);

/// <summary>One step's execution, as the pipeline recorded it.</summary>
/// <param name="Name">The step's declared name.</param>
/// <param name="Outcome">What it did.</param>
/// <param name="Duration">How long it took — a long step is only legible if its length is recorded.</param>
/// <param name="Reason">Why, where the outcome warrants explaining.</param>
/// <docs>proposals/startup-pipeline</docs>
public sealed record StartupStepResult(
  string Name, StartupStepOutcome Outcome, TimeSpan Duration, string? Reason);

/// <summary>
/// One declared unit of startup work.
/// </summary>
/// <remarks>
/// Implementations are registered explicitly rather than discovered, consistent with the framework's
/// zero-reflection and native-AOT constraints. No assembly scanning.
/// </remarks>
/// <docs>proposals/startup-pipeline</docs>
public interface IStartupStep {
  /// <summary>What this step is, what it needs, and who runs it.</summary>
  StartupStepDescriptor Descriptor { get; }

  /// <summary>Performs the step and reports what it actually did.</summary>
  /// <param name="cancellationToken">Cancellation for host shutdown.</param>
  ValueTask<StartupStepReport> ExecuteAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Runs the registered startup steps in the order their declared dependencies imply, recording what
/// each one did.
/// </summary>
/// <remarks>
/// <para>
/// The runner is <b>re-entrant</b>: calling it again runs the pipeline again. Nothing re-enters it
/// today, but an instance reviving from standby does exactly that — it re-enters at the assessment
/// step, finds the schema unchanged, and comes back up — and a runner that quietly refused a second
/// run would have to be rebuilt to permit it. Honouring that now costs nothing.
/// </para>
/// <para>
/// A step that throws is recorded as <see cref="StartupStepOutcome.Failed"/> with the exception's
/// message rather than being allowed to escape. The report is how everything downstream — health,
/// the status surface, an operator — learns what happened, and an exception that unwinds the runner
/// destroys exactly that record.
/// </para>
/// </remarks>
/// <docs>proposals/startup-pipeline</docs>
/// <tests>tests/Whizbang.Core.Tests/Startup/StartupPipelineRunnerTests.cs</tests>
public sealed class StartupPipelineRunner {
  private readonly IReadOnlyList<IStartupStep> _steps;
  private readonly IReadOnlyList<IStartupStepObserver> _observers;
  private readonly IDutyElector? _dutyElector;

  /// <summary>Creates a runner over the registered steps.</summary>
  /// <param name="steps">The registered steps, in any order — the resolver decides the real one.</param>
  /// <param name="observers">
  /// The observers to notify around each step and at run completion. Advisory: an observer that
  /// throws is skipped for that notification and never fails the step, the run, or its peers.
  /// </param>
  /// <param name="dutyElector">
  /// Wins duties for steps that require an exclusive capability. Optional: without one, a duty
  /// degrades to a shared capability — every instance runs the step — which is survivable only
  /// because the framework's exclusive steps are individually idempotent and separately guarded
  /// (the migration by its advisory lock, the rewrite by its request record).
  /// </param>
  /// <exception cref="ArgumentNullException"><paramref name="steps"/> is <see langword="null"/>.</exception>
  public StartupPipelineRunner(
      IReadOnlyList<IStartupStep> steps,
      IReadOnlyList<IStartupStepObserver>? observers = null,
      IDutyElector? dutyElector = null) {
    ArgumentNullException.ThrowIfNull(steps);
    _steps = steps;
    _observers = observers ?? [];
    _dutyElector = dutyElector;
  }

  /// <summary>
  /// How often a non-holder whose behaviour is <see cref="NonHolderBehavior.Await"/> re-attempts
  /// the duty. The retry is the poll-backstop layer — the holder's release is the real signal,
  /// and re-attempting IS how a waiter learns of it. Init-settable so tests run in test time.
  /// </summary>
  internal TimeSpan DutyRetryInterval { get; init; } = TimeSpan.FromSeconds(1);

  /// <summary>
  /// Resolves the order and runs every enabled step, returning one result per step that ran.
  /// </summary>
  /// <param name="cancellationToken">Cancellation for host shutdown.</param>
  /// <returns>The results of this run only; a previous run's results are not carried forward.</returns>
  /// <exception cref="StartupPipelineConfigurationException">
  /// The declared steps cannot be ordered — thrown before anything executes.
  /// </exception>
  public async Task<IReadOnlyList<StartupStepResult>> RunAsync(CancellationToken cancellationToken) {
    var byName = new Dictionary<string, IStartupStep>(_steps.Count, StringComparer.Ordinal);
    var descriptors = new List<StartupStepDescriptor>(_steps.Count);
    foreach (var step in _steps) {
      descriptors.Add(step.Descriptor);
      byName[step.Descriptor.Name] = step;
    }

    // Resolution happens per run rather than once, so a re-entered pipeline honours whatever the
    // descriptors say now — enablement in particular can differ between runs.
    var ordered = StartupStepOrderResolver.Resolve(descriptors);

    // Announce the whole plan before anything executes: readiness ("the blocking steps have
    // drained") is only computable by an observer that knows which steps are coming.
    await _notifyAsync(o => o.OnRunStartingAsync(new StartupRunPlan(ordered), cancellationToken))
      .ConfigureAwait(false);

    var results = new List<StartupStepResult>(ordered.Count);
    foreach (var descriptor in ordered) {
      cancellationToken.ThrowIfCancellationRequested();

      await _notifyAsync(o => o.OnStepStartingAsync(new StartupStepContext(descriptor), cancellationToken))
        .ConfigureAwait(false);

      var step = byName[descriptor.Name];
      var watch = Stopwatch.StartNew();
      var report = descriptor.RequiredCapability != StartupCapabilities.EVERY_INSTANCE && _dutyElector is not null
        ? await _executeExclusiveAsync(step, descriptor, cancellationToken).ConfigureAwait(false)
        : await _executeAsync(step, cancellationToken).ConfigureAwait(false);
      watch.Stop();

      var result = new StartupStepResult(descriptor.Name, report.Outcome, watch.Elapsed, report.Reason);
      results.Add(result);

      await _notifyAsync(o => o.OnStepCompletedAsync(result, cancellationToken)).ConfigureAwait(false);
    }

    var summary = new StartupSummary(results);
    await _notifyAsync(o => o.OnPipelineCompletedAsync(summary, cancellationToken)).ConfigureAwait(false);

    return results;
  }

  /// <summary>Runs the step's body, mapping a throw to <see cref="StartupStepOutcome.Failed"/> —
  /// the report is how everything downstream learns what happened, and an exception that unwinds
  /// the runner destroys exactly that record.</summary>
  private static async ValueTask<StartupStepReport> _executeAsync(
      IStartupStep step, CancellationToken cancellationToken) {
    try {
      return await step.ExecuteAsync(cancellationToken).ConfigureAwait(false);
    } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
      throw;
    } catch (Exception ex) {
      return new StartupStepReport(StartupStepOutcome.Failed, ex.Message);
    }
  }

  /// <summary>
  /// Runs a step that requires a duty. The holder executes it and releases on completion; a
  /// non-holder does what the descriptor declares: <see cref="NonHolderBehavior.Skip"/> reports
  /// <c>capability not held</c> and carries on, <see cref="NonHolderBehavior.Await"/> keeps
  /// re-attempting until it wins — the holder's release is the completion signal, and the step's
  /// own idempotency makes the late winner find nothing to do. That is exactly the behaviour the
  /// migration advisory lock produces today: losers block, then re-check, then continue.
  /// </summary>
  private async ValueTask<StartupStepReport> _executeExclusiveAsync(
      IStartupStep step, StartupStepDescriptor descriptor, CancellationToken cancellationToken) {
    var grant = await _dutyElector!.TryAcquireAsync(descriptor.RequiredCapability, cancellationToken)
      .ConfigureAwait(false);
    if (grant is null && descriptor.NonHolderBehavior == NonHolderBehavior.Skip) {
      return new StartupStepReport(StartupStepOutcome.Skipped, "capability not held");
    }
    while (grant is null) {
      cancellationToken.ThrowIfCancellationRequested();
      await Task.Delay(DutyRetryInterval, cancellationToken).ConfigureAwait(false);
      grant = await _dutyElector.TryAcquireAsync(descriptor.RequiredCapability, cancellationToken)
        .ConfigureAwait(false);
    }
    await using (grant.ConfigureAwait(false)) {
      return await _executeAsync(step, cancellationToken).ConfigureAwait(false);
    }
  }

  /// <summary>
  /// Notifies every observer, swallowing what any one of them throws: a diagnostic must not be
  /// able to break a boot, and one broken diagnostic must not silence the rest.
  /// </summary>
  private async ValueTask _notifyAsync(Func<IStartupStepObserver, ValueTask> notification) {
    foreach (var observer in _observers) {
#pragma warning disable CA1031, RCS1075 // deliberately swallowed: the observer contract is advisory,
      // and the framework's logging observer is itself an observer — there is no lower layer to
      // report to without creating the recursion this guard exists to prevent.
      try {
        await notification(observer).ConfigureAwait(false);
      } catch (Exception) {
        // advisory — see pragma justification above
      }
#pragma warning restore CA1031, RCS1075
    }
  }
}
