// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Routing;

namespace Whizbang.Core.Tests.Routing;

/// <summary>
/// Branch backfill for <see cref="TopologyManifestBuilder.Build"/>'s deterministic ordering: two
/// publish destinations for the same message type are ordered by kind, not by catalog order.
/// </summary>
public class TopologyManifestBranchCoverageTests {

  [Test]
  public async Task Build_SameTypeUnderTwoKinds_OrdersByKindWhateverTheCatalogOrderAsync() {
    var type = typeof(OutboxTestTypes.Orders.Events.OrderCreated);
    var context = new InboxSubscriptionContext(
      "OrderService", new HashSet<string>(["outboxtesttypes.orders.commands"], StringComparer.OrdinalIgnoreCase), []);
    // The event row comes first in the catalog; the command row second.
    var catalog = new[] {
      new MessageTypeCatalogEntry(type, type.FullName!, "event", PinnedId: null),
      new MessageTypeCatalogEntry(type, type.FullName!, "command", PinnedId: null),
    };

    var manifest = TopologyManifestBuilder.Build(new SharedTopicOutboxStrategy(), new SharedTopicInboxStrategy(), context, catalog);

    await Assert.That(manifest.PublishDestinations.Count).IsEqualTo(2);
    await Assert.That(manifest.PublishDestinations[0].Kind).IsEqualTo(MessageKind.Command)
      .Because("on a type-name tie the kind breaks it, so catalog enumeration order cannot leak into the manifest");
    await Assert.That(manifest.PublishDestinations[1].Kind).IsEqualTo(MessageKind.Event);
  }

  /// <summary>
  /// #2074: a plain message (neither command nor event) has its own catalog kind, "message". It still publishes the
  /// way it did when the catalog called it an event, so the topology (and what a provisioner creates) is unchanged.
  /// </summary>
  [Test]
  public async Task Build_APlainMessage_PublishesAsItDidBeforeItHadItsOwnKindAsync() {
    var type = typeof(OutboxTestTypes.Orders.Events.OrderCreated);
    var context = new InboxSubscriptionContext("OrderService", new HashSet<string>(StringComparer.OrdinalIgnoreCase), []);
    var asMessage = new[] { new MessageTypeCatalogEntry(type, type.FullName!, "message", PinnedId: null) };
    var asEvent = new[] { new MessageTypeCatalogEntry(type, type.FullName!, "event", PinnedId: null) };

    var message = TopologyManifestBuilder.Build(new SharedTopicOutboxStrategy(), new SharedTopicInboxStrategy(), context, asMessage);
    var @event = TopologyManifestBuilder.Build(new SharedTopicOutboxStrategy(), new SharedTopicInboxStrategy(), context, asEvent);

    await Assert.That(message.PublishDestinations).IsEquivalentTo(@event.PublishDestinations)
      .Because("the kind split must not move where a plain message publishes or what the provisioner creates for it");
  }
}
