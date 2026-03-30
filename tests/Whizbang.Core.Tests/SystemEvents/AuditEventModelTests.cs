using System.Text.Json;
using TUnit.Core;
using Whizbang.Core.Audit;
using Whizbang.Core.SystemEvents;
using Whizbang.Core.SystemEvents.Audit;

namespace Whizbang.Core.Tests.SystemEvents;

/// <summary>
/// Tests for <see cref="AuditEventModel"/> properties and
/// <see cref="AuditEventProjection"/> humanization logic.
/// </summary>
public class AuditEventModelTests {
  // ── AuditEventModel property defaults ──

  [Test]
  public async Task AuditEventModel_EventType_DefaultsToEmptyStringAsync() {
    var model = new AuditEventModel();
    await Assert.That(model.EventType).IsEqualTo(string.Empty);
  }

  [Test]
  public async Task AuditEventModel_EventName_DefaultsToEmptyStringAsync() {
    var model = new AuditEventModel();
    await Assert.That(model.EventName).IsEqualTo(string.Empty);
  }

  [Test]
  public async Task AuditEventModel_EventDescription_DefaultsToEmptyStringAsync() {
    var model = new AuditEventModel();
    await Assert.That(model.EventDescription).IsEqualTo(string.Empty);
  }

  [Test]
  public async Task AuditEventModel_EventVersion_DefaultsTo1Point0Async() {
    var model = new AuditEventModel();
    await Assert.That(model.EventVersion).IsEqualTo("1.0");
  }

  [Test]
  public async Task AuditEventModel_EventStreamId_DefaultsToEmptyStringAsync() {
    var model = new AuditEventModel();
    await Assert.That(model.EventStreamId).IsEqualTo(string.Empty);
  }

  [Test]
  public async Task ScopeEntry_Key_DefaultsToEmptyStringAsync() {
    var entry = new ScopeEntry();
    await Assert.That(entry.Key).IsEqualTo(string.Empty);
  }

  [Test]
  public async Task ScopeEntry_Value_DefaultsToNullAsync() {
    var entry = new ScopeEntry();
    await Assert.That(entry.Value).IsNull();
  }

  // ── AuditEventProjection.Apply ──

  [Test]
  [NotInParallel]
  public async Task Apply_MapsAllFieldsFromEventAuditedAsync() {
    var id = Guid.NewGuid();
    var originalEventId = Guid.NewGuid();
    var timestamp = DateTimeOffset.UtcNow;
    var scope = new Dictionary<string, string?> { ["TenantId"] = "t1", ["UserId"] = "u1" };

    var @event = new EventAudited {
      Id = id,
      OriginalEventId = originalEventId,
      OriginalEventType = "MyApp.Orders.OrderCreatedEvent",
      OriginalStreamId = "Order-123",
      OriginalStreamPosition = 5,
      OriginalBody = JsonSerializer.SerializeToElement(new { }),
      Timestamp = timestamp,
      CorrelationId = "corr-1",
      CausationId = "cause-1",
      Scope = scope
    };

    var result = AuditEventProjection.Apply(new AuditEventModel(), @event);

    await Assert.That(result.Id).IsEqualTo(id);
    await Assert.That(result.CreatedAt).IsEqualTo(timestamp);
    await Assert.That(result.UpdatedAt).IsEqualTo(timestamp);
    await Assert.That(result.OriginalEventId).IsEqualTo(originalEventId);
    await Assert.That(result.EventType).IsEqualTo("MyApp.Orders.OrderCreatedEvent");
    await Assert.That(result.EventStreamId).IsEqualTo("Order-123");
    await Assert.That(result.EventStreamPosition).IsEqualTo(5L);
    await Assert.That(result.OccurredAt).IsEqualTo(timestamp);
    await Assert.That(result.CorrelationId).IsEqualTo("corr-1");
    await Assert.That(result.CausationId).IsEqualTo("cause-1");
    await Assert.That(result.EventVersion).IsEqualTo("1.0");
    await Assert.That(result.OriginalScope).IsNotNull();
    await Assert.That(result.OriginalScope!.Count).IsEqualTo(2);
  }

