// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.SystemEvents;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.SystemEvents;

/// <summary>
/// Branch coverage for <see cref="AuditOutboxMessageBuilder"/>: a source envelope whose hop list is
/// null, the shape a stored envelope deserialized with <c>"Hops": null</c> takes at runtime despite
/// the non-nullable declaration.
/// </summary>
/// <code-under-test>src/Whizbang.Core/SystemEvents/AuditOutboxMessageBuilder.cs</code-under-test>
[Category("SystemEvents")]
public class AuditOutboxMessageBuilderBranchCoverageTests {

  /// <summary>
  /// With no source hops there is no lineage to carry, and the audit record must still be built
  /// with exactly its own Current hop rather than fault on the null list.
  /// </summary>
  [Test]
  public async Task TryBuildAuditMessage_SourceEnvelopeWithNullHops_BuildsWithOnlyTheAuditHopAsync() {
    var options = new SystemEventOptions().EnableEventAudit();
    var envelope = new MessageEnvelope<JsonElement> {
      MessageId = MessageId.New(),
      Payload = JsonSerializer.SerializeToElement(new { v = 1 }),
      // Not constructible through the typed initializer path, but exactly what STJ produces for a
      // stored envelope whose hops were written as null (nullable annotations are not enforced).
      Hops = null!,
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Outbox },
    };
    var source = new OutboxMessage {
      MessageId = envelope.MessageId.Value,
      Destination = "topic",
      Envelope = envelope,
      Metadata = new EnvelopeMetadata { MessageId = envelope.MessageId, Hops = [] },
      EnvelopeType = "T",
      StreamId = Guid.CreateVersion7(),
      IsEvent = true,
      MessageType = typeof(PerspectiveCoverageGapDetected).AssemblyQualifiedName!,
      Scope = null,
    };

    var built = AuditOutboxMessageBuilder.TryBuildAuditMessage(source, options);

    await Assert.That(built).IsNotNull();
    var hops = built!.Envelope.Hops;
    await Assert.That(hops.Count).IsEqualTo(1)
      .Because("no source hops means no causation lineage, only the audit record's own hop");
    await Assert.That(hops[0].Type).IsEqualTo(HopType.Current);
  }
}
