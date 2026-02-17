using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Messaging;

/// <summary>
/// Default implementation of <see cref="IReceptorInvoker"/> that invokes receptors
/// based on lifecycle stage.
/// </summary>
/// <remarks>
/// <para>
/// This implementation queries the <see cref="IReceptorRegistry"/> for receptors registered
/// at the specified stage and invokes them. All categorization (which receptors fire at which
/// stages) is done at compile time by the source generator:
/// </para>
/// <list type="bullet">
/// <item><description>Receptors WITH [FireAt(X)] are registered at stage X only</description></item>
/// <item><description>Receptors WITHOUT [FireAt] are registered at LocalImmediateInline, PreOutboxInline, and PostInboxInline</description></item>
/// </list>
/// <para>
/// No runtime logic is needed to determine when a receptor fires - it's all compile-time categorization.
/// </para>
/// <para>
/// <strong>Scoped Services:</strong> This invoker creates a new scope for each invocation to ensure
/// receptors with scoped dependencies (like IEventStore) can be resolved correctly, even when
/// called from singleton services like TransportConsumerWorker.
/// </para>
/// </remarks>
/// <docs>core-concepts/lifecycle-receptors</docs>
/// <tests>tests/Whizbang.Core.Tests/Messaging/ReceptorInvokerTests.cs</tests>
public sealed class ReceptorInvoker : IReceptorInvoker {
  private readonly IReceptorRegistry _registry;
  private readonly IServiceScopeFactory _scopeFactory;

  /// <summary>
  /// Creates a new ReceptorInvoker.
  /// </summary>
  /// <param name="registry">The receptor registry to query for discovered receptors.</param>
  /// <param name="scopeFactory">Factory to create service scopes for resolving scoped dependencies.</param>
  public ReceptorInvoker(IReceptorRegistry registry, IServiceScopeFactory scopeFactory) {
    ArgumentNullException.ThrowIfNull(registry);
    ArgumentNullException.ThrowIfNull(scopeFactory);
    _registry = registry;
    _scopeFactory = scopeFactory;
  }

  /// <inheritdoc/>
  public async ValueTask InvokeAsync(
      object message,
      LifecycleStage stage,
      ILifecycleContext? context = null,
      CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(message);

    // Note: context is available for receptors that need metadata about the invocation
    // (stream ID, message source, etc.) but is not used by the invoker itself.
    // Receptors can access it if needed for logging, tracing, or conditional logic.
    _ = context; // Currently unused by invoker, but passed to receptors if needed

    // GetType() is AOT-safe - returns the runtime type
    var messageType = message.GetType();

    // Registry already has categorized receptors at compile time
    // Just get receptors for this type/stage combination and invoke them
    var receptors = _registry.GetReceptorsFor(messageType, stage);

    if (receptors.Count == 0) {
      // No receptors registered for this message type and stage - this is normal
      return;
    }

    // Create a scope to resolve scoped dependencies (e.g., IEventStore, DbContext)
    // This is critical when called from singleton services like TransportConsumerWorker
    using var scope = _scopeFactory.CreateScope();
    var scopedProvider = scope.ServiceProvider;

    foreach (var receptor in receptors) {
      // InvokeAsync is a pre-compiled delegate (no reflection)
      // Pass the scoped provider so receptor can be resolved with its dependencies
      await receptor.InvokeAsync(scopedProvider, message, cancellationToken).ConfigureAwait(false);
    }
  }
}

/// <summary>
/// No-op implementation of <see cref="IReceptorInvoker"/> used when no registry is available.
/// </summary>
/// <remarks>
/// This is used as a fallback when <c>AddWhizbangReceptorRegistry()</c> has not been called.
/// It allows the system to function without lifecycle receptor invocation.
/// </remarks>
internal sealed class NullReceptorInvoker : IReceptorInvoker {
  /// <inheritdoc/>
  public ValueTask InvokeAsync(
      object message,
      LifecycleStage stage,
      ILifecycleContext? context = null,
      CancellationToken cancellationToken = default) {
    // No-op - no receptors to invoke
    return ValueTask.CompletedTask;
  }
}
