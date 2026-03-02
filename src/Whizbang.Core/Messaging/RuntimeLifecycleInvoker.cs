using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
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
/// <para>
/// <strong>Stage Isolation</strong>: Receptors fire ONLY at their registered stage.
/// A receptor registered at PostPerspectiveAsync will NOT fire at PrePerspectiveAsync
/// or any other stage.
/// </para>
/// </remarks>
/// <docs>core-concepts/lifecycle-receptors#stage-isolation</docs>
/// <tests>Whizbang.Core.Tests/Messaging/LifecycleStageIsolationTests.cs</tests>
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
      IMessageEnvelope envelope,
      LifecycleStage stage,
      ILifecycleContext? context = null,
      CancellationToken cancellationToken = default) {

    ArgumentNullException.ThrowIfNull(envelope);

    // Extract payload from envelope - this is what handlers receive
    var message = envelope.Payload;
    var messageType = message.GetType();

    // Get pre-compiled AOT-compatible invocation delegates from registry
    var handlers = _registry.GetHandlers(messageType, stage);

    if (handlers.Count == 0) {
      // No receptors registered for this message type and stage - this is normal
      return;
    }

    // Extract parent context from envelope hops for trace correlation
    // This ensures receptor spans are parented to the original request even on background threads
    var parentContext = _extractParentContext(envelope.Hops);

    // Invoke all registered receptors with individual tracing
    var handlerIndex = 0;
    foreach (var handler in handlers) {
      // Create activity for each lifecycle receptor invocation
      // Pass parentContext to ensure proper parenting when Activity.Current is null (background threads)
      using var receptorActivity = WhizbangActivitySource.Tracing.StartActivity(
        $"LifecycleReceptor {messageType.Name}[{handlerIndex}]",
        ActivityKind.Internal,
        parentContext: parentContext);
      receptorActivity?.SetTag("whizbang.receptor.message_type", messageType.FullName);
      receptorActivity?.SetTag("whizbang.lifecycle.stage", stage.ToString());
      receptorActivity?.SetTag("whizbang.receptor.index", handlerIndex);

      try {
        await handler(message, context, cancellationToken).ConfigureAwait(false);
      } catch (Exception ex) {
        receptorActivity?.SetTag("whizbang.receptor.error", true);
        receptorActivity?.SetTag("whizbang.receptor.error_type", ex.GetType().FullName);
        // Log error but don't stop processing other receptors
        // In production, this should use ILogger, but for now we'll rethrow to catch test issues
        // FUTURE: Add ILogger support for error logging
        throw new InvalidOperationException(
          $"Lifecycle receptor failed at stage {stage} for message type {messageType.Name}: {ex.Message}",
          ex);
      }

      handlerIndex++;
    }
  }

  /// <summary>
  /// Extracts parent ActivityContext from message hops for trace correlation.
  /// Uses the last hop's TraceParent to link receptor spans to the original HTTP request.
  /// </summary>
  private static ActivityContext _extractParentContext(IReadOnlyList<MessageHop> hops) {
    var traceParent = hops
      .Select(h => h.TraceParent)
      .LastOrDefault(tp => tp is not null);

    if (traceParent is not null && ActivityContext.TryParse(traceParent, null, out var parentContext)) {
      return parentContext;
    }

    return default;
  }
}
