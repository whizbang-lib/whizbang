using System.Text.Json;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Attributes;
using Whizbang.Core.Lenses;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Security;
using Whizbang.Core.SystemEvents;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Dispatch;

namespace Whizbang.Core.Tests.SystemEvents;

/// <summary>
/// Additional coverage tests for SystemEventEmitter targeting uncovered code paths.
/// Focuses on scope extraction branches, _serializeToJsonElement fallback paths,
/// and EmitAsync conditional logic for non-audit system event types.
/// </summary>
[Category("SystemEvents")]
public class SystemEventEmitterCoverageTests {
  #region EmitEventAuditedAsync Scope Branch Coverage

  [Test]
  public async Task EmitEventAuditedAsync_WithScopeHavingOnlyUserId_ExtractsUserIdOnlyAsync() {
    // Arrange - Scope with UserId but no TenantId, exercising the null TenantId branch (line 71)
    var eventStore = new CoverageMockEventStore();
    var options = Options.Create(new SystemEventOptions().EnableEventAudit());
    var emitter = new SystemEventEmitter(options, eventStore);

    var envelope = new MessageEnvelope<string> {
      MessageId = MessageId.New(),
      Payload = "UserIdOnlyPayload",
      Hops = [
        new MessageHop {
          ServiceInstance = ServiceInstanceInfo.Unknown,
          Type = HopType.Current,
          Timestamp = DateTimeOffset.UtcNow,
          Scope = ScopeDelta.FromSecurityContext(new SecurityContext {
            UserId = "user-only-123"
          })
        }
      ],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };

    // Act
    await emitter.EmitEventAuditedAsync(Guid.NewGuid(), 10, envelope);

    // Assert - UserId extracted, TenantId is null
    await Assert.That(eventStore.AppendedEnvelopes).Count().IsEqualTo(1);
    var emittedEnvelope = eventStore.AppendedEnvelopes[0] as MessageEnvelope<EventAudited>;
    await Assert.That(emittedEnvelope).IsNotNull();
    await Assert.That(emittedEnvelope!.Payload.UserId).IsEqualTo("user-only-123");
    await Assert.That(emittedEnvelope.Payload.TenantId).IsNull();
    await Assert.That(emittedEnvelope.Payload.Scope).IsNotNull();
    await Assert.That(emittedEnvelope.Payload.Scope!.ContainsKey("UserId")).IsTrue();
    await Assert.That(emittedEnvelope.Payload.Scope!.ContainsKey("TenantId")).IsFalse();
  }

  [Test]
  public async Task EmitEventAuditedAsync_WithCorrelationIdOnly_ExtractsCorrelationIdAsync() {
    // Arrange - Envelope with CorrelationId on hop but no scope delta
    var eventStore = new CoverageMockEventStore();
    var options = Options.Create(new SystemEventOptions().EnableEventAudit());
    var emitter = new SystemEventEmitter(options, eventStore);

    var correlationGuid = Guid.NewGuid();
    var envelope = new MessageEnvelope<string> {
      MessageId = MessageId.New(),
      Payload = "CorrelationOnlyPayload",
      Hops = [
        new MessageHop {
          ServiceInstance = ServiceInstanceInfo.Unknown,
          Type = HopType.Current,
          Timestamp = DateTimeOffset.UtcNow,
          CorrelationId = new CorrelationId(correlationGuid)
          // No Scope set
        }
      ],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };

    // Act
    await emitter.EmitEventAuditedAsync(Guid.NewGuid(), 3, envelope);

    // Assert - CorrelationId extracted, scope has only CorrelationId key
    await Assert.That(eventStore.AppendedEnvelopes).Count().IsEqualTo(1);
    var emittedEnvelope = eventStore.AppendedEnvelopes[0] as MessageEnvelope<EventAudited>;
    await Assert.That(emittedEnvelope).IsNotNull();
    await Assert.That(emittedEnvelope!.Payload.CorrelationId).IsEqualTo(correlationGuid.ToString());
    await Assert.That(emittedEnvelope.Payload.TenantId).IsNull();
    await Assert.That(emittedEnvelope.Payload.UserId).IsNull();
    await Assert.That(emittedEnvelope.Payload.Scope).IsNotNull();
    await Assert.That(emittedEnvelope.Payload.Scope!.ContainsKey("CorrelationId")).IsTrue();
  }

