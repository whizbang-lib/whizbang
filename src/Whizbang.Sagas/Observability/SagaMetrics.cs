using System.Diagnostics.Metrics;
using Whizbang.Core.Observability;

namespace Whizbang.Sagas.Observability;

/// <summary>
/// Saga-level metrics tagged with <c>saga_name</c>. Adopters get the
/// industry-standard observability surface (initiated, completed,
/// failed, duration, items-per-saga, hook outcomes) out of the box
/// without each consumer wiring its own counters.
/// </summary>
/// <remarks>
/// <para>
/// Meter name: <c>Whizbang.Sagas</c>. Add to OpenTelemetry exporters
/// alongside <c>Whizbang.Dispatcher</c>.
/// </para>
/// </remarks>
public sealed class SagaMetrics {
#pragma warning disable CA1707
  /// <summary>OpenTelemetry meter name.</summary>
  public const string METER_NAME = "Whizbang.Sagas";
#pragma warning restore CA1707

  /// <summary>Sagas initiated. Tagged <c>saga_name</c>.</summary>
  public Counter<long> SagasInitiated { get; }

  /// <summary>Sagas that reached a terminal completed state (Completed or CompletedWithFailures). Tagged <c>saga_name</c>, <c>final_status</c>.</summary>
  public Counter<long> SagasCompleted { get; }

  /// <summary>Sagas that fail-fasted before all items finished. Tagged <c>saga_name</c>.</summary>
  public Counter<long> SagasFailed { get; }

  /// <summary>End-to-end duration in seconds (Initiated → Completed). Tagged <c>saga_name</c>, <c>final_status</c>.</summary>
  public Histogram<double> SagaDurationSeconds { get; }

  /// <summary>Items completed per saga. Tagged <c>saga_name</c>.</summary>
  public Histogram<int> ItemsCompletedPerSaga { get; }

  /// <summary>Items failed per saga. Tagged <c>saga_name</c>.</summary>
  public Histogram<int> ItemsFailedPerSaga { get; }

  /// <summary>Hook executions that succeeded. Tagged <c>saga_name</c>, <c>hook_name</c>.</summary>
  public Counter<long> HooksCompleted { get; }

  /// <summary>Hook executions that failed. Tagged <c>saga_name</c>, <c>hook_name</c>.</summary>
  public Counter<long> HooksFailed { get; }

  /// <summary>Items reset via a <c>SagaResetEvent</c>. Tagged <c>saga_name</c>.</summary>
  public Counter<long> ItemsReset { get; }

  /// <summary>Initializes a new instance using the shared <see cref="WhizbangMetrics"/> factory.</summary>
  public SagaMetrics(WhizbangMetrics whizbangMetrics) {
    var meter = whizbangMetrics.MeterFactory?.Create(METER_NAME) ?? new Meter(METER_NAME);

    SagasInitiated = meter.CreateCounter<long>("whizbang.sagas.initiated", description: "Sagas initiated");
    SagasCompleted = meter.CreateCounter<long>("whizbang.sagas.completed", description: "Sagas reached a terminal completed state");
    SagasFailed = meter.CreateCounter<long>("whizbang.sagas.failed", description: "Sagas that fail-fasted");
    SagaDurationSeconds = meter.CreateHistogram<double>("whizbang.sagas.duration", "s", "End-to-end saga duration in seconds");
    ItemsCompletedPerSaga = meter.CreateHistogram<int>("whizbang.sagas.items_completed", description: "Items completed per saga");
    ItemsFailedPerSaga = meter.CreateHistogram<int>("whizbang.sagas.items_failed", description: "Items failed per saga");
    HooksCompleted = meter.CreateCounter<long>("whizbang.sagas.hooks_completed", description: "Saga hooks that succeeded");
    HooksFailed = meter.CreateCounter<long>("whizbang.sagas.hooks_failed", description: "Saga hooks that failed");
    ItemsReset = meter.CreateCounter<long>("whizbang.sagas.items_reset", description: "Saga items reset via SagaResetEvent");
  }
}
