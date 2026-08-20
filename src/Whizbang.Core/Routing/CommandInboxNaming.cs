using Whizbang.Core.Transports;

namespace Whizbang.Core.Routing;

/// <summary>
/// The ONE derivation for per-namespace command inbox entity names (topology arc phases 5/6).
/// The inbox side (<see cref="NamespaceInboxStrategy"/> subscriptions) and the outbox side
/// (<see cref="NamespaceOutboxStrategy"/> publish destinations, the publish-time flip in
/// <c>TransportPublishStrategy</c>) both derive entity names from THIS helper, so publisher
/// and subscriber can never disagree on casing or shape — cross-consistency by construction,
/// not by duplicated string logic.
/// </summary>
/// <docs>fundamentals/dispatcher/routing#namespace-inbox</docs>
/// <tests>tests/Whizbang.Core.Tests/Routing/CommandInboxNamingTests.cs</tests>
public static class CommandInboxNaming {
  /// <summary>Prefix for per-namespace command inbox entities.</summary>
  private const string PER_NAMESPACE_TOPIC_PREFIX = "inbox.";

  /// <summary>The system broadcast inbox entity every service subscribes to.</summary>
  private const string SYSTEM_BROADCAST_INBOX_TOPIC = "inbox.whizbang";

  /// <summary>The framework-reserved contract-namespace subtree (lowercase-invariant).</summary>
  private const string FRAMEWORK_RESERVED_NAMESPACE = "whizbang.core";

  /// <summary>Backing constant for <see cref="RequireProvisionedEntityMetadataKey"/>.</summary>
  private const string REQUIRE_PROVISIONED_ENTITY_METADATA_KEY = "RequireProvisionedEntity";

  /// <summary>Gets the per-namespace command inbox topic prefix (<c>inbox.</c>).</summary>
  public static string TopicPrefix => PER_NAMESPACE_TOPIC_PREFIX;

  /// <summary>Gets the system broadcast inbox entity name (<c>inbox.whizbang</c>).</summary>
  public static string SystemBroadcastTopic => SYSTEM_BROADCAST_INBOX_TOPIC;

  /// <summary>
  /// Metadata key marking a <see cref="TransportDestination"/> as a consumer-provisioned
  /// entity the publisher must NEVER create: command inbox entities are provisioned by the
  /// handling service (phase-5 dark provisioning), so entity existence is the proxy for
  /// "a subscriber is provisioned". Transports honor the marker by refusing to auto-create
  /// the entity and failing LOUDLY (<see cref="UnroutableDestinationException"/>) when it
  /// does not exist — never a silent broker-side drop.
  /// </summary>
  public static string RequireProvisionedEntityMetadataKey => REQUIRE_PROVISIONED_ENTITY_METADATA_KEY;

  /// <summary>
  /// Derives the per-namespace command inbox entity name for a contract namespace:
  /// <c>inbox.&lt;contract-namespace&gt;</c>, lowercase-invariant.
  /// </summary>
  /// <param name="contractNamespace">The command's contract (CLR) namespace.</param>
  /// <returns>The inbox entity name (e.g. <c>inbox.myapp.orders.commands</c>).</returns>
  /// <exception cref="ArgumentException">Thrown when the namespace is null or whitespace.</exception>
  public static string TopicFor(string contractNamespace) {
    ArgumentException.ThrowIfNullOrWhiteSpace(contractNamespace);
    return PER_NAMESPACE_TOPIC_PREFIX + contractNamespace.ToLowerInvariant();
  }

  /// <summary>
  /// True when the contract namespace sits in the framework-reserved subtree
  /// (<c>whizbang.core</c> and below) — broadcast/control/minted traffic that rides the
  /// system broadcast inbox and never gets a per-namespace inbox.
  /// </summary>
  /// <param name="contractNamespace">The contract namespace (any casing).</param>
  /// <returns>True for the reserved subtree; false otherwise (including null/empty).</returns>
  public static bool IsFrameworkReserved(string? contractNamespace) {
    if (string.IsNullOrEmpty(contractNamespace)) {
      return false;
    }

    return contractNamespace.Equals(FRAMEWORK_RESERVED_NAMESPACE, StringComparison.OrdinalIgnoreCase)
      || contractNamespace.StartsWith(FRAMEWORK_RESERVED_NAMESPACE + ".", StringComparison.OrdinalIgnoreCase);
  }

  /// <summary>
  /// True when a publish address names a CONSUMER-provisioned inbox entity (a per-namespace
  /// command inbox or the system broadcast inbox — anything under the <c>inbox.</c> prefix).
  /// Publisher-side manifest provisioning must skip these: creating the entity from the
  /// publish side would turn "no subscriber provisioned" into a silent broker-side drop
  /// instead of the loud publish failure the flip guarantees. The bare legacy shared inbox
  /// (<c>inbox</c>) is NOT consumer-provisioned — publishers create it today, unchanged.
  /// </summary>
  /// <param name="address">The publish destination address.</param>
  /// <returns>True for <c>inbox.*</c> entities; false otherwise.</returns>
  public static bool IsConsumerProvisionedInboxEntity(string? address) {
    return !string.IsNullOrEmpty(address)
      && address.StartsWith(PER_NAMESPACE_TOPIC_PREFIX, StringComparison.OrdinalIgnoreCase);
  }

  /// <summary>
  /// True when the destination carries the <see cref="RequireProvisionedEntityMetadataKey"/>
  /// marker — the transport must verify the entity exists (consumer-provisioned) instead of
  /// auto-creating it, and throw <see cref="UnroutableDestinationException"/> when missing.
  /// </summary>
  /// <param name="destination">The transport destination.</param>
  /// <returns>True when the marker is present and true.</returns>
  public static bool RequiresProvisionedEntity(TransportDestination? destination) {
    return destination?.Metadata?.TryGetValue(REQUIRE_PROVISIONED_ENTITY_METADATA_KEY, out var marker) == true
      && marker.ValueKind == System.Text.Json.JsonValueKind.True;
  }
}
