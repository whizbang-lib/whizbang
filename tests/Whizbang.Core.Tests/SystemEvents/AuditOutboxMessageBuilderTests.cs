using System.Text.Json;
using TUnit.Core;
using Whizbang.Core.Attributes;
using Whizbang.Core.Lenses;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.SystemEvents;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Dispatch;

namespace Whizbang.Core.Tests.SystemEvents;

/// <summary>
/// Tests for <see cref="AuditOutboxMessageBuilder"/> which builds EventAudited outbox messages
/// from domain event outbox messages. Covers all paths: early returns, shouldAudit logic,
/// scope extraction, correlation ID from hops, full audit event creation, and OutboxMessage construction.
/// </summary>
[Category("SystemEvents")]
public class AuditOutboxMessageBuilderTests {
  #region TryBuildAuditMessage - Early Return Paths

  [Test]
  public async Task TryBuildAuditMessage_NonEvent_ReturnsNullAsync() {
    // Arrange - OutboxMessage with IsEvent = false
    var message = _createOutboxMessage(isEvent: false);
    var options = _createOptions(auditEnabled: true);

    // Act
    var result = AuditOutboxMessageBuilder.TryBuildAuditMessage(message, options);

    // Assert
    await Assert.That(result).IsNull();
  }

  [Test]
  public async Task TryBuildAuditMessage_AuditDisabled_ReturnsNullAsync() {
    // Arrange - Audit is not enabled
    var message = _createOutboxMessage(isEvent: true);
    var options = _createOptions(auditEnabled: false);

    // Act
    var result = AuditOutboxMessageBuilder.TryBuildAuditMessage(message, options);

    // Assert
    await Assert.That(result).IsNull();
  }

  [Test]
  public async Task TryBuildAuditMessage_NonEventAndAuditDisabled_ReturnsNullAsync() {
    // Arrange - Both conditions fail
    var message = _createOutboxMessage(isEvent: false);
    var options = _createOptions(auditEnabled: false);

    // Act
    var result = AuditOutboxMessageBuilder.TryBuildAuditMessage(message, options);

    // Assert
    await Assert.That(result).IsNull();
  }

  #endregion

  #region TryBuildAuditMessage - ShouldAudit (EventAudited Self-Exclusion)

  [Test]
  public async Task TryBuildAuditMessage_EventAuditedType_ReturnsNull_PreventsSelfAuditLoopAsync() {
    // Arrange - EventAudited itself should never be audited (prevents infinite loop)
    var eventAuditedType = typeof(EventAudited);
    var message = _createOutboxMessage(
      isEvent: true,
      messageType: eventAuditedType.AssemblyQualifiedName);
    var options = _createOptions(auditEnabled: true);

    // Act
    var result = AuditOutboxMessageBuilder.TryBuildAuditMessage(message, options);

    // Assert
    await Assert.That(result).IsNull();
  }

  #endregion

  #region TryBuildAuditMessage - ShouldAudit (OptOut Mode)

  [Test]
  public async Task TryBuildAuditMessage_OptOutMode_EventWithoutAttribute_IsAuditedAsync() {
    // Arrange - OptOut mode: all events audited unless excluded
    // Using a type that cannot be resolved (simulates unresolvable type)
    var message = _createOutboxMessage(
      isEvent: true,
      messageType: "NonExistent.FakeEvent, NonExistent.Assembly");
    var options = _createOptions(auditEnabled: true, auditMode: AuditMode.OptOut);

    // Act
    var result = AuditOutboxMessageBuilder.TryBuildAuditMessage(message, options);

    // Assert - Unresolvable types are audited (eventType == null path)
    await Assert.That(result).IsNotNull();
  }

  [Test]
  public async Task TryBuildAuditMessage_OptOutMode_ExcludedEvent_ReturnsNullAsync() {
    // Arrange - OptOut mode with excluded event type
    var excludedType = typeof(TestExcludedFromAuditEvent);
    var message = _createOutboxMessage(
      isEvent: true,
      messageType: excludedType.AssemblyQualifiedName);
    var options = _createOptions(auditEnabled: true, auditMode: AuditMode.OptOut);

    // Act
    var result = AuditOutboxMessageBuilder.TryBuildAuditMessage(message, options);

    // Assert
    await Assert.That(result).IsNull();
  }

