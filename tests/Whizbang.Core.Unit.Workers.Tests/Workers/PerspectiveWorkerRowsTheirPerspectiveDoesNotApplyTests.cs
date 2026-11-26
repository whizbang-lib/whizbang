// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// #2601: the fetched rows whose perspective applies none of the batch's typed events for them, by (stream, perspective).
/// These are the only rows the drain reads stored cursors for, to run a flagged rewind no drain pair can ask for.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Workers/PerspectiveWorker.FlaggedRewinds.cs</code-under-test>
public class PerspectiveWorkerRowsTheirPerspectiveDoesNotApplyTests {
  private const string READER = "Orders+Projection";
  private const string OTHER = "Invoices+Projection";

  private sealed record ReadEvent(string Data) : IEvent;
  private sealed record UnreadEvent(string Data) : IEvent;

  private static StreamEventData _row(Guid streamId, Guid eventId, string? perspective) => new() {
    StreamId = streamId,
    EventId = eventId,
    EventType = "Probe.Event, Probe",
    EventData = "{}",
    EventWorkId = Guid.CreateVersion7(),
    PerspectiveName = perspective,
  };

  private static MessageEnvelope<IEvent> _envelope(Guid eventId, IEvent payload) => new() {
    MessageId = new MessageId(eventId),
    Payload = payload,
    Hops = [],
    DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local },
  };

  [Test]
  public async Task RowsTheirPerspectiveDoesNotApply_AreGroupedByPair_LeavingTheAppliedTheSinkAndTheUnnamedAsync() {
    var stream = Guid.CreateVersion7();
    var (read1, read2, unread, unknown) = (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());
    List<MessageEnvelope<IEvent>> typed = [
      _envelope(read1, new ReadEvent("a")), _envelope(read2, new ReadEvent("b")), _envelope(unread, new UnreadEvent("c"))];
    var map = new Dictionary<string, IReadOnlyList<string>> {
      [Whizbang.Core.TypeNameFormatter.Format(typeof(ReadEvent))] = [READER],
    };
    var unreadRow = _row(stream, unread, READER);
    var unknownRow = _row(stream, unknown, READER);
    var otherRow = _row(stream, read1, OTHER);
    List<StreamEventData> rows = [
      _row(stream, read1, READER), _row(stream, read2, READER), unreadRow, unknownRow, otherRow,
      _row(stream, unknown, CollectiveRouting.SINK_PERSPECTIVE_NAME), _row(stream, unknown, null)];

    var unapplied = PerspectiveWorker.RowsTheirPerspectiveDoesNotApply(rows, typed, map);

    await Assert.That(unapplied.Keys).IsEquivalentTo([(stream, READER), (stream, OTHER)]);
    await Assert.That(unapplied[(stream, READER)]).IsEquivalentTo([unreadRow, unknownRow])
      .Because("a type the perspective does not read and an event the store did not read are both unapplied");
    await Assert.That(unapplied[(stream, OTHER)]).IsEquivalentTo([otherRow])
      .Because("an event a different perspective reads is not this one's");
  }
}
