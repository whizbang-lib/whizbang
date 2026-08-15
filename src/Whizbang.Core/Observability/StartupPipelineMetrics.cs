using System.Diagnostics.Metrics;
using Whizbang.Core.Startup;

namespace Whizbang.Core.Observability;

/// <summary>
/// Metrics for the startup pipeline's per-step outcomes. Meter name: <c>Whizbang.Startup</c>.
/// </summary>
/// <remarks>
/// <para>
/// These instruments are what make a slow or silently-skipping boot queryable and alertable across
/// a fleet instead of only visible in per-pod logs:
/// </para>
/// <list type="bullet">
///   <item><description><b>StepDuration</b> — a step's wall time (ms), tagged <c>step</c> and
///   <c>outcome</c>. A long <c>Migrate</c> is only legible if its length is recorded.</description></item>
///   <item><description><b>StepOutcomes</b> — one count per step completion, tagged <c>step</c> and
///   <c>outcome</c>. A sustained <c>outcome=Skipped</c> on a step that should be doing work is the
///   silent-skip class this pipeline exists to expose.</description></item>
/// </list>
/// </remarks>
/// <docs>proposals/startup-pipeline#hooks</docs>
/// <tests>tests/Whizbang.Core.Tests/Startup/StartupBuiltInObserversTests.cs</tests>
public sealed class StartupPipelineMetrics {
#pragma warning disable CA1707
  /// <summary>OpenTelemetry meter name.</summary>
  public const string METER_NAME = "Whizbang.Startup";
#pragma warning restore CA1707

  /// <summary>A step's wall time in milliseconds. Tagged by <c>step</c> and <c>outcome</c>.</summary>
  public Histogram<double> StepDuration { get; }

  /// <summary>One count per step completion. Tagged by <c>step</c> and <c>outcome</c>.</summary>
  public Counter<long> StepOutcomes { get; }

  /// <summary>Initializes a new instance of <see cref="StartupPipelineMetrics"/>.</summary>
  public StartupPipelineMetrics(WhizbangMetrics whizbangMetrics) {
    ArgumentNullException.ThrowIfNull(whizbangMetrics);
    var meter = whizbangMetrics.MeterFactory?.Create(METER_NAME) ?? new Meter(METER_NAME);

    StepDuration = meter.CreateHistogram<double>(
      "whizbang.startup.step_duration",
      unit: "ms",
      description: "Wall time of one startup step, tagged by step and outcome.");

    StepOutcomes = meter.CreateCounter<long>(
      "whizbang.startup.step_outcomes",
      description: "Startup step completions, tagged by step and outcome.");
  }

  /// <summary>Records one step's completion.</summary>
  public void Record(StartupStepResult result) {
    ArgumentNullException.ThrowIfNull(result);
    var tags = new System.Diagnostics.TagList {
      { "step", result.Name },
      { "outcome", result.Outcome.ToString() },
    };
    StepDuration.Record(result.Duration.TotalMilliseconds, tags);
    StepOutcomes.Add(1, tags);
  }
}
