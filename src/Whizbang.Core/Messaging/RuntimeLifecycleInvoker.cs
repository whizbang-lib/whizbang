using System;
using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Messaging;

/// <summary>
/// Runtime implementation of ILifecycleInvoker that queries the ILifecycleReceptorRegistry
/// for dynamically registered receptors and invokes them using pre-compiled delegates.
/// </summary>
/// <remarks>
/// <para>
/// This implementation supports runtime receptor registration (primarily for tests).
/// In future phases, the source generator may create a full implementation with
/// compile-time routing for receptors with [FireAt] attributes.
/// </para>
/// <para>
/// Invocation is AOT-compatible - the registry provides pre-compiled delegates
/// that eliminate reflection at invocation time.
/// </para>
/// </remarks>
public sealed class RuntimeLifecycleInvoker : ILifecycleInvoker {
  private readonly ILifecycleReceptorRegistry _registry;

  /// <summary>
  /// Creates a new RuntimeLifecycleInvoker.
  /// </summary>
  /// <param name="registry">The receptor registry to query for registered receptors.</param>
  public RuntimeLifecycleInvoker(ILifecycleReceptorRegistry registry) {
    ArgumentNullException.ThrowIfNull(registry);
    _registry = registry;
  }

  /// <inheritdoc/>
  public async ValueTask InvokeAsync(
      object message,
      LifecycleStage stage,
      ILifecycleContext? context = null,
      CancellationToken cancellationToken = default) {

    ArgumentNullException.ThrowIfNull(message);

    var messageType = message.GetType();

    // Get pre-compiled AOT-compatible invocation delegates from registry
    var handlers = _registry.GetHandlers(messageType, stage);

    if (handlers.Count == 0) {
      // No receptors registered for this message type and stage - this is normal
      return;
    }

    // Invoke all registered receptors
    foreach (var handler in handlers) {
      try {
        await handler(message, context, cancellationToken).ConfigureAwait(false);
      } catch (Exception ex) {
        // Log error but don't stop processing other receptors
        // In production, this should use ILogger, but for now we'll rethrow to catch test issues
        // FUTURE: Add ILogger support for error logging
        throw new InvalidOperationException(
          $"Lifecycle receptor failed at stage {stage} for message type {messageType.Name}: {ex.Message}",
          ex);
      }
    }
  }
}
