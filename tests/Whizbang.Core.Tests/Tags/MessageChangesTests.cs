// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Attributes;
using Whizbang.Core.Messaging;
using Whizbang.Core.Minting;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Tags;

namespace Whizbang.Core.Tests.Tags;

/// <summary>
/// One way to ask which fields a message changed, whatever kind it is (#1045): a per-stream event reports itself, a
/// collective event its applied specs by model, a composite its inner events.
/// </summary>
/// <docs>fundamentals/messages/message-tags#changed-properties</docs>
[Category("Core")]
[Category("Tags")]
public class MessageChangesTests {
  [Test]
  public async Task ForEvent_ReportsTheEventsOwnPropertiesAsync() {
    var changes = MessageChanges.ForEvent(typeof(ChangesTestOrderPlaced));

    await Assert.That(changes.Kind).IsEqualTo(MessageChangeKind.Event);
    await Assert.That(changes.EventProperties).IsEquivalentTo(["OrderId", "Status"]);
    await Assert.That(changes.ByModel).IsEmpty();
    await Assert.That(changes.Inner).IsEmpty();
    await Assert.That(MessageChanges.ForEvent(typeof(ChangesTestOrderPlaced)).EventProperties)
      .IsSameReferenceAs(changes.EventProperties).Because("a type's properties are read once");
  }

  [Test]
  public async Task ForEvent_OfATypeNoContextKnows_ReportsNoPropertiesAsync() =>
    await Assert.That(MessageChanges.ForEvent(typeof(Unknown)).EventProperties).IsEmpty();

  [Test]
  public async Task For_ACollectiveEventBeforeItApplied_ReportsNoModelsYetAsync() {
    var changes = MessageChanges.For(new Archive(), typeof(Archive));

    await Assert.That(changes.Kind).IsEqualTo(MessageChangeKind.Collective);
    await Assert.That(changes.ByModel).IsEmpty();
    await Assert.That(changes.EventProperties).IsEmpty();
  }

  [Test]
  public async Task For_ACompositeEvent_ReportsEachInnerEventAsync() {
    var changes = MessageChanges.For(new Bundle([new Archive(), new Unknown()]), typeof(Bundle));

    await Assert.That(changes.Kind).IsEqualTo(MessageChangeKind.Composite);
    await Assert.That(changes.Inner.Select(i => i.Kind)).IsEquivalentTo([MessageChangeKind.Collective, MessageChangeKind.Event]);
    await Assert.That(changes.Inner).IsSameReferenceAs(changes.Inner).Because("the inner events are enumerated once");
  }

  [Test]
  public async Task For_APerStreamEvent_ReportsTheEventAsync() =>
    await Assert.That(MessageChanges.For(new Unknown(), typeof(Unknown)).Kind).IsEqualTo(MessageChangeKind.Event);

  [Test]
  public async Task Factories_RejectNullAsync() {
    await Assert.That(() => MessageChanges.ForEvent(null!)).Throws<ArgumentNullException>();
    await Assert.That(() => MessageChanges.ForCollective(null!)).Throws<ArgumentNullException>();
    await Assert.That(() => MessageChanges.ForComposite(null!)).Throws<ArgumentNullException>();
  }

  /// <summary>A context nobody gave changes to describes its own message.</summary>
  [Test]
  public async Task TagContext_WithoutChanges_DescribesItsMessageAsync() {
    var context = new TagContext<MessageTagAttribute> {
      Attribute = new SignalTagAttribute { Tag = "t" },
      Message = new Archive(),
      MessageType = typeof(Archive),
      Payload = JsonDocument.Parse("{}").RootElement,
    };

    await Assert.That(context.Changes.Kind).IsEqualTo(MessageChangeKind.Collective);
    await Assert.That(context.Changes).IsSameReferenceAs(context.Changes);
  }

  private sealed record Unknown : IEvent;

  private sealed record Archive : ICollectiveEvent {
    public CollectiveScope Scope { get; init; } = null!;
    public IReadOnlyList<Guid> MatchedStreamIds { get; init; } = [];
  }

  private sealed record Bundle(IReadOnlyList<IMessage> Events) : ICompositeEvent {
    public IEnumerable<IMessage> InnerEvents => Events;
  }
}

/// <summary>A per-stream event the generated JSON context knows, so its properties come from serializer metadata.</summary>
public sealed record ChangesTestOrderPlaced([property: StreamId] Guid OrderId, string Status) : IEvent;
