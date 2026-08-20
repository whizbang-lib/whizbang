namespace Whizbang.Core.Routing;

/// <summary>
/// One publish destination in a <see cref="TopologyManifest"/> — the exact
/// <c>GetDestination</c> projection for one (message type, kind) pair, plus the composite
/// group key derived from the same projection.
/// </summary>
/// <param name="MessageTypeName">The published message's CLR type name (from the catalog's
/// <c>ClrTypeName</c>).</param>
/// <param name="Kind">The message kind the destination was projected for.</param>
/// <param name="Address">Destination address as named by the outbox strategy.</param>
/// <param name="RoutingKey">Destination routing key as named by the outbox strategy.</param>
/// <param name="CompositeGroupKey">The strategy's composite group key for the pair —
/// invariant: same key ⇔ same destination.</param>
/// <docs>fundamentals/dispatcher/routing#topology-manifest</docs>
/// <tests>tests/Whizbang.Core.Tests/Routing/TopologyManifestTests.cs:Build_PublishDestinations_AreExactProjectionsOfGetDestinationAsync</tests>
public sealed record TopologyPublishDestination(
  string MessageTypeName,
  MessageKind Kind,
  string Address,
  string? RoutingKey,
  string CompositeGroupKey
);

/// <summary>
/// A service's transport topology as a derived projection (topology arc phase 3): every
/// publish destination (union over the message catalog via the outbox strategy's
/// <c>GetDestination</c>, per kind) and the full subscription set (the inbox strategy's
/// plural <c>GetSubscriptions</c>). Later phases consume this for startup provisioning,
/// drift checks, and acceptor budgets.
/// </summary>
/// <remarks>
/// The manifest is PURE data computed by <see cref="TopologyManifestBuilder"/> — it names
/// no entity the strategies didn't name, and recomputing from the same inputs yields an
/// identical manifest (deterministic ordering, deduplicated entries).
/// </remarks>
/// <param name="PublishDestinations">Publish projections, deduplicated by
/// (type name, kind) and ordered ordinally by type name then kind.</param>
/// <param name="Subscriptions">The subscription set exactly as the inbox strategy
/// returned it.</param>
/// <docs>fundamentals/dispatcher/routing#topology-manifest</docs>
/// <tests>tests/Whizbang.Core.Tests/Routing/TopologyManifestTests.cs:Build_RecomputedFromSameInputs_IsIdenticalAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Routing/TopologyManifestTests.cs:Build_NamesNoEntityTheStrategiesDidntNameAsync</tests>
public sealed record TopologyManifest(
  IReadOnlyList<TopologyPublishDestination> PublishDestinations,
  IReadOnlyList<InboxSubscription> Subscriptions
);

/// <summary>
/// Pure function building a <see cref="TopologyManifest"/> from routing strategies, a
/// subscription context, and the compile-time message catalog. No DI, no I/O, no hidden
/// state — route, split, subscribe, and provision are all projections of
/// <c>GetDestination</c>/<c>GetSubscriptions</c>, so the manifest can never disagree with
/// what the running service actually routes.
/// </summary>
/// <docs>fundamentals/dispatcher/routing#topology-manifest</docs>
/// <tests>tests/Whizbang.Core.Tests/Routing/TopologyManifestTests.cs</tests>
public static class TopologyManifestBuilder {
  /// <summary>
  /// Builds the manifest: publish destinations are the union over
  /// <paramref name="messageCatalog"/> of the outbox strategy's <c>GetDestination</c>
  /// per entry kind ("command" → <see cref="MessageKind.Command"/>, "event" →
  /// <see cref="MessageKind.Event"/>; other catalog kinds such as "perspective" publish
  /// nothing and are skipped); subscriptions come from the inbox strategy's plural
  /// <c>GetSubscriptions</c> seam.
  /// </summary>
  /// <param name="outboxStrategy">Outbox routing strategy naming publish entities.</param>
  /// <param name="inboxStrategy">Inbox routing strategy naming subscription entities.</param>
  /// <param name="context">Service identity, owned domains, handled-message enumeration.</param>
  /// <param name="messageCatalog">The generated message catalog entries
  /// (<see cref="IMessageTypeCatalog.GetAll"/>), or any equivalent enumeration.</param>
  /// <returns>The deterministic topology manifest.</returns>
  /// <exception cref="ArgumentNullException">Thrown when any argument is null.</exception>
  /// <docs>fundamentals/dispatcher/routing#topology-manifest</docs>
  /// <tests>tests/Whizbang.Core.Tests/Routing/TopologyManifestTests.cs:Build_PublishDestinations_AreExactProjectionsOfGetDestinationAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Routing/TopologyManifestTests.cs:Build_CatalogOrder_DoesNotLeakIntoManifestOrderAsync</tests>
  public static TopologyManifest Build(
      IOutboxRoutingStrategy outboxStrategy,
      IInboxRoutingStrategy inboxStrategy,
      InboxSubscriptionContext context,
      IEnumerable<MessageTypeCatalogEntry> messageCatalog) {
    ArgumentNullException.ThrowIfNull(outboxStrategy);
    ArgumentNullException.ThrowIfNull(inboxStrategy);
    ArgumentNullException.ThrowIfNull(context);
    ArgumentNullException.ThrowIfNull(messageCatalog);

    // Publish destinations: project every publishable catalog entry through GetDestination,
    // deduplicated by (type name, kind) so a duplicated catalog row cannot double-provision.
    var seen = new HashSet<(string TypeName, MessageKind Kind)>();
    var published = new List<TopologyPublishDestination>();
    foreach (var entry in messageCatalog) {
      var kind = _publishKindFor(entry.Kind);
      if (kind is null || !seen.Add((entry.ClrTypeName, kind.Value))) {
        continue;
      }

      var destination = outboxStrategy.GetDestination(entry.Type, context.OwnedDomains, kind.Value);
      var groupKey = outboxStrategy.GetCompositeGroupKey(entry.Type, context.OwnedDomains, kind.Value);
      published.Add(new TopologyPublishDestination(
          entry.ClrTypeName,
          kind.Value,
          destination.Address,
          destination.RoutingKey,
          groupKey));
    }

    // Deterministic order: catalog enumeration order must not leak into the manifest.
    published.Sort(static (a, b) => {
      var byName = string.CompareOrdinal(a.MessageTypeName, b.MessageTypeName);
      return byName != 0 ? byName : a.Kind.CompareTo(b.Kind);
    });

    var subscriptions = inboxStrategy.GetSubscriptions(context);

    return new TopologyManifest(published, subscriptions);
  }

  /// <summary>
  /// Maps a catalog kind string to the MessageKind it publishes as, or null when the
  /// entry publishes nothing (e.g. "perspective").
  /// </summary>
  private static MessageKind? _publishKindFor(string catalogKind) => catalogKind switch {
    "command" => MessageKind.Command,
    "event" => MessageKind.Event,
    _ => null
  };
}
