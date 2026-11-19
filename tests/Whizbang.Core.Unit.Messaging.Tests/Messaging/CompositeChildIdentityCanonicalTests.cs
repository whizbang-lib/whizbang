// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Minting;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// One derivation of a composite child's id, from one rendering of the child's type (#2266). The raw expansion read the
/// type name off the wire, the publisher's versioned assembly-qualified name; the typed expansion and the publisher's
/// local fan-out rendered the local CLR type the same way. A receiver whose contract assembly carries another version
/// derived a third id for the same child. The canonical rendering drops the version decoration, so every path derives
/// the same id from what it has.
/// </summary>
/// <docs>fundamentals/messaging/composite-events#deterministic-child-ids</docs>
[Category("Unit")]
public partial class CompositeChildIdentityCanonicalTests {
  private sealed record ChildEvent(string Id) : IEvent;

  private sealed class Composite(params IMessage[] inner) : ICompositeEvent {
    public IEnumerable<IMessage> InnerEvents => inner;
  }

  private sealed class RawBundle(params (string Type, string Json)[] inner) : IRawInnerComposite {
    public IEnumerable<IMessage> InnerEvents => [];
    public IReadOnlyList<JsonElement> InnerPayloads { get; } = [.. inner.Select(i => JsonSerializer.Deserialize<JsonElement>(i.Json))];
    public IReadOnlyList<string> InnerTypeNames { get; } = [.. inner.Select(i => i.Type)];
  }

  private sealed class Serializer : IEnvelopeSerializer {
    public SerializedEnvelope SerializeEnvelope<TMessage>(IMessageEnvelope<TMessage> envelope) {
      var aqn = envelope.Payload!.GetType().AssemblyQualifiedName!;
      var jsonEnv = new MessageEnvelope<JsonElement> {
        DispatchContext = envelope.DispatchContext,
        MessageId = envelope.MessageId,
        Payload = JsonElement.Parse("{}"),
        Hops = envelope.Hops?.ToList() ?? [],
      };
      return new SerializedEnvelope(jsonEnv, $"Whizbang.Core.Observability.MessageEnvelope`1[[{aqn}]], Whizbang.Core", aqn);
    }
    public object DeserializeMessage(MessageEnvelope<JsonElement> jsonEnvelope, string messageTypeName) => throw new NotSupportedException();
  }

  private static ServiceProvider _scope() =>
    new ServiceCollection().AddSingleton<IEnvelopeSerializer>(new Serializer()).BuildServiceProvider();

