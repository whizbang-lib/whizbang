// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.Reflection;
using Whizbang.Core.Messaging;
using Whizbang.Core.Minting;
using Whizbang.Core.Serialization;

namespace Whizbang.Core.Tags;

/// <summary>What kind of message a <see cref="MessageChanges"/> describes, which says where its answer came from.</summary>
/// <docs>fundamentals/messages/message-tags#changed-properties</docs>
public enum MessageChangeKind {
  /// <summary>
  /// A per-stream event. One event type is one set of fields, so the change is the whole event:
  /// <see cref="MessageChanges.EventProperties"/>.
  /// </summary>
  Event,

  /// <summary>
  /// A collective event. The fields are the ones its <c>[CollectiveApplyFor]</c> specs assign, per model, in
  /// <see cref="MessageChanges.ByModel"/>: filled once the specs have applied, empty before.
  /// </summary>
  Collective,

  /// <summary>A composite event: the changes are its inner events', in <see cref="MessageChanges.Inner"/>.</summary>
  Composite,
}

/// <summary>
/// Which fields a message changed, asked the same way for every kind of message (#1045). A tag hook that turns events
/// into change notifications reads it from <see cref="TagContext{TAttribute}.Changes"/> rather than keeping a list
/// beside each event that drifts silently from the handlers.
/// </summary>
/// <remarks>
/// <para>
/// For a collective event the framework is the only participant that sees every handler: the specs can live in other
/// assemblies than the publisher, and a newly added handler contributes fields nothing else declared. The properties
/// are read from each spec's setters as it applies, grouped by model type, because two models can share a property
/// name and mean different things by it.
/// </para>
/// <para>
/// Indirection is out of reach and stays out: a setter that points a row at another record reports that key, while
/// the fields a reader perceives as changed belong to the record it references. The consumer knows what the
/// reference means; the framework does not.
/// </para>
/// </remarks>
/// <docs>fundamentals/messages/message-tags#changed-properties</docs>
/// <tests>tests/Whizbang.Core.Tests/Tags/MessageChangesTests.cs</tests>
public sealed class MessageChanges {
  private static readonly ConcurrentDictionary<Type, IReadOnlyList<string>> _eventProperties = new();
  private static readonly IReadOnlyDictionary<Type, IReadOnlyList<string>> _noModels =
    new Dictionary<Type, IReadOnlyList<string>>();

  private readonly Func<IReadOnlyList<MessageChanges>>? _inner;
  private IReadOnlyList<MessageChanges>? _innerValue;

  private MessageChanges(
      MessageChangeKind kind,
      IReadOnlyList<string> eventProperties,
      IReadOnlyDictionary<Type, IReadOnlyList<string>> byModel,
      Func<IReadOnlyList<MessageChanges>>? inner) {
    Kind = kind;
    EventProperties = eventProperties;
    ByModel = byModel;
    _inner = inner;
  }

  /// <summary>What kind of message this describes.</summary>
  public MessageChangeKind Kind { get; }

  /// <summary>The event's own properties, for a per-stream event; empty otherwise.</summary>
  public IReadOnlyList<string> EventProperties { get; }

  /// <summary>
  /// For a collective event, the properties each model's specs assigned, by model type; empty for any other kind, and
  /// for a collective event whose specs have not applied yet.
  /// </summary>
  public IReadOnlyDictionary<Type, IReadOnlyList<string>> ByModel { get; }

  /// <summary>
  /// For a composite event, its inner events' changes in the order it yields them; empty for any other kind. Reading
  /// it enumerates the composite's inner events again.
  /// </summary>
  public IReadOnlyList<MessageChanges> Inner => _innerValue ??= _inner?.Invoke() ?? [];

  /// <summary>The changes of a per-stream event: its own properties.</summary>
  /// <param name="eventType">The event type.</param>
  public static MessageChanges ForEvent(Type eventType) {
    ArgumentNullException.ThrowIfNull(eventType);
    return new(MessageChangeKind.Event, _propertiesOf(eventType), _noModels, inner: null);
  }

  /// <summary>The changes a collective event's specs made, by model type.</summary>
  /// <param name="byModel">The properties each model's specs assigned.</param>
  public static MessageChanges ForCollective(IReadOnlyDictionary<Type, IReadOnlyList<string>> byModel) {
    ArgumentNullException.ThrowIfNull(byModel);
    return new(MessageChangeKind.Collective, [], byModel, inner: null);
  }

  /// <summary>The changes of a composite event: one per inner event.</summary>
  /// <param name="composite">The composite.</param>
  public static MessageChanges ForComposite(ICompositeEvent composite) {
    ArgumentNullException.ThrowIfNull(composite);
    return new(MessageChangeKind.Composite, [], _noModels,
      () => [.. composite.InnerEvents.Select(inner => For(inner, inner.GetType()))]);
  }

  /// <summary>
  /// The changes a message describes before anything has applied it: the whole event for a per-stream event, nothing
  /// yet for a collective event, the inner events for a composite.
  /// </summary>
  /// <param name="message">The message.</param>
  /// <param name="messageType">Its type.</param>
  public static MessageChanges For(object message, Type messageType) => message switch {
    ICompositeEvent composite => ForComposite(composite),
    ICollectiveEvent => ForCollective(_noModels),
    _ => ForEvent(messageType),
  };

  // The event's properties from its serializer metadata, which the generated JSON contexts supply without reflection;
  // a type no context knows reports none rather than guessing.
  private static IReadOnlyList<string> _propertiesOf(Type eventType) {
    if (_eventProperties.TryGetValue(eventType, out var known)) {
      return known;
    }
    IReadOnlyList<string> names = JsonContextRegistry.CreateCombinedOptions().TryGetTypeInfo(eventType, out var info)
      ? [.. info.Properties.Select(p => p.AttributeProvider is MemberInfo member ? member.Name : p.Name)]
      : [];
    _eventProperties[eventType] = names;
    return names;
  }
}
