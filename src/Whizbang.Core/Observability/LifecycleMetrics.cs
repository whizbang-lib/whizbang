using System.Diagnostics.Metrics;

namespace Whizbang.Core.Observability;

/// <summary>
/// Metrics for all 20 lifecycle stages, tag hooks, and receptor invocation.
/// Meter name: Whizbang.Lifecycle
/// </summary>
/// <docs>operations/observability/metrics</docs>
/// <tests>tests/Whizbang.Core.Tests/Observability/LifecycleMetricsTests.cs</tests>
public sealed class LifecycleMetrics {
#pragma warning disable CA1707
  /// <summary>The OpenTelemetry meter name for this metrics group.</summary>
  public const string METER_NAME = "Whizbang.Lifecycle";
#pragma warning restore CA1707


  // Stage timing

  /// <summary>Time executing all receptors for a stage.</summary>
  public Histogram<double> StageDuration { get; }

  /// <summary>Individual receptor invocation time.</summary>
  public Histogram<double> ReceptorDuration { get; }

  // Stage counters

  /// <summary>Total invocations per lifecycle stage.</summary>
  public Counter<long> StageInvocations { get; }

  /// <summary>Individual receptor invocations.</summary>
  public Counter<long> ReceptorInvocations { get; }

  /// <summary>Receptor failures per stage.</summary>
  public Counter<long> ReceptorErrors { get; }

  // Tag hook timing

  /// <summary>Per-hook execution time.</summary>
  public Histogram<double> TagHookDuration { get; }

  /// <summary>Total tag processing time (all hooks).</summary>
  public Histogram<double> TagProcessingDuration { get; }

  // Tag hook counters

  /// <summary>Hook invocations.</summary>
  public Counter<long> TagHookInvocations { get; }

  /// <summary>Hook failures.</summary>
  public Counter<long> TagHookErrors { get; }

  /// <summary>Initializes a new instance of the <see cref="LifecycleMetrics"/> class.</summary>
  /// <param name="whizbangMetrics">The shared metrics factory providing the meter.</param>
  public LifecycleMetrics(WhizbangMetrics whizbangMetrics) {
    var meter = whizbangMetrics.MeterFactory?.Create(METER_NAME) ?? new Meter(METER_NAME);

    StageDuration = meter.CreateHistogram<double>("whizbang.lifecycle.stage.duration", "ms", "Time executing all receptors for a stage");
    ReceptorDuration = meter.CreateHistogram<double>("whizbang.lifecycle.receptor.duration", "ms", "Individual receptor invocation time");

    StageInvocations = meter.CreateCounter<long>("whizbang.lifecycle.stage.invocations", description: "Total invocations per lifecycle stage");
    ReceptorInvocations = meter.CreateCounter<long>("whizbang.lifecycle.receptor.invocations", description: "Individual receptor invocations");
    ReceptorErrors = meter.CreateCounter<long>("whizbang.lifecycle.receptor.errors", description: "Receptor failures per stage");

    TagHookDuration = meter.CreateHistogram<double>("whizbang.lifecycle.tag_hook.duration", "ms", "Per-hook execution time");
    TagProcessingDuration = meter.CreateHistogram<double>("whizbang.lifecycle.tag_processing.duration", "ms", "Total tag processing time (all hooks)");

    TagHookInvocations = meter.CreateCounter<long>("whizbang.lifecycle.tag_hook.invocations", description: "Hook invocations");
    TagHookErrors = meter.CreateCounter<long>("whizbang.lifecycle.tag_hook.errors", description: "Hook failures");
  }
}