  private static MessageEnvelope<JsonElement> _source(Guid streamId, Guid compositeId) => new() {
    DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Outbox },
    MessageId = MessageId.From(compositeId),
    Payload = JsonSerializer.SerializeToElement(new { }),
    Hops = [new MessageHop {
      Type = HopType.Current,
      Timestamp = new DateTimeOffset(2026, 11, 11, 0, 0, 0, TimeSpan.Zero),
      ServiceInstance = ServiceInstanceInfo.Unknown,
      Metadata = new Dictionary<string, JsonElement> { ["AggregateId"] = JsonSerializer.SerializeToElement(streamId.ToString()) },
    }],
    SourceServiceId = Guid.Parse("00000000-0000-0000-0000-000000000001"),
    SourceCommitSequence = 42,
  };

  /// <summary>The child's type as a publisher on another build of the contract assembly writes it on the wire.</summary>
  private static string _wireNameFromAnotherBuild() {
    var type = typeof(ChildEvent);
    return $"{type.FullName}, {type.Assembly.GetName().Name}, Version=9.8.7.6, Culture=neutral, PublicKeyToken=null";
  }

  [Test]
  public async Task ForChild_TheTypeAndEveryRenderingOfItsName_DeriveOneIdAsync() {
    var composite = TrackedGuid.New().Value;
    var type = typeof(ChildEvent);

    var fromType = CompositeChildIdentity.ForChild(composite, 2, type);
    var fromLocalName = CompositeChildIdentity.ForChild(composite, 2, type.AssemblyQualifiedName!);
    var fromOtherBuild = CompositeChildIdentity.ForChild(composite, 2, _wireNameFromAnotherBuild());
    var fromBareName = CompositeChildIdentity.ForChild(composite, 2, $"{type.FullName}, {type.Assembly.GetName().Name}");

    await Assert.That(fromLocalName).IsEqualTo(fromType);
    await Assert.That(fromOtherBuild).IsEqualTo(fromType).Because("an assembly version says nothing about which child this is");
    await Assert.That(fromBareName).IsEqualTo(fromType);
  }

  // ===== generic and nested child types (#2266 hardening) =====

  /// <summary>A child type nested in another, whose CLR name carries a '+'. Only its name is hashed here.</summary>
  public sealed record NestedChild(string Id);

  /// <summary>A generic child type; its type arguments carry assembly-qualified names of their own.</summary>
  public sealed record GenericChild<T>(T Value);

  [System.Text.RegularExpressions.GeneratedRegex(@", Version=[^,\]]+, Culture=[^,\]]+, PublicKeyToken=[^,\]]+")]
  private static partial System.Text.RegularExpressions.Regex _decoration();

  private const string OTHER_BUILD = ", Version=9.8.7.6, Culture=neutral, PublicKeyToken=0123456789abcdef";

  /// <summary>
  /// The type's assembly-qualified name as a publisher on another build of EVERY assembly involved writes it: the
  /// decoration of the outer type and of each type argument, at any depth, is that build's.
  /// </summary>
  private static string _asAnotherBuildWritesIt(Type type) =>
    _decoration().Replace(TypeNameFormatter.AssemblyQualifiedName(type), OTHER_BUILD);

  private static IEnumerable<Type> _shapes() => [
    typeof(ChildEvent),
    typeof(NestedChild),
    typeof(GenericChild<string>),                                     // a type argument from the core library
    typeof(GenericChild<ChildEvent>),                                 // a type argument from this assembly
    typeof(GenericChild<GenericChild<NestedChild>>),                  // nested generic arguments, nested type inside
    typeof(GenericChild<Dictionary<string, List<NestedChild>>>),      // several arguments from several assemblies
  ];

  [Test]
  public async Task ForChild_GenericAndNestedTypes_DeriveOneIdFromTheTypeAndFromAnotherBuildsWireNameAsync() {
    var composite = TrackedGuid.New().Value;
    foreach (var type in _shapes()) {
      var wire = _asAnotherBuildWritesIt(type);
      await Assert.That(wire).IsNotEqualTo(type.AssemblyQualifiedName!).Because($"precondition: {type} is renamed");
      await Assert.That(CompositeChildIdentity.ForChild(composite, 1, wire))
        .IsEqualTo(CompositeChildIdentity.ForChild(composite, 1, type))
        .Because($"{type}: no assembly's version, at any depth of the name, says which child this is");
      await Assert.That(CompositeChildIdentity.ForChild(composite, 1, type.AssemblyQualifiedName!))
        .IsEqualTo(CompositeChildIdentity.ForChild(composite, 1, type));
    }
  }

  [Test]
  public async Task HashedTypeName_IsOneDefinition_ForTheTypeAndForEveryRenderingOfItsNameAsync() {
    foreach (var type in _shapes()) {
      var hashed = CompositeChildIdentity.HashedTypeName(type);
      await Assert.That(CompositeChildIdentity.HashedTypeName(type.AssemblyQualifiedName!)).IsEqualTo(hashed);
      await Assert.That(CompositeChildIdentity.HashedTypeName(_asAnotherBuildWritesIt(type))).IsEqualTo(hashed);
      await Assert.That(hashed.Contains("Version=", StringComparison.Ordinal)).IsFalse()
        .Because($"{type}: the hashed name carries no version at any depth ({hashed})");
      await Assert.That(hashed).IsEqualTo(EventTypeMatchingHelper.NormalizeTypeName(type.AssemblyQualifiedName!))
        .Because("the hashed name is the framework's version-free wire form, the one the SQL normalizer agrees with");
    }
  }

  [Test]
  public async Task HashedTypeName_ForATypeWithoutTypeArguments_IsTheCanonicalWireFormAsync() {
    // TypeNameFormatter.Format is the canonical "Namespace.Type, Assembly" form for a name with no type arguments.
    await Assert.That(CompositeChildIdentity.HashedTypeName(typeof(ChildEvent))).IsEqualTo(TypeNameFormatter.Format(typeof(ChildEvent)));
    await Assert.That(CompositeChildIdentity.HashedTypeName(typeof(NestedChild))).IsEqualTo(TypeNameFormatter.Format(typeof(NestedChild)));
    await Assert.That(CompositeChildIdentity.HashedTypeName(typeof(NestedChild)).Contains('+')).IsTrue()
      .Because("a nested type keeps the CLR '+' separator");
  }

  [Test]
  public async Task ForChild_DifferentTypes_StillDeriveDifferentIdsAsync() {
    var composite = TrackedGuid.New().Value;

    await Assert.That(CompositeChildIdentity.ForChild(composite, 0, typeof(ChildEvent)))
      .IsNotEqualTo(CompositeChildIdentity.ForChild(composite, 0, typeof(Composite)));
  }

  [Test]
  public async Task ForChild_ANullType_ThrowsAsync() {
    await Assert.That(() => CompositeChildIdentity.ForChild(TrackedGuid.New().Value, 0, (Type)null!)).Throws<ArgumentNullException>();
  }

  [Test]
  public async Task TryExpand_TheTypedAndTheRawPath_DeriveTheSameIds_WhenThePublisherRanAnotherBuildAsync() {
    var streamId = TrackedGuid.New().Value;
    var compositeId = TrackedGuid.New().Value;
    var typed = new Composite(new ChildEvent("a"), new ChildEvent("b"));
    var raw = new RawBundle((_wireNameFromAnotherBuild(), "{\"Id\":\"a\"}"), (_wireNameFromAnotherBuild(), "{\"Id\":\"b\"}"));

    var viaTyped = CompositeInboxFanout.TryExpand(typed, _source(streamId, compositeId), _scope());
    var viaRaw = CompositeInboxFanout.TryExpand(raw, _source(streamId, compositeId), _scope());

    await Assert.That(viaTyped.Outcome).IsEqualTo(CompositeInboxFanout.FanoutOutcome.Expanded);
    await Assert.That(viaRaw.Outcome).IsEqualTo(CompositeInboxFanout.FanoutOutcome.Expanded);
    await Assert.That(viaRaw.Children.Select(c => c.MessageId).ToList())
      .IsEquivalentTo(viaTyped.Children.Select(c => c.MessageId).ToList(), TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("the same child of the same composite is one event, whichever path expanded it and whichever build sent it");
  }
}
