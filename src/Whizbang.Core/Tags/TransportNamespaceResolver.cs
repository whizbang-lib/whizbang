using System.Collections.Concurrent;
using Whizbang.Core.Messaging;
using Whizbang.Core.Transports;

namespace Whizbang.Core.Tags;

/// <summary>
/// The AOT tag lookup the transport boundary consults (transport traffic classes, topology arc
/// phase 8): resolves a message's stored type-name string to its TransportNamespace key — the
/// tag-bound routing from <see cref="TagOptions.RouteNamespace"/> — falling back to
/// <see cref="TransportNamespaces.DefaultKey"/> for unmatched traffic. Routing strategies stay
/// TransportNamespace-unaware (<c>GetDestination</c> keeps naming entities); the publish
/// strategy stamps the resolved key onto destination metadata and the transport maps it to a
/// client.
/// </summary>
/// <remarks>
/// <para>
/// Resolution rides the generated <see cref="MessageTagRegistry"/> and
/// <see cref="EventTypeMatchingHelper"/>'s canonical name forms — no <c>Type.GetType</c>, no
/// attribute reflection. The registry keys by <see cref="Type"/> while the publish path only
/// has the stored type-name STRING, so the lookup is name-keyed: built once (lazily, after
/// every module initializer has registered its registry) and cached per distinct type-name
/// string in a <see cref="ConcurrentDictionary{TKey,TValue}"/> — the
/// <see cref="CoalesceGroupResolver"/> idiom exactly.
/// </para>
/// <para>
/// Startup validation (<see cref="TagPolicyValidator"/>) guarantees a message type's
/// route-bound tags map to at most ONE distinct TransportNamespace key, so resolution is
/// deterministic by construction.
/// </para>
/// </remarks>
/// <docs>fundamentals/messages/message-tags#transport-namespace-routing</docs>
/// <tests>tests/Whizbang.Core.Tests/Tags/TransportNamespaceResolverTests.cs:Resolve_BoundTag_ReturnsRoutedKeyAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Tags/TransportNamespaceResolverTests.cs:Resolve_NoBindingsAtAll_ReturnsDefaultAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Tags/TransportNamespaceResolverTests.cs:Resolve_ResolutionIsCachedPerTypeNameAsync</tests>
public sealed class TransportNamespaceResolver {
  private readonly TagOptions _tagOptions;
  private readonly Func<IEnumerable<MessageTagRegistration>> _registrationSource;
  private readonly ConcurrentDictionary<string, string> _keyByTypeName = new(StringComparer.Ordinal);
  private readonly Lazy<(Dictionary<string, Type> Lookup, Dictionary<Type, string> KeyByType)> _index;

  /// <summary>
  /// Creates a resolver over the given tag options.
  /// </summary>
  /// <param name="tagOptions">The tag options carrying the routing bindings.</param>
  /// <param name="registrationSource">
  /// Tag registration source; defaults to <see cref="MessageTagRegistry.GetAllTags"/>.
  /// Injectable so tests avoid the process-global registry.
  /// </param>
  public TransportNamespaceResolver(
      TagOptions tagOptions,
      Func<IEnumerable<MessageTagRegistration>>? registrationSource = null) {
    _tagOptions = tagOptions ?? throw new ArgumentNullException(nameof(tagOptions));
    _registrationSource = registrationSource ?? MessageTagRegistry.GetAllTags;
    // Lazy + thread-safe: built on first resolution, after module initializers have populated
    // the registry and after host configuration has finalized the bindings.
    _index = new(_buildIndex, LazyThreadSafetyMode.ExecutionAndPublication);
  }

  /// <summary>
  /// Whether any routing binding exists at all — the cheap guard callers use to skip
  /// per-message work entirely when the feature is unused (the spec's single-namespace no-op
  /// guarantee rides this).
  /// </summary>
  public bool HasBindings => _tagOptions.RouteNamespaceBindings.Count > 0;

  /// <summary>
  /// Resolves the TransportNamespace key for <paramref name="messageTypeName"/>: the routed
  /// key when the type carries a route-bound tag, <see cref="TransportNamespaces.DefaultKey"/>
  /// otherwise (unbound tag, untagged type, unresolvable name).
  /// </summary>
  /// <param name="messageTypeName">The stored message type-name string (any canonical form
  /// <see cref="EventTypeMatchingHelper"/> understands).</param>
  /// <returns>The TransportNamespace key; never null.</returns>
  public string ResolveNamespaceKey(string messageTypeName) {
    ArgumentNullException.ThrowIfNull(messageTypeName);

    if (!HasBindings) {
      return TransportNamespaces.DefaultKey;
    }

    return _keyByTypeName.GetOrAdd(messageTypeName, name => {
      var (lookup, keyByType) = _index.Value;
      return EventTypeMatchingHelper.TryResolveType(lookup, name, out var type)
        ? keyByType[type]
        : TransportNamespaces.DefaultKey;
    });
  }

  /// <summary>
  /// Projects the consume-side namespace set: the distinct NON-default TransportNamespace keys
  /// any of <paramref name="messageTypeNames"/> resolves to, ordinally sorted. The transports
  /// mirror their subscription set into each of these namespaces (the consume-side rule:
  /// normal set in default PLUS the same entity set per active class namespace).
  /// </summary>
  /// <param name="messageTypeNames">The consumed/handled message type names (e.g. the receptor
  /// registry's handled-message enumeration).</param>
  /// <returns>Distinct non-default keys in ordinal order; empty when nothing routes.</returns>
  public IReadOnlyList<string> ResolveConsumeNamespaceKeys(IEnumerable<string> messageTypeNames) {
    ArgumentNullException.ThrowIfNull(messageTypeNames);

    if (!HasBindings) {
      return [];
    }

    var keys = new SortedSet<string>(StringComparer.Ordinal);
    foreach (var typeName in messageTypeNames) {
      var key = ResolveNamespaceKey(typeName);
      if (!TransportNamespaces.IsDefault(key)) {
        keys.Add(key);
      }
    }

    return [.. keys];
  }

  private (Dictionary<string, Type> Lookup, Dictionary<Type, string> KeyByType) _buildIndex() {
    var bindings = _tagOptions.RouteNamespaceBindings;

    var keyByType = new Dictionary<Type, string>();
    if (bindings.Count > 0) {
      foreach (var registration in _registrationSource()) {
        if (bindings.TryGetValue(registration.Tag, out var namespaceKey)) {
          // Startup validation (TagPolicyValidator) guarantees at most one distinct routed
          // key per message type, so a plain assignment cannot lose information.
          keyByType[registration.MessageType] = namespaceKey;
        }
      }
    }

    var lookup = EventTypeMatchingHelper.BuildTypeLookup([.. keyByType.Keys]);
    return (lookup, keyByType);
  }
}
