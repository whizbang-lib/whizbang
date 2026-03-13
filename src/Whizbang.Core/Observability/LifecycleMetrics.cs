using System.Diagnostics.Metrics;

namespace Whizbang.Core.Observability;

/// <summary>
/// Metrics for all 20 lifecycle stages, tag hooks, and receptor invocation.
/// Meter name: Whizbang.Lifecycle
/// </summary>
/// <docs>observability/metrics</docs>
/// <tests>tests/Whizbang.Core.Tests/Observability/LifecycleMetricsTests.cs</tests>
public sealed class LifecycleMetrics {
#pragma warning disable CA1707
  public const string METER_NAME = "Whizbang.Lifecycle";
#pragma warning restore CA1707

  private readonly Meter _meter;

  // Stage timing
  public Histogram<double> StageDuration { get; }
  public Histogram<double> ReceptorDuration { get; }

  // Stage counters
  public Counter<long> StageInvocations { get; }
  public Counter<long> ReceptorInvocations { get; }
  public Counter<long> ReceptorErrors { get; }

  // Tag hook timing
  public Histogram<double> TagHookDuration { get; }
  public Histogram<double> TagProcessingDuration { get; }

  // Tag hook counters
  public Counter<long> TagHookInvocations { get; }
  public Counter<long> TagHookErrors { get; }

  public LifecycleMetrics(WhizbangMetrics whizbangMetrics) {
    _meter = whizbangMetrics.MeterFactory?.Create(METER_NAME) ?? new Meter(METER_NAME);

    StageDuration = _meter.CreateHistogram<double>("whizbang.lifecycle.stage.duration", "ms", "Time executing all receptors for a stage");
    ReceptorDuration = _meter.CreateHistogram<double>("whizbang.lifecycle.receptor.duration", "ms", "Individual receptor invocation time");

    StageInvocations = _meter.CreateCounter<long>("whizbang.lifecycle.stage.invocations", description: "Total invocations per lifecycle stage");
    ReceptorInvocations = _meter.CreateCounter<long>("whizbang.lifecycle.receptor.invocations", description: "Individual receptor invocations");
    ReceptorErrors = _meter.CreateCounter<long>("whizbang.lifecycle.receptor.errors", description: "Receptor failures per stage");

    TagHookDuration = _meter.CreateHistogram<double>("whizbang.lifecycle.tag_hook.duration", "ms", "Per-hook execution time");
    TagProcessingDuration = _meter.CreateHistogram<double>("whizbang.lifecycle.tag_processing.duration", "ms", "Total tag processing time (all hooks)");

    TagHookInvocations = _meter.CreateCounter<long>("whizbang.lifecycle.tag_hook.invocations", description: "Hook invocations");
    TagHookErrors = _meter.CreateCounter<long>("whizbang.lifecycle.tag_hook.errors", description: "Hook failures");
  }
}
