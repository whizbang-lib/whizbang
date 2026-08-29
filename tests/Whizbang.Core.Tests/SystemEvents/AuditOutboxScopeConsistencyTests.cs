using System.Text.Json;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Lenses;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Security;
using Whizbang.Core.SystemEvents;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.SystemEvents;

/// <summary>
/// Every path that writes an audit record must scope it the same way.
/// </summary>
/// <remarks>
/// <para>
/// Audit records are produced from three independent places, and they disagreed. One wrote no scope
/// at all. One wrote the audited tenant plus the system marker. This one COPIES the original event's
/// hops wholesale, so the audit record inherited the acting user's scope — handing the subject of an
/// audit record a key to their own audit trail.
/// </para>
/// <para>
/// The lineage those copied hops provide is still worth keeping, so they are retained as CAUSATION
/// hops rather than dropped. Only <c>HopType.Current</c> hops contribute scope, so the trace history
/// survives while the authority does not.
/// </para>
/// <para>
/// Losing the user does not cost the consumer its security context: the extractor returns a context
/// when EITHER tenant or user is present, and the tenant is carried.
/// </para>
/// </remarks>
/// <code-under-test>src/Whizbang.Core/SystemEvents/AuditOutboxMessageBuilder.cs</code-under-test>
[Category("SystemEvents")]
public class AuditOutboxScopeConsistencyTests {

  private static OutboxMessage _sourceEvent(string tenantId, string userId) {
    var hop = new MessageHop {
      ServiceInstance = ServiceInstanceInfo.Unknown,
      Type = HopType.Current,
      Timestamp = DateTimeOffset.UtcNow,
      Scope = ScopeDelta.FromPerspectiveScope(new PerspectiveScope { TenantId = tenantId, UserId = userId }),
    };
    var envelope = new MessageEnvelope<JsonElement> {
      MessageId = MessageId.New(),
      Payload = JsonSerializer.SerializeToElement(new { v = 1 }),
      Hops = [hop],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Outbox },
    };
    return new OutboxMessage {
      MessageId = envelope.MessageId.Value,
      Destination = "topic",
      Envelope = envelope,
      Metadata = new EnvelopeMetadata { MessageId = envelope.MessageId, Hops = [hop] },
      EnvelopeType = "T",
      StreamId = Guid.CreateVersion7(),
      IsEvent = true,
      MessageType = "Contracts.ThingHappened, Contracts",
      Scope = new PerspectiveScope { TenantId = tenantId, UserId = userId },
    };
  }

  [Test]
  public async Task TheAuditRecordCarriesTheTenantAsync() {
    var options = new SystemEventOptions().EnableEventAudit();

    var audit = AuditOutboxMessageBuilder.TryBuildAuditMessage(_sourceEvent("tenant-a", "user-1"), options);

    await Assert.That(audit).IsNotNull()
      .Because("no audit message was produced, so the assertions below would be vacuous");
    var scope = audit!.Envelope.GetCurrentScope();
    await Assert.That(scope!.Scope.TenantId).IsEqualTo("tenant-a")
      .Because("audit rows are filtered, exported and deleted by tenant, so the tenant has to be "
             + "on the record");
  }

  [Test]
  public async Task TheAuditRecordDoesNotInheritTheActingUserAsync() {
    var options = new SystemEventOptions().EnableEventAudit();

    var audit = AuditOutboxMessageBuilder.TryBuildAuditMessage(_sourceEvent("tenant-a", "user-1"), options);

    await Assert.That(audit!.Envelope.GetCurrentScope()!.Scope.UserId).IsNull()
      .Because("copying the source hops wholesale carries the acting user's scope onto the audit "
             + "record, which hands the SUBJECT of that record a key to their own audit trail");
  }

  [Test]
  public async Task TheAuditRecordIsMarkedSystemEmittedAsync() {
    var options = new SystemEventOptions().EnableEventAudit();

    var audit = AuditOutboxMessageBuilder.TryBuildAuditMessage(_sourceEvent("tenant-a", "user-1"), options);

    await Assert.That(audit!.Envelope.GetCurrentScope()!.Scope.IsSystem).IsTrue()
      .Because("all three audit paths must agree; a record that is system-emitted on one path and "
             + "unmarked on another cannot be reasoned about at all");
  }

  [Test]
  public async Task TheOriginalLineageIsKeptAsCausationAsync() {
    var options = new SystemEventOptions().EnableEventAudit();

    var audit = AuditOutboxMessageBuilder.TryBuildAuditMessage(_sourceEvent("tenant-a", "user-1"), options);

    var hops = audit!.Envelope.Hops!;
    await Assert.That(hops.Count).IsGreaterThan(1)
      .Because("the source hops carry the trace history back to the audited event and are worth "
             + "keeping — only their AUTHORITY had to go");
    await Assert.That(hops.Any(h => h.Type == HopType.Causation)).IsTrue()
      .Because("demoting them to causation keeps the lineage readable while excluding them from "
             + "scope resolution, which merges Current hops only");
  }
}