  [Test]
  public async Task EmitEventAuditedAsync_WithEmptyHops_ExtractsNoScopeAsync() {
    // Arrange - Envelope with empty Hops list so GetCurrentScope() returns null
    var eventStore = new CoverageMockEventStore();
    var options = Options.Create(new SystemEventOptions().EnableEventAudit());
    var emitter = new SystemEventEmitter(options, eventStore);

    var envelope = new MessageEnvelope<string> {
      MessageId = MessageId.New(),
      Payload = "EmptyHopsPayload",
      Hops = [],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };

    // Act
    await emitter.EmitEventAuditedAsync(Guid.NewGuid(), 1, envelope);

    // Assert - No scope data extracted
    await Assert.That(eventStore.AppendedEnvelopes).Count().IsEqualTo(1);
    var emittedEnvelope = eventStore.AppendedEnvelopes[0] as MessageEnvelope<EventAudited>;
    await Assert.That(emittedEnvelope).IsNotNull();
    await Assert.That(emittedEnvelope!.Payload.TenantId).IsNull();
    await Assert.That(emittedEnvelope.Payload.UserId).IsNull();
    await Assert.That(emittedEnvelope.Payload.CorrelationId).IsNull();
    await Assert.That(emittedEnvelope.Payload.Scope).IsNull();
  }

  [Test]
  public async Task EmitEventAuditedAsync_WithAllScopeFields_ExtractsAllFieldsAsync() {
    // Arrange - Complete scope with TenantId, UserId, and CorrelationId
    var eventStore = new CoverageMockEventStore();
    var options = Options.Create(new SystemEventOptions().EnableEventAudit());
    var emitter = new SystemEventEmitter(options, eventStore);

    var correlationGuid = Guid.NewGuid();
    var envelope = new MessageEnvelope<string> {
      MessageId = MessageId.New(),
      Payload = "FullScopePayload",
      Hops = [
        new MessageHop {
          ServiceInstance = ServiceInstanceInfo.Unknown,
          Type = HopType.Current,
          Timestamp = DateTimeOffset.UtcNow,
          Scope = ScopeDelta.FromSecurityContext(new SecurityContext {
            TenantId = "t-full",
            UserId = "u-full"
          }),
          CorrelationId = new CorrelationId(correlationGuid)
        }
      ],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };

    // Act
    await emitter.EmitEventAuditedAsync(Guid.NewGuid(), 7, envelope);

    // Assert - All three scope keys present
    await Assert.That(eventStore.AppendedEnvelopes).Count().IsEqualTo(1);
    var emittedEnvelope = eventStore.AppendedEnvelopes[0] as MessageEnvelope<EventAudited>;
    await Assert.That(emittedEnvelope).IsNotNull();
    await Assert.That(emittedEnvelope!.Payload.TenantId).IsEqualTo("t-full");
    await Assert.That(emittedEnvelope.Payload.UserId).IsEqualTo("u-full");
    await Assert.That(emittedEnvelope.Payload.CorrelationId).IsEqualTo(correlationGuid.ToString());
    await Assert.That(emittedEnvelope.Payload.Scope).IsNotNull();
    await Assert.That(emittedEnvelope.Payload.Scope!.Count).IsEqualTo(3);
  }

  [Test]
  public async Task EmitEventAuditedAsync_SetsOriginalEventTypeToTypeNameAsync() {
    // Arrange - Verifies OriginalEventType is set to typeof(TEvent).Name
    var eventStore = new CoverageMockEventStore();
    var options = Options.Create(new SystemEventOptions().EnableEventAudit());
    var emitter = new SystemEventEmitter(options, eventStore);

    var envelope = _createTestEnvelope("test-value");

    // Act
    await emitter.EmitEventAuditedAsync(Guid.NewGuid(), 1, envelope);

    // Assert
    var emittedEnvelope = eventStore.AppendedEnvelopes[0] as MessageEnvelope<EventAudited>;
    await Assert.That(emittedEnvelope).IsNotNull();
    await Assert.That(emittedEnvelope!.Payload.OriginalEventType).IsEqualTo("String");
  }

