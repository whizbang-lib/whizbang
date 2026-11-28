// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// #2588: the comparison's one entry point for integrity exclusions, without a store: an excluded event's lanes come out
/// of the origin's digest by XOR, its count is taken off, the id lanes follow only where the origin sent them, and a host
/// without a store connection (or an answer with nothing in it) is passed through untouched.
/// </summary>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/IntegrityExclusionAdjustment.cs</code-under-test>
[Category("Unit")]
[Category("Shard1")]
public class IntegrityExclusionAdjustmentTests {
  private const string TYPE = "Contracts.Probe, Contracts";

  [Test]
  public async Task Subtract_TakesTheLanesOutByXor_AndTheCountOff_AndFollowsTheIdLanesTheOriginSentAsync() {
    var origin = new StreamDigest { EventType = TYPE, StreamId = Guid.Empty, DigestLo = 0b1100, DigestHi = 0b1010, EventCount = 3, IdLo = 7, IdHi = 9 };
    var removal = new StreamDigest { EventType = TYPE, StreamId = Guid.Empty, DigestLo = 0b0100, DigestHi = 0b0010, EventCount = 1, IdLo = 3, IdHi = 1 };

    var adjusted = IntegrityExclusionAdjustment.Subtract(origin, removal);
    var withoutIdLanes = IntegrityExclusionAdjustment.Subtract(origin with { IdLo = null, IdHi = null }, removal);
    var removalWithoutIdLanes = IntegrityExclusionAdjustment.Subtract(origin, removal with { IdLo = null, IdHi = null });

    await Assert.That(adjusted.DigestLo).IsEqualTo(0b1000L);
    await Assert.That(adjusted.DigestHi).IsEqualTo(0b1000L);
    await Assert.That(adjusted.EventCount).IsEqualTo(2);
    await Assert.That(adjusted.IdLo).IsEqualTo(7L ^ 3L);
    await Assert.That(adjusted.IdHi).IsEqualTo(9L ^ 1L);
    await Assert.That(withoutIdLanes.IdLo).IsNull().Because("an origin that sent no id lanes keeps none");
    await Assert.That(withoutIdLanes.IdHi).IsNull();
    await Assert.That(removalWithoutIdLanes.IdLo).IsEqualTo(7L);
    await Assert.That(removalWithoutIdLanes.IdHi).IsEqualTo(9L);
  }

  [Test]
  public async Task Apply_WithoutAStoreConnection_OrWithAnEmptyAnswer_ChangesNothingAsync() {
    await using var services = new ServiceCollection().BuildServiceProvider();
    var answer = _answer([new StreamDigest { EventType = TYPE, StreamId = Guid.Empty, DigestLo = 1, DigestHi = 2, EventCount = 1 }]);
    var empty = _answer([]);

    var noStore = await IntegrityExclusionAdjustment.ApplyAsync(services, answer, CancellationToken.None);
    var nothingToAdjust = await IntegrityExclusionAdjustment.ApplyAsync(services, empty, CancellationToken.None);
    var counts = await IntegrityExclusionAdjustment.CheckpointCountsAsync(services, answer.OriginServiceId, 0, 10, CancellationToken.None);

    await Assert.That(noStore.Message).IsSameReferenceAs(answer);
    await Assert.That(noStore.Pending).IsEmpty();
    await Assert.That(nothingToAdjust.Message).IsSameReferenceAs(empty);
    await Assert.That(counts).IsEmpty();
  }

  private static IntegrityManifest _answer(List<StreamDigest> digests) => new() {
    ManifestStreamId = TrackedGuid.New().Value,
    OriginServiceId = TrackedGuid.New().Value,
    OriginServiceName = "origin-svc",
    Level = ManifestLevel.Types,
    Digests = digests,
  };
}
