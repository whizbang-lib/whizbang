namespace Whizbang.Core.SystemEvents;

/// <summary>
/// Marker interface for Whizbang system events.
/// System events are internal events emitted by Whizbang for audit, monitoring, and operations.
/// </summary>
/// <remarks>
/// <para>
/// System events flow through the same event infrastructure as domain events but are
/// stored in a dedicated system stream (<see cref="SystemEventStream.Name"/>).
/// </para>
/// <para>
/// System events are opt-in per host via <c>options.SystemEvents.EnableXxx()</c> methods.
/// This allows different services to capture different system events based on their needs.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// // Enable audit system events in BFF
/// services.AddWhizbang(options => {
///   options.SystemEvents.EnableAudit();
/// });
///
/// // Create a perspective for audit entries
/// public class AuditPerspective : IPerspectiveFor&lt;AuditLogEntry, EventAudited&gt; {
///   public AuditLogEntry Apply(AuditLogEntry current, EventAudited @event) {
///     return new AuditLogEntry { ... };
///   }
/// }
/// </code>
/// </example>
/// <docs>core-concepts/system-events</docs>
public interface ISystemEvent : IEvent {
}