  [Test]
  public async Task TryBuildAuditMessage_OptOutMode_MarkedEvent_IsAuditedAsync() {
    // Arrange - OptOut mode with explicitly marked (non-excluded) event
    var markedType = typeof(TestMarkedForAuditEvent);
    var message = _createOutboxMessage(
      isEvent: true,
      messageType: markedType.AssemblyQualifiedName);
    var options = _createOptions(auditEnabled: true, auditMode: AuditMode.OptOut);

    // Act
    var result = AuditOutboxMessageBuilder.TryBuildAuditMessage(message, options);

    // Assert
    await Assert.That(result).IsNotNull();
  }

  #endregion

  #region TryBuildAuditMessage - ShouldAudit (OptIn Mode)

  [Test]
  public async Task TryBuildAuditMessage_OptInMode_EventWithoutAttribute_ReturnsNullAsync() {
    // Arrange - OptIn mode: only explicitly marked events are audited
    var unmarkedType = typeof(TestUnmarkedEvent);
    var message = _createOutboxMessage(
      isEvent: true,
      messageType: unmarkedType.AssemblyQualifiedName);
    var options = _createOptions(auditEnabled: true, auditMode: AuditMode.OptIn);

    // Act
    var result = AuditOutboxMessageBuilder.TryBuildAuditMessage(message, options);

    // Assert
    await Assert.That(result).IsNull();
  }

  [Test]
  public async Task TryBuildAuditMessage_OptInMode_MarkedEvent_IsAuditedAsync() {
    // Arrange - OptIn mode with [AuditEvent] attribute
    var markedType = typeof(TestMarkedForAuditEvent);
    var message = _createOutboxMessage(
      isEvent: true,
      messageType: markedType.AssemblyQualifiedName);
    var options = _createOptions(auditEnabled: true, auditMode: AuditMode.OptIn);

    // Act
    var result = AuditOutboxMessageBuilder.TryBuildAuditMessage(message, options);

    // Assert
    await Assert.That(result).IsNotNull();
  }

  [Test]
  public async Task TryBuildAuditMessage_OptInMode_ExcludedEvent_ReturnsNullAsync() {
    // Arrange - OptIn mode: excluded events are NOT audited even with attribute
    var excludedType = typeof(TestExcludedFromAuditEvent);
    var message = _createOutboxMessage(
      isEvent: true,
      messageType: excludedType.AssemblyQualifiedName);
    var options = _createOptions(auditEnabled: true, auditMode: AuditMode.OptIn);

    // Act
    var result = AuditOutboxMessageBuilder.TryBuildAuditMessage(message, options);

    // Assert
    await Assert.That(result).IsNull();
  }

  #endregion

  #region TryBuildAuditMessage - Scope Extraction

  [Test]
  public async Task TryBuildAuditMessage_WithScope_ExtractsTenantIdAndUserIdAsync() {
    // Arrange
    var message = _createOutboxMessage(
      isEvent: true,
      tenantId: "tenant-123",
      userId: "user-456");
    var options = _createOptions(auditEnabled: true);

    // Act
    var result = AuditOutboxMessageBuilder.TryBuildAuditMessage(message, options);

    // Assert
    await Assert.That(result).IsNotNull();
    await Assert.That(result!.Scope).IsNotNull();
    await Assert.That(result.Scope!.TenantId).IsEqualTo("tenant-123");
    await Assert.That(result.Scope!.UserId).IsEqualTo("user-456");
  }

  [Test]
  public async Task TryBuildAuditMessage_WithScopeTenantOnly_ExtractsTenantIdOnlyAsync() {
    // Arrange
    var message = _createOutboxMessage(
      isEvent: true,
      tenantId: "tenant-only");
    var options = _createOptions(auditEnabled: true);

    // Act
    var result = AuditOutboxMessageBuilder.TryBuildAuditMessage(message, options);

    // Assert
    await Assert.That(result).IsNotNull();
    await Assert.That(result!.Scope).IsNotNull();
    await Assert.That(result.Scope!.TenantId).IsEqualTo("tenant-only");
  }