  [Test]
  [NotInParallel]
  public async Task Apply_NullScope_SetsOriginalScopeToNullAsync() {
    var @event = new EventAudited {
      Id = Guid.NewGuid(),
      OriginalEventType = "SimpleEvent",
      OriginalStreamId = "stream-1",
      OriginalStreamPosition = 1,
      OriginalBody = JsonSerializer.SerializeToElement(new { }),
      Timestamp = DateTimeOffset.UtcNow,
      Scope = null
    };

    var result = AuditEventProjection.Apply(new AuditEventModel(), @event);

    await Assert.That(result.OriginalScope).IsNull();
  }

  [Test]
  [NotInParallel]
  public async Task Apply_UsesInlineHumanizerOverCustomHumanizerAsync() {
    // Set a global custom humanizer that should be ignored when inline is provided
    var previousCustom = AuditEventProjection.CustomHumanizer;
    var previousDesc = AuditEventProjection.CustomDescriptionHumanizer;
    try {
      AuditEventProjection.CustomHumanizer = _ => "Global Custom";
      AuditEventProjection.CustomDescriptionHumanizer = null;

      var @event = new EventAudited {
        Id = Guid.NewGuid(),
        OriginalEventType = "SomeEvent",
        OriginalStreamId = "stream-1",
        OriginalStreamPosition = 1,
        OriginalBody = JsonSerializer.SerializeToElement(new { }),
        Timestamp = DateTimeOffset.UtcNow
      };

      var result = AuditEventProjection.Apply(
        new AuditEventModel(),
        @event,
        eventNameHumanizer: _ => "Inline Custom");

      await Assert.That(result.EventName).IsEqualTo("Inline Custom");
    } finally {
      AuditEventProjection.CustomHumanizer = previousCustom;
      AuditEventProjection.CustomDescriptionHumanizer = previousDesc;
    }
  }

  [Test]
  [NotInParallel]
  public async Task Apply_FallsBackToCustomHumanizerWhenNoInlineAsync() {
    var previousCustom = AuditEventProjection.CustomHumanizer;
    var previousDesc = AuditEventProjection.CustomDescriptionHumanizer;
    try {
      AuditEventProjection.CustomHumanizer = _ => "Global Custom";
      AuditEventProjection.CustomDescriptionHumanizer = null;

      var @event = new EventAudited {
        Id = Guid.NewGuid(),
        OriginalEventType = "SomeEvent",
        OriginalStreamId = "stream-1",
        OriginalStreamPosition = 1,
        OriginalBody = JsonSerializer.SerializeToElement(new { }),
        Timestamp = DateTimeOffset.UtcNow
      };

      var result = AuditEventProjection.Apply(new AuditEventModel(), @event);

      await Assert.That(result.EventName).IsEqualTo("Global Custom");
    } finally {
      AuditEventProjection.CustomHumanizer = previousCustom;
      AuditEventProjection.CustomDescriptionHumanizer = previousDesc;
    }
  }

  // ── HumanizeEventType ──

  [Test]
  public async Task HumanizeEventType_SimpleEvent_RemovesEventSuffixAsync() {
    var result = AuditEventProjection.HumanizeEventType("OrderCreatedEvent");
    await Assert.That(result).IsEqualTo("Order Created");
  }

  [Test]
  public async Task HumanizeEventType_NoEventSuffix_SplitsPascalCaseAsync() {
    var result = AuditEventProjection.HumanizeEventType("OrderShipped");
    await Assert.That(result).IsEqualTo("Order Shipped");
  }

  [Test]
  public async Task HumanizeEventType_WithNamespace_StripsNamespaceAsync() {
    var result = AuditEventProjection.HumanizeEventType("MyApp.Orders.OrderCreatedEvent");
    await Assert.That(result).IsEqualTo("Order Created");
  }

