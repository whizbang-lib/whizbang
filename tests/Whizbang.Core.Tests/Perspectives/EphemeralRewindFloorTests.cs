// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Perspectives;

namespace Whizbang.Core.Tests.Perspectives;

/// <summary>
/// #2295: the marker a perspective that keeps no rewind floor registers beside its associations, in the shape
/// <c>register_message_associations</c> reads.
/// </summary>
/// <docs>fundamentals/events/ephemeral-events#snapshots-follow-what-a-read-model-consumes</docs>
public class EphemeralRewindFloorTests {
  [Test]
  public async Task TheMarkerEntry_FollowsAnEntry_AndNamesThePerspectiveAsItsTargetAsync() {
    var entry = EphemeralRewindFloor.NoFloorAssociationEntryJson("App.Presence+Perspective", "svc");

    await Assert.That(entry).StartsWith(",").Because("it is appended after the perspective associations");
    using var doc = JsonDocument.Parse(entry[1..]);
    var root = doc.RootElement;
    await Assert.That(root.GetProperty("AssociationType").GetString()).IsEqualTo(EphemeralRewindFloor.NO_FLOOR_ASSOCIATION_TYPE);
    await Assert.That(root.GetProperty("TargetName").GetString()).IsEqualTo("App.Presence+Perspective")
      .Because("the reaper matches the marker to the perspective by target name");
    await Assert.That(root.GetProperty("MessageType").GetString()).IsEqualTo("App.Presence+Perspective")
      .Because("one marker per perspective: the message type is the perspective itself, never an event type");
    await Assert.That(root.GetProperty("ServiceName").GetString()).IsEqualTo("svc")
      .Because("the orphan delete is scoped to the service, so the marker goes when the perspective stops qualifying");
  }

  [Test]
  public async Task TheMarkerEntry_EscapesWhatItQuotesAsync() {
    var entry = EphemeralRewindFloor.NoFloorAssociationEntryJson("A\"B", "s\\v");

    using var doc = JsonDocument.Parse(entry[1..]);
    await Assert.That(doc.RootElement.GetProperty("TargetName").GetString()).IsEqualTo("A\"B");
    await Assert.That(doc.RootElement.GetProperty("ServiceName").GetString()).IsEqualTo("s\\v");
  }
}
