using System.Diagnostics.CodeAnalysis;
using Whizbang.Core.Attributes;
using Whizbang.Core.Messaging;

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
///   options.Tags.UseHook&lt;SignalTagAttribute, SignalRNotificationHook&gt;();
///   options.Tags.UseHook&lt;TelemetryTagAttribute, OpenTelemetrySpanHook&gt;(priority: -10);
///   options.Tags.UseHook&lt;MetricTagAttribute, MetricsPublishHook&gt;(priority: 30);
/// });
/// </code>
/// </example>
/// <docs>fundamentals/messages/message-tags#configuration</docs>
/// <tests>Whizbang.Core.Tests/Tags/TagOptionsTests.cs</tests>
public sealed class TagOptions {
  private readonly List<TagHookRegistration> _hookRegistrations = [];
  private readonly Dictionary<string, CoalescePolicyOptions> _coalesceBindings = new(StringComparer.Ordinal);
  private readonly Dictionary<string, string> _routeNamespaceBindings = new(StringComparer.Ordinal);

  /// <summary>
  /// Gets the registered hook configurations.
  /// </summary>
  public IReadOnlyList<TagHookRegistration> HookRegistrations => _hookRegistrations;

  /// <summary>
  /// Gets the coalesce policies bound per tag. See <see cref="Coalesce(string, Action{CoalescePolicyOptions})"/>.
  /// </summary>
  public IReadOnlyDictionary<string, CoalescePolicyOptions> CoalesceBindings => _coalesceBindings;

  /// <summary>
  /// Gets the TransportNamespace keys bound per tag. See <see cref="RouteNamespace(string, string)"/>.
  /// </summary>
  public IReadOnlyDictionary<string, string> RouteNamespaceBindings => _routeNamespaceBindings;

  /// <summary>
  /// Size in bytes at which the tag processor logs a warning for a built payload.
  /// Default is 8192 (8 KiB). Set to <c>null</c> to disable the warning.
  /// </summary>
  /// <remarks>
  /// Measured against the raw JSON text length. Notification payloads are pushed
  /// over real-time transports like SignalR — oversized payloads indicate the
  /// author likely forgot to narrow <see cref="Attributes.MessageTagAttribute.Properties"/>.
  /// </remarks>
  public int? PayloadSizeWarningThresholdBytes { get; set; } = 8192;

  /// <summary>
  /// Size in bytes above which the tag processor throws an
  /// <see cref="InvalidOperationException"/> instead of dispatching the payload.
  /// Default is <c>null</c> (disabled). Set this in production to catch runaway payloads.
  /// </summary>
  /// <remarks>
  /// Evaluated before hooks run; when tripped, no hook fires for the offending tag.
  /// </remarks>
  public int? PayloadSizeErrorThresholdBytes { get; set; }