  [Test]
  public async Task HumanizeEventType_NestedType_ProducesArrowSeparatedAsync() {
    var result = AuditEventProjection.HumanizeEventType("SessionContracts+EndedEvent");
    await Assert.That(result).IsEqualTo("Session \u2192 Ended");
  }

  [Test]
  public async Task HumanizeEventType_NestedTypeWithNamespace_StripsNamespaceAndContractsAsync() {
    var result = AuditEventProjection.HumanizeEventType(
      "JDX.Contracts.Session.SessionContracts+EndedEvent");
    await Assert.That(result).IsEqualTo("Session \u2192 Ended");
  }

  [Test]
  public async Task HumanizeEventType_ContractsSuffix_RemovedFromAllSegmentsAsync() {
    var result = AuditEventProjection.HumanizeEventType("OrderContracts+ShippedEvent");
    await Assert.That(result).IsEqualTo("Order \u2192 Shipped");
  }

  [Test]
  public async Task HumanizeEventType_MultipleNestedSegments_AllHumanizedAsync() {
    var result = AuditEventProjection.HumanizeEventType(
      "ChatConversationsContracts+AgentMessage+SentEvent");
    await Assert.That(result).IsEqualTo("Chat Conversations \u2192 Agent Message \u2192 Sent");
  }

  [Test]
  public async Task HumanizeEventType_Acronyms_KeptTogetherAsync() {
    var result = AuditEventProjection.HumanizeEventType("HTTPRequestReceivedEvent");
    await Assert.That(result).IsEqualTo("HTTP Request Received");
  }

  [Test]
  public async Task HumanizeEventType_CustomHumanizer_ReturnsCustomValueAsync() {
    var result = AuditEventProjection.HumanizeEventType(
      "SomeEvent",
      _ => "My Custom Name");
    await Assert.That(result).IsEqualTo("My Custom Name");
  }

  [Test]
  public async Task HumanizeEventType_CustomHumanizerReturnsNull_FallsBackToDefaultAsync() {
    var result = AuditEventProjection.HumanizeEventType(
      "OrderCreatedEvent",
      _ => null);
    await Assert.That(result).IsEqualTo("Order Created");
  }

  [Test]
  public async Task HumanizeEventType_EmptyString_ReturnsOriginalAsync() {
    var result = AuditEventProjection.HumanizeEventType("");
    await Assert.That(result).IsEqualTo("");
  }

  [Test]
  public async Task HumanizeEventType_SingleWord_NoEventSuffix_ReturnsSameAsync() {
    var result = AuditEventProjection.HumanizeEventType("Order");
    await Assert.That(result).IsEqualTo("Order");
  }

  [Test]
  public async Task HumanizeEventType_OnlyEventSuffix_ReturnsOriginalStringAsync() {
    // "Event" with suffix removed becomes empty, so humanized list is empty,
    // falls back to original eventType
    var result = AuditEventProjection.HumanizeEventType("Event");
    await Assert.That(result).IsEqualTo("Event");
  }

  [Test]
  public async Task HumanizeEventType_ContractsOnly_ReturnsOriginalAsync() {
    // "Contracts" suffix removed becomes empty, empty segments filtered out
    var result = AuditEventProjection.HumanizeEventType("Contracts");
    await Assert.That(result).IsEqualTo("Contracts");
  }

  // ── HumanizeNamespace (HumanizeEventDescription) ──

  [Test]
  public async Task HumanizeNamespace_NoNestedNoNamespace_ReturnsEmptyAsync() {
    var result = AuditEventProjection.HumanizeNamespace("OrderCreatedEvent");
    await Assert.That(result).IsEqualTo(string.Empty);
  }

  [Test]
  public async Task HumanizeNamespace_WithNamespaceNoNested_ReturnsHumanizedNamespaceAsync() {
    var result = AuditEventProjection.HumanizeNamespace("MyApp.Orders.OrderCreatedEvent");
    await Assert.That(result).IsEqualTo("My App \u2192 Orders");
  }

  [Test]
  public async Task HumanizeNamespace_NestedType_ReturnsParentSegmentAsync() {
    var result = AuditEventProjection.HumanizeNamespace("SessionContracts+EndedEvent");
    await Assert.That(result).IsEqualTo("Session Contracts");
  }

