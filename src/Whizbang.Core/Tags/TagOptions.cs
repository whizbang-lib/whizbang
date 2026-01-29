using Whizbang.Core.Attributes;

namespace Whizbang.Core.Tags;

/// <summary>
/// Configuration options for the message tag system.
/// Supports fluent API for registering tag hooks.
/// </summary>
/// <remarks>
/// <para>
/// The tag system enables cross-cutting concerns like notifications, telemetry, and metrics
/// to be applied to messages through attributes. Hooks process tagged messages after
/// successful handling.
/// </para>
/// <para>
/// Hooks are executed in priority order (ascending: -100 → 500).
/// Use <see cref="UseHook{TAttribute,THook}(int)"/> to register hooks with explicit priorities.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// services.AddWhizbang(options => {
///   options.Tags.UseHook&lt;NotificationTagAttribute, SignalRNotificationHook&gt;();
///   options.Tags.UseHook&lt;TelemetryTagAttribute, OpenTelemetrySpanHook&gt;(priority: -10);
///   options.Tags.UseHook&lt;MetricTagAttribute, MetricsPublishHook&gt;(priority: 30);
/// });
/// </code>
/// </example>
/// <docs>core-concepts/message-tags#configuration</docs>
/// <tests>Whizbang.Core.Tests/Tags/TagOptionsTests.cs</tests>
public sealed class TagOptions {
  private readonly List<TagHookRegistration> _hookRegistrations = [];

  /// <summary>
  /// Gets the registered hook configurations.
  /// </summary>
  public IReadOnlyList<TagHookRegistration> HookRegistrations => _hookRegistrations;

  /// <summary>
  /// Registers a hook for processing messages tagged with the specified attribute type.
  /// </summary>
  /// <typeparam name="TAttribute">The tag attribute type to handle.</typeparam>
  /// <typeparam name="THook">The hook implementation type.</typeparam>
  /// <param name="priority">Execution priority. Lower values execute first. Default is -100.</param>
  /// <returns>This options instance for chaining.</returns>
  /// <example>
  /// <code>
  /// options.Tags.UseHook&lt;NotificationTagAttribute, SignalRNotificationHook&gt;();
  /// options.Tags.UseHook&lt;AuditTagAttribute, AuditLogHook&gt;(priority: -10);
  /// </code>
  /// </example>
  public TagOptions UseHook<TAttribute, THook>(int priority = -100)
    where TAttribute : MessageTagAttribute
    where THook : class, IMessageTagHook<TAttribute> {
    var registration = new TagHookRegistration(
      AttributeType: typeof(TAttribute),
      HookType: typeof(THook),
      Priority: priority
    );

    _hookRegistrations.Add(registration);
    return this;
  }

  /// <summary>
  /// Registers a hook that handles all tag types (base MessageTagAttribute).
  /// Useful for universal logging or tracing of all tagged messages.
  /// </summary>
  /// <typeparam name="THook">The hook implementation type.</typeparam>
  /// <param name="priority">Execution priority. Lower values execute first. Default is -100.</param>
  /// <returns>This options instance for chaining.</returns>
  /// <example>
  /// <code>
  /// // Log all tagged messages
  /// options.Tags.UseUniversalHook&lt;UniversalTagLoggerHook&gt;();
  /// </code>
  /// </example>
  public TagOptions UseUniversalHook<THook>(int priority = -100)
    where THook : class, IMessageTagHook<MessageTagAttribute> {
    return UseHook<MessageTagAttribute, THook>(priority);
  }

  /// <summary>
  /// Gets the hook registrations sorted by priority (ascending).
  /// Lower priority values execute first.
  /// </summary>
  /// <returns>Hook registrations in execution order.</returns>
  public IEnumerable<TagHookRegistration> GetHooksInExecutionOrder() {
    return _hookRegistrations.OrderBy(r => r.Priority);
  }

  /// <summary>
  /// Gets all hook registrations for a specific attribute type.
  /// </summary>
  /// <typeparam name="TAttribute">The attribute type to get hooks for.</typeparam>
  /// <returns>Hook registrations for the specified attribute type.</returns>
  public IEnumerable<TagHookRegistration> GetHooksFor<TAttribute>()
    where TAttribute : MessageTagAttribute {
    var targetType = typeof(TAttribute);
    return _hookRegistrations
      .Where(r => r.AttributeType == targetType || r.AttributeType == typeof(MessageTagAttribute))
      .OrderBy(r => r.Priority);
  }

  /// <summary>
  /// Gets all hook registrations for a specific attribute type (non-generic).
  /// </summary>
  /// <param name="attributeType">The attribute type to get hooks for.</param>
  /// <returns>Hook registrations for the specified attribute type.</returns>
  public IEnumerable<TagHookRegistration> GetHooksFor(Type attributeType) {
    ArgumentNullException.ThrowIfNull(attributeType);
    return _hookRegistrations
      .Where(r => r.AttributeType == attributeType || r.AttributeType == typeof(MessageTagAttribute))
      .OrderBy(r => r.Priority);
  }
}
