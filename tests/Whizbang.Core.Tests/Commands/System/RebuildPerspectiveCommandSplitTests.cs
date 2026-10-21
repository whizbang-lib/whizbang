// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Globalization;
using TUnit.Core;
using Whizbang.Core.Commands.System;
using Whizbang.Core.Messaging;
using Whizbang.Core.Perspectives;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Commands.System;

/// <summary>
/// #1525: a rebuild of many named streams is sent as several commands, each naming a bounded share of the streams and
/// carrying its own request id derived from the caller's, so no one command carries an unbounded list through the
/// outbox, the transport, each inbox and command audit.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Commands/System/SystemCommands.cs</code-under-test>
/// <docs>fundamentals/perspectives/rebuild#large-stream-selections</docs>
public class RebuildPerspectiveCommandSplitTests {
  private static Guid[] _streams(int count) => [.. Enumerable.Range(0, count).Select(_ => Guid.CreateVersion7())];

  [Test]
  public async Task SplitByStreams_ALargeSelection_BecomesCommandsOfAtMostTheChunkSize_InOrderAsync() {
    var streams = _streams(1201);
    var command = new RebuildPerspectiveCommand(["OrderSummary"], RebuildMode.SelectedStreams, IncludeStreamIds: streams);

    var chunks = command.SplitByStreams(500);

    await Assert.That(chunks.Select(c => c.IncludeStreamIds!.Length)).IsEquivalentTo([500, 500, 201], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    await Assert.That(chunks.SelectMany(c => c.IncludeStreamIds!)).IsEquivalentTo(streams, TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("every stream is named exactly once, in the order the caller gave them");
  }

  [Test]
  public async Task SplitByStreams_DerivesEachRequestIdFromTheCallersAndTheChunkIndexAsync() {
    var requestId = Guid.CreateVersion7();
    var command = new RebuildPerspectiveCommand(IncludeStreamIds: _streams(3), RequestId: requestId);

    var chunks = command.SplitByStreams(1);
    var again = command.SplitByStreams(1);

    await Assert.That(chunks.Select(c => c.RequestId!.Value)).IsEquivalentTo(
      [RebuildPerspectiveCommand.ChunkRequestId(requestId, 0), RebuildPerspectiveCommand.ChunkRequestId(requestId, 1), RebuildPerspectiveCommand.ChunkRequestId(requestId, 2)],
      TUnit.Assertions.Enums.CollectionOrdering.Matching);
    await Assert.That(chunks.Select(c => c.RequestId).Distinct().Count()).IsEqualTo(3)
      .Because("each chunk is its own run, so a redelivered chunk finds its own run and no other");
    await Assert.That(again.Select(c => c.RequestId)).IsEquivalentTo(chunks.Select(c => c.RequestId), TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("the same request splits into the same ids every time, so a resent chunk is the same run");
  }

  [Test]
  public async Task ChunkRequestId_IsTheNameBasedIdOfTheChunkIndexWithinTheRequestAsync() {
    var requestId = Guid.CreateVersion7();

    await Assert.That(RebuildPerspectiveCommand.ChunkRequestId(requestId, 7))
      .IsEqualTo(NameBasedGuid.V5(requestId, 7.ToString(CultureInfo.InvariantCulture)));
  }

  [Test]
  public async Task SplitByStreams_WithoutARequestId_LeavesEachChunkWithoutOneAsync() {
    var chunks = new RebuildPerspectiveCommand(IncludeStreamIds: _streams(3)).SplitByStreams(2);

    await Assert.That(chunks.Count).IsEqualTo(2);
    await Assert.That(chunks.All(c => c.RequestId is null)).IsTrue()
      .Because("with no caller id to derive from, each receiving service stamps its own, as for an unsplit command");
  }

  [Test]
  public async Task SplitByStreams_KeepsEveryOtherField_AndDropsTheExclusionsTheIncludeListOverridesAsync() {
    var command = new RebuildPerspectiveCommand(["OrderSummary"], RebuildMode.InPlace, IncludeStreamIds: _streams(2),
      ExcludeStreamIds: _streams(4), FromEventId: 42, RequestId: Guid.CreateVersion7(), RequestedBy: "operator-1");

    var chunk = command.SplitByStreams(5).Single();

    await Assert.That(chunk.PerspectiveNames).IsEquivalentTo(["OrderSummary"]);
    await Assert.That(chunk.Mode).IsEqualTo(RebuildMode.InPlace);
    await Assert.That(chunk.FromEventId).IsEqualTo(42L);
    await Assert.That(chunk.RequestedBy).IsEqualTo("operator-1");
    await Assert.That(chunk.ExcludeStreamIds).IsNull()
      .Because("a command that names streams ignores its exclusions, so each chunk would only carry them for nothing");
  }

  [Test]
  public async Task SplitByStreams_AStreamNamedTwice_IsRebuiltOnceAsync() {
    var stream = Guid.CreateVersion7();
    var other = Guid.CreateVersion7();

    var chunks = new RebuildPerspectiveCommand(IncludeStreamIds: [stream, other, stream]).SplitByStreams(10);

    await Assert.That(chunks.Single().IncludeStreamIds).IsEquivalentTo([stream, other], TUnit.Assertions.Enums.CollectionOrdering.Matching);
  }

  [Test]
  public async Task SplitByStreams_NoStreamSelection_ReturnsTheCommandUnchangedAsync() {
    var everyStream = new RebuildPerspectiveCommand(["OrderSummary"], RequestId: Guid.CreateVersion7());
    var emptyList = new RebuildPerspectiveCommand(["OrderSummary"], RebuildMode.SelectedStreams, IncludeStreamIds: []);

    await Assert.That(everyStream.SplitByStreams().Single()).IsSameReferenceAs(everyStream)
      .Because("a rebuild of every stream carries no list, so there is nothing to split");
    await Assert.That(emptyList.SplitByStreams().Single()).IsSameReferenceAs(emptyList)
      .Because("an empty selection is refused by the receptor as it stands; splitting must not turn it into something else");
  }

  [Test]
  public async Task SplitByStreams_ByDefault_NamesAtMostTheRedeliveryBoundPerCommandAsync() {
    const int bound = StreamRedeliveryRequester.MAX_STREAMS_PER_REQUEST;
    var chunks = new RebuildPerspectiveCommand(IncludeStreamIds: _streams(bound + 1)).SplitByStreams();

    await Assert.That(chunks.Select(c => c.IncludeStreamIds!.Length)).IsEquivalentTo([bound, 1], TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("by default a command names no more streams than a stream redelivery request does");
  }

  [Test]
  public async Task SplitByStreams_AChunkSizeBelowOne_IsRefusedAsync() {
    var command = new RebuildPerspectiveCommand(IncludeStreamIds: _streams(1));

    await Assert.That(() => command.SplitByStreams(0)).Throws<ArgumentOutOfRangeException>();
  }
}
