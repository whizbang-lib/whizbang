using System.Collections.Frozen;

namespace Whizbang.Core.Routing;

/// <summary>
/// Per-namespace command inboxes (topology arc phase 5, spec migration step 1): the service
/// subscribes to one <c>inbox.&lt;contract-namespace&gt;</c> topic for every DISTINCT contract
/// namespace it handles commands from (derived from the receptor registry's handled-message
/// enumeration), PLUS the system broadcast inbox (<c>inbox.whizbang</c>), PLUS — transitionally —
/// today's shared-inbox subscription unchanged, so behavior is a strict superset of the shared
/// strategy while publishers still send everything to the shared inbox.
/// </summary>
/// <remarks>
/// <para>Retirement schedule per subscription part:</para>
/// <list type="bullet">
///   <item><b>Per-namespace command inboxes</b> — permanent; the target topology. They sit DARK
///   (no publisher sends to them) until the publisher flip (phase 6).</item>
///   <item><b>System broadcast inbox</b> (<c>inbox.whizbang</c>) — permanent; carries the
///   system-command + integrity control-plane + minted-composite patterns the shared inbox
///   carries today. Broadcast/control/minted envelope types NEVER route to per-namespace
///   inboxes — the whole <c>whizbang.core</c> contract subtree is reserved for it.</item>
///   <item><b>Transitional shared-inbox subscription</b> — retires via
///   <see cref="RoutingOptions.RetireSharedInbox"/> (phase 7, valid only after the full
///   publisher flip); until then it keeps every command flowing exactly as today.</item>
/// </list>
/// <para>NOT the default strategy — opt in via
/// <see cref="InboxRoutingOptionsBuilder.UseNamespaceInboxes"/>; the default flip decision is
/// phase 6/7.</para>
/// </remarks>
/// <docs>fundamentals/dispatcher/routing#namespace-inbox</docs>
/// <tests>tests/Whizbang.Core.Tests/Routing/NamespaceInboxStrategyTests.cs</tests>
public sealed class NamespaceInboxStrategy : IInboxRoutingStrategy {
  /// <summary>Backing constant for <see cref="OwnedCommandInboxMetadataKey"/>.</summary>
  private const string OWNED_COMMAND_INBOX_METADATA_KEY = "OwnedCommandInbox";

  /// <summary>
  /// Metadata key marking a subscription as an OWNED per-namespace command inbox. Provisioners
  /// key the startup ownership-drift check on this marker (a second service's subscription on
  /// an owned command inbox is a modeling error — one service per command namespace).
  /// </summary>
  public static string OwnedCommandInboxMetadataKey => OWNED_COMMAND_INBOX_METADATA_KEY;

  /// <summary>Gets the system broadcast inbox topic (<c>inbox.whizbang</c>). Naming shared
  /// with the publish side through <see cref="CommandInboxNaming"/> (phase 6).</summary>
  public static string SystemBroadcastInboxTopic => CommandInboxNaming.SystemBroadcastTopic;

  private readonly SharedTopicInboxStrategy _transitionalShared;
  private readonly RoutingOptions? _routingOptions;

  /// <summary>
  /// Creates the strategy. The transitional shared-inbox subscription (superset guarantee,
  /// retires with <see cref="RoutingOptions.RetireSharedInbox"/>) uses
  /// <paramref name="sharedInboxTopic"/>. Without bound options the strategy can never see a
  /// retirement flag — prefer the options-bound overload (what
  /// <see cref="InboxRoutingOptionsBuilder.UseNamespaceInboxes"/> wires).
  /// </summary>
  /// <param name="sharedInboxTopic">Today's shared inbox topic. Default: "inbox".</param>
  public NamespaceInboxStrategy(string sharedInboxTopic = "inbox")
      : this(null, sharedInboxTopic) { }