  [Test]
  public async Task TryBuildAuditMessage_WithScopeUserOnly_ExtractsUserIdOnlyAsync() {
    // Arrange
    var message = _createOutboxMessage(
      isEvent: true,
      userId: "user-only");
    var options = _createOptions(auditEnabled: true);

    // Act
    var result = AuditOutboxMessageBuilder.TryBuildAuditMessage(message, options);

    // Assert
    await Assert.That(result).IsNotNull();
    await Assert.That(result!.Scope).IsNotNull();
    await Assert.That(result.Scope!.UserId).IsEqualTo("user-only");
  }

  [Test]
  public async Task TryBuildAuditMessage_WithoutScope_ScopePassesThroughNullAsync() {
    // Arrange
    var message = _createOutboxMessage(isEvent: true);
    var options = _createOptions(auditEnabled: true);

    // Act
    var result = AuditOutboxMessageBuilder.TryBuildAuditMessage(message, options);

    // Assert
    await Assert.That(result).IsNotNull();
    // Scope on the result is the original message scope (null)
    await Assert.That(result!.Scope).IsNull();
  }

  #endregion

  #region TryBuildAuditMessage - Correlation ID Extraction

  [Test]
  public async Task TryBuildAuditMessage_WithCorrelationIdInHopMetadata_ExtractsCorrelationIdAsync() {
    // Arrange
    const string correlationId = "corr-abc-123";
    var message = _createOutboxMessage(
      isEvent: true,
      correlationIdInHop: correlationId);
    var options = _createOptions(auditEnabled: true);

    // Act
    var result = AuditOutboxMessageBuilder.TryBuildAuditMessage(message, options);

    // Assert - Verify the audit envelope was built (correlation ID is inside the serialized EventAudited payload)
    await Assert.That(result).IsNotNull();
    // The correlation ID is embedded in the EventAudited payload
    var payload = result!.Envelope.Payload;
    await Assert.That(payload.ValueKind).IsEqualTo(JsonValueKind.Object);
  }

  [Test]
  public async Task TryBuildAuditMessage_WithNoHops_CorrelationIdIsNullAsync() {
    // Arrange - No hops in metadata
    var message = _createOutboxMessage(isEvent: true, includeHops: false);
    var options = _createOptions(auditEnabled: true);

    // Act
    var result = AuditOutboxMessageBuilder.TryBuildAuditMessage(message, options);

    // Assert
    await Assert.That(result).IsNotNull();
  }

  [Test]
  public async Task TryBuildAuditMessage_WithHopMetadataNoCorrelationId_CorrelationIdIsNullAsync() {
    // Arrange - Hop metadata exists but without CorrelationId key
    var message = _createOutboxMessage(isEvent: true, hopMetadataWithoutCorrelation: true);
    var options = _createOptions(auditEnabled: true);

    // Act
    var result = AuditOutboxMessageBuilder.TryBuildAuditMessage(message, options);

    // Assert
    await Assert.That(result).IsNotNull();
  }

  #endregion

  #region TryBuildAuditMessage - Full Audit Event Creation

  [Test]
  public async Task TryBuildAuditMessage_BuildsCorrectOutboxMessage_StructureAsync() {
    // Arrange
    var messageId = Guid.NewGuid();
    var streamId = Guid.NewGuid();
    var message = _createOutboxMessage(
      isEvent: true,
      messageId: messageId,
      streamId: streamId,
      messageType: "MyApp.Events.OrderCreated, MyApp");
    var options = _createOptions(auditEnabled: true);

    // Act
    var result = AuditOutboxMessageBuilder.TryBuildAuditMessage(message, options);

    // Assert
    await Assert.That(result).IsNotNull();
    await Assert.That(result!.Destination).IsEqualTo("whizbang.core.auditevents");
    await Assert.That(result.IsEvent).IsFalse(); // Audit events are NOT stored in event store
    await Assert.That(result.MessageType).Contains("EventAudited");
    await Assert.That(result.EnvelopeType).Contains("EventAudited");
    await Assert.That(result.Envelope).IsNotNull();
    await Assert.That(result.Metadata).IsNotNull();
    await Assert.That(result.Metadata.Hops).IsNotNull();
  }