  /// <summary>
  /// Registers a hook for processing messages tagged with the specified attribute type.
  /// </summary>
  /// <typeparam name="TAttribute">The tag attribute type to handle.</typeparam>
  /// <typeparam name="THook">The hook implementation type.</typeparam>
  /// <param name="priority">Execution priority. Lower values execute first. Default is -100.</param>
  /// <param name="fireAt">Lifecycle stage when this hook fires. Null (default) means fire at all stages.</param>
  /// <returns>This options instance for chaining.</returns>
  /// <example>
  /// <code>
  /// options.Tags.UseHook&lt;SignalTagAttribute, SignalRNotificationHook&gt;();
  /// options.Tags.UseHook&lt;AuditTagAttribute, AuditLogHook&gt;(priority: -10);
  /// options.Tags.UseHook&lt;NotificationTagAttribute, NotificationHook&gt;(fireAt: LifecycleStage.PostPerspectiveInline);
  /// </code>
  /// </example>
  public TagOptions UseHook<TAttribute, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] THook>(
      int priority = -100,
      LifecycleStage? fireAt = null)
    where TAttribute : MessageTagAttribute
    where THook : class, IMessageTagHook<TAttribute> {
    var registration = new TagHookRegistration(
      AttributeType: typeof(TAttribute),
      HookType: typeof(THook),
      Priority: priority,
      FireAt: fireAt
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
  /// <param name="fireAt">Lifecycle stage when this hook fires. Null (default) means fire at all stages.</param>
  /// <returns>This options instance for chaining.</returns>
  /// <example>
  /// <code>
  /// // Log all tagged messages
  /// options.Tags.UseUniversalHook&lt;UniversalTagLoggerHook&gt;();
  /// </code>
  /// </example>
  public TagOptions UseUniversalHook<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] THook>(
      int priority = -100,
      LifecycleStage? fireAt = null)
    where THook : class, IMessageTagHook<MessageTagAttribute> {
    return UseHook<MessageTagAttribute, THook>(priority, fireAt);
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
  /// Gets all hook registrations for a specific attribute type that fire at the specified lifecycle stage.
  /// </summary>
  /// <typeparam name="TAttribute">The attribute type to get hooks for.</typeparam>
  /// <param name="stage">The lifecycle stage to filter by.</param>
  /// <returns>Hook registrations for the specified attribute type and stage.</returns>
  public IEnumerable<TagHookRegistration> GetHooksFor<TAttribute>(LifecycleStage stage)
    where TAttribute : MessageTagAttribute {
    var targetType = typeof(TAttribute);
    return _hookRegistrations
      .Where(r => (r.AttributeType == targetType || r.AttributeType == typeof(MessageTagAttribute)) && (r.FireAt is null || r.FireAt == stage))
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

  /// <summary>
  /// Gets all hook registrations for a specific attribute type that fire at the specified lifecycle stage (non-generic).
  /// </summary>
  /// <param name="attributeType">The attribute type to get hooks for.</param>
  /// <param name="stage">The lifecycle stage to filter by.</param>
  /// <returns>Hook registrations for the specified attribute type and stage.</returns>
  public IEnumerable<TagHookRegistration> GetHooksFor(Type attributeType, LifecycleStage stage) {
    ArgumentNullException.ThrowIfNull(attributeType);
    return _hookRegistrations
      .Where(r => (r.AttributeType == attributeType || r.AttributeType == typeof(MessageTagAttribute)) && (r.FireAt is null || r.FireAt == stage))
      .OrderBy(r => r.Priority);
  }

  /// <summary>
  /// Registers a hook using an existing registration record.
  /// Used internally for merging hooks when AddWhizbang() is called multiple times.
  /// </summary>
  /// <param name="registration">The hook registration to add.</param>
  /// <returns>This options instance for chaining.</returns>
  internal TagOptions UseHookRegistration(TagHookRegistration registration) {
    _hookRegistrations.Add(registration);
    return this;
  }

  /// <summary>
  /// Binds a coalesce policy to <paramref name="tag"/>: outbox singles whose message type
  /// carries the tag are folded by the sliding-window batcher into composite envelopes instead
  /// of shipping individually. Tags classify; policies bind — no attribute or field on the
  /// message types themselves.
  /// </summary>
  /// <remarks>
  /// Registration is <b>last-wins per tag</b>: a later binding for the same tag replaces the
  /// earlier one entirely. That is the shipped-default-override mechanism — built-in bindings
  /// (e.g. the audit binding registered by <c>EnableAudit()</c>) are registered first, and a
  /// host binding for the same tag replaces them.
  /// </remarks>
  /// <param name="tag">The tag string the policy binds to (e.g. <c>SystemTags.Audit</c>).</param>
  /// <param name="configure">Configures the policy knobs; unset knobs keep the defaults (15 / 120 / 500 / Independent).</param>
  /// <returns>This options instance for chaining.</returns>
  /// <example>
  /// <code>
  /// services.AddWhizbang(options => {
  ///   options.Tags.Coalesce("notification-digest", c => {
  ///     c.SlideSeconds = 15;
  ///     c.MaxDelaySeconds = 120;
  ///     c.MaxBatchCount = 500;
  ///   });
  /// });
  /// </code>
  /// </example>
  /// <docs>fundamentals/messages/message-tags#coalescing</docs>
  /// <tests>tests/Whizbang.Core.Tests/Tags/TagOptionsCoalesceTests.cs:Coalesce_RegistersBindingForTagAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Tags/TagOptionsCoalesceTests.cs:Coalesce_LastWinsPerTag_ReplacesNotMergesAsync</tests>
  public TagOptions Coalesce(string tag, Action<CoalescePolicyOptions> configure) {
    ArgumentException.ThrowIfNullOrWhiteSpace(tag);
    ArgumentNullException.ThrowIfNull(configure);

    var policy = new CoalescePolicyOptions();
    configure(policy);
    _coalesceBindings[tag] = policy;  // last-wins per tag
    return this;
  }

  /// <summary>
  /// Registers a built-in coalesce binding with add-if-absent semantics: it behaves as
  /// "registered FIRST" regardless of the order the host composed its registrations in, so a
  /// host binding for the same tag always survives. Framework use only (e.g. the audit
  /// binding derived from <c>SystemEventOptions</c>).
  /// </summary>
  /// <param name="tag">The tag string the built-in policy binds to.</param>
  /// <param name="policy">The built-in policy instance.</param>
  /// <returns>This options instance for chaining.</returns>
  internal TagOptions UseCoalesceBindingDefault(string tag, CoalescePolicyOptions policy) {
    ArgumentException.ThrowIfNullOrWhiteSpace(tag);
    ArgumentNullException.ThrowIfNull(policy);

    _coalesceBindings.TryAdd(tag, policy);
    return this;
  }

  /// <summary>
  /// Overwrites (or adds) a coalesce binding from an existing policy instance.
  /// Used internally for merging bindings when AddWhizbang() is called multiple times —
  /// last-wins per tag applies across calls too.
  /// </summary>
  /// <param name="tag">The tag string the policy binds to.</param>
  /// <param name="policy">The policy instance to bind.</param>
  /// <returns>This options instance for chaining.</returns>
  internal TagOptions UseCoalesceBinding(string tag, CoalescePolicyOptions policy) {
    ArgumentException.ThrowIfNullOrWhiteSpace(tag);
    ArgumentNullException.ThrowIfNull(policy);

    _coalesceBindings[tag] = policy;
    return this;
  }

  /// <summary>
  /// Binds a TransportNamespace to <paramref name="tag"/> (transport traffic classes, topology
  /// arc phase 8): messages whose type carries the tag are published through — and consumed
  /// from — the broker namespace registered under
  /// <paramref name="transportNamespaceKey"/> in the transport's namespace connection map.
  /// Tags classify; policies bind — no attribute or field on the message types themselves, and
  /// the same tag may carry BOTH a coalesce policy and a routing binding (additive policies,
  /// one vocabulary).
  /// </summary>
  /// <remarks>
  /// <para>
  /// Registration is <b>last-wins per tag</b>, exactly like
  /// <see cref="Coalesce(string, Action{CoalescePolicyOptions})"/>. Unmatched traffic — and
  /// every message on a host with no routing bindings — uses the
  /// <see cref="Transports.TransportNamespaces.DefaultKey"/> namespace, so single-namespace
  /// hosts are unaffected. A key the transport has no connection for also falls back to
  /// default (graceful degradation: routing bindings without a multi-namespace deployment
  /// change nothing but a metadata stamp).
  /// </para>
  /// <para>
  /// The <c>sys-</c> tag prefix stays reserved: binding a sys-* tag the framework does not
  /// ship fails startup validation (<see cref="TagPolicyValidator"/>), mirroring the coalesce
  /// rule. Bindings are also configuration-bindable at
  /// <c>Whizbang:Tags:RouteNamespace:&lt;tag&gt;</c> (configuration wins over code).
  /// </para>
  /// </remarks>
  /// <param name="tag">The tag string the routing binds to (e.g. <c>"sys-control"</c>).</param>
  /// <param name="transportNamespaceKey">The TransportNamespace key (e.g. <c>"control"</c>).</param>
  /// <returns>This options instance for chaining.</returns>
  /// <example>
  /// <code>
  /// services.AddWhizbang(options => {
  ///   options.Tags.RouteNamespace("sys-control", "control");
  ///   options.Tags.RouteNamespace("bulk-import", "bulk");
  /// });
  /// </code>
  /// </example>
  /// <docs>fundamentals/messages/message-tags#transport-namespace-routing</docs>
  /// <tests>tests/Whizbang.Core.Tests/Tags/TagOptionsRouteNamespaceTests.cs:RouteNamespace_RegistersBindingForTagAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Tags/TagOptionsRouteNamespaceTests.cs:RouteNamespace_LastWinsPerTagAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Tags/TagOptionsRouteNamespaceTests.cs:RouteNamespace_AndCoalesce_AreAdditiveOnOneTagAsync</tests>
  public TagOptions RouteNamespace(string tag, string transportNamespaceKey) {
    ArgumentException.ThrowIfNullOrWhiteSpace(tag);
    ArgumentException.ThrowIfNullOrWhiteSpace(transportNamespaceKey);

    _routeNamespaceBindings[tag] = transportNamespaceKey;  // last-wins per tag
    return this;
  }

  /// <summary>
  /// Overwrites (or adds) a routing binding. Used internally for merging bindings when
  /// AddWhizbang() is called multiple times and for the configuration binder — last-wins per
  /// tag applies across both, consistent with <see cref="RouteNamespace"/>.
  /// </summary>
  /// <param name="tag">The tag string the routing binds to.</param>
  /// <param name="transportNamespaceKey">The TransportNamespace key to bind.</param>
  /// <returns>This options instance for chaining.</returns>
  internal TagOptions UseRouteNamespaceBinding(string tag, string transportNamespaceKey) {
    ArgumentException.ThrowIfNullOrWhiteSpace(tag);
    ArgumentException.ThrowIfNullOrWhiteSpace(transportNamespaceKey);

    _routeNamespaceBindings[tag] = transportNamespaceKey;
    return this;
  }
}
