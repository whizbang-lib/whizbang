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
/// <docs>proposals/startup-pipeline#status</docs>
/// <tests>tests/Whizbang.Core.Tests/Health/StartupPipelineHealthSourceTests.cs</tests>
public sealed class StartupPipelineHealthSource : IWhizbangHealthSource {
  private readonly IStartupPipelineState _state;
  private readonly IStartupReadySignal? _readySignal;

  /// <summary>Creates the source over the pipeline state and, when registered, the composite signal.</summary>
  public StartupPipelineHealthSource(IStartupPipelineState state, IStartupReadySignal? readySignal = null) {
    ArgumentNullException.ThrowIfNull(state);
    _state = state;
    _readySignal = readySignal;
  }

  /// <inheritdoc />
  public string Component => "startup";

  /// <inheritdoc />
  public ValueTask<ComponentHealth> ReportAsync(CancellationToken cancellationToken) {
    if (!_state.HasRunStarted) {
      return new ValueTask<ComponentHealth>(
        new ComponentHealth(ComponentState.Starting, "pipeline not started"));
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
      return new ValueTask<ComponentHealth>(new ComponentHealth(
        ComponentState.Faulted, $"blocking step '{failedBlockingStep}' failed"));
    }

    var composite = _readySignal?.IsReady ?? _state.IsReady;
    if (composite) {
      var detail = postReadyRunning.Count > 0
        ? $"ready; post-ready steps running: {string.Join(", ", postReadyRunning)}"
        : $"ready ({terminal}/{steps.Count} steps complete)";
      return new ValueTask<ComponentHealth>(new ComponentHealth(ComponentState.Ready, detail));
    }

    // In progress. Migrating is the state operators reason about most, so it gets its own answer;
    // everything else in the pre-ready band reports as Starting with the current step in detail.
    var state = currentStep == FrameworkStartupSteps.MIGRATE
      ? ComponentState.Migrating
      : ComponentState.Starting;
    var progress = new StringBuilder();
    progress.Append(currentStep ?? "waiting");
    progress.Append(" (").Append(terminal).Append('/').Append(steps.Count).Append(" steps complete)");
    return new ValueTask<ComponentHealth>(new ComponentHealth(state, progress.ToString()));
  }
}