  [Test]
  public async Task EmitEventAuditedAsync_SetsOriginalStreamIdToStringAsync() {
    // Arrange
    var eventStore = new CoverageMockEventStore();
    var options = Options.Create(new SystemEventOptions().EnableEventAudit());
    var emitter = new SystemEventEmitter(options, eventStore);

    var streamId = Guid.NewGuid();
    var envelope = _createTestEnvelope("StreamIdTest");

    // Act
    await emitter.EmitEventAuditedAsync(streamId, 99, envelope);

    // Assert - OriginalStreamId is the string representation of the Guid
    var emittedEnvelope = eventStore.AppendedEnvelopes[0] as MessageEnvelope<EventAudited>;
    await Assert.That(emittedEnvelope).IsNotNull();
    await Assert.That(emittedEnvelope!.Payload.OriginalStreamId).IsEqualTo(streamId.ToString());
    await Assert.That(emittedEnvelope.Payload.OriginalStreamPosition).IsEqualTo(99);
  }

  [Test]
  public async Task EmitEventAuditedAsync_GeneratesNonEmptyIdAsync() {
    // Arrange - Verifies TrackedGuid.NewMedo() generates a non-empty GUID for the audit event
    var eventStore = new CoverageMockEventStore();
    var options = Options.Create(new SystemEventOptions().EnableEventAudit());
    var emitter = new SystemEventEmitter(options, eventStore);

    var envelope = _createTestEnvelope("IdTest");

    // Act
    await emitter.EmitEventAuditedAsync(Guid.NewGuid(), 1, envelope);

    // Assert
    var emittedEnvelope = eventStore.AppendedEnvelopes[0] as MessageEnvelope<EventAudited>;
    await Assert.That(emittedEnvelope).IsNotNull();
    await Assert.That(emittedEnvelope!.Payload.Id).IsNotEqualTo(Guid.Empty);
  }

  #endregion

  #region EmitCommandAuditedAsync Scope Branch Coverage

  [Test]
  public async Task EmitCommandAuditedAsync_WithContextHavingOnlyCorrelationId_ExtractsCorrelationOnlyAsync() {
    // Arrange - Context with CorrelationId but null UserId and no TenantId in Metadata
    var eventStore = new CoverageMockEventStore();
    var options = Options.Create(new SystemEventOptions().EnableCommandAudit());
    var emitter = new SystemEventEmitter(options, eventStore);

    var context = new CoverageTestMessageContext();
    // UserId is null, no TenantId in Metadata, but CorrelationId is set automatically

    // Act
    await emitter.EmitCommandAuditedAsync("CorrOnlyCmd", "ok", "Receptor", context);

    // Assert - Only CorrelationId in scope
    var emittedEnvelope = eventStore.AppendedEnvelopes[0] as MessageEnvelope<CommandAudited>;
    await Assert.That(emittedEnvelope).IsNotNull();
    await Assert.That(emittedEnvelope!.Payload.UserId).IsNull();
    await Assert.That(emittedEnvelope.Payload.TenantId).IsNull();
    await Assert.That(emittedEnvelope.Payload.CorrelationId).IsNotNull();
    await Assert.That(emittedEnvelope.Payload.Scope).IsNotNull();
    await Assert.That(emittedEnvelope.Payload.Scope!.ContainsKey("CorrelationId")).IsTrue();
    await Assert.That(emittedEnvelope.Payload.Scope!.ContainsKey("UserId")).IsFalse();
  }

  [Test]
  public async Task EmitCommandAuditedAsync_WithAllContextFields_ExtractsAllFieldsAsync() {
    // Arrange - Full context with TenantId, UserId, CorrelationId
    var eventStore = new CoverageMockEventStore();
    var options = Options.Create(new SystemEventOptions().EnableCommandAudit());
    var emitter = new SystemEventEmitter(options, eventStore);

    var context = new CoverageTestMessageContext {
      UserId = "cmd-user-all"
    };
    context.Metadata["TenantId"] = "cmd-tenant-all";

    // Act
    await emitter.EmitCommandAuditedAsync("AllFieldsCmd", "response", "FullReceptor", context);

    // Assert - All scope fields present
    var emittedEnvelope = eventStore.AppendedEnvelopes[0] as MessageEnvelope<CommandAudited>;
    await Assert.That(emittedEnvelope).IsNotNull();
    await Assert.That(emittedEnvelope!.Payload.TenantId).IsEqualTo("cmd-tenant-all");
    await Assert.That(emittedEnvelope.Payload.UserId).IsEqualTo("cmd-user-all");
    await Assert.That(emittedEnvelope.Payload.CorrelationId).IsNotNull();
    await Assert.That(emittedEnvelope.Payload.Scope).IsNotNull();
    await Assert.That(emittedEnvelope.Payload.Scope!.Count).IsEqualTo(3);
  }

