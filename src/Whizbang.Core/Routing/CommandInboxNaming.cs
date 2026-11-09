// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

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

  /// <summary>
  /// The control-class broadcast entity (topology arc phase 9). Carries the SUPERSEDABLE control
  /// signals only; created when the control split is opted into
  /// (<c>ControlClassOptions.SessionlessSubscriptions</c>) so the class can be provisioned
  /// sessionless while durable system commands keep their session-ordered entity.
  /// </summary>
  private const string CONTROL_BROADCAST_INBOX_TOPIC = "inbox.whizbang.control";

  /// <summary>
  /// Prefix of each service's own control inbox (#2090): <c>inbox.whizbang.service.&lt;service&gt;</c>. Only that service
  /// subscribes to it, so directed integrity traffic addressed to it reaches it and no other service, structurally.
  /// </summary>
  private const string SERVICE_CONTROL_TOPIC_PREFIX = "inbox.whizbang.service.";

  /// <summary>The framework-reserved contract-namespace subtree (lowercase-invariant).</summary>
  private const string FRAMEWORK_RESERVED_NAMESPACE = "whizbang.core";

  /// <summary>Backing constant for <see cref="RequireProvisionedEntityMetadataKey"/>.</summary>
  private const string REQUIRE_PROVISIONED_ENTITY_METADATA_KEY = "RequireProvisionedEntity";

  /// <summary>Gets the per-namespace command inbox topic prefix (<c>inbox.</c>).</summary>
  public static string TopicPrefix => PER_NAMESPACE_TOPIC_PREFIX;

  /// <summary>Gets the system broadcast inbox entity name (<c>inbox.whizbang</c>).</summary>
  public static string SystemBroadcastTopic => SYSTEM_BROADCAST_INBOX_TOPIC;

  /// <summary>Gets the control-class broadcast entity name (<c>inbox.whizbang.control</c>).</summary>
  public static string ControlBroadcastTopic => CONTROL_BROADCAST_INBOX_TOPIC;

  /// <summary>Gets the prefix of each service's own control inbox (<c>inbox.whizbang.service.</c>).</summary>
  public static string ServiceControlTopicPrefix => SERVICE_CONTROL_TOPIC_PREFIX;

  /// <summary>
  /// Derives a service's own control inbox entity (#2090): <c>inbox.whizbang.service.&lt;service&gt;</c>, lowercase, with
  /// every character a broker entity name cannot carry (anything but letters, digits, <c>.</c>, <c>-</c> and <c>_</c>)
  /// replaced by <c>-</c>. Directed integrity traffic (repair and redelivery requests, manifests and the bundles that
  /// answer them) is addressed here: a per-namespace command inbox is not exclusive (every service handling one of its
  /// commands reads it, #2089) and its subscriptions filter to command types, so it can carry neither.
  /// </summary>
  /// <param name="serviceName">The service's name.</param>
  /// <returns>The control inbox entity name.</returns>
  /// <exception cref="ArgumentException">Thrown when the service name is null or whitespace.</exception>
  /// <docs>fundamentals/dispatcher/routing#service-control-inbox</docs>
  /// <tests>tests/Whizbang.Core.Tests/Routing/CommandInboxNamingTests.cs:ServiceControlTopicFor_IsTheServicesOwnLowercaseControlEntityAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Routing/CommandInboxNamingTests.cs:ServiceControlTopicFor_ReplacesCharactersAnEntityNameCannotCarryAsync</tests>
  public static string ServiceControlTopicFor(string serviceName) {
    ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);
    var lowered = serviceName.Trim().ToLowerInvariant();
    var chars = new char[lowered.Length];
    for (var i = 0; i < lowered.Length; i++) {
      var c = lowered[i];
      chars[i] = char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '-';
    }
    return SERVICE_CONTROL_TOPIC_PREFIX + new string(chars);
  }

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
