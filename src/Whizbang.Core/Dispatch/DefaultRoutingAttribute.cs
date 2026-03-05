namespace Whizbang.Core.Dispatch;

/// <summary>
/// Specifies the default dispatch routing for a message type or receptor class.
/// </summary>
/// <remarks>
/// <para>
/// Apply this attribute to enforce routing policies:
/// </para>
/// <list type="bullet">
///   <item>
///     <b>On message types</b>: Defines where this message type should always be dispatched,
///     regardless of wrappers. This is the highest priority routing decision.
///   </item>
///   <item>
///     <b>On receptor classes</b>: Defines default routing for all messages returned by this receptor,
///     unless overridden by message-level attributes.
///   </item>
/// </list>
/// <para>
/// Priority order (highest to lowest):
/// </para>
/// <list type="number">
///   <item><b>Message type attribute</b> - enforced policy, cannot be overridden</item>
///   <item><b>Receptor class attribute</b> - applies to all returns from handler</item>
///   <item><b>Individual Routed&lt;T&gt; wrapper</b> - explicit per-item routing</item>
///   <item><b>Collection Routed&lt;T&gt; wrapper</b> - applies to all items in collection</item>
///   <item><b>System default</b> - DispatchMode.Outbox</item>
/// </list>
/// </remarks>
/// <example>
/// <code>
/// // Message type with enforced local routing (highest priority)
/// [DefaultRouting(DispatchMode.Local)]
/// public record CacheInvalidatedEvent : IEvent {
///   public required string Key { get; init; }
/// }
///
/// // Receptor with default local routing for all returns
/// [DefaultRouting(DispatchMode.Local)]
/// public class CacheManagementHandler : IReceptor&lt;InvalidateCacheCommand, CacheInvalidatedEvent&gt; {
///   public ValueTask&lt;CacheInvalidatedEvent&gt; HandleAsync(InvalidateCacheCommand cmd, CancellationToken ct) {
///     return ValueTask.FromResult(new CacheInvalidatedEvent { Key = cmd.CacheKey });
///   }
/// }
/// </code>
/// </example>
/// <docs>core-concepts/dispatcher#routed-message-cascading</docs>
/// <tests>tests/Whizbang.Core.Tests/Dispatch/DefaultRoutingAttributeTests.cs</tests>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct)]
public sealed class DefaultRoutingAttribute : Attribute {
  /// <summary>
  /// Gets the default dispatch mode for the decorated type.
  /// </summary>
  public DispatchMode Mode { get; }

  /// <summary>
  /// Initializes a new instance with the specified dispatch mode.
  /// </summary>
  /// <param name="mode">The default dispatch mode for the decorated type.</param>
  public DefaultRoutingAttribute(DispatchMode mode) {
    Mode = mode;
  }
}
