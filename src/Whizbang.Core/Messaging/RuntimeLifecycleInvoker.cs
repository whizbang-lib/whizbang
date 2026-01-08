namespace Whizbang.Core.Messaging;

/// <summary>
/// Placeholder implementation of ILifecycleInvoker for Phase 3.
/// In Phase 4, the source generator will create the full implementation with:
/// - Compile-time routing for receptors with [FireAt] attributes
/// - Runtime registry integration for dynamically registered receptors
/// - AOT-compatible invocation without reflection
/// </summary>
/// <remarks>
/// This implementation currently does nothing and serves as a marker until
/// the generator creates the full lifecycle invoker. Tests will need to register
/// receptors and rely on generated code for invocation once Phase 4 is complete.
/// </remarks>
public sealed class RuntimeLifecycleInvoker : ILifecycleInvoker {
  /// <inheritdoc/>
  public ValueTask InvokeAsync(
      object message,
      LifecycleStage stage,
      ILifecycleContext? context = null,
      CancellationToken cancellationToken = default) {

    // Phase 3: Placeholder - no implementation yet
    // Phase 4: Generator will create full implementation with compile-time and runtime routing
    return ValueTask.CompletedTask;
  }
}
