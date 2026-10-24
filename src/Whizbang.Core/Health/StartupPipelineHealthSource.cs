// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.CodeAnalysis;
using System.Text;
using Whizbang.Core.Startup;

namespace Whizbang.Core.Health;

/// <summary>
/// The <c>"startup"</c> managed-resource health source: probes report the pipeline's current step
/// and its progress, so "why is this pod not ready" is answerable from the health surface without
/// reading logs. Most of the machinery already existed — <see cref="IWhizbangHealthSource"/>
/// reports a state plus free-text detail, and the aggregator maps state through
/// <see cref="HealthPolicy"/> — what was missing was a source that reports <em>the pipeline</em>.
/// </summary>
/// <remarks>
/// <para>
/// State mapping: not started → <see cref="ComponentState.Starting"/>; a failed blocking step →
/// <see cref="ComponentState.Faulted"/> (fail-closed — readiness goes unhealthy under both
/// built-in policies); the composite signalled → <see cref="ComponentState.Ready"/>, including
/// while post-ready steps still run (they never gate readiness — the detail says which are going);
/// otherwise in progress, <see cref="ComponentState.Migrating"/> while <c>Migrate</c> runs and
/// <see cref="ComponentState.Starting"/> around it.
/// </para>
/// <para>
/// The detail is framework-authored content only: step names, counts, the current step. A failed
/// step's detail names the step, never its reason — reasons originate in exception messages and
/// belong to the status surface's separate opt-in, and a health endpoint is usually the LEAST
/// protected surface a pod has.
/// </para>
/// </remarks>
/// <docs>operations/startup/startup-status</docs>
/// <tests>tests/Whizbang.Core.Tests/Health/StartupPipelineHealthSourceTests.cs</tests>
public sealed class StartupPipelineHealthSource : IWhizbangHealthSource {
  private readonly IStartupPipelineState _state;
  private readonly IStartupReadySignal _readySignal;
  private readonly IStartupStageReporter _stages;

  /// <summary>Creates the source over the pipeline state and, when registered, the composite signal.</summary>
  /// <param name="state">The startup pipeline's state.</param>
  /// <param name="readySignal">The composite ready signal.</param>
  /// <param name="stages">
  /// The startup stage reporter. When it has entered a stage, the detail leads with the stage's line
  /// (<c>Stage X of 7: &lt;name&gt; - &lt;message&gt;</c>, #1636). A host with no stages passes
  /// <see cref="NullStartupStageReporter.Instance"/>.
  /// </param>
  public StartupPipelineHealthSource(
      IStartupPipelineState state, IStartupReadySignal readySignal, IStartupStageReporter stages) {
    ArgumentNullException.ThrowIfNull(state);
    ArgumentNullException.ThrowIfNull(stages);
    _state = state;
    _readySignal = readySignal;
    _stages = stages;
  }

  /// <inheritdoc />
  public string Component => "startup";

  /// <inheritdoc />
  /// <remarks>
  /// Once a startup stage has been entered (#1636) the detail leads with the stage's line, the same text the instance
  /// logs (<c>Stage X of 7: &lt;name&gt; - &lt;message&gt;</c>), followed by the pipeline's own detail. The state is
  /// the pipeline's, unchanged.
  /// </remarks>
  public ValueTask<ComponentHealth> ReportAsync(CancellationToken cancellationToken) {
    var health = _report();
    return new ValueTask<ComponentHealth>(_stages.Current is { } stage
      ? health with { Detail = $"{stage.Text}; {health.Detail}" }
      : health);
  }

  [SuppressMessage("Major Code Smell", "S3776:Cognitive Complexity of methods should not be too high", Justification = "One report walks every step once and folds its status, whether it blocks, and whether the run has started into a single component health. The branches are the status table, and a helper per status would return a value this method has to fold anyway.")]
  private ComponentHealth _report() {
    if (!_state.HasRunStarted) {
      return new ComponentHealth(ComponentState.Starting, "pipeline not started");
    }

    var steps = _state.SnapshotSteps();
    var terminal = 0;
    string? currentStep = null;
    string? failedBlockingStep = null;
    var postReadyRunning = new List<string>();
    foreach (var step in steps) {
      switch (step.Status) {
        case StartupStepStatus.Completed or StartupStepStatus.Skipped:
          terminal++;
          break;
        case StartupStepStatus.Failed:
          terminal++;
          if (step.Blocking) {
            failedBlockingStep ??= step.Name;
          }
          break;
        case StartupStepStatus.Running:
          currentStep = step.Name;
          if (!step.Blocking) {
            postReadyRunning.Add(step.Name);
          }
          break;
      }
    }

    // Fail-closed: a failed blocking step means this boot never reports ready, and health says so
    // — the step's NAME is framework-authored; its reason is not, and stays off this surface.
    if (failedBlockingStep is not null) {
      return new ComponentHealth(ComponentState.Faulted, $"blocking step '{failedBlockingStep}' failed");
    }

    var composite = _readySignal.IsReady;
    if (composite) {
      var detail = postReadyRunning.Count > 0
        ? $"ready; post-ready steps running: {string.Join(", ", postReadyRunning)}"
        : $"ready ({terminal}/{steps.Count} steps complete)";
      return new ComponentHealth(ComponentState.Ready, detail);
    }

    // In progress. Migrating is the state operators reason about most, so it gets its own answer —
    // everything else in the pre-ready band reports as Starting with the current step in detail.
    var state = currentStep == FrameworkStartupSteps.MIGRATE
      ? ComponentState.Migrating
      : ComponentState.Starting;
    var progress = new StringBuilder();
    progress.Append(currentStep ?? "waiting");
    progress.Append(" (").Append(terminal).Append('/').Append(steps.Count).Append(" steps complete)");
    return new ComponentHealth(state, progress.ToString());
  }
}
