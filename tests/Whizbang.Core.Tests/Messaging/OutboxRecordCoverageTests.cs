using System.Text.Json;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Coverage round 23 tail: <see cref="OutboxRecord.Scope"/>. The existing
/// "full initialization round-trip" test in RecordTypesConstructionTests.cs sets every OTHER
/// property but skips Scope, so its getter/setter has never run. Scope is a real, persisted
/// column (see WhizbangModelBuilderExtensions' <c>entity.Property(e => e.Scope)</c> JSONB
/// mapping) feeding multi-tenancy query filtering, not a vestigial field.
/// </summary>
[Category("Messaging")]
public class OutboxRecordCoverageTests {

  /// <summary>
  /// Operator impact: Scope carries the tenant/user/customer bucket used to filter outbox rows
  /// for multi-tenant deployments. If the setter silently dropped the value (or the property were
  /// removed and callers fell back to a stale default), tenant-scoped rows would round-trip
  /// through the entity as unscoped, which is a data-leak-shaped bug, not a cosmetic one.
  /// </summary>
  [Test]
  public async Task Scope_SetThenRead_RoundTripsAsync() {
    var scope = new PerspectiveScope { TenantId = "tenant-123", UserId = "user-456" };
    var record = new OutboxRecord {
      MessageId = Guid.NewGuid(),
      MessageType = "MyApp.Events.OrderCreated",
      MessageData = new OutboxMessageData { MessageId = MessageId.New(), Payload = JsonDocument.Parse("{}").RootElement, Hops = [] },
      Metadata = new EnvelopeMetadata { MessageId = MessageId.New(), Hops = [] },
      Scope = scope,
    };

    await Assert.That(record.Scope).IsSameReferenceAs(scope)
      .Because("the Scope setter must carry through the exact value assigned — a copy or a "
             + "dropped assignment would silently break multi-tenant filtering on this row");
    await Assert.That(record.Scope!.TenantId).IsEqualTo("tenant-123");
  }

  /// <summary>
  /// Operator impact: event-store-only / unscoped rows must stay scope-free by default rather
  /// than the getter fabricating a non-null scope, which would make an unscoped row look
  /// tenant-owned to a query filter.
  /// </summary>
  [Test]
  public async Task Scope_WhenNotSet_DefaultsToNullAsync() {
    var record = new OutboxRecord {
      MessageId = Guid.NewGuid(),
      MessageType = "MyApp.Events.OrderCreated",
      MessageData = new OutboxMessageData { MessageId = MessageId.New(), Payload = JsonDocument.Parse("{}").RootElement, Hops = [] },
      Metadata = new EnvelopeMetadata { MessageId = MessageId.New(), Hops = [] },
    };

    await Assert.That(record.Scope).IsNull()
      .Because("an outbox row constructed without an explicit scope must read back null, not a "
             + "fabricated default scope");
  }
}