  /// <summary>
  /// Creates the strategy bound to its service's <see cref="RoutingOptions"/> (topology arc
  /// phase 7): <see cref="RoutingOptions.SharedInboxRetired"/> is consulted LIVE on every
  /// subscription computation — a configuration-bound retirement (applied on options
  /// resolution) needs no strategy re-registration, mirroring the outbox flip set.
  /// </summary>
  /// <param name="routingOptions">The service's routing options; null behaves like the
  /// unbound overload (retirement can never engage).</param>
  /// <param name="sharedInboxTopic">Today's shared inbox topic. Default: "inbox".</param>
  public NamespaceInboxStrategy(RoutingOptions? routingOptions, string sharedInboxTopic = "inbox") {
    _transitionalShared = new SharedTopicInboxStrategy(sharedInboxTopic);
    _routingOptions = routingOptions;
  }

  /// <summary>
  /// Legacy singular surface — returns today's shared-inbox subscription (the transitional
  /// part), because a single subscription cannot express the per-namespace set. Consumers of
  /// this strategy must use the plural <see cref="GetSubscriptions"/> seam. Under
  /// shared-inbox retirement (phase 7) this surface THROWS: the only subscription it can
  /// express is the retired one, and answering with it would silently recreate the catch-all.
  /// </summary>
  /// <inheritdoc />
  /// <exception cref="InvalidOperationException">Thrown when
  /// <see cref="RoutingOptions.SharedInboxRetired"/> is set on the bound options.</exception>
  /// <tests>tests/Whizbang.Core.Tests/Routing/NamespaceInboxStrategyTests.cs:GetSubscription_Singular_UnderRetirement_ThrowsAsync</tests>
  public InboxSubscription GetSubscription(
    IReadOnlySet<string> ownedDomains,
    string serviceName,
    MessageKind kind
  ) {
    if (_routingOptions?.SharedInboxRetired == true) {
      throw new InvalidOperationException(
        "The shared inbox is retired (RetireSharedInbox): the singular GetSubscription surface "
        + "can only express the retired shared-inbox subscription, and answering with it would "
        + "recreate the catch-all. Consume the plural GetSubscriptions seam instead.");
    }
    return _transitionalShared.GetSubscription(ownedDomains, serviceName, kind);
  }

