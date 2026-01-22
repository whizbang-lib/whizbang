using System.Text.Json;
using Whizbang.Core.Attributes;

namespace Whizbang.Core.Tags;

/// <summary>
/// Orchestrates the execution of message tag hooks.
/// Invokes registered hooks in priority order for tagged messages.
/// </summary>
/// <remarks>
/// <para>
/// The processor resolves hooks from <see cref="TagOptions"/> and executes them
/// in ascending priority order. Lower priority values execute first (-100 → 500).
/// </para>
/// <para>
/// Hooks can optionally modify the payload by returning a new <see cref="JsonElement"/>.
/// Modified payloads are passed to subsequent hooks in the chain.
/// </para>
/// </remarks>
/// <docs>core-concepts/message-tags#processing</docs>
/// <tests>Whizbang.Core.Tests/Tags/MessageTagProcessorTests.cs</tests>
public sealed class MessageTagProcessor {
  private readonly TagOptions _options;
  private readonly Func<Type, object?>? _hookResolver;

  /// <summary>
  /// Creates a new message tag processor.
  /// </summary>
  /// <param name="options">Tag options containing hook registrations.</param>
  /// <param name="hookResolver">Optional resolver for hook instances. If null, hooks are not invoked.</param>
  public MessageTagProcessor(TagOptions options, Func<Type, object?>? hookResolver = null) {
    _options = options ?? throw new ArgumentNullException(nameof(options));
    _hookResolver = hookResolver;
  }

  /// <summary>
  /// Processes a tagged message by invoking all matching hooks in priority order.
  /// </summary>
  /// <typeparam name="TAttribute">The tag attribute type.</typeparam>
  /// <param name="context">The tag context with message and attribute information.</param>
  /// <param name="ct">Cancellation token.</param>
  /// <returns>A task representing the asynchronous operation.</returns>
  public async ValueTask ProcessAsync<TAttribute>(
      TagContext<TAttribute> context,
      CancellationToken ct)
      where TAttribute : MessageTagAttribute {
    if (_hookResolver is null) {
      return;
    }

    var hooks = _options.GetHooksFor<TAttribute>();
    var currentPayload = context.Payload;

    foreach (var registration in hooks) {
      var hookInstance = _hookResolver(registration.HookType);
      if (hookInstance is null) {
        continue;
      }

      // Create context with current payload
      var hookContext = _createHookContext(context, currentPayload, registration.AttributeType);
      var result = await _invokeHookAsync(hookInstance, hookContext, registration.AttributeType, ct);

      // Update payload if hook returned a modified one
      if (result.HasValue) {
        currentPayload = result.Value;
      }
    }
  }

  private static object _createHookContext<TAttribute>(
      TagContext<TAttribute> originalContext,
      JsonElement currentPayload,
      Type attributeType)
      where TAttribute : MessageTagAttribute {
    // If hook is for MessageTagAttribute (universal), create that context type
    if (attributeType == typeof(MessageTagAttribute)) {
      return new TagContext<MessageTagAttribute> {
        Attribute = originalContext.Attribute,
        Message = originalContext.Message,
        MessageType = originalContext.MessageType,
        Payload = currentPayload,
        Scope = originalContext.Scope
      };
    }

    // Otherwise create context for the specific attribute type
    return new TagContext<TAttribute> {
      Attribute = originalContext.Attribute,
      Message = originalContext.Message,
      MessageType = originalContext.MessageType,
      Payload = currentPayload,
      Scope = originalContext.Scope
    };
  }

  private static async ValueTask<JsonElement?> _invokeHookAsync(
      object hookInstance,
      object context,
      Type attributeType,
      CancellationToken ct) {
    // Invoke the hook using the correct generic interface
    // This is a controlled set of known types, not arbitrary reflection
    if (attributeType == typeof(MessageTagAttribute) &&
        hookInstance is IMessageTagHook<MessageTagAttribute> universalHook &&
        context is TagContext<MessageTagAttribute> universalContext) {
      return await universalHook.OnTaggedMessageAsync(universalContext, ct);
    }

    if (attributeType == typeof(NotificationTagAttribute) &&
        hookInstance is IMessageTagHook<NotificationTagAttribute> notificationHook &&
        context is TagContext<NotificationTagAttribute> notificationContext) {
      return await notificationHook.OnTaggedMessageAsync(notificationContext, ct);
    }

    if (attributeType == typeof(TelemetryTagAttribute) &&
        hookInstance is IMessageTagHook<TelemetryTagAttribute> telemetryHook &&
        context is TagContext<TelemetryTagAttribute> telemetryContext) {
      return await telemetryHook.OnTaggedMessageAsync(telemetryContext, ct);
    }

    if (attributeType == typeof(MetricTagAttribute) &&
        hookInstance is IMessageTagHook<MetricTagAttribute> metricHook &&
        context is TagContext<MetricTagAttribute> metricContext) {
      return await metricHook.OnTaggedMessageAsync(metricContext, ct);
    }

    // For other attribute types, we'd need the source generator to generate the dispatch code
    // This provides the built-in tag types without reflection
    return null;
  }
}
