using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Tags;

/// <summary>
/// The AOT tag lookup the RECEIVE boundary consults (transport traffic classes, topology arc
/// phase 9): answers whether a message's stored type-name string belongs to the control class —
/// the <see cref="SystemTags.CONTROL"/> tag — so the non-durable receive path can be taken for it
/// and for nothing else.
/// </summary>
/// <remarks>
/// <para>
/// The sibling of <see cref="TransportNamespaceResolver"/> and built the same way, for the same
/// reason: the receive path holds only the persisted type-name STRING, never a <see cref="Type"/>,
/// so the registry's Type-keyed entries are indexed once (lazily, after every module initializer
/// has registered) and resolution is cached per distinct name — no <c>Type.GetType</c>, no
/// attribute reflection, the <c>CoalesceGroupResolver</c> idiom.
/// </para>
/// <para>
/// An unresolvable or untagged name is NOT control class. That direction of the default is
/// deliberate: guessing wrong toward "control" would silently take a domain message off the
/// durable inbox, which is data loss; guessing wrong toward "durable" merely keeps today's
/// behavior.
/// </para>
/// </remarks>
/// <docs>fundamentals/messages/message-tags#system-tags</docs>
/// <tests>tests/Whizbang.Core.Tests/Workers/TransportConsumerWorkerControlClassReceiveTests.cs:ControlClassResolver_RecognizesTaggedTypesByNameAsync</tests>
public sealed class ControlClassResolver {
  private readonly Func<IEnumerable<MessageTagRegistration>> _registrationSource;
  private readonly ConcurrentDictionary<string, bool> _byTypeName = new(StringComparer.Ordinal);
  private readonly Lazy<(Dictionary<string, Type> Lookup, HashSet<Type> Members)> _index;

  /// <summary>
  /// Creates a resolver over the discovered tag registrations.
  /// </summary>
  /// <param name="registrationSource">
  /// Tag registration source; defaults to <see cref="MessageTagRegistry.GetAllTags"/>.
  /// Injectable so tests avoid the process-global registry.
  /// </param>
  public ControlClassResolver(Func<IEnumerable<MessageTagRegistration>>? registrationSource = null) {
    _registrationSource = registrationSource ?? MessageTagRegistry.GetAllTags;
    _index = new(_buildIndex, LazyThreadSafetyMode.ExecutionAndPublication);
  }

  /// <summary>
  /// Whether <paramref name="messageTypeName"/> names a control-class message.
  /// </summary>
  /// <param name="messageTypeName">The stored message type-name string (any canonical form
  /// <see cref="EventTypeMatchingHelper"/> understands); null or empty answers false.</param>
  /// <returns>True only for types carrying <see cref="SystemTags.CONTROL"/>.</returns>
  public bool IsControlClass(string? messageTypeName) {
    if (string.IsNullOrEmpty(messageTypeName)) {
      return false;
    }

    return _byTypeName.GetOrAdd(messageTypeName, name => {
      var (lookup, members) = _index.Value;
      return EventTypeMatchingHelper.TryResolveType(lookup, name, out var type) && members.Contains(type);
    });
  }

  private (Dictionary<string, Type> Lookup, HashSet<Type> Members) _buildIndex() {
    var members = new HashSet<Type>();
    foreach (var registration in _registrationSource()) {
      if (string.Equals(registration.Tag, SystemTags.CONTROL, StringComparison.Ordinal)) {
        members.Add(registration.MessageType);
      }
    }

    return (EventTypeMatchingHelper.BuildTypeLookup([.. members]), members);
  }
}