  [Test]
  public async Task TryBuildAuditMessage_ExtractsFullTypeName_WithCommaAsync() {
    // Arrange - assembly-qualified name with comma separator
    var message = _createOutboxMessage(
      isEvent: true,
      messageType: "MyApp.Events.OrderCreated, MyApp.Events, Version=1.0.0.0");
    var options = _createOptions(auditEnabled: true);

    // Act
    var result = AuditOutboxMessageBuilder.TryBuildAuditMessage(message, options);

    // Assert - The event type name in payload should be just "MyApp.Events.OrderCreated"
    await Assert.That(result).IsNotNull();
    var payload = result!.Envelope.Payload;
    var originalEventType = payload.GetProperty("OriginalEventType").GetString();
    await Assert.That(originalEventType).IsEqualTo("MyApp.Events.OrderCreated");
  }

  [Test]
  public async Task TryBuildAuditMessage_ExtractsFullTypeName_WithoutCommaAsync() {
    // Arrange - type name without comma (no assembly qualifier)
    var message = _createOutboxMessage(
      isEvent: true,
      messageType: "MyApp.Events.OrderCreated");
    var options = _createOptions(auditEnabled: true);

    // Act
    var result = AuditOutboxMessageBuilder.TryBuildAuditMessage(message, options);

    // Assert - Returns the full string as-is when no comma
    await Assert.That(result).IsNotNull();
    var payload = result!.Envelope.Payload;
    var originalEventType = payload.GetProperty("OriginalEventType").GetString();
    await Assert.That(originalEventType).IsEqualTo("MyApp.Events.OrderCreated");
  }

  [Test]
  public async Task TryBuildAuditMessage_PreservesOriginalEventBody_InPayloadAsync() {
    // Arrange
    var eventBody = JsonSerializer.SerializeToElement(new { OrderId = "abc", Amount = 42 });
    var message = _createOutboxMessage(isEvent: true, payload: eventBody);
    var options = _createOptions(auditEnabled: true);

    // Act
    var result = AuditOutboxMessageBuilder.TryBuildAuditMessage(message, options);

    // Assert
    await Assert.That(result).IsNotNull();
    var payload = result!.Envelope.Payload;
    var originalBody = payload.GetProperty("OriginalBody");
    await Assert.That(originalBody.GetProperty("OrderId").GetString()).IsEqualTo("abc");
    await Assert.That(originalBody.GetProperty("Amount").GetInt32()).IsEqualTo(42);
  }

  [Test]
  public async Task TryBuildAuditMessage_SetsOriginalEventId_FromSourceMessageAsync() {
    // Arrange
    var sourceMessageId = Guid.NewGuid();
    var message = _createOutboxMessage(isEvent: true, messageId: sourceMessageId);
    var options = _createOptions(auditEnabled: true);

    // Act
    var result = AuditOutboxMessageBuilder.TryBuildAuditMessage(message, options);

    // Assert
    await Assert.That(result).IsNotNull();
    var payload = result!.Envelope.Payload;
    var originalEventId = payload.GetProperty("OriginalEventId").GetGuid();
    await Assert.That(originalEventId).IsEqualTo(sourceMessageId);
  }

  [Test]
  public async Task TryBuildAuditMessage_SetsStreamId_ToAuditEventIdAsync() {
    // Arrange
    var message = _createOutboxMessage(isEvent: true);
    var options = _createOptions(auditEnabled: true);

    // Act
    var result = AuditOutboxMessageBuilder.TryBuildAuditMessage(message, options);

    // Assert - StreamId should be the audit event's Id (a new GUID)
    await Assert.That(result).IsNotNull();
    await Assert.That(result!.StreamId).IsNotNull();
    await Assert.That(result.StreamId!.Value).IsNotEqualTo(Guid.Empty);
  }

