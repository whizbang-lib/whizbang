using System.Text.Json;
using Whizbang.Core.Transports;

namespace Whizbang.Core.Routing;

/// <summary>
/// The publisher flip (topology arc phase 6, spec migration step 2): kind-aware outbox
/// routing that sends commands to their per-namespace inbox entity
/// (<c>inbox.&lt;contract-namespace&gt;</c>) once the namespace is FLIPPED via
/// <see cref="RoutingOptions.RouteCommandNamespaceToInbox"/> /
/// <see cref="RoutingOptions.RouteAllCommandNamespacesToInbox"/> (or configuration:
/// <c>Whizbang:Routing:CommandNamespacesToInbox</c>).
/// </summary>
/// <remarks>
/// <para>Routing per kind:</para>
/// <list type="bullet">
///   <item><b>Events</b> (and Query/Unknown) — domain topics, byte-identical to
///   <see cref="DomainTopicOutboxStrategy"/> (the default strategy today).</item>
///   <item><b>Commands, flipped namespace</b> — <c>inbox.&lt;ns&gt;</c> (derivation shared with
///   <see cref="NamespaceInboxStrategy"/> through <see cref="CommandInboxNaming"/>), routing key
///   <c>{ns}.{typename}</c>, destination marked
///   <see cref="CommandInboxNaming.RequireProvisionedEntityMetadataKey"/> so transports fail
///   LOUDLY when the handling service never provisioned the entity.</item>
///   <item><b>Commands, unflipped namespace</b> — byte-identical to
///   <see cref="SharedTopicOutboxStrategy"/> (the legacy shared inbox); rollback of a flip is
///   removing the namespace from the flip set.</item>
///   <item><b>Commands in the framework-reserved subtree</b> (<c>whizbang.core.*</c>) — when
///   flipped they route to the system broadcast inbox (<c>inbox.whizbang</c>), NEVER to a
///   per-namespace inbox (the whole subtree is reserved for broadcast, phase 5); unflipped
///   they stay on the legacy shared inbox.</item>
///   <item><b>System kind</b> — the system broadcast inbox, unconditionally.</item>
/// </list>
/// <para>The flip set is consulted LIVE on every call (the strategy holds the
/// <see cref="RoutingOptions"/> instance), so configuration-bound flips and rollbacks apply
/// without re-registering the strategy. NOT the default strategy — opt in via
/// <see cref="OutboxRoutingOptionsBuilder.UseNamespaceRouting"/>.</para>
/// </remarks>
/// <docs>fundamentals/dispatcher/routing#namespace-outbox</docs>
/// <tests>tests/Whizbang.Core.Tests/Routing/NamespaceOutboxStrategyTests.cs</tests>
public sealed class NamespaceOutboxStrategy : IOutboxRoutingStrategy, ICommandInboxAddressResolver {
  private readonly RoutingOptions _routingOptions;
  private readonly SharedTopicOutboxStrategy _legacyShared;
  private readonly DomainTopicOutboxStrategy _domainTopics = new();
  private readonly NamespaceRoutingStrategy _topicResolver = new();

  /// <summary>
  /// Gets the legacy shared inbox topic unflipped commands keep routing to (byte-identical
  /// to <see cref="SharedTopicOutboxStrategy"/> output until their namespace flips).
  /// </summary>
  public string SharedInboxTopic { get; }

  /// <summary>
  /// The command-inbox seam (topology arc phase 7): unflipped commands default to the legacy
  /// shared inbox topic. Same value as <see cref="SharedInboxTopic"/>, exposed through the
  /// strategy-agnostic interface the transports' DI factories consume.
  /// </summary>
  /// <docs>fundamentals/dispatcher/routing#namespace-outbox</docs>
  /// <tests>tests/Whizbang.Core.Tests/Routing/CommandInboxAddressResolverTests.cs:NamespaceOutboxStrategy_ImplementsTheSeam_DefaultIsSharedInboxTopicAsync</tests>
  public string DefaultCommandInboxAddress => SharedInboxTopic;

  /// <summary>
  /// Creates the strategy.
  /// </summary>
  /// <param name="routingOptions">The routing options whose flip set
  /// (<see cref="RoutingOptions.CommandNamespacesToInbox"/>) is consulted live per call.</param>
  /// <param name="sharedInboxTopic">The legacy shared inbox topic for unflipped commands.
  /// Default: "inbox" (must match the fleet's shared inbox until full migration).</param>
  /// <exception cref="ArgumentNullException">Thrown when routingOptions is null.</exception>
  public NamespaceOutboxStrategy(RoutingOptions routingOptions, string sharedInboxTopic = "inbox") {
    ArgumentNullException.ThrowIfNull(routingOptions);
    ArgumentException.ThrowIfNullOrWhiteSpace(sharedInboxTopic);
    _routingOptions = routingOptions;
    SharedInboxTopic = sharedInboxTopic;
    _legacyShared = new SharedTopicOutboxStrategy(sharedInboxTopic);
  }

