// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Minting;
using Whizbang.Core.Observability;
using Whizbang.Core.SystemEvents;
using Whizbang.Core.Tags;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.SystemEvents;

/// <summary>
/// Branch coverage for <see cref="SystemEventCoalesceDefaults.BuildAuditComposite"/>: a single with
/// no stream id rides the composite as <see cref="Guid.Empty"/>, keeping the inner arrays aligned.
/// </summary>
/// <code-under-test>src/Whizbang.Core/SystemEvents/SystemEventCoalesceDefaults.cs</code-under-test>
[Category("SystemEvents")]
public class SystemEventCoalesceDefaultsBranchCoverageTests {

  private static OutboxMessage _single(Guid? streamId) {
    var envelope = new MessageEnvelope<JsonElement> {
      MessageId = MessageId.New(),
      Payload = JsonSerializer.SerializeToElement(new { v = 1 }),
      Hops = [],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Outbox },
    };
    return new OutboxMessage {
      MessageId = envelope.MessageId.Value,
      Destination = AuditingEventStoreDecorator.AUDIT_TOPIC_DESTINATION,
      Envelope = envelope,
      Metadata = new EnvelopeMetadata { MessageId = envelope.MessageId, Hops = [] },
      EnvelopeType = "T",
      StreamId = streamId,
      IsEvent = true,
      MessageType = "Audit.Single, Probe",
    };
  }

  /// <summary>
  /// The receiver restores each child's stream by index. A single without a stream id must still
  /// occupy its slot (as <see cref="Guid.Empty"/>); skipping it would shift every later child onto
  /// its neighbour's stream.
  /// </summary>
  [Test]
  public async Task BuildAuditComposite_SingleWithoutStreamId_CarriesEmptyGuidInItsSlotAsync() {
    var withStream = Guid.CreateVersion7();
    var batch = new CoalesceFoldBatch {
      Group = SystemTags.AUDIT,
      Singles = [_single(null), _single(withStream)],
      Atomicity = FanoutAtomicity.Independent,
    };

    var composite = (AuditEventsComposite)SystemEventCoalesceDefaults.BuildAuditComposite(batch);

    await Assert.That(composite.InnerStreamIds.Count).IsEqualTo(2);
    await Assert.That(composite.InnerStreamIds[0]).IsEqualTo(Guid.Empty)
      .Because("a single with no stream id keeps its slot as Guid.Empty");
    await Assert.That(composite.InnerStreamIds[1]).IsEqualTo(withStream)
      .Because("the next single's stream must stay aligned with its own index");
  }
}
