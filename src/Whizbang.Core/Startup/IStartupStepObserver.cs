using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Whizbang.Core.Startup;

/// <summary>A step about to run, as an observer sees it.</summary>
/// <param name="Descriptor">The step's declaration — name, dependencies, capability, blocking.</param>
/// <docs>operations/startup/startup-pipeline#hooks</docs>
public sealed record StartupStepContext(StartupStepDescriptor Descriptor);

/// <summary>
/// The resolved plan for a run: every enabled step, in the order the resolver derived. Announced
/// before the first step executes, so observers know the full shape of the run — in particular
/// which steps are blocking, without which "the blocking steps have drained" cannot be computed.
/// </summary>
/// <param name="Steps">The ordered descriptors this run will execute.</param>
/// <docs>operations/startup/startup-pipeline#hooks</docs>
public sealed record StartupRunPlan(IReadOnlyList<StartupStepDescriptor> Steps);

/// <summary>One completed pipeline run, as observers and the status surface see it.</summary>
/// <param name="Results">Every step that ran, in execution order, with outcome, duration and reason.</param>
/// <docs>operations/startup/startup-pipeline#hooks</docs>
public sealed record StartupSummary(IReadOnlyList<StartupStepResult> Results);

/// <summary>
/// Watches the pipeline run: called as each step starts and finishes, and once when the whole run
/// completes. This is the seam the framework's own logging and metrics use, and the same one a
/// consumer registers a diagnostic on — one path, not a privileged internal one and a lesser
/// public one.
/// </summary>
/// <remarks>
/// Observers are <b>advisory</b>. One that throws is recorded and skipped for that notification;
/// it never fails the step, the run, or the other observers — a diagnostic must not be able to
/// break a boot. Implementations are registered explicitly, consistent with the framework's
/// zero-reflection and native-AOT constraints.
/// </remarks>
/// <docs>operations/startup/startup-pipeline#hooks</docs>
/// <tests>tests/Whizbang.Core.Tests/Startup/StartupPipelineHooksTests.cs</tests>
public interface IStartupStepObserver {
  /// <summary>
  /// A run is about to begin; the plan carries every step it will execute, in order. Default
  /// no-op so an observer that only cares about step transitions implements nothing extra.
  /// </summary>
  ValueTask OnRunStartingAsync(StartupRunPlan plan, CancellationToken cancellationToken)
    => ValueTask.CompletedTask;

  /// <summary>A step is about to execute.</summary>
  ValueTask OnStepStartingAsync(StartupStepContext context, CancellationToken cancellationToken);

  /// <summary>A step finished, with what it actually did.</summary>
  ValueTask OnStepCompletedAsync(StartupStepResult result, CancellationToken cancellationToken);

  /// <summary>The run finished; every step's result is in the summary.</summary>
  ValueTask OnPipelineCompletedAsync(StartupSummary summary, CancellationToken cancellationToken);
}
