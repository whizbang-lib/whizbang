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
}