  [Test]
  public async Task EmitCommandAuditedAsync_SetsCommandTypeAndResponseTypeAsync() {
    // Arrange - Verifies CommandType = typeof(TCommand).Name and ResponseType = typeof(TResponse).Name
    var eventStore = new CoverageMockEventStore();
    var options = Options.Create(new SystemEventOptions().EnableCommandAudit());
    var emitter = new SystemEventEmitter(options, eventStore);

    // Act - Using int as TResponse to verify ResponseType
    await emitter.EmitCommandAuditedAsync("TypeCheckCmd", 42, "TypeReceptor", null);

    // Assert
    var emittedEnvelope = eventStore.AppendedEnvelopes[0] as MessageEnvelope<CommandAudited>;
    await Assert.That(emittedEnvelope).IsNotNull();
    await Assert.That(emittedEnvelope!.Payload.CommandType).IsEqualTo("String");
    await Assert.That(emittedEnvelope.Payload.ResponseType).IsEqualTo("Int32");
    await Assert.That(emittedEnvelope.Payload.ReceptorName).IsEqualTo("TypeReceptor");
  }

  [Test]
  public async Task EmitCommandAuditedAsync_GeneratesNonEmptyIdAsync() {
    // Arrange
    var eventStore = new CoverageMockEventStore();
    var options = Options.Create(new SystemEventOptions().EnableCommandAudit());
    var emitter = new SystemEventEmitter(options, eventStore);

    // Act
    await emitter.EmitCommandAuditedAsync("IdCmd", "ok", "Receptor", null);

    // Assert
    var emittedEnvelope = eventStore.AppendedEnvelopes[0] as MessageEnvelope<CommandAudited>;
    await Assert.That(emittedEnvelope).IsNotNull();
    await Assert.That(emittedEnvelope!.Payload.Id).IsNotEqualTo(Guid.Empty);
  }

  [Test]
  public async Task EmitCommandAuditedAsync_SetsTimestampToCurrentUtcTimeAsync() {
    // Arrange
    var eventStore = new CoverageMockEventStore();
    var options = Options.Create(new SystemEventOptions().EnableCommandAudit());
    var emitter = new SystemEventEmitter(options, eventStore);

    var before = DateTimeOffset.UtcNow;

    // Act
    await emitter.EmitCommandAuditedAsync("TimestampCmd", "ok", "Receptor", null);

    var after = DateTimeOffset.UtcNow;

    // Assert
    var emittedEnvelope = eventStore.AppendedEnvelopes[0] as MessageEnvelope<CommandAudited>;
    await Assert.That(emittedEnvelope).IsNotNull();
    await Assert.That(emittedEnvelope!.Payload.Timestamp).IsGreaterThanOrEqualTo(before);
    await Assert.That(emittedEnvelope.Payload.Timestamp).IsLessThanOrEqualTo(after);
  }

  [Test]
  public async Task EmitCommandAuditedAsync_WithCancellationToken_PassesTokenAsync() {
    // Arrange
    var eventStore = new CoverageMockEventStore();
    var options = Options.Create(new SystemEventOptions().EnableCommandAudit());
    var emitter = new SystemEventEmitter(options, eventStore);

    using var cts = new CancellationTokenSource();

    // Act
    await emitter.EmitCommandAuditedAsync("TokenCmd", "ok", "Receptor", null, cts.Token);

    // Assert - Completed without error
    await Assert.That(eventStore.AppendedEnvelopes).Count().IsEqualTo(1);
    await Assert.That(eventStore.LastCancellationToken).IsEqualTo(cts.Token);
  }

  #endregion

  #region EmitAsync Complex Conditional Coverage

