using System.Text.Json;
using Whizbang.Core.Attributes;
using Whizbang.Core.Security;

namespace Whizbang.Core.Tags;

/// <summary>
/// Dispatches hook invocations for custom MessageTagAttribute types.
/// Implementations are source-generated per consuming assembly.
/// </summary>
/// <remarks>
/// <para>
/// Built-in Whizbang attribute types (SignalTagAttribute, TelemetryTagAttribute, MetricTagAttribute)
/// are handled directly by MessageTagProcessor with fast paths.
/// </para>
/// <para>
/// Custom attribute types require generated dispatchers because the processor cannot know
/// about all possible attribute types at compile time. The MessageTagDiscoveryGenerator
/// generates an implementation for each assembly that defines custom attributes.
/// </para>
/// </remarks>
/// <docs>core-concepts/message-tags#dispatcher-registry</docs>
public interface IMessageTagHookDispatcher {
  /// <summary>
  /// Attempts to create a typed TagContext for the given attribute type.
  /// </summary>
  /// <param name="attributeType">The attribute type to create context for.</param>
  /// <param name="attribute">The attribute instance.</param>
  /// <param name="message">The message being processed.</param>
  /// <param name="messageType">The type of the message.</param>
  /// <param name="payload">The serialized payload.</param>
  /// <param name="scope">Optional security scope context.</param>
  /// <returns>The typed context, or null if this dispatcher doesn't handle the attribute type.</returns>
  object? TryCreateContext(
      Type attributeType,
      MessageTagAttribute attribute,
      object message,
      Type messageType,
      JsonElement payload,
      IScopeContext? scope);

  /// <summary>
  /// Attempts to invoke a hook for the given attribute type.
  /// </summary>
  /// <param name="hookInstance">The hook instance to invoke.</param>
  /// <param name="context">The typed context (from TryCreateContext).</param>
  /// <param name="attributeType">The attribute type being processed.</param>
  /// <param name="ct">Cancellation token.</param>
  /// <returns>The modified payload, or null if not modified or not handled.</returns>
  ValueTask<JsonElement?> TryDispatchAsync(
      object hookInstance,
      object context,
      Type attributeType,
      CancellationToken ct);
}
