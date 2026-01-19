namespace Whizbang.Core.Messaging;

/// <summary>
/// Indicates the source of a message for lifecycle stage invocations.
/// </summary>
/// <remarks>
/// <para>
/// Used to distinguish whether a lifecycle stage is firing for:
/// <list type="bullet">
///   <item><strong>Outbox</strong> - Local message publication (publishing)</item>
///   <item><strong>Inbox</strong> - External message receipt (consuming)</item>
/// </list>
/// </para>
/// <para>
/// <strong>Use Case:</strong> Distribute lifecycle stages (PreDistribute, Distribute, PostDistribute)
/// fire for BOTH outbox and inbox messages. Receptors can use MessageSource to filter:
/// </para>
/// <code>
/// [FireAt(LifecycleStage.PreDistributeInline)]
/// public class DistributeReceptor : IReceptor&lt;ProductCreatedEvent&gt;, IAcceptsLifecycleContext {
///   private ILifecycleContext? _context;
///
///   public void SetLifecycleContext(ILifecycleContext context) => _context = context;
///
///   public ValueTask HandleAsync(ProductCreatedEvent evt, CancellationToken ct) {
///     // Only fire for local publication, ignore transport consumption
///     if (_context?.MessageSource == MessageSource.Inbox) {
///       return ValueTask.CompletedTask;
///     }
///
///     // Handle outbox publication
///     Console.WriteLine("Event published locally!");
///     return ValueTask.CompletedTask;
///   }
/// }
/// </code>
/// </remarks>
/// <docs>core-concepts/lifecycle-receptors</docs>
public enum MessageSource {
  /// <summary>
  /// Message is being dispatched locally within the same process (no transport involved).
  /// Used for ImmediateAsync lifecycle stage and direct command/event dispatch.
  /// </summary>
  Local,

  /// <summary>
  /// Message is being published from the local service (outbox).
  /// Distribute lifecycle stages fire when the message is written to the outbox table
  /// and sent to the transport.
  /// </summary>
  Outbox,

  /// <summary>
  /// Message was received from an external transport (inbox).
  /// Distribute lifecycle stages fire when the message is received from the transport
  /// and written to the inbox table.
  /// </summary>
  Inbox
}
