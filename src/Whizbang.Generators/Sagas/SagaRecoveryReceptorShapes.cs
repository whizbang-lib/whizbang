namespace Whizbang.Generators.Sagas;

/// <summary>
/// One recovery receptor the saga generator emits into a <c>[Saga]</c>-marked class.
/// </summary>
/// <param name="ClassName">Nested receptor class name, e.g. <c>SagaItemCompletedRecoveryHandler</c>.</param>
/// <param name="SagaEventClassName">Nested saga event class it handles, or <c>null</c> when it handles
/// a framework-owned event named by <paramref name="FrameworkMessageType"/> instead.</param>
/// <param name="FrameworkMessageType">Fully qualified framework event type it handles, or <c>null</c>
/// when it handles one of the saga's own generated events.</param>
/// <param name="LifecycleStage">Fully qualified <c>LifecycleStage</c> enum member the emitted
/// <c>[FireAt]</c> names, or <c>null</c> when the receptor carries no <c>[FireAt]</c> and takes the
/// default stage.</param>
public sealed record SagaRecoveryReceptorShape(
    string ClassName,
    string? SagaEventClassName,
    string? FrameworkMessageType,
    string? LifecycleStage);

/// <summary>
/// The compile-time shape of the three recovery receptors
/// <c>Whizbang.Sagas.Generators.SagaGenerator._emitRecoveryReceptors</c> emits into every
/// <c>[Saga]</c>-marked class that generates a service.
///
/// <para><b>Why this exists.</b> Source generators do not observe each other's output, so
/// <c>ReceptorDiscoveryGenerator</c> — which discovers receptors syntactically — cannot see these
/// classes. Left undescribed they are neither DI-registered nor routed, and the framework-owned
/// completion path never runs: a saga dispatches every item, every item finishes, and the saga
/// silently never completes. This is the receptor-side twin of <see cref="SagaEventShapes"/>.</para>
///
/// <para><b>Keeping it honest.</b> A drift guard in Whizbang.Sagas.Tests resolves each described
/// receptor against the classes actually emitted and fails if a name, message type, or lifecycle
/// stage here stops matching.</para>
/// </summary>
/// <docs>fundamentals/sagas/completion-orchestration</docs>
public static class SagaRecoveryReceptorShapes {
  /// <summary>Stage the two per-item terminal handlers declare via <c>[FireAt]</c>.</summary>
  private const string POST_ALL_PERSPECTIVES_INLINE =
      "global::Whizbang.Core.Messaging.LifecycleStage.PostAllPerspectivesInline";

  /// <summary>The framework-owned tick event the watchdog handler receives — shared by every saga.</summary>
  internal const string WATCHDOG_TICK_EVENT = "Whizbang.Sagas.SagaCompletionWatchdogTickEvent";

  /// <summary>
  /// Named argument on <c>[Saga]</c> that suppresses the generated service — and with it these
  /// receptors, which take that service as their constructor dependency.
  /// </summary>
  internal const string GENERATE_SERVICE_ARGUMENT = "GenerateService";

  /// <summary>
  /// Every recovery receptor the generator emits, in emission order. Mirrors
  /// <c>SagaGenerator._emitRecoveryReceptors</c> one-for-one.
  /// </summary>
  public static readonly SagaRecoveryReceptorShape[] All = [
    // Per-item terminals nudge recovery on every completion so the last item across all pods drives
    // SagaCompletedEvent — the in-memory completion tracker is per-pod sharded and never reaches
    // Total alone.
    new("SagaItemCompletedRecoveryHandler",
        SagaEventClassName: "ItemCompletedEvent",
        FrameworkMessageType: null,
        LifecycleStage: POST_ALL_PERSPECTIVES_INLINE),

    new("SagaItemFailedRecoveryHandler",
        SagaEventClassName: "ItemFailedEvent",
        FrameworkMessageType: null,
        LifecycleStage: POST_ALL_PERSPECTIVES_INLINE),

    // The safety net. No [FireAt] — it takes the default stage. Its message is the framework's own
    // tick type, NOT a per-saga generated one, so all sagas share the tick shape.
    new("SagaCompletionWatchdogTickHandler",
        SagaEventClassName: null,
        FrameworkMessageType: WATCHDOG_TICK_EVENT,
        LifecycleStage: null),
  ];
}