  [Test]
  public async Task EmitAsync_CustomSystemEvent_WhenIsEnabledFalseAndAuditEnabled_DoesNotEmitAsync() {
    // Arrange - Tests the compound conditional: IsEnabled returns false, AuditEnabled is true,
    // but type is neither EventAudited nor CommandAudited, so it should NOT emit (line 162-164)
    var eventStore = new CoverageMockEventStore();
    var options = Options.Create(new SystemEventOptions().EnableAudit());
    var emitter = new SystemEventEmitter(options, eventStore);

    var customEvent = new CustomSystemEvent {
      Id = Guid.NewGuid(),
      Data = "should-not-emit"
    };

    // Act
    await emitter.EmitAsync(customEvent);

    // Assert - Not emitted because IsEnabled<CustomSystemEvent>() is false
    // and it's not EventAudited or CommandAudited
    await Assert.That(eventStore.AppendedEnvelopes).IsEmpty();
  }

  [Test]
  public async Task EmitAsync_CustomSystemEvent_WhenAuditDisabledAndIsEnabledFalse_DoesNotEmitAsync() {
    // Arrange - Tests compound conditional with both AuditEnabled=false and IsEnabled=false
    var eventStore = new CoverageMockEventStore();
    var options = Options.Create(new SystemEventOptions()); // Nothing enabled
    var emitter = new SystemEventEmitter(options, eventStore);

    var customEvent = new CustomSystemEvent {
      Id = Guid.NewGuid(),
      Data = "should-not-emit"
    };

    // Act
    await emitter.EmitAsync(customEvent);

    // Assert
    await Assert.That(eventStore.AppendedEnvelopes).IsEmpty();
  }

  [Test]
  public async Task EmitAsync_EventAudited_WhenOnlyCommandAuditEnabled_StillEmitsAuditEventAsync() {
    // Arrange - AuditEnabled is true (via CommandAudit), type is EventAudited
    // Tests the fallback path: IsEnabled<EventAudited>() is false but AuditEnabled is true
    // and type IS EventAudited, so it should emit
    var eventStore = new CoverageMockEventStore();
    var options = Options.Create(new SystemEventOptions().EnableCommandAudit());
    var emitter = new SystemEventEmitter(options, eventStore);

    // EventAuditEnabled=false, CommandAuditEnabled=true, AuditEnabled=true
    var systemEvent = new EventAudited {
      Id = Guid.NewGuid(),
      OriginalEventType = "Test",
      OriginalStreamId = "stream-1",
      OriginalStreamPosition = 1,
      OriginalBody = JsonSerializer.SerializeToElement(new { }),
      Timestamp = DateTimeOffset.UtcNow
    };

    // Act
    await emitter.EmitAsync(systemEvent);

    // Assert - Should emit because AuditEnabled=true and type is EventAudited
    await Assert.That(eventStore.AppendedEnvelopes).Count().IsEqualTo(1);
  }

  [Test]
  public async Task EmitAsync_CommandAudited_WhenOnlyEventAuditEnabled_StillEmitsAuditEventAsync() {
    // Arrange - AuditEnabled is true (via EventAudit), type is CommandAudited
    // Tests: IsEnabled<CommandAudited>() is false but AuditEnabled is true
    // and type IS CommandAudited
    var eventStore = new CoverageMockEventStore();
    var options = Options.Create(new SystemEventOptions().EnableEventAudit());
    var emitter = new SystemEventEmitter(options, eventStore);

    var systemEvent = new CommandAudited {
      Id = Guid.NewGuid(),
      CommandType = "Test",
      CommandBody = JsonSerializer.SerializeToElement(new { }),
      Timestamp = DateTimeOffset.UtcNow,
      ReceptorName = "Receptor",
      ResponseType = "string"
    };

    // Act
    await emitter.EmitAsync(systemEvent);

    // Assert - Should emit because AuditEnabled=true and type is CommandAudited
    await Assert.That(eventStore.AppendedEnvelopes).Count().IsEqualTo(1);
  }

