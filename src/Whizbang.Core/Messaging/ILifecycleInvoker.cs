using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Messaging;

/// <summary>
/// Invokes lifecycle receptors at specific stages of the message processing pipeline.
/// Used by workers (PerspectiveWorker, ServiceBusConsumerWorker, etc.) to notify
/// receptors when lifecycle events occur (perspective complete, inbox processed, etc.).
/// </summary>
/// <remarks>
/// <para>
/// Lifecycle invocation enables deterministic test synchronization by allowing tests
/// to register receptors that fire after perspectives complete, eliminating race conditions
/// from polling-based synchronization.
/// </para>
/// <para>
/// The invoker routes messages to receptors based on:
/// - Message type (e.g., ProductCreatedEvent)
/// - Lifecycle stage (e.g., PostPerspectiveInline)
/// - [FireAt] attributes on receptor classes (compile-time discovery)
/// - Runtime registrations via ILifecycleReceptorRegistry (test-time registration)
/// </para>
/// </remarks>
/// <docs>core-concepts/lifecycle-stages</docs>
public interface ILifecycleInvoker {
  /// <summary>
  /// Invokes all receptors registered for the given message type and lifecycle stage.
  /// Includes both compile-time discovered receptors (via [FireAt] attributes) and
  /// runtime registered receptors (via ILifecycleReceptorRegistry).
  /// </summary>
  /// <param name="envelope">The message envelope containing the payload and metadata (hops, security context)</param>
  /// <param name="stage">The lifecycle stage at which to invoke receptors</param>
  /// <param name="context">Optional context providing metadata about the invocation (stream ID, perspective name, etc.)</param>
  /// <param name="cancellationToken">Cancellation token</param>
  /// <returns>Task that completes when all receptors have been invoked</returns>
  /// <remarks>
  /// The invoker extracts the payload from the envelope and invokes all registered handlers.
  /// </remarks>
  ValueTask InvokeAsync(
      IMessageEnvelope envelope,
      LifecycleStage stage,
      ILifecycleContext? context = null,
      CancellationToken cancellationToken = default);
}
