namespace Whizbang.Core.SystemEvents;

/// <summary>
/// Configuration options for system events.
/// Controls which system events are enabled and their transport behavior.
/// </summary>
/// <remarks>
/// <para>
/// System events are opt-in per host. Different services may need different system events:
/// </para>
/// <list type="bullet">
///   <item><b>BFF</b>: Enable audit for compliance, receives events from all domains</item>
///   <item><b>Worker services</b>: May only need error tracking, not full audit</item>
/// </list>
/// <para>
/// Use <see cref="LocalOnly"/> to prevent system events from being published to the outbox
/// or received from the inbox. This prevents duplicate auditing when multiple hosts have
/// audit enabled - each host audits events it processes, but doesn't rebroadcast.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// services.AddWhizbang(options => {
///   options.SystemEvents.EnableAudit();
///   options.SystemEvents.LocalOnly = true; // Don't broadcast to other services
/// });
/// </code>
/// </example>
/// <docs>core-concepts/system-events#configuration</docs>
public sealed class SystemEventOptions {
  /// <summary>
  /// When <c>true</c>, system events are stored locally but NOT published to the outbox
  /// or received from the inbox. This prevents duplicate auditing across services.
  /// Default is <c>true</c>.
  /// </summary>
  /// <remarks>
  /// <para>
  /// <b>Why LocalOnly?</b>
  /// </para>
  /// <para>
  /// Consider: BFF receives events from Orders and Users services. If audit is enabled
  /// on both BFF and Users service:
  /// </para>
  /// <list type="bullet">
  ///   <item>Users service audits UserCreated locally</item>
  ///   <item>BFF receives UserCreated, also audits it locally</item>
  ///   <item>Without LocalOnly, both would broadcast EventAudited, causing duplication</item>
  /// </list>
  /// <para>
  /// With <c>LocalOnly = true</c>, each service audits what it processes, but audit
  /// events stay local - no network traffic, no duplication.
  /// </para>
  /// </remarks>
  public bool LocalOnly { get; set; } = true;

  /// <summary>
  /// Enables <see cref="EventAudited"/> system events.
  /// When enabled, an EventAudited is emitted for each domain event appended.
  /// </summary>
  public bool EventAuditEnabled { get; private set; }

  /// <summary>
  /// Enables <see cref="CommandAudited"/> system events.
  /// When enabled, a CommandAudited is emitted for each command processed by a receptor.
  /// </summary>
  public bool CommandAuditEnabled { get; private set; }

  /// <summary>
  /// Returns true if either event or command auditing is enabled.
  /// </summary>
  public bool AuditEnabled => EventAuditEnabled || CommandAuditEnabled;

  /// <summary>
  /// Enables perspective-related system events (PerspectiveRebuilding, PerspectiveRebuilt).
  /// </summary>
  public bool PerspectiveEventsEnabled { get; private set; }

  /// <summary>
  /// Enables error-related system events (ReceptorFailed, PerspectiveFailed, MessageDeadLettered).
  /// </summary>
  public bool ErrorEventsEnabled { get; private set; }

  /// <summary>
  /// Enables all system events.
  /// </summary>
  public SystemEventOptions EnableAll() {
    EventAuditEnabled = true;
    CommandAuditEnabled = true;
    PerspectiveEventsEnabled = true;
    ErrorEventsEnabled = true;
    return this;
  }

  /// <summary>
  /// Enables both event and command audit system events.
  /// </summary>
  /// <seealso cref="EnableEventAudit"/>
  /// <seealso cref="EnableCommandAudit"/>
  public SystemEventOptions EnableAudit() {
    EventAuditEnabled = true;
    CommandAuditEnabled = true;
    return this;
  }

  /// <summary>
  /// Enables event audit system events (<see cref="EventAudited"/>).
  /// An EventAudited is emitted for each domain event appended.
  /// </summary>
  /// <remarks>
  /// Use this if you only want to audit events, not commands.
  /// For auditing both, use <see cref="EnableAudit"/>.
  /// </remarks>
  public SystemEventOptions EnableEventAudit() {
    EventAuditEnabled = true;
    return this;
  }

  /// <summary>
  /// Enables command audit system events (<see cref="CommandAudited"/>).
  /// A CommandAudited is emitted for each command processed by a receptor.
  /// </summary>
  /// <remarks>
  /// Use this if you only want to audit commands, not events.
  /// For auditing both, use <see cref="EnableAudit"/>.
  /// </remarks>
  public SystemEventOptions EnableCommandAudit() {
    CommandAuditEnabled = true;
    return this;
  }

  /// <summary>
  /// Enables perspective-related system events.
  /// </summary>
  public SystemEventOptions EnablePerspectiveEvents() {
    PerspectiveEventsEnabled = true;
    return this;
  }

  /// <summary>
  /// Enables error-related system events.
  /// </summary>
  public SystemEventOptions EnableErrorEvents() {
    ErrorEventsEnabled = true;
    return this;
  }

  /// <summary>
  /// When <c>true</c>, system events will be published to the outbox and can be
  /// received from the inbox. Use this if you want centralized system event collection.
  /// Default is <c>false</c> (LocalOnly = true).
  /// </summary>
  /// <remarks>
  /// <para>
  /// Use <c>Broadcast()</c> when you have a dedicated system monitoring service that
  /// collects system events from all hosts. This is an advanced scenario.
  /// </para>
  /// </remarks>
  public SystemEventOptions Broadcast() {
    LocalOnly = false;
    return this;
  }

  /// <summary>
  /// Checks if a specific system event type is enabled.
  /// </summary>
  public bool IsEnabled<TSystemEvent>() where TSystemEvent : ISystemEvent {
    return IsEnabled(typeof(TSystemEvent));
  }

  /// <summary>
  /// Checks if a specific system event type is enabled.
  /// </summary>
  public bool IsEnabled(Type systemEventType) {
    if (!typeof(ISystemEvent).IsAssignableFrom(systemEventType)) {
      return false;
    }

    // Check by event type
    if (systemEventType == typeof(EventAudited)) {
      return EventAuditEnabled;
    }

    if (systemEventType == typeof(CommandAudited)) {
      return CommandAuditEnabled;
    }

    // Add more system event type checks as they are added
    // PerspectiveRebuilding, PerspectiveRebuilt -> PerspectiveEventsEnabled
    // ReceptorFailed, MessageDeadLettered -> ErrorEventsEnabled

    return false;
  }
}
