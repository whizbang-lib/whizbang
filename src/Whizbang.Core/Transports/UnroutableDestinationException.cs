namespace Whizbang.Core.Transports;

/// <summary>
/// Thrown at PUBLISH time when a message targets a consumer-provisioned inbox entity
/// (a per-namespace command inbox or the system broadcast inbox — see the
/// <c>RequireProvisionedEntity</c> destination marker) that does not exist on the broker.
/// The topology arc's flip guarantee: an unroutable command is a LOUD publish failure
/// carrying the entity name, never a silent broker-side drop — publishers never create
/// command inbox entities, so entity existence proves the handling service dark-provisioned
/// its subscription (phase 5) before the namespace was flipped (phase 6).
/// </summary>
/// <remarks>
/// Rollback path: remove the namespace from
/// <c>RoutingOptions.RouteCommandNamespaceToInbox</c> (or the
/// <c>Whizbang:Routing:CommandNamespacesToInbox</c> configuration entry) and the namespace's
/// commands return to the legacy shared inbox.
/// </remarks>
/// <docs>fundamentals/dispatcher/routing#namespace-outbox</docs>
/// <tests>tests/Whizbang.Transports.Tests/UnroutableDestinationExceptionTests.cs</tests>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Roslynator", "RCS1194:Implement exception constructors",
  Justification = "UnroutableDestinationException always carries the missing broker EntityName — that name IS the diagnostic (which entity to provision, which namespace to roll back). The standard parameterless / message-only constructors would allow constructing it without the one field that makes it actionable.")]
public sealed class UnroutableDestinationException : InvalidOperationException {
  /// <summary>Gets the broker entity name the publish targeted (topic/exchange).</summary>
  public string EntityName { get; }

  /// <summary>
  /// Creates the exception for a missing consumer-provisioned entity.
  /// </summary>
  /// <param name="entityName">The missing broker entity (topic/exchange) name.</param>
  public UnroutableDestinationException(string entityName)
    : base(_buildMessage(entityName)) {
    EntityName = entityName;
  }

  /// <summary>
  /// Creates the exception wrapping the broker's entity-not-found failure.
  /// </summary>
  /// <param name="entityName">The missing broker entity (topic/exchange) name.</param>
  /// <param name="innerException">The transport-level failure that revealed the missing entity.</param>
  public UnroutableDestinationException(string entityName, Exception innerException)
    : base(_buildMessage(entityName), innerException) {
    EntityName = entityName;
  }

  private static string _buildMessage(string entityName) =>
    $"Unroutable command: destination entity '{entityName}' does not exist on the broker. " +
    "Per-namespace command inbox entities are provisioned by the HANDLING service (dark " +
    "provisioning) — publishers never create them, so a missing entity means no subscriber " +
    "is provisioned and publishing would silently drop the message. Provision the handling " +
    "service first, or roll the namespace back to the shared inbox by removing it from " +
    "RouteCommandNamespaceToInbox / Whizbang:Routing:CommandNamespacesToInbox.";
}