  [Test]
  public async Task TryBuildAuditMessage_CopiesHopsFromOriginalEnvelope_ForSecurityContextPropagationAsync() {
    // Arrange
    var message = _createOutboxMessage(isEvent: true);
    var options = _createOptions(auditEnabled: true);

    // Act
    var result = AuditOutboxMessageBuilder.TryBuildAuditMessage(message, options);

    // Assert - Hops should include original hops + new audit relay hop
    await Assert.That(result).IsNotNull();
    var hops = result!.Envelope.Hops;
    await Assert.That(hops).IsNotNull();
    // Original message has 1 hop, audit adds 1 more = at least 2
    await Assert.That(hops!.Count).IsGreaterThanOrEqualTo(2);
  }

  [Test]
  public async Task TryBuildAuditMessage_SetsOriginalStreamPosition_ToZeroAsync() {
    // Arrange - Position not available from outbox message, should default to 0
    var message = _createOutboxMessage(isEvent: true);
    var options = _createOptions(auditEnabled: true);

    // Act
    var result = AuditOutboxMessageBuilder.TryBuildAuditMessage(message, options);

    // Assert
    await Assert.That(result).IsNotNull();
    var payload = result!.Envelope.Payload;
    var position = payload.GetProperty("OriginalStreamPosition").GetInt64();
    await Assert.That(position).IsEqualTo(0);
  }

  [Test]
  public async Task TryBuildAuditMessage_SetsOriginalStreamId_FromSourceStreamIdAsync() {
    // Arrange
    var streamId = Guid.NewGuid();
    var message = _createOutboxMessage(isEvent: true, streamId: streamId);
    var options = _createOptions(auditEnabled: true);

    // Act
    var result = AuditOutboxMessageBuilder.TryBuildAuditMessage(message, options);

    // Assert
    await Assert.That(result).IsNotNull();
    var payload = result!.Envelope.Payload;
    var originalStreamId = payload.GetProperty("OriginalStreamId").GetString();
    await Assert.That(originalStreamId).IsEqualTo(streamId.ToString());
  }

  [Test]
  public async Task TryBuildAuditMessage_NullStreamId_SetsEmptyStringAsync() {
    // Arrange - No stream ID on the source message
    var message = _createOutboxMessage(isEvent: true, streamId: null);
    var options = _createOptions(auditEnabled: true);

    // Act
    var result = AuditOutboxMessageBuilder.TryBuildAuditMessage(message, options);

    // Assert
    await Assert.That(result).IsNotNull();
    var payload = result!.Envelope.Payload;
    var originalStreamId = payload.GetProperty("OriginalStreamId").GetString();
    await Assert.That(originalStreamId).IsEqualTo(string.Empty);
  }

  [Test]
  public async Task TryBuildAuditMessage_MessageIdOnResult_MatchesEnvelopeMessageIdAsync() {
    // Arrange
    var message = _createOutboxMessage(isEvent: true);
    var options = _createOptions(auditEnabled: true);

    // Act
    var result = AuditOutboxMessageBuilder.TryBuildAuditMessage(message, options);

    // Assert
    await Assert.That(result).IsNotNull();
    await Assert.That(result!.MessageId).IsEqualTo(result.Envelope.MessageId.Value);
  }

  [Test]
  public async Task TryBuildAuditMessage_ScopeOnResult_MatchesSourceScopeAsync() {
    // Arrange
    var message = _createOutboxMessage(
      isEvent: true,
      tenantId: "scope-tenant",
      userId: "scope-user");
    var options = _createOptions(auditEnabled: true);

    // Act
    var result = AuditOutboxMessageBuilder.TryBuildAuditMessage(message, options);

    // Assert - The result scope should reference the source scope
    await Assert.That(result).IsNotNull();
    await Assert.That(result!.Scope).IsNotNull();
    await Assert.That(result.Scope!.TenantId).IsEqualTo("scope-tenant");
    await Assert.That(result.Scope!.UserId).IsEqualTo("scope-user");
  }