  /// <inheritdoc />
  public TransportDestination GetDestination(
    Type messageType,
    IReadOnlySet<string> ownedDomains,
    MessageKind kind
  ) {
    ArgumentNullException.ThrowIfNull(messageType);
    ArgumentNullException.ThrowIfNull(ownedDomains);

    if (kind is not (MessageKind.Command or MessageKind.System)) {
      // Events (and Query/Unknown): domain topics, byte-identical to today's default.
      return _domainTopics.GetDestination(messageType, ownedDomains, kind);
    }

    var contractNamespace = _topicResolver.ResolveTopic(messageType, "", null);
    var routingKey = $"{contractNamespace}.{messageType.Name.ToLowerInvariant()}";

    if (kind == MessageKind.System) {
      // System traffic is broadcast by nature — one send, N broadcast-inbox deliveries.
      // The "{namespace}.{typename}" key keeps the broadcast subscription patterns
      // (whizbang.core.commands.system.# etc.) matching on the wire subject.
      return new TransportDestination(
        CommandInboxNaming.SystemBroadcastTopic,
        routingKey,
        _flippedMetadata(contractNamespace, MessageKind.System));
    }

    var flippedAddress = ResolveFlippedCommandInboxAddress(contractNamespace);
    if (flippedAddress is null) {
      // Unflipped namespace: byte-identical to the legacy shared inbox (rollback shape).
      return _legacyShared.GetDestination(messageType, ownedDomains, MessageKind.Command);
    }

    return new TransportDestination(flippedAddress, routingKey, _flippedMetadata(contractNamespace, MessageKind.Command));
  }

  /// <summary>
  /// Name-based publish-time flip seam consumed by <c>TransportPublishStrategy</c> (which
  /// only has the outbox row's type-name STRING, not the CLR type — AOT-safe by
  /// construction). Returns the flipped inbox entity for a command contract namespace, or
  /// null when the namespace is not flipped (the caller keeps today's legacy shared-inbox
  /// wire shape, byte-identical). Parity with <see cref="GetDestination"/> is locked by
  /// tests: for every command type, the Address it returns equals this method's answer for
  /// the type's namespace (or the shared topic when null).
  /// </summary>
  /// <param name="contractNamespace">The command's contract namespace (any casing).</param>
  /// <returns><c>inbox.&lt;ns&gt;</c> for flipped namespaces, <c>inbox.whizbang</c> for flipped
  /// framework-reserved namespaces, null when not flipped.</returns>
  public string? ResolveFlippedCommandInboxAddress(string? contractNamespace) {
    if (string.IsNullOrWhiteSpace(contractNamespace)
        || !_routingOptions.IsCommandNamespaceRoutedToInbox(contractNamespace)) {
      return null;
    }

    // The framework-reserved subtree (whizbang.core.*) NEVER gets a per-namespace inbox —
    // a flip covering it redirects to the system broadcast inbox every namespace-inbox
    // service subscribes to (phase 5). inbox.whizbang.core.* would have no subscriber, ever.
    return CommandInboxNaming.IsFrameworkReserved(contractNamespace)
      ? CommandInboxNaming.SystemBroadcastTopic
      : CommandInboxNaming.TopicFor(contractNamespace);
  }

  /// <summary>
  /// Metadata for flipped/broadcast destinations: the Namespace + Kind pair the shared
  /// strategy stamps today, plus the <see cref="CommandInboxNaming.RequireProvisionedEntityMetadataKey"/>
  /// marker — transports must verify the (consumer-provisioned) entity exists and fail
  /// loudly instead of auto-creating it.
  /// </summary>
  private static Dictionary<string, JsonElement> _flippedMetadata(string contractNamespace, MessageKind kind) {
    return new Dictionary<string, JsonElement> {
      ["Namespace"] = JsonElementHelper.FromString(contractNamespace),
      ["Kind"] = JsonElementHelper.FromString(kind.ToString()),
      [CommandInboxNaming.RequireProvisionedEntityMetadataKey] = JsonElementHelper.FromBoolean(true)
    };
  }
}
