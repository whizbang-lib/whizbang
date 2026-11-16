// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;

namespace Whizbang.Core.Tests.Perspectives;

/// <summary>
/// A prepared pair is exactly one of: a write for the batch, nothing to write, or a hand-back to the per-pair path.
/// A pair that is both a write and a hand-back would be written twice, once by the sink and once by the per-pair path,
/// so it cannot be built.
/// </summary>
/// <docs>fundamentals/perspectives/write-modes#prepare-and-commit</docs>
public class PreparedPerspectiveApplyTests {
  private static readonly PendingPerspectiveWrite _purge = PendingPerspectiveWrite.Purge("\"wh_per_x\"", Guid.NewGuid());

  [Test]
  public async Task PerPair_CarriesThePairAndItsReasonAsync() {
    var streamId = Guid.NewGuid();
    var lastEventId = Guid.NewGuid();
    IReadOnlyList<MessageEnvelope<IEvent>> events = [];

    var prepared = PreparedPerspectiveApply.PerPair(
      streamId, "orders", lastEventId, events, PerspectivePerPairReason.PurgedStream);

    await Assert.That(prepared.StreamId).IsEqualTo(streamId);
    await Assert.That(prepared.PerspectiveName).IsEqualTo("orders");
    await Assert.That(prepared.LastProcessedEventId).IsEqualTo(lastEventId);
    await Assert.That(prepared.Events).IsSameReferenceAs(events);
    await Assert.That(prepared.Write).IsNull();
    await Assert.That(prepared.PerPairReason).IsEqualTo(PerspectivePerPairReason.PurgedStream);
    await Assert.That(prepared.RunsPerPair).IsTrue();
  }

  [Test]
  public async Task PerPair_WithoutAReason_IsRefusedAsync() {
    await Assert.That(() => PreparedPerspectiveApply.PerPair(Guid.NewGuid(), "orders", null, [], PerspectivePerPairReason.None))
      .Throws<ArgumentOutOfRangeException>()
      .Because("a hand-back with no reason would read as a pair with nothing to write, and its events would be lost");
  }

  [Test]
  public async Task AWrite_IsNotRunPerPairAsync() {
    var prepared = new Pair(_purge, PerspectivePerPairReason.None);

    await Assert.That(prepared.Write).IsSameReferenceAs(_purge);
    await Assert.That(prepared.RunsPerPair).IsFalse();
  }

  [Test]
  public async Task AWriteThatIsAlsoHandedBack_IsRefusedAsync() {
    await Assert.That(() => new Pair(_purge, PerspectivePerPairReason.WriteNotPreparable))
      .Throws<ArgumentException>()
      .Because("the sink would write it and the per-pair path would write it again");
  }

  [Test]
  public async Task ABlankPerspectiveName_IsRefusedAsync() {
    await Assert.That(() => PreparedPerspectiveApply.PerPair(Guid.NewGuid(), " ", null, [], PerspectivePerPairReason.PurgedStream))
      .Throws<ArgumentException>();
  }

  [Test]
  public async Task NullEvents_AreRefusedAsync() {
    await Assert.That(() => PreparedPerspectiveApply.PerPair(Guid.NewGuid(), "orders", null, null!, PerspectivePerPairReason.PurgedStream))
      .Throws<ArgumentNullException>();
  }

  private sealed class Pair(PendingPerspectiveWrite? write, PerspectivePerPairReason reason)
    : PreparedPerspectiveApply(Guid.NewGuid(), "orders", null, [], write, reason);
}
