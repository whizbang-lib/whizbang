using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Whizbang.Core.Attributes;
using Whizbang.Core.Messaging;

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
public sealed class MessageTagProcessor : IMessageTagProcessor {
  private readonly TagOptions _options;
  private readonly Func<Type, object?>? _hookResolver;
  private readonly IServiceScopeFactory? _scopeFactory;

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
  /// Creates a new message tag processor with scope factory for resolving scoped hooks.
  /// </summary>
  /// <param name="options">Tag options containing hook registrations.</param>
  /// <param name="scopeFactory">Service scope factory for creating scopes to resolve hooks.</param>
  /// <remarks>
  /// Use this constructor when the processor is registered as Singleton but hooks need to be Scoped
  /// (e.g., for accessing DbContext). A new scope is created for each ProcessTagsAsync call.
  /// </remarks>
  public MessageTagProcessor(TagOptions options, IServiceScopeFactory scopeFactory) {
    _options = options ?? throw new ArgumentNullException(nameof(options));
    _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
  }

  /// <inheritdoc />
  public async ValueTask ProcessTagsAsync(
      object message,
      Type messageType,
      LifecycleStage stage,
      IReadOnlyDictionary<string, object?>? scope = null,
      CancellationToken ct = default) {
    Console.WriteLine($"[TAG PROCESSOR] ProcessTagsAsync called for {messageType.Name} at stage {stage}");

    // Early return if no hook resolver or scope factory configured
    if (_hookResolver is null && _scopeFactory is null) {
      Console.WriteLine($"[TAG PROCESSOR] No hook resolver or scope factory - returning early");
      return;
    }

    // Early return if no tags registered for this message type
    // Check before creating scope to avoid unnecessary scope creation
    var tags = MessageTagRegistry.GetTagsFor(messageType).ToList();
    Console.WriteLine($"[TAG PROCESSOR] Found {tags.Count} tag registrations for {messageType.Name}");
    if (tags.Count == 0) {
      return;
    }

    // If using scope factory, create a scope for this entire ProcessTagsAsync call
    // All hooks resolved during this call will share the same scope
    if (_scopeFactory is not null) {
      Console.WriteLine($"[TAG PROCESSOR] Using scope factory to create scope");
      await using var serviceScope = _scopeFactory.CreateAsyncScope();
      Func<Type, object?> scopedResolver = type => serviceScope.ServiceProvider.GetService(type);
      await _processAllTagsAsync(message, messageType, stage, scope, scopedResolver, ct);
    } else {
      Console.WriteLine($"[TAG PROCESSOR] Using direct hook resolver");
      await _processAllTagsAsync(message, messageType, stage, scope, _hookResolver!, ct);
    }
  }

  /// <summary>
  /// Processes all tags for a message using the provided hook resolver.
  /// </summary>
  private async ValueTask _processAllTagsAsync(
      object message,
      Type messageType,
      LifecycleStage stage,
      IReadOnlyDictionary<string, object?>? scope,
      Func<Type, object?> hookResolver,
      CancellationToken ct) {
    // Get tag registrations for this message type from the registry
    foreach (var registration in MessageTagRegistry.GetTagsFor(messageType)) {
      // Build payload using the pre-compiled builder
      var payload = registration.PayloadBuilder(message);

      // Get the attribute instance
      var attribute = registration.AttributeFactory();

      // Create context and invoke hooks for this attribute type
      await _processTagRegistrationAsync(message, messageType, attribute, payload, stage, scope, hookResolver, ct);
    }
  }

