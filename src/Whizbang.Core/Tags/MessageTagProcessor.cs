using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Whizbang.Core.Attributes;
using Whizbang.Core.Messaging;
using Whizbang.Core.Security;

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
/// <docs>fundamentals/messages/message-tags#processing</docs>
/// <tests>Whizbang.Core.Tests/Tags/MessageTagProcessorTests.cs</tests>
public sealed class MessageTagProcessor : IMessageTagProcessor {
  private readonly TagOptions _options;
  private readonly Func<Type, object?>? _hookResolver;
  private readonly IServiceScopeFactory? _scopeFactory;

  // Lazy-resolved logger for diagnostic tracing (avoids constructor changes)
#pragma warning disable S4487 // Backing field for TagLogger lazy property
  private ILogger? _tagLogger;
#pragma warning restore S4487
#pragma warning disable IDE1006 // Naming rule - property follows internal naming convention
  private ILogger TagLogger => _tagLogger ??= _scopeFactory?.CreateScope().ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger("Whizbang.Core.Tags.MessageTagProcessor") ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
#pragma warning restore IDE1006

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
      IScopeContext? scope = null,
      CancellationToken ct = default) {
#pragma warning disable CA1848 // Diagnostic logging - performance not critical
    if (TagLogger.IsEnabled(LogLevel.Debug)) {
      TagLogger.LogDebug("[TAG PROCESSOR] ProcessTagsAsync called for {MessageType} at stage {Stage}", messageType.Name, stage);
    }

    // Early return if no hook resolver or scope factory configured
    if (_hookResolver is null && _scopeFactory is null) {
      if (TagLogger.IsEnabled(LogLevel.Debug)) {
        TagLogger.LogDebug("[TAG PROCESSOR] No hook resolver or scope factory - returning early");
      }
      return;
    }

    // Early return if no tags registered for this message type
    // Check before creating scope to avoid unnecessary scope creation
    var tags = MessageTagRegistry.GetTagsFor(messageType).ToList();
    if (TagLogger.IsEnabled(LogLevel.Debug)) {
      TagLogger.LogDebug("[TAG PROCESSOR] Found {TagCount} tag registrations for {MessageType}", tags.Count, messageType.Name);
    }
    if (tags.Count == 0) {
      return;
    }

    // If using scope factory, create a scope for this entire ProcessTagsAsync call
    // All hooks resolved during this call will share the same scope
    if (_scopeFactory is not null) {
      if (TagLogger.IsEnabled(LogLevel.Debug)) {
        TagLogger.LogDebug("[TAG PROCESSOR] Using scope factory to create scope");
      }
      await using var serviceScope = _scopeFactory.CreateAsyncScope();
      Func<Type, object?> scopedResolver = type => serviceScope.ServiceProvider.GetService(type);
      await _processAllTagsAsync(message, messageType, stage, scope, scopedResolver, ct);
    } else {
      if (TagLogger.IsEnabled(LogLevel.Debug)) {
        TagLogger.LogDebug("[TAG PROCESSOR] Using direct hook resolver");
      }
      await _processAllTagsAsync(message, messageType, stage, scope, _hookResolver!, ct);
    }
#pragma warning restore CA1848
  }

  /// <summary>
  /// Processes all tags for a message using the provided hook resolver.
  /// </summary>
  private async ValueTask _processAllTagsAsync(
      object message,
      Type messageType,
      LifecycleStage stage,
      IScopeContext? scope,
      Func<Type, object?> hookResolver,
      CancellationToken ct) {
    // Get tag registrations for this message type from the registry
    foreach (var registration in MessageTagRegistry.GetTagsFor(messageType)) {
      // Build payload using the pre-compiled builder
      var payload = registration.PayloadBuilder(message);

      // Get the attribute instance
      var attribute = registration.AttributeFactory();

      // Create context and invoke hooks for this attribute type (pass stage so hooks can filter)
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
      IScopeContext? scope,
      Func<Type, object?> hookResolver,
      CancellationToken ct) {
    var attributeType = attribute.GetType();
    var hooks = _options.GetHooksFor(attributeType).ToList();
#pragma warning disable CA1848 // Diagnostic logging - performance not critical
    if (TagLogger.IsEnabled(LogLevel.Debug)) {
      TagLogger.LogDebug("[TAG PROCESSOR] Processing attribute {AttributeType} at stage {Stage}, found {HookCount} hooks", attributeType.Name, stage, hooks.Count);
    }

    var currentPayload = payload;

    foreach (var registration in hooks) {
      var hookInstance = _resolveHookInstance(registration, hookResolver);
      if (hookInstance is null) {
        continue;
      }

      var hookContext = _createContextForRegistration(registration, attribute, message, messageType, currentPayload, scope, stage);
      currentPayload = await _invokeAndUpdatePayloadAsync(hookInstance, hookContext, registration.AttributeType, currentPayload, ct);
    }
#pragma warning restore CA1848
  }

  private object? _resolveHookInstance(TagHookRegistration registration, Func<Type, object?> hookResolver) {
#pragma warning disable CA1848
    if (TagLogger.IsEnabled(LogLevel.Debug)) {
      TagLogger.LogDebug("[TAG PROCESSOR] Resolving hook {HookType}", registration.HookType.Name);
    }
    var hookInstance = hookResolver(registration.HookType);
    if (hookInstance is null) {
      if (TagLogger.IsEnabled(LogLevel.Debug)) {
        TagLogger.LogDebug("[TAG PROCESSOR] Hook {HookType} resolved to NULL - skipping", registration.HookType.Name);
      }
      return null;
    }
    if (TagLogger.IsEnabled(LogLevel.Debug)) {
      TagLogger.LogDebug("[TAG PROCESSOR] Hook {HookType} resolved successfully", registration.HookType.Name);
    }
#pragma warning restore CA1848
    return hookInstance;
  }

  private static object _createContextForRegistration(
      TagHookRegistration registration,
      MessageTagAttribute attribute,
      object message,
      Type messageType,
      JsonElement currentPayload,
      IScopeContext? scope,
      LifecycleStage stage) {
    // For universal hooks (AttributeType == MessageTagAttribute), we need TagContext<MessageTagAttribute>
    // For typed hooks, we use the actual attribute type for the context
    var contextAttributeType = registration.AttributeType == typeof(MessageTagAttribute) ? typeof(MessageTagAttribute) : attribute.GetType();
    return contextAttributeType == typeof(MessageTagAttribute)
      ? new TagContext<MessageTagAttribute> {
        Attribute = attribute,
        Message = message,
        MessageType = messageType,
        Payload = currentPayload,
        Scope = scope,
        Stage = stage
      }
      : _createHookContextForAttribute(attribute, message, messageType, currentPayload, scope, stage);
  }

  private async ValueTask<JsonElement> _invokeAndUpdatePayloadAsync(
      object hookInstance,
      object hookContext,
      Type attributeType,
      JsonElement currentPayload,
      CancellationToken ct) {
#pragma warning disable CA1848
    if (TagLogger.IsEnabled(LogLevel.Debug)) {
      TagLogger.LogDebug("[TAG PROCESSOR] Created hook context of type {ContextType}", hookContext.GetType().Name);
      TagLogger.LogDebug("[TAG PROCESSOR] Invoking hook...");
    }
    var result = await _invokeHookAsync(hookInstance, hookContext, attributeType, ct);
    if (TagLogger.IsEnabled(LogLevel.Debug)) {
      TagLogger.LogDebug("[TAG PROCESSOR] Hook invocation complete, result: {Result}", result.HasValue ? "modified payload" : "null");
    }
#pragma warning restore CA1848
    return result ?? currentPayload;
  }

  private static object _createHookContextForAttribute(
      MessageTagAttribute attribute,
      object message,
      Type messageType,
      JsonElement payload,
      IScopeContext? scope,
      LifecycleStage stage) {
    // Create the appropriate typed context based on attribute type
    if (attribute is SignalTagAttribute notificationAttr) {
      return new TagContext<SignalTagAttribute> {
        Attribute = notificationAttr,
        Message = message,
        MessageType = messageType,
        Payload = payload,
        Scope = scope,
        Stage = stage
      };
    }

    if (attribute is TelemetryTagAttribute telemetryAttr) {
      return new TagContext<TelemetryTagAttribute> {
        Attribute = telemetryAttr,
        Message = message,
        MessageType = messageType,
        Payload = payload,
        Scope = scope,
        Stage = stage
      };
    }

    if (attribute is MetricTagAttribute metricAttr) {
      return new TagContext<MetricTagAttribute> {
        Attribute = metricAttr,
        Message = message,
        MessageType = messageType,
        Payload = payload,
        Scope = scope,
        Stage = stage
      };
    }

    // Try dispatcher registry for custom attribute types
    var customContext = MessageTagHookDispatcherRegistry.TryCreateContext(
        attribute.GetType(), attribute, message, messageType, payload, scope, stage);
    if (customContext is not null) {
      return customContext;
    }

    // Fallback to base MessageTagAttribute context
    return new TagContext<MessageTagAttribute> {
      Attribute = attribute,
      Message = message,
      MessageType = messageType,
      Payload = payload,
      Scope = scope,
      Stage = stage
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
        Scope = originalContext.Scope,
        Stage = originalContext.Stage
      };
    }

    // Otherwise create context for the specific attribute type
    return new TagContext<TAttribute> {
      Attribute = originalContext.Attribute,
      Message = originalContext.Message,
      MessageType = originalContext.MessageType,
      Payload = currentPayload,
      Scope = originalContext.Scope,
      Stage = originalContext.Stage
    };
  }

  private async ValueTask<JsonElement?> _invokeHookAsync(
      object hookInstance,
      object context,
      Type attributeType,
      CancellationToken ct) {
    // Try known built-in attribute types first
    var builtInResult = await _tryInvokeBuiltInHookAsync(hookInstance, context, attributeType, ct);
    if (builtInResult.Matched) {
      return builtInResult.Result;
    }

    // Try dispatcher registry for custom attribute types
#pragma warning disable CA1848 // Diagnostic logging - performance not critical
    if (TagLogger.IsEnabled(LogLevel.Debug)) {
      TagLogger.LogDebug("[TAG PROCESSOR] Trying dispatcher registry for {AttributeType}", attributeType.Name);
    }
    var dispatchResult = await MessageTagHookDispatcherRegistry.TryDispatchAsync(
        hookInstance, context, attributeType, ct);
    if (TagLogger.IsEnabled(LogLevel.Debug)) {
      TagLogger.LogDebug("[TAG PROCESSOR] Dispatcher registry result: {Result}", dispatchResult.HasValue ? "success" : "null");
    }
#pragma warning restore CA1848
    return dispatchResult;
  }

  private static async ValueTask<(bool Matched, JsonElement? Result)> _tryInvokeBuiltInHookAsync(
      object hookInstance,
      object context,
      Type attributeType,
      CancellationToken ct) {
    if (attributeType == typeof(MessageTagAttribute) &&
        hookInstance is IMessageTagHook<MessageTagAttribute> universalHook &&
        context is TagContext<MessageTagAttribute> universalContext) {
      return (true, await universalHook.OnTaggedMessageAsync(universalContext, ct));
    }

    if (attributeType == typeof(SignalTagAttribute) &&
        hookInstance is IMessageTagHook<SignalTagAttribute> notificationHook &&
        context is TagContext<SignalTagAttribute> notificationContext) {
      return (true, await notificationHook.OnTaggedMessageAsync(notificationContext, ct));
    }

    if (attributeType == typeof(TelemetryTagAttribute) &&
        hookInstance is IMessageTagHook<TelemetryTagAttribute> telemetryHook &&
        context is TagContext<TelemetryTagAttribute> telemetryContext) {
      return (true, await telemetryHook.OnTaggedMessageAsync(telemetryContext, ct));
    }

    if (attributeType == typeof(MetricTagAttribute) &&
        hookInstance is IMessageTagHook<MetricTagAttribute> metricHook &&
        context is TagContext<MetricTagAttribute> metricContext) {
      return (true, await metricHook.OnTaggedMessageAsync(metricContext, ct));
    }

    return (false, null);
  }
}