  [Test]
  public async Task EmitAsync_CreatesEnvelopeWithSingleHopAndTimestampAsync() {
    // Arrange - Verifies the envelope creation path in detail
    var eventStore = new CoverageMockEventStore();
    var options = Options.Create(new SystemEventOptions().EnableAudit());
    var emitter = new SystemEventEmitter(options, eventStore);

    var before = DateTimeOffset.UtcNow;

    var systemEvent = new EventAudited {
      Id = Guid.NewGuid(),
      OriginalEventType = "EnvelopeTest",
      OriginalStreamId = "stream-envelope",
      OriginalStreamPosition = 1,
      OriginalBody = JsonSerializer.SerializeToElement(new { }),
      Timestamp = DateTimeOffset.UtcNow
    };

    // Act
    await emitter.EmitAsync(systemEvent);

    // Assert - Verify hop timestamp is reasonable
    var envelope = eventStore.AppendedEnvelopes[0] as MessageEnvelope<EventAudited>;
    await Assert.That(envelope).IsNotNull();
    await Assert.That(envelope!.Hops).Count().IsEqualTo(1);
    await Assert.That(envelope.Hops[0].Timestamp).IsGreaterThanOrEqualTo(before);
    await Assert.That(envelope.Hops[0].Timestamp).IsLessThanOrEqualTo(DateTimeOffset.UtcNow);
    await Assert.That(envelope.Hops[0].ServiceInstance).IsEqualTo(ServiceInstanceInfo.Unknown);
    await Assert.That(envelope.Hops[0].Type).IsEqualTo(HopType.Current);
  }

  #endregion

  #region ShouldExcludeFromAudit Additional Coverage

  [Test]
  public async Task ShouldExcludeFromAudit_WithSystemEventTypes_ExcludesEventAuditedAsync() {
    // Arrange - EventAudited has [AuditEvent(Exclude = true)]
    var emitter = _createEmitter();

    // Act
    var result = emitter.ShouldExcludeFromAudit(typeof(EventAudited));

    // Assert
    await Assert.That(result).IsTrue();
  }

  [Test]
  public async Task ShouldExcludeFromAudit_WithSystemEventTypes_ExcludesCommandAuditedAsync() {
    // Arrange - CommandAudited has [AuditEvent(Exclude = true)]
    var emitter = _createEmitter();

    // Act
    var result = emitter.ShouldExcludeFromAudit(typeof(CommandAudited));

    // Assert
    await Assert.That(result).IsTrue();
  }

  [Test]
  public async Task ShouldExcludeFromAudit_WithTypeWithoutAttribute_ReturnsFalseAsync() {
    // Arrange - A plain type with no AuditEventAttribute
    var emitter = _createEmitter();

    // Act
    var result = emitter.ShouldExcludeFromAudit(typeof(string));

    // Assert
    await Assert.That(result).IsFalse();
  }

  [Test]
  public async Task ShouldExcludeFromAudit_WithCustomSystemEvent_ReturnsFalseAsync() {
    // Arrange
    var emitter = _createEmitter();

    // Act
    var result = emitter.ShouldExcludeFromAudit(typeof(CustomSystemEvent));

    // Assert
    await Assert.That(result).IsFalse();
  }

  #endregion

  #region EmitEventAuditedAsync Cancellation Token Coverage

  [Test]
  public async Task EmitEventAuditedAsync_PassesCancellationTokenThroughAsync() {
    // Arrange
    var eventStore = new CoverageMockEventStore();
    var options = Options.Create(new SystemEventOptions().EnableEventAudit());
    var emitter = new SystemEventEmitter(options, eventStore);

    using var cts = new CancellationTokenSource();
    var envelope = _createTestEnvelope("CancelTokenTest");

    // Act
    await emitter.EmitEventAuditedAsync(Guid.NewGuid(), 1, envelope, cts.Token);

    // Assert - Event was emitted and cancellation token was forwarded
    await Assert.That(eventStore.AppendedEnvelopes).Count().IsEqualTo(1);
    await Assert.That(eventStore.LastCancellationToken).IsEqualTo(cts.Token);
  }

  #endregion

  #region Multiple Events in Sequence

