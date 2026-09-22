using System.Text.Json;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Configuration;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Messaging;

#pragma warning disable CA1707

/// <summary>
/// Covers <see cref="EmptyStreamIdGuard.ThrowIfAnyHasEmptyStreamId(OutboxMessage[], EmptyStreamIdPolicy)"/>
/// and its inbox overload under a non-Reject policy — the sibling <c>EmptyStreamIdGuardTests</c> only
/// exercises the single-row primitive <see cref="EmptyStreamIdGuard.ThrowIfEmpty"/> directly, never the
/// batch overloads used at actual storage time.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Messaging/EmptyStreamIdGuard.cs</code-under-test>
public class EmptyStreamIdGuardCoverageTests {

  [Test]
  public async Task ThrowIfAnyHasEmptyStreamId_Outbox_NonRejectPolicy_SkipsTheCheckEntirelyAsync() {
    // Under a non-Reject policy the batch guard must return before even iterating rows — Reject is
    // the only policy that fails at write time. A Guid.Empty row proves the point: if the early
    // return were missing, this would throw.
    var messages = new[] { _outboxMessage(Guid.Empty) };

    await Assert.That(() =>
        EmptyStreamIdGuard.ThrowIfAnyHasEmptyStreamId(messages, EmptyStreamIdPolicy.FallbackToMessageId))
      .ThrowsNothing()
      .Because("non-Reject policies allow Empty-stream-id rows to land; the coordinator-side recovery handles them, not the storage-time guard.");
  }

  [Test]
  public async Task ThrowIfAnyHasEmptyStreamId_Inbox_NonRejectPolicy_SkipsTheCheckEntirelyAsync() {
    var messages = new[] { _inboxMessage(Guid.Empty) };

    await Assert.That(() =>
        EmptyStreamIdGuard.ThrowIfAnyHasEmptyStreamId(messages, EmptyStreamIdPolicy.Purge))
      .ThrowsNothing()
      .Because("non-Reject policies allow Empty-stream-id rows to land; the coordinator-side recovery handles them, not the storage-time guard.");
  }

  private static OutboxMessage _outboxMessage(Guid streamId) {
    var id = Guid.NewGuid();
    return new OutboxMessage {
      MessageId = id,
      MessageType = "Sample.Type, Sample",
      StreamId = streamId,
      Envelope = _envelope(id),
      EnvelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[System.Object, System.Private.CoreLib]], Whizbang.Core",
      Metadata = new EnvelopeMetadata { MessageId = new MessageId(id), Hops = [] },
    };
  }

  private static InboxMessage _inboxMessage(Guid streamId) {
    var id = Guid.NewGuid();
    return new InboxMessage {
      MessageId = id,
      HandlerName = "SampleHandler",
      MessageType = "Sample.Type, Sample",
      StreamId = streamId,
      Envelope = _envelope(id),
      EnvelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[System.Object, System.Private.CoreLib]], Whizbang.Core",
    };
  }

  private static MessageEnvelope<JsonElement> _envelope(Guid messageId) => new() {
    MessageId = new MessageId(messageId),
    Payload = JsonDocument.Parse("{}").RootElement,
    Hops = [],
    DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local },
  };
}