  /// <summary>
  /// The full subscription set: per-namespace command inboxes (from the handled-message
  /// enumeration), the system broadcast inbox, and — until
  /// <see cref="RoutingOptions.SharedInboxRetired"/> — the transitional shared-inbox
  /// subscription. Under retirement the set is exactly per-namespace inboxes + the broadcast
  /// inbox: no catch-all remnant.
  /// </summary>
  /// <param name="context">Service identity, owned domains, handled-message enumeration, and
  /// consumed event namespaces.</param>
  /// <returns>The subscription set; never empty (the system broadcast part is unconditional).</returns>
  /// <exception cref="ArgumentNullException">Thrown when context is null.</exception>
  /// <exception cref="InvalidOperationException">Thrown when retirement is enabled while the
  /// command-namespace flip is incomplete (the retirement guard).</exception>
  /// <tests>tests/Whizbang.Core.Tests/Routing/NamespaceInboxStrategyTests.cs:GetSubscriptions_UnderRetirement_ExactlyPerNamespaceInboxesPlusBroadcastAsync</tests>
  /// <docs>fundamentals/dispatcher/routing#namespace-inbox</docs>
  /// <tests>tests/Whizbang.Core.Tests/Routing/NamespaceInboxStrategyTests.cs:GetSubscriptions_HandledCommandNamespaces_OnePerDistinctNamespaceAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Routing/NamespaceInboxStrategyTests.cs:GetSubscriptions_ContainsTodaysSharedSubscriptionUnchangedAsync</tests>
  public IReadOnlyList<InboxSubscription> GetSubscriptions(InboxSubscriptionContext context) {
    ArgumentNullException.ThrowIfNull(context);

    // Retirement guard (phase 7), defense in depth for manually constructed options — the
    // WithRouting options factory enforces the same invariant at startup: dropping the shared
    // subscription while any namespace still publishes to the shared inbox is silent loss.
    _routingOptions?.ThrowIfRetirementIncomplete();

    var subscriptions = new List<InboxSubscription>();

    // Part 1 (permanent) — one inbox per DISTINCT handled COMMAND contract namespace.
    // Sorted for deterministic manifests; framework-reserved namespaces ride part 2 instead.
    var commandNamespaces = new SortedSet<string>(StringComparer.Ordinal);
    foreach (var handled in context.HandledMessages) {
      if (handled.Kind != MessageKind.Command || string.IsNullOrWhiteSpace(handled.ContractNamespace)) {
        continue;
      }
      var contractNamespace = handled.ContractNamespace.ToLowerInvariant();
      if (CommandInboxNaming.IsFrameworkReserved(contractNamespace)) {
        continue;
      }
      commandNamespaces.Add(contractNamespace);
    }

    foreach (var contractNamespace in commandNamespaces) {
      subscriptions.Add(new InboxSubscription(
        Topic: CommandInboxNaming.TopicFor(contractNamespace),
        FilterExpression: null, // the topic IS the filter — the entity carries one namespace
        Metadata: new Dictionary<string, object> {
          [OWNED_COMMAND_INBOX_METADATA_KEY] = true,
          ["ContractNamespace"] = contractNamespace
        }));
    }

    // Part 2 (permanent) — the system broadcast inbox. Patterns come from the SAME builder the
    // shared strategy uses today (with no owned domains), so it carries exactly the
    // system-command + control-plane + minting patterns — locked by tests.
    var systemPatterns = SharedTopicInboxStrategy.BuildRoutingPatterns(FrozenSet<string>.Empty);
    subscriptions.Add(new InboxSubscription(
      Topic: CommandInboxNaming.SystemBroadcastTopic,
      FilterExpression: string.Join(",", systemPatterns),
      Metadata: new Dictionary<string, object> {
        ["RoutingPatterns"] = systemPatterns
      }));

    // Part 3 (transitional) — today's shared-inbox subscription UNCHANGED, so the set is a
    // strict superset of the shared strategy while publishers still target it. Under
    // RETIREMENT (phase 7, the migration end-state) this part is DROPPED: the subscription
    // set becomes exactly per-namespace inboxes + the system broadcast inbox — no catch-all
    // remnant. Rollback = clear RetireSharedInbox; the transitional subscription returns.
    if (_routingOptions?.SharedInboxRetired != true) {
      subscriptions.Add(_transitionalShared.GetSubscription(
        context.OwnedDomains, context.ServiceName, MessageKind.Command));
    }

    return subscriptions;
  }

  /// <summary>
  /// Computes every contract namespace this service consumes ANY constituent from — the union
  /// of receptor-handled message namespaces (all kinds) and the perspective/receptor/manual
  /// consumed event namespaces. This is the composite/raw-carry routing surface (the spec's
  /// flagged highest-risk mapping): a composite must route to every service that handles any
  /// constituent's namespace, and receptor-only enumeration misses perspective-consumed events.
  /// Note the minted composite ENVELOPE types themselves ride the system/minting patterns on
  /// the broadcast inbox — they never route to per-namespace inboxes.
  /// </summary>
  /// <param name="context">The subscription context.</param>
  /// <returns>Lowercase-invariant namespace set; empty when the service consumes nothing.</returns>
  /// <exception cref="ArgumentNullException">Thrown when context is null.</exception>
  /// <tests>tests/Whizbang.Core.Tests/Routing/NamespaceInboxStrategyTests.cs:ComputeConsumedNamespaces_UnionsHandledAndConsumedEventNamespacesAsync</tests>
  public static IReadOnlySet<string> ComputeConsumedNamespaces(InboxSubscriptionContext context) {
    ArgumentNullException.ThrowIfNull(context);

    var namespaces = new HashSet<string>(StringComparer.Ordinal);
    foreach (var handled in context.HandledMessages) {
      if (!string.IsNullOrWhiteSpace(handled.ContractNamespace)) {
        namespaces.Add(handled.ContractNamespace.ToLowerInvariant());
      }
    }
    foreach (var consumed in context.ConsumedEventNamespaces) {
      if (!string.IsNullOrWhiteSpace(consumed)) {
        namespaces.Add(consumed.ToLowerInvariant());
      }
    }
    return namespaces;
  }

}