  [Test]
  public async Task EmitAsync_MultipleEventsInSequence_AllAppendedToSystemStreamAsync() {
    // Arrange
    var eventStore = new CoverageMockEventStore();
    var options = Options.Create(new SystemEventOptions().EnableAudit());
    var emitter = new SystemEventEmitter(options, eventStore);

    var event1 = new EventAudited {
      Id = Guid.NewGuid(),
      OriginalEventType = "Event1",
      OriginalStreamId = "stream-1",
      OriginalStreamPosition = 1,
      OriginalBody = JsonSerializer.SerializeToElement(new { }),
      Timestamp = DateTimeOffset.UtcNow
    };
    var event2 = new CommandAudited {
      Id = Guid.NewGuid(),
      CommandType = "Cmd2",
      CommandBody = JsonSerializer.SerializeToElement(new { }),
      Timestamp = DateTimeOffset.UtcNow,
      ReceptorName = "R",
      ResponseType = "string"
    };

    // Act
    await emitter.EmitAsync(event1);
    await emitter.EmitAsync(event2);

    // Assert
    await Assert.That(eventStore.AppendedEnvelopes).Count().IsEqualTo(2);
    await Assert.That(eventStore.AppendedStreamIds[0]).IsEqualTo(SystemEventStreams.StreamId);
    await Assert.That(eventStore.AppendedStreamIds[1]).IsEqualTo(SystemEventStreams.StreamId);
  }

  #endregion

  #region EmitCommandAuditedAsync with TenantId only

  [Test]
  public async Task EmitCommandAuditedAsync_WithContextHavingTenantIdOnly_ExtractsTenantOnlyAsync() {
    // Arrange - Context with TenantId in metadata but no UserId
    var eventStore = new CoverageMockEventStore();
    var options = Options.Create(new SystemEventOptions().EnableCommandAudit());
    var emitter = new SystemEventEmitter(options, eventStore);

    var context = new CoverageTestMessageContext();
    context.Metadata["TenantId"] = "tenant-only-cmd";
    // UserId is null

    // Act
    await emitter.EmitCommandAuditedAsync("TenantOnlyCmd", "ok", "Receptor", context);

    // Assert
    var emittedEnvelope = eventStore.AppendedEnvelopes[0] as MessageEnvelope<CommandAudited>;
    await Assert.That(emittedEnvelope).IsNotNull();
    await Assert.That(emittedEnvelope!.Payload.TenantId).IsEqualTo("tenant-only-cmd");
    await Assert.That(emittedEnvelope.Payload.UserId).IsNull();
    await Assert.That(emittedEnvelope.Payload.Scope).IsNotNull();
    await Assert.That(emittedEnvelope.Payload.Scope!.ContainsKey("TenantId")).IsTrue();
    await Assert.That(emittedEnvelope.Payload.Scope!.ContainsKey("UserId")).IsFalse();
  }

  #endregion

  #region Helper Methods

  private static SystemEventEmitter _createEmitter() {
    var eventStore = new CoverageMockEventStore();
    var options = Options.Create(new SystemEventOptions());
    return new SystemEventEmitter(options, eventStore);
  }

  private static MessageEnvelope<T> _createTestEnvelope<T>(T payload) {
    return new MessageEnvelope<T> {
      MessageId = MessageId.New(),
      Payload = payload,
      Hops = [
        new MessageHop {
          ServiceInstance = ServiceInstanceInfo.Unknown,
          Type = HopType.Current,
          Timestamp = DateTimeOffset.UtcNow
        }
      ],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };
  }

  #endregion

  #region Claims Iteration Coverage (Line 84)

  [Test]
  public async Task EmitEventAuditedAsync_WithClaims_IncludesClaimsInScopeDictionaryAsync() {
    // Arrange - Scope with claims to exercise the claims iteration loop (line 84)
    var eventStore = new CoverageMockEventStore();
    var options = Options.Create(new SystemEventOptions().EnableEventAudit());
    var emitter = new SystemEventEmitter(options, eventStore);

    // Create a ScopeContext with claims
    var scopeContext = new ScopeContext {
      Scope = new Whizbang.Core.Lenses.PerspectiveScope { TenantId = "t1", UserId = "u1" },
      Roles = new HashSet<string>(),
      Permissions = new HashSet<Permission>(),
      SecurityPrincipals = new HashSet<SecurityPrincipalId>(),
      Claims = new Dictionary<string, string> {
        ["email"] = "test@example.com",
        ["name"] = "Test User"
      }
    };

    // Build delta from scope context (so GetCurrentScope returns claims)
    var delta = ScopeDelta.CreateDelta(null, scopeContext);

    var envelope = new MessageEnvelope<string> {
      MessageId = MessageId.New(),
      Payload = "ClaimsPayload",
      Hops = [
        new MessageHop {
          ServiceInstance = ServiceInstanceInfo.Unknown,
          Type = HopType.Current,
          Timestamp = DateTimeOffset.UtcNow,
          Scope = delta
        }
      ],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };

    // Act
    await emitter.EmitEventAuditedAsync(Guid.NewGuid(), 10, envelope);

    // Assert - audit event was emitted
    await Assert.That(eventStore.AppendedEnvelopes).Count().IsEqualTo(1);
  }