  [Test]
  public async Task HumanizeNamespace_NestedTypeWithNamespace_UsesParentNotNamespaceAsync() {
    var result = AuditEventProjection.HumanizeNamespace(
      "JDX.Contracts.Session.SessionContracts+EndedEvent");
    await Assert.That(result).IsEqualTo("Session Contracts");
  }

  [Test]
  public async Task HumanizeNamespace_MultipleNestedSegments_AllParentsExceptLastAsync() {
    var result = AuditEventProjection.HumanizeNamespace(
      "ChatConversationsContracts+AgentMessage+SentEvent");
    await Assert.That(result).IsEqualTo("Chat Conversations Contracts \u2192 Agent Message");
  }

  [Test]
  public async Task HumanizeNamespace_CustomHumanizer_ReturnsCustomValueAsync() {
    var result = AuditEventProjection.HumanizeNamespace(
      "SomeEvent",
      _ => "Custom Description");
    await Assert.That(result).IsEqualTo("Custom Description");
  }

  [Test]
  public async Task HumanizeNamespace_CustomHumanizerReturnsNull_FallsBackToDefaultAsync() {
    var result = AuditEventProjection.HumanizeNamespace(
      "MyApp.Orders.OrderCreatedEvent",
      _ => null);
    await Assert.That(result).IsEqualTo("My App \u2192 Orders");
  }

  [Test]
  public async Task HumanizeNamespace_SingleWordNoNamespace_ReturnsEmptyAsync() {
    var result = AuditEventProjection.HumanizeNamespace("Order");
    await Assert.That(result).IsEqualTo(string.Empty);
  }

  // ── Apply sets EventDescription via HumanizeNamespace ──

  [Test]
  [NotInParallel]
  public async Task Apply_SetsEventDescriptionFromNamespaceAsync() {
    var previousCustom = AuditEventProjection.CustomHumanizer;
    var previousDesc = AuditEventProjection.CustomDescriptionHumanizer;
    try {
      AuditEventProjection.CustomHumanizer = null;
      AuditEventProjection.CustomDescriptionHumanizer = null;

      var @event = new EventAudited {
        Id = Guid.NewGuid(),
        OriginalEventType = "MyApp.Billing.InvoicePaidEvent",
        OriginalStreamId = "Invoice-1",
        OriginalStreamPosition = 1,
        OriginalBody = JsonSerializer.SerializeToElement(new { }),
        Timestamp = DateTimeOffset.UtcNow
      };

      var result = AuditEventProjection.Apply(new AuditEventModel(), @event);

      await Assert.That(result.EventDescription).IsEqualTo("My App \u2192 Billing");
    } finally {
      AuditEventProjection.CustomHumanizer = previousCustom;
      AuditEventProjection.CustomDescriptionHumanizer = previousDesc;
    }
  }

  [Test]
  [NotInParallel]
  public async Task Apply_UsesCustomDescriptionHumanizerAsync() {
    var previousCustom = AuditEventProjection.CustomHumanizer;
    var previousDesc = AuditEventProjection.CustomDescriptionHumanizer;
    try {
      AuditEventProjection.CustomHumanizer = null;
      AuditEventProjection.CustomDescriptionHumanizer = _ => "Custom Desc";

      var @event = new EventAudited {
        Id = Guid.NewGuid(),
        OriginalEventType = "SomeEvent",
        OriginalStreamId = "stream-1",
        OriginalStreamPosition = 1,
        OriginalBody = JsonSerializer.SerializeToElement(new { }),
        Timestamp = DateTimeOffset.UtcNow
      };

      var result = AuditEventProjection.Apply(new AuditEventModel(), @event);

      await Assert.That(result.EventDescription).IsEqualTo("Custom Desc");
    } finally {
      AuditEventProjection.CustomHumanizer = previousCustom;
      AuditEventProjection.CustomDescriptionHumanizer = previousDesc;
    }
  }
}