  /// <summary>
  /// Processes a single tag registration by creating context and invoking matching hooks.
  /// </summary>
  private async ValueTask _processTagRegistrationAsync(
      object message,
      Type messageType,
      MessageTagAttribute attribute,
      JsonElement payload,
      LifecycleStage stage,
      IReadOnlyDictionary<string, object?>? scope,
      Func<Type, object?> hookResolver,
      CancellationToken ct) {
    // Get hooks that match this attribute type AND the specified lifecycle stage
    var attributeType = attribute.GetType();
    var hooks = _options.GetHooksFor(attributeType, stage).ToList();
    Console.WriteLine($"[TAG PROCESSOR] Processing attribute {attributeType.Name} at stage {stage}, found {hooks.Count} hooks");

    var currentPayload = payload;

    foreach (var registration in hooks) {
      Console.WriteLine($"[TAG PROCESSOR] Resolving hook {registration.HookType.Name}");
      var hookInstance = hookResolver(registration.HookType);
      if (hookInstance is null) {
        Console.WriteLine($"[TAG PROCESSOR] Hook {registration.HookType.Name} resolved to NULL - skipping");
        continue;
      }
      Console.WriteLine($"[TAG PROCESSOR] Hook {registration.HookType.Name} resolved successfully");

      // Create context based on attribute type
      var hookContext = _createHookContextForAttribute(attribute, message, messageType, currentPayload, scope);
      Console.WriteLine($"[TAG PROCESSOR] Created hook context of type {hookContext.GetType().Name}");

      // Invoke the hook
      Console.WriteLine($"[TAG PROCESSOR] Invoking hook...");
      var result = await _invokeHookAsync(hookInstance, hookContext, registration.AttributeType, ct);
      Console.WriteLine($"[TAG PROCESSOR] Hook invocation complete, result: {(result.HasValue ? "modified payload" : "null")}");

      // Update payload if hook returned a modified one
      if (result.HasValue) {
        currentPayload = result.Value;
      }
    }
  }

  private static object _createHookContextForAttribute(
      MessageTagAttribute attribute,
      object message,
      Type messageType,
      JsonElement payload,
      IReadOnlyDictionary<string, object?>? scope) {
    // Create the appropriate typed context based on attribute type
    if (attribute is SignalTagAttribute notificationAttr) {
      return new TagContext<SignalTagAttribute> {
        Attribute = notificationAttr,
        Message = message,
        MessageType = messageType,
        Payload = payload,
        Scope = scope
      };
    }

    if (attribute is TelemetryTagAttribute telemetryAttr) {
      return new TagContext<TelemetryTagAttribute> {
        Attribute = telemetryAttr,
        Message = message,
        MessageType = messageType,
        Payload = payload,
        Scope = scope
      };
    }

    if (attribute is MetricTagAttribute metricAttr) {
      return new TagContext<MetricTagAttribute> {
        Attribute = metricAttr,
        Message = message,
        MessageType = messageType,
        Payload = payload,
        Scope = scope
      };
    }

    // Try dispatcher registry for custom attribute types
    var customContext = MessageTagHookDispatcherRegistry.TryCreateContext(
        attribute.GetType(), attribute, message, messageType, payload, scope);
    if (customContext is not null) {
      return customContext;
    }

    // Fallback to base MessageTagAttribute context
    return new TagContext<MessageTagAttribute> {
      Attribute = attribute,
      Message = message,
      MessageType = messageType,
      Payload = payload,
      Scope = scope
    };
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

    if (attributeType == typeof(SignalTagAttribute) &&
        hookInstance is IMessageTagHook<SignalTagAttribute> notificationHook &&
        context is TagContext<SignalTagAttribute> notificationContext) {
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

    // Try dispatcher registry for custom attribute types
    // Source-generated dispatchers handle AOT-compatible dispatch without reflection
    Console.WriteLine($"[TAG PROCESSOR] Trying dispatcher registry for {attributeType.Name}");
    var dispatchResult = await MessageTagHookDispatcherRegistry.TryDispatchAsync(
        hookInstance, context, attributeType, ct);
    Console.WriteLine($"[TAG PROCESSOR] Dispatcher registry result: {(dispatchResult.HasValue ? "success" : "null")}");
    return dispatchResult;
  }
}
