// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using TUnit.Assertions;
using TUnit.Core;
using Whizbang.Core.Perspectives.Sync;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// The hand-written inquiry array the batch sync-status function reads. Each flag must arrive as a
/// JSON boolean in both states: the function reads a missing or misspelled flag as false, so a true
/// flag that fails to serialize silently drops the pending or processed id lists.
/// </summary>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/EFCoreWorkCoordinator.cs</code-under-test>
[Category("Shard2")]
public class InquiriesJsonTests {
  private static readonly string[] _typeFilter = ["Orders.OrderPlaced", "Orders.Order\\Shipped"];

  [Test]
  public async Task BuildInquiriesJson_WritesEveryFlagInBothStatesAndTheOptionalArraysAsync() {
    var eventA = (Guid)TrackedGuid.New();
    var eventB = (Guid)TrackedGuid.New();
    SyncInquiry[] inquiries = [
      new SyncInquiry {
        StreamId = (Guid)TrackedGuid.New(),
        PerspectiveName = "Orders.\"Summary\"",
        DiscoverPendingFromOutbox = true,
        IncludePendingEventIds = true,
        IncludeProcessedEventIds = true,
        EventIds = [eventA, eventB],
        EventTypeFilter = _typeFilter
      },
      new SyncInquiry {
        StreamId = (Guid)TrackedGuid.New(),
        PerspectiveName = "Orders.Detail",
        DiscoverPendingFromOutbox = false,
        IncludePendingEventIds = false,
        IncludeProcessedEventIds = false
      }
    ];

    var json = EFCoreWorkCoordinator<WorkCoordinationDbContext>.BuildInquiriesJson(inquiries);

    using var doc = JsonDocument.Parse(json);
    var root = doc.RootElement;
    await Assert.That(root.GetArrayLength()).IsEqualTo(2);

    var all = root[0];
    await Assert.That(all.GetProperty("InquiryId").GetGuid()).IsEqualTo(inquiries[0].InquiryId);
    await Assert.That(all.GetProperty("StreamId").GetGuid()).IsEqualTo(inquiries[0].StreamId);
    await Assert.That(all.GetProperty("PerspectiveName").GetString()).IsEqualTo("Orders.\"Summary\"");
    await Assert.That(all.GetProperty("DiscoverPendingFromOutbox").ValueKind).IsEqualTo(JsonValueKind.True);
    await Assert.That(all.GetProperty("IncludePendingEventIds").ValueKind).IsEqualTo(JsonValueKind.True);
    await Assert.That(all.GetProperty("IncludeProcessedEventIds").ValueKind).IsEqualTo(JsonValueKind.True);
    var ids = all.GetProperty("EventIds").EnumerateArray().Select(e => e.GetGuid()).ToArray();
    await Assert.That(ids).IsEquivalentTo([eventA, eventB]);
    var types = all.GetProperty("EventTypeFilter").EnumerateArray().Select(e => e.GetString() ?? "<null>").ToArray();
    await Assert.That(types).IsEquivalentTo(_typeFilter);

    var none = root[1];
    await Assert.That(none.GetProperty("DiscoverPendingFromOutbox").ValueKind).IsEqualTo(JsonValueKind.False);
    await Assert.That(none.GetProperty("IncludePendingEventIds").ValueKind).IsEqualTo(JsonValueKind.False);
    await Assert.That(none.GetProperty("IncludeProcessedEventIds").ValueKind).IsEqualTo(JsonValueKind.False);
    await Assert.That(none.TryGetProperty("EventIds", out _)).IsFalse();
    await Assert.That(none.TryGetProperty("EventTypeFilter", out _)).IsFalse();
  }
}
