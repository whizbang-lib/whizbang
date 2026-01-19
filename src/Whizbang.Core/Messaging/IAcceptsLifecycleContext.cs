namespace Whizbang.Core.Messaging;

/// <summary>
/// Optional interface for receptors that need access to lifecycle execution context.
/// When a receptor implements this interface, the runtime will call SetLifecycleContext
/// before invoking HandleAsync, allowing the receptor to filter by perspective name, etc.
/// </summary>
/// <remarks>
/// This is primarily used by test receptors that need to filter invocations by perspective name.
/// Production receptors typically don't need this as they're registered via [FireAt] attribute
/// which provides compile-time filtering.
/// </remarks>
/// <docs>testing/lifecycle-synchronization</docs>
public interface IAcceptsLifecycleContext {
  /// <summary>
  /// Sets the lifecycle context for the current invocation.
  /// Called by the runtime immediately before HandleAsync.
  /// </summary>
  /// <param name="context">The lifecycle context containing stage, perspective name, stream ID, etc.</param>
  void SetLifecycleContext(ILifecycleContext context);
}
