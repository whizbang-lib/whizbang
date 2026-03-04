using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Messaging;


/// <summary>
/// Invokes receptors based on lifecycle stage and [FireAt] attributes.
/// Handles both explicit stage targeting and default stage behavior.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Invocation Rules:</strong>
/// </para>
/// <list type="bullet">
/// <item><description>Receptors with [FireAt(stage)] are invoked at that stage only</description></item>
/// <item><description>Receptors without [FireAt] are invoked at default stages</description></item>
/// </list>
/// <para>
/// <strong>Default Stages (when no [FireAt] attribute):</strong>
/// </para>
/// <list type="bullet">
/// <item><description><see cref="LifecycleStage.LocalImmediateInline"/> - Local dispatch (mediator pattern)</description></item>
/// <item><description><see cref="LifecycleStage.PreOutboxInline"/> - Distributed path sender side</description></item>
/// <item><description><see cref="LifecycleStage.PostInboxInline"/> - Distributed path receiver side</description></item>
/// </list>
/// <para>
/// <strong>Path Exclusivity:</strong>
/// Local and distributed paths are mutually exclusive. A message goes through one path only:
/// </para>
/// <list type="bullet">
/// <item><description>Local path: LocalImmediate only (no persistence)</description></item>
/// <item><description>Distributed path: PreOutbox (sender) + PostInbox (receiver)</description></item>
/// </list>
/// </remarks>
/// <docs>core-concepts/lifecycle-receptors</docs>
/// <tests>tests/Whizbang.Core.Tests/Messaging/ReceptorInvokerTests.cs</tests>
public interface IReceptorInvoker {
  /// <summary>
  /// Invokes all receptors for the message at the specified lifecycle stage.
  /// </summary>
  /// <param name="envelope">The message envelope containing the payload and metadata (hops, security context).</param>
  /// <param name="stage">The lifecycle stage at which to invoke receptors.</param>
  /// <param name="context">Optional context providing metadata about the invocation (stream ID, message source, etc.).</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>Task that completes when all receptors have been invoked.</returns>
  /// <remarks>
  /// The invoker extracts the payload from the envelope, establishes security context
  /// (if IMessageSecurityContextProvider is configured), then invokes all registered receptors.
  /// </remarks>
  ValueTask InvokeAsync(
      IMessageEnvelope envelope,
      LifecycleStage stage,
      ILifecycleContext? context = null,
      CancellationToken cancellationToken = default);
}
