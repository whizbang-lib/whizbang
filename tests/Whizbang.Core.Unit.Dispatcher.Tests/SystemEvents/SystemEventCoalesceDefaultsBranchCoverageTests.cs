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

  private static OutboxMessage _single(Guid? streamId, JsonElement? payload = null) {
    var envelope = new MessageEnvelope<JsonElement> {
      MessageId = MessageId.New(),
      Payload = payload ?? JsonSerializer.SerializeToElement(new { v = 1 }),
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

  /// <summary>
  /// The audit ledger (#1485): a bundle of one tenant's records rides that tenant's ledger stream, because
  /// a fold never mixes tenants and every record of the tenant is already on it.
  /// </summary>
  [Test]
  public async Task BuildAuditComposite_RecordsOfOneLedger_InheritTheLedgerStreamAsync() {
    var singles = _auditSingles("tenant-fold", 3);

    var composite = (AuditEventsComposite)SystemEventCoalesceDefaults.BuildAuditComposite(new CoalesceFoldBatch {
      Group = SystemTags.AUDIT,
      Singles = singles,
      Atomicity = FanoutAtomicity.Independent,
    });

    await Assert.That(composite.StreamId).IsEqualTo(AuditLedger.StreamFor("tenant-fold"))
      .Because("the bundle inherits its records' ledger instead of minting a stream nobody orders by");
  }

  /// <summary>
  /// The receiver keys each record's stored row by the stream its child lands on. Restoring each child onto
  /// the record's own id keeps one row per record there; restoring them onto the shared ledger would fold a
  /// tenant's whole trail into one row.
  /// </summary>
  [Test]
  public async Task BuildAuditComposite_RecordsOfOneLedger_RestoreEachChildOntoItsRecordIdAsync() {
    var singles = _auditSingles("tenant-fold", 2);

    var composite = (AuditEventsComposite)SystemEventCoalesceDefaults.BuildAuditComposite(new CoalesceFoldBatch {
      Group = SystemTags.AUDIT,
      Singles = singles,
      Atomicity = FanoutAtomicity.Independent,
    });

    for (var i = 0; i < singles.Count; i++) {
      var recordId = singles[i].Envelope.Payload.GetProperty(nameof(EventAudited.Id)).GetGuid();
      await Assert.That(composite.InnerStreamIds[i]).IsEqualTo(recordId);
    }
  }

  [Test]
  public async Task BuildAuditComposite_SinglesOnDifferentStreams_MintAFreshCompositeStreamAsync() {
    var first = Guid.CreateVersion7();
    var second = Guid.CreateVersion7();

    var composite = SystemEventCoalesceDefaults.BuildAuditComposite(new CoalesceFoldBatch {
      Group = SystemTags.AUDIT,
      Singles = [_single(first), _single(second)],
      Atomicity = FanoutAtomicity.Independent,
    });

    await Assert.That(composite.StreamId).IsNotEqualTo(Guid.Empty);
    await Assert.That(composite.StreamId).IsNotEqualTo(first);
    await Assert.That(composite.StreamId).IsNotEqualTo(second);
  }

  /// <summary>A payload that names no usable record id falls back to the single's own stream.</summary>
  [Test]
  [Arguments("\"not-an-object\"")]
  [Arguments("{\"Id\": 42}")]
  [Arguments("{\"Id\": \"not-a-guid\"}")]
  public async Task BuildAuditComposite_PayloadWithoutARecordId_FallsBackToTheSinglesStreamAsync(string payloadJson) {
    var stream = Guid.CreateVersion7();
    using var document = JsonDocument.Parse(payloadJson);

    var composite = (AuditEventsComposite)SystemEventCoalesceDefaults.BuildAuditComposite(new CoalesceFoldBatch {
      Group = SystemTags.AUDIT,
      Singles = [_single(stream, document.RootElement.Clone())],
      Atomicity = FanoutAtomicity.Independent,
    });

    await Assert.That(composite.InnerStreamIds[0]).IsEqualTo(stream);
  }

  [Test]
  public async Task BuildAuditComposite_SinglesWithoutAStream_MintAFreshCompositeStreamAsync() {
    var composite = SystemEventCoalesceDefaults.BuildAuditComposite(new CoalesceFoldBatch {
      Group = SystemTags.AUDIT,
      Singles = [_single(null), _single(null)],
      Atomicity = FanoutAtomicity.Independent,
    });

    await Assert.That(composite.StreamId).IsNotEqualTo(Guid.Empty)
      .Because("singles with no stream share no ledger, so the bundle cannot inherit one");
  }

  private static List<OutboxMessage> _auditSingles(string tenantId, int count) {
    var options = new SystemEventOptions().EnableEventAudit();
    var singles = new List<OutboxMessage>();
    for (var i = 0; i < count; i++) {
      var source = _single(Guid.CreateVersion7()) with {
        IsEvent = true,
        Scope = new Whizbang.Core.Lenses.PerspectiveScope { TenantId = tenantId },
      };
      singles.Add(AuditOutboxMessageBuilder.TryBuildAuditMessage(source, options)!);
    }
    return singles;
  }
}
