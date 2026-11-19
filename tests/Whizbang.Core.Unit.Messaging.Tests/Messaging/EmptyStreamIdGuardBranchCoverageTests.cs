// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

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

/// <summary>
/// Branch coverage for the batch overloads of <see cref="EmptyStreamIdGuard"/> under
/// <see cref="EmptyStreamIdPolicy.Reject"/>, the secure default the storage path actually runs:
/// each row is checked, the first Empty stream id fails the batch naming that row, and a batch of
/// real or absent stream ids passes.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Messaging/EmptyStreamIdGuard.cs</code-under-test>
public class EmptyStreamIdGuardBranchCoverageTests {

  [Test]
  public async Task ThrowIfAnyHasEmptyStreamId_Outbox_Reject_NamesTheFirstOffendingRowAsync() {
    var offending = _outboxMessage(Guid.Empty);
    var messages = new[] { _outboxMessage(Guid.NewGuid()), offending, _outboxMessage(Guid.Empty) };

    var thrown = await Assert.That(() => EmptyStreamIdGuard.ThrowIfAnyHasEmptyStreamId(messages, EmptyStreamIdPolicy.Reject))
      .Throws<EmptyStreamIdException>()
      .Because("under Reject an Empty stream id fails the producer's write instead of landing in the outbox");

    await Assert.That(thrown!.MessageId).IsEqualTo(offending.MessageId)
      .Because("the exception names the first offending row, so the producer can find the message at fault");
  }

  [Test]
  public async Task ThrowIfAnyHasEmptyStreamId_Outbox_Reject_RealAndAbsentStreamIdsPassAsync() {
    var messages = new[] { _outboxMessage(Guid.NewGuid()), _outboxMessage(null) };

    await Assert.That(() => EmptyStreamIdGuard.ThrowIfAnyHasEmptyStreamId(messages, EmptyStreamIdPolicy.Reject))
      .ThrowsNothing()
      .Because("a real stream id and an absent one (the singleton-stream marker) are both legitimate");
  }

  [Test]
  public async Task ThrowIfAnyHasEmptyStreamId_Inbox_Reject_NamesTheFirstOffendingRowAsync() {
    var offending = _inboxMessage(Guid.Empty);
    var messages = new[] { _inboxMessage(Guid.NewGuid()), offending };

    var thrown = await Assert.That(() => EmptyStreamIdGuard.ThrowIfAnyHasEmptyStreamId(messages, EmptyStreamIdPolicy.Reject))
      .Throws<EmptyStreamIdException>();

    await Assert.That(thrown!.MessageId).IsEqualTo(offending.MessageId);
    await Assert.That(thrown.MessageType).IsEqualTo(offending.MessageType);
  }

  [Test]
  public async Task ThrowIfAnyHasEmptyStreamId_Inbox_Reject_RealAndAbsentStreamIdsPassAsync() {
    var messages = new[] { _inboxMessage(Guid.NewGuid()), _inboxMessage(null) };

    await Assert.That(() => EmptyStreamIdGuard.ThrowIfAnyHasEmptyStreamId(messages, EmptyStreamIdPolicy.Reject))
      .ThrowsNothing();
  }

  private static OutboxMessage _outboxMessage(Guid? streamId) {
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

  private static InboxMessage _inboxMessage(Guid? streamId) {
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

  private static MessageEnvelope<JsonElement> _envelope(Guid messageId) {
    using var document = JsonDocument.Parse("{}");
    return new() {
      MessageId = new MessageId(messageId),
      Payload = document.RootElement.Clone(),
      Hops = [],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local },
    };
  }
}