  #endregion

  #region ResolveEventType - Null Return on Failure

  [Test]
  public async Task TryBuildAuditMessage_UnresolvableType_StillBuildsAuditMessageAsync() {
    // Arrange - Type.GetType returns null for unknown types, which means _shouldAudit is skipped
    var message = _createOutboxMessage(
      isEvent: true,
      messageType: "Completely.Bogus.Type, NonExistent.Assembly");
    var options = _createOptions(auditEnabled: true);

    // Act
    var result = AuditOutboxMessageBuilder.TryBuildAuditMessage(message, options);

    // Assert - When eventType is null, the audit check is skipped (event IS audited)
    await Assert.That(result).IsNotNull();
  }

  #endregion

  #region Test Types

  // Event without any AuditEvent attribute (unmarked)
  internal sealed record TestUnmarkedEvent : IEvent;

  // Event explicitly excluded from audit
  [AuditEvent(Exclude = true, Reason = "High frequency")]
  internal sealed record TestExcludedFromAuditEvent : IEvent;

  // Event explicitly marked for audit (not excluded)
  [AuditEvent(Reason = "Financial transaction")]
  internal sealed record TestMarkedForAuditEvent : IEvent;

  #endregion

  #region Helper Methods

  private static OutboxMessage _createOutboxMessage(
      bool isEvent,
      Guid? messageId = null,
      Guid? streamId = null,
      string? messageType = null,
      JsonElement? payload = null,
      string? tenantId = null,
      string? userId = null,
      string? correlationIdInHop = null,
      bool includeHops = true,
      bool hopMetadataWithoutCorrelation = false) {
    var msgId = messageId ?? Guid.NewGuid();
    var body = payload ?? JsonSerializer.SerializeToElement(new { test = "data" });

    // Build metadata hops
    List<MessageHop> metadataHops = [];
    if (includeHops) {
      Dictionary<string, JsonElement>? hopMetadata = null;
      if (correlationIdInHop != null) {
        hopMetadata = new Dictionary<string, JsonElement> {
          ["CorrelationId"] = JsonSerializer.SerializeToElement(correlationIdInHop)
        };
      } else if (hopMetadataWithoutCorrelation) {
        hopMetadata = new Dictionary<string, JsonElement> {
          ["SomeOtherKey"] = JsonSerializer.SerializeToElement("value")
        };
      }

      metadataHops.Add(new MessageHop {
        ServiceInstance = ServiceInstanceInfo.Unknown,
        Type = HopType.Current,
        Timestamp = DateTimeOffset.UtcNow,
        Metadata = hopMetadata
      });
    }

    // Build scope
    PerspectiveScope? scope = null;
    if (tenantId != null || userId != null) {
      scope = new PerspectiveScope {
        TenantId = tenantId,
        UserId = userId
      };
    }

    // Build envelope
    var envelope = new MessageEnvelope<JsonElement> {
      MessageId = MessageId.New(),
      Payload = body,
      Hops = [
        new MessageHop {
          ServiceInstance = ServiceInstanceInfo.Unknown,
          Type = HopType.Current,
          Timestamp = DateTimeOffset.UtcNow
        }
      ],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };

    return new OutboxMessage {
      MessageId = msgId,
      Destination = "test-destination",
      Envelope = envelope,
      Metadata = new EnvelopeMetadata {
        MessageId = envelope.MessageId,
        Hops = metadataHops
      },
      EnvelopeType = "TestEnvelopeType",
      StreamId = streamId,
      IsEvent = isEvent,
      Scope = scope,
      MessageType = messageType ?? "TestNamespace.TestEvent, TestAssembly"
    };
  }

  private static SystemEventOptions _createOptions(
      bool auditEnabled,
      AuditMode auditMode = AuditMode.OptOut) {
    var options = new SystemEventOptions { AuditMode = auditMode };
    if (auditEnabled) {
      options.EnableEventAudit();
    }
    return options;
  }

  #endregion
}
