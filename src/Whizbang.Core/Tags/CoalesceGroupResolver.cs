using System.Collections.Concurrent;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Tags;

/// <summary>
/// The AOT tag lookup the outbox mint seams consult: resolves an outbox message's stored
/// type-name string to its coalesce group (or null) and stamps
/// <c>CoalesceGroup = tag</c> + the <c>ScheduledFor = now + MaxDelaySeconds</c> safety floor
/// on messages whose type carries a tag with an enabled coalesce binding.
/// </summary>
/// <remarks>
/// <para>
/// Resolution rides the generated <see cref="MessageTagRegistry"/> and
/// <see cref="EventTypeMatchingHelper"/>'s canonical name forms — no
/// <c>Type.GetType</c>, no attribute reflection. The registry keys by <see cref="Type"/> while
/// the mint seams only have the stored type-name STRING, so the lookup is name-keyed: built
/// once (lazily, after every module initializer has registered its registry) and the answer
/// per distinct type-name string is cached in a <see cref="ConcurrentDictionary{TKey,TValue}"/>
/// — the mint path pays a dictionary hit, not a registry walk.
/// </para>
/// <para>
/// Pass-through rules: an already-stamped message is never re-stamped (a second seam must not
/// move the floor), and a caller-scheduled message (<c>ScheduledFor</c> already set) is never
/// coalesced — scheduled dispatch declared its own timeline, and folding would ship it early.
/// </para>
/// </remarks>
/// <docs>fundamentals/messages/message-tags#coalescing</docs>
/// <tests>tests/Whizbang.Core.Tests/Tags/CoalesceGroupResolverTests.cs:Apply_BoundTag_StampsGroupAndMaxDelayFloorAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Tags/CoalesceGroupResolverTests.cs:Apply_AlreadyScheduledMessage_IsNotCoalescedAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Tags/CoalesceGroupResolverTests.cs:Apply_ResolutionIsCachedPerTypeNameAsync</tests>
public sealed class CoalesceGroupResolver {
  private readonly TagOptions _tagOptions;
  private readonly TimeProvider _timeProvider;
  private readonly Func<IEnumerable<MessageTagRegistration>> _registrationSource;
  private readonly ConcurrentDictionary<string, string?> _groupByTypeName = new(StringComparer.Ordinal);
  private readonly Lazy<(Dictionary<string, Type> Lookup, Dictionary<Type, string> GroupByType)> _index;

  /// <summary>
  /// Creates a resolver over the given tag options.
  /// </summary>
  /// <param name="tagOptions">The tag options carrying the coalesce bindings.</param>
  /// <param name="timeProvider">Clock for the floor stamp; defaults to <see cref="TimeProvider.System"/>.</param>
  /// <param name="registrationSource">
  /// Tag registration source; defaults to <see cref="MessageTagRegistry.GetAllTags"/>. Injectable
  /// so tests avoid the process-global registry.
  /// </param>
  public CoalesceGroupResolver(
      TagOptions tagOptions,
      TimeProvider? timeProvider = null,
      Func<IEnumerable<MessageTagRegistration>>? registrationSource = null) {
    _tagOptions = tagOptions ?? throw new ArgumentNullException(nameof(tagOptions));
    _timeProvider = timeProvider ?? TimeProvider.System;
    _registrationSource = registrationSource ?? MessageTagRegistry.GetAllTags;
    // Lazy + thread-safe: built on first mint, after module initializers have populated the
    // registry and after host configuration has finalized the bindings.
    _index = new(_buildIndex, LazyThreadSafetyMode.ExecutionAndPublication);
  }

  /// <summary>
  /// Whether any enabled (SlideSeconds &gt; 0) coalesce binding exists at all — the cheap guard
  /// callers use to skip per-message work entirely when the feature is unused.
  /// </summary>
  public bool HasEnabledBindings => _tagOptions.CoalesceBindings.Any(b => b.Value.SlideSeconds > 0);

  /// <summary>
  /// Returns the enabled coalesce policy bound to <paramref name="group"/>, or null when the
  /// group is unknown or disabled (SlideSeconds = 0).
  /// </summary>
  /// <param name="group">The coalesce group (tag string).</param>
  /// <returns>The policy, or null.</returns>
  public CoalescePolicyOptions? GetBinding(string group) {
    ArgumentNullException.ThrowIfNull(group);
    return _tagOptions.CoalesceBindings.TryGetValue(group, out var policy) && policy.SlideSeconds > 0
      ? policy
      : null;
  }

  /// <summary>
  /// Stamps <paramref name="message"/> with its coalesce group and max-delay floor when its
  /// type carries an enabled coalesce-bound tag; returns the message unchanged otherwise
  /// (unbound type, already stamped, or caller-scheduled).
  /// </summary>
  /// <param name="message">The outbox message about to be queued.</param>
  /// <returns>The (possibly stamped copy of the) message to queue.</returns>
  public OutboxMessage ApplyCoalescePolicy(OutboxMessage message) {
    ArgumentNullException.ThrowIfNull(message);

    if (message.CoalesceGroup is not null || message.ScheduledFor is not null) {
      return message;
    }

    var group = _resolveGroup(message.MessageType);
    if (group is null) {
      return message;
    }

    // _resolveGroup only returns groups with an enabled binding, so this lookup always hits.
    var binding = _tagOptions.CoalesceBindings[group];
    return message with {
      CoalesceGroup = group,
      ScheduledFor = _timeProvider.GetUtcNow().AddSeconds(binding.MaxDelaySeconds)
    };
  }

  private string? _resolveGroup(string messageTypeName) {
    return _groupByTypeName.GetOrAdd(messageTypeName, name => {
      var (lookup, groupByType) = _index.Value;
      return EventTypeMatchingHelper.TryResolveType(lookup, name, out var type)
        ? groupByType[type]
        : null;
    });
  }

  private (Dictionary<string, Type> Lookup, Dictionary<Type, string> GroupByType) _buildIndex() {
    var enabledTags = new HashSet<string>(StringComparer.Ordinal);
    foreach (var binding in _tagOptions.CoalesceBindings) {
      if (binding.Value.SlideSeconds > 0) {
        enabledTags.Add(binding.Key);
      }
    }

    var groupByType = new Dictionary<Type, string>();
    if (enabledTags.Count > 0) {
      foreach (var registration in _registrationSource()) {
        if (enabledTags.Contains(registration.Tag)) {
          // Startup validation (TagPolicyValidator) guarantees at most one enabled binding
          // per message type, so a plain assignment cannot lose information.
          groupByType[registration.MessageType] = registration.Tag;
        }
      }
    }

    var lookup = EventTypeMatchingHelper.BuildTypeLookup([.. groupByType.Keys]);
    return (lookup, groupByType);
  }
}
