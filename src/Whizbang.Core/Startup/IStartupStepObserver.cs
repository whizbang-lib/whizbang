using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Whizbang.Core.Startup;

/// <summary>A step about to run, as an observer sees it.</summary>
/// <param name="Descriptor">The step's declaration — name, dependencies, capability, blocking.</param>
/// <docs>proposals/startup-pipeline#hooks</docs>
public sealed record StartupStepContext(StartupStepDescriptor Descriptor);

/// <summary>One completed pipeline run, as observers and the status surface see it.</summary>
/// <param name="Results">Every step that ran, in execution order, with outcome, duration and reason.</param>
/// <docs>proposals/startup-pipeline#hooks</docs>
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
/// <docs>proposals/startup-pipeline#hooks</docs>
/// <tests>tests/Whizbang.Core.Tests/Startup/StartupPipelineHooksTests.cs</tests>
public interface IStartupStepObserver {
  /// <summary>A step is about to execute.</summary>
  ValueTask OnStepStartingAsync(StartupStepContext context, CancellationToken cancellationToken);

  /// <summary>A step finished, with what it actually did.</summary>
  ValueTask OnStepCompletedAsync(StartupStepResult result, CancellationToken cancellationToken);

  /// <summary>The run finished; every step's result is in the summary.</summary>
  ValueTask OnPipelineCompletedAsync(StartupSummary summary, CancellationToken cancellationToken);
}