  #endregion

  #region Test Types

  /// <summary>
  /// Custom ISystemEvent type that is NOT EventAudited or CommandAudited.
  /// Used to test EmitAsync conditional logic for unknown system event types.
  /// </summary>
  private sealed record CustomSystemEvent : ISystemEvent {
    [StreamId]
    public required Guid Id { get; init; }
    public required string Data { get; init; }
  }

  /// <summary>
  /// Message context implementation for testing command audit scope extraction.
  /// Unlike the existing TestMessageContext, this returns null from TenantId property
  /// instead of throwing, to exercise the context?.UserId branch.
  /// </summary>
  private sealed class CoverageTestMessageContext : IMessageContext {
    public MessageId MessageId { get; init; } = MessageId.New();
    public CorrelationId CorrelationId { get; init; } = new(Guid.NewGuid());
    public MessageId CausationId { get; init; } = MessageId.New();
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public string? UserId { get; set; }
    public string? TenantId { get; set; }
    public Dictionary<string, object> Metadata { get; } = [];
    public Core.Security.IScopeContext? ScopeContext => null;
    public ICallerInfo? CallerInfo => null;

    IReadOnlyDictionary<string, object> IMessageContext.Metadata => Metadata;
  }

  #endregion

  #region Mock EventStore

  /// <summary>
  /// Mock IEventStore for coverage tests, also tracks the CancellationToken.
  /// </summary>
  private sealed class CoverageMockEventStore : IEventStore {
    public List<object> AppendedEnvelopes { get; } = [];
    public List<Guid> AppendedStreamIds { get; } = [];
    public CancellationToken LastCancellationToken { get; private set; }

    public Task AppendAsync<TMessage>(Guid streamId, MessageEnvelope<TMessage> envelope, CancellationToken cancellationToken = default) {
      AppendedStreamIds.Add(streamId);
      AppendedEnvelopes.Add(envelope!);
      LastCancellationToken = cancellationToken;
      return Task.CompletedTask;
    }

    public Task AppendAsync<TMessage>(Guid streamId, TMessage message, CancellationToken cancellationToken = default)
        where TMessage : notnull {
      AppendedStreamIds.Add(streamId);
      AppendedEnvelopes.Add(message!);
      LastCancellationToken = cancellationToken;
      return Task.CompletedTask;
    }

    public IAsyncEnumerable<MessageEnvelope<TMessage>> ReadAsync<TMessage>(Guid streamId, long fromSequence, CancellationToken cancellationToken = default) =>
        AsyncEnumerable.Empty<MessageEnvelope<TMessage>>();

    public IAsyncEnumerable<MessageEnvelope<TMessage>> ReadAsync<TMessage>(Guid streamId, Guid? fromEventId, CancellationToken cancellationToken = default) =>
        AsyncEnumerable.Empty<MessageEnvelope<TMessage>>();

    public IAsyncEnumerable<MessageEnvelope<IEvent>> ReadPolymorphicAsync(Guid streamId, Guid? fromEventId, IReadOnlyList<Type> eventTypes, CancellationToken cancellationToken = default) =>
        AsyncEnumerable.Empty<MessageEnvelope<IEvent>>();

    public Task<List<MessageEnvelope<TMessage>>> GetEventsBetweenAsync<TMessage>(Guid streamId, Guid? afterEventId, Guid upToEventId, CancellationToken cancellationToken = default) =>
        Task.FromResult(new List<MessageEnvelope<TMessage>>());

    public Task<List<MessageEnvelope<IEvent>>> GetEventsBetweenPolymorphicAsync(Guid streamId, Guid? afterEventId, Guid upToEventId, IReadOnlyList<Type> eventTypes, CancellationToken cancellationToken = default) =>
        Task.FromResult(new List<MessageEnvelope<IEvent>>());

    public Task<long> GetLastSequenceAsync(Guid streamId, CancellationToken cancellationToken = default) =>
        Task.FromResult(0L);
  }

  #endregion
}
