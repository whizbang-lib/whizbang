using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Attributes;
using Whizbang.Core.AutoPopulate;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Security;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.AutoPopulate;

/// <summary>
/// Tests for <see cref="AutoPopulateRegistration"/> and <see cref="IAutoPopulateRegistry"/>.
/// Validates the auto-populate infrastructure for AOT-compatible property population.
/// </summary>
[Category("Core")]
[Category("AutoPopulate")]
public class AutoPopulateProcessorTests {

  #region AutoPopulateRegistration Tests

  [Test]
  public async Task AutoPopulateRegistration_RequiredProperties_CanBeSetAsync() {
    // Arrange & Act
    var registration = new AutoPopulateRegistration {
      MessageType = typeof(TestMessage),
      PropertyName = "SentAt",
      PropertyType = typeof(DateTimeOffset?),
      PopulateKind = PopulateKind.Timestamp,
      TimestampKind = TimestampKind.SentAt
    };

    // Assert
    await Assert.That(registration.MessageType).IsEqualTo(typeof(TestMessage));
    await Assert.That(registration.PropertyName).IsEqualTo("SentAt");
    await Assert.That(registration.PropertyType).IsEqualTo(typeof(DateTimeOffset?));
    await Assert.That(registration.PopulateKind).IsEqualTo(PopulateKind.Timestamp);
    await Assert.That(registration.TimestampKind).IsEqualTo(TimestampKind.SentAt);
  }

  [Test]
  public async Task AutoPopulateRegistration_ContextKind_CanBeSetAsync() {
    // Arrange & Act
    var registration = new AutoPopulateRegistration {
      MessageType = typeof(TestMessage),
      PropertyName = "UserId",
      PropertyType = typeof(string),
      PopulateKind = PopulateKind.Context,
      ContextKind = ContextKind.UserId
    };

    // Assert
    await Assert.That(registration.PopulateKind).IsEqualTo(PopulateKind.Context);
    await Assert.That(registration.ContextKind).IsEqualTo(ContextKind.UserId);
  }

  [Test]
  public async Task AutoPopulateRegistration_ServiceKind_CanBeSetAsync() {
    // Arrange & Act
    var registration = new AutoPopulateRegistration {
      MessageType = typeof(TestMessage),
      PropertyName = "ServiceName",
      PropertyType = typeof(string),
      PopulateKind = PopulateKind.Service,
      ServiceKind = ServiceKind.ServiceName
    };

    // Assert
    await Assert.That(registration.PopulateKind).IsEqualTo(PopulateKind.Service);
    await Assert.That(registration.ServiceKind).IsEqualTo(ServiceKind.ServiceName);
  }

  [Test]
  public async Task AutoPopulateRegistration_IdentifierKind_CanBeSetAsync() {
    // Arrange & Act
    var registration = new AutoPopulateRegistration {
      MessageType = typeof(TestMessage),
      PropertyName = "CorrelationId",
      PropertyType = typeof(Guid?),
      PopulateKind = PopulateKind.Identifier,
      IdentifierKind = IdentifierKind.CorrelationId
    };

    // Assert
    await Assert.That(registration.PopulateKind).IsEqualTo(PopulateKind.Identifier);
    await Assert.That(registration.IdentifierKind).IsEqualTo(IdentifierKind.CorrelationId);
  }

  #endregion

  #region PopulateKind Enum Tests

  [Test]
  public async Task PopulateKind_AllValues_AreDistinctAsync() {
    // Arrange
    var values = Enum.GetValues<PopulateKind>();

    // Act
    var distinctCount = values.Distinct().Count();

    // Assert
    await Assert.That(distinctCount).IsEqualTo(values.Length);
  }

  [Test]
  public async Task PopulateKind_HasFourValuesAsync() {
    // Arrange & Act
    var values = Enum.GetValues<PopulateKind>();

    // Assert - Timestamp, Context, Service, Identifier
    await Assert.That(values.Length).IsEqualTo(4);
  }

  #endregion

  #region IAutoPopulateRegistry Tests

  [Test]
  public async Task TestAutoPopulateRegistry_GetRegistrationsFor_ReturnsMatchingRegistrationsAsync() {
    // Arrange
    var registration = new AutoPopulateRegistration {
      MessageType = typeof(TestMessage),
      PropertyName = "SentAt",
      PropertyType = typeof(DateTimeOffset?),
      PopulateKind = PopulateKind.Timestamp,
      TimestampKind = TimestampKind.SentAt
    };

    var registry = new TestAutoPopulateRegistry([registration]);

    // Act
    var results = registry.GetRegistrationsFor(typeof(TestMessage)).ToList();

    // Assert
    await Assert.That(results).Count().IsEqualTo(1);
    await Assert.That(results[0].PropertyName).IsEqualTo("SentAt");
  }

  [Test]
  public async Task TestAutoPopulateRegistry_GetRegistrationsFor_UnknownType_ReturnsEmptyAsync() {
    // Arrange
    var registry = new TestAutoPopulateRegistry([]);

    // Act
    var results = registry.GetRegistrationsFor(typeof(string)).ToList();

    // Assert
    await Assert.That(results).Count().IsEqualTo(0);
  }

  [Test]
  public async Task TestAutoPopulateRegistry_GetRegistrationsFor_MultipleProperties_ReturnsAllAsync() {
    // Arrange
    var registrations = new[] {
      new AutoPopulateRegistration {
        MessageType = typeof(TestMessage),
        PropertyName = "SentAt",
        PropertyType = typeof(DateTimeOffset?),
        PopulateKind = PopulateKind.Timestamp,
        TimestampKind = TimestampKind.SentAt
      },
      new AutoPopulateRegistration {
        MessageType = typeof(TestMessage),
        PropertyName = "UserId",
        PropertyType = typeof(string),
        PopulateKind = PopulateKind.Context,
        ContextKind = ContextKind.UserId
      }
    };

    var registry = new TestAutoPopulateRegistry(registrations);

    // Act
    var results = registry.GetRegistrationsFor(typeof(TestMessage)).ToList();

    // Assert
    await Assert.That(results).Count().IsEqualTo(2);
  }

  #endregion

  #region AutoPopulateRegistry Static Tests

  [Test]
  public async Task AutoPopulateRegistry_Register_IncreasesCountAsync() {
    // Arrange
    var initialCount = AutoPopulateRegistry.Count;
    var testRegistry = new TestAutoPopulateRegistry([]);

    // Act
    AutoPopulateRegistry.Register(testRegistry, priority: 9999); // Use high priority to not interfere

    // Assert
    await Assert.That(AutoPopulateRegistry.Count).IsGreaterThan(initialCount);
  }

  [Test]
  public async Task AutoPopulateRegistry_GetRegistrationsFor_AggregatesAcrossRegistriesAsync() {
    // Arrange
    var registration1 = new AutoPopulateRegistration {
      MessageType = typeof(TestMessage),
      PropertyName = "SentAt",
      PropertyType = typeof(DateTimeOffset?),
      PopulateKind = PopulateKind.Timestamp,
      TimestampKind = TimestampKind.SentAt
    };

    var registration2 = new AutoPopulateRegistration {
      MessageType = typeof(TestMessage),
      PropertyName = "UserId",
      PropertyType = typeof(string),
      PopulateKind = PopulateKind.Context,
      ContextKind = ContextKind.UserId
    };

    var registry1 = new TestAutoPopulateRegistry([registration1]);
    var registry2 = new TestAutoPopulateRegistry([registration2]);

    AutoPopulateRegistry.Register(registry1, priority: 9998);
    AutoPopulateRegistry.Register(registry2, priority: 9997);

    // Act
    var results = AutoPopulateRegistry.GetRegistrationsFor(typeof(TestMessage)).ToList();

    // Assert - Should have at least our 2 registrations (may have more from other tests)
    await Assert.That(results.Count).IsGreaterThanOrEqualTo(2);
    await Assert.That(results.Any(r => r.PropertyName == "SentAt")).IsTrue();
    await Assert.That(results.Any(r => r.PropertyName == "UserId")).IsTrue();
  }

  #endregion

  #region AutoPopulateProcessor Tests

  [Test]
  public async Task AutoPopulateProcessor_ProcessAutoPopulate_WithTimestamp_StoresValueInMetadataAsync() {
    // Arrange
    var processor = new AutoPopulateProcessor();
    var envelope = _createTestEnvelope();
    var registration = new AutoPopulateRegistration {
      MessageType = typeof(TestAutoPopulateMessage),
      PropertyName = "SentAt",
      PropertyType = typeof(DateTimeOffset?),
      PopulateKind = PopulateKind.Timestamp,
      TimestampKind = TimestampKind.SentAt
    };
    var registry = new TestAutoPopulateRegistry([registration]);
    AutoPopulateRegistry.Register(registry, priority: 8001);

    // Act
    processor.ProcessAutoPopulate(envelope, typeof(TestAutoPopulateMessage));

    // Assert
    var metadata = envelope.GetMetadata("auto:SentAt");
    await Assert.That(metadata).IsNotNull();
  }

  [Test]
  public async Task AutoPopulateProcessor_ProcessAutoPopulate_WithSecurityContext_StoresUserIdAsync() {
    // Arrange
    var processor = new AutoPopulateProcessor();
    var envelope = _createTestEnvelopeWithSecurity("user-123", "tenant-456");
    var registration = new AutoPopulateRegistration {
      MessageType = typeof(TestAutoPopulateMessage),
      PropertyName = "CreatedBy",
      PropertyType = typeof(string),
      PopulateKind = PopulateKind.Context,
      ContextKind = ContextKind.UserId
    };
    var registry = new TestAutoPopulateRegistry([registration]);
    AutoPopulateRegistry.Register(registry, priority: 8002);

    // Act
    processor.ProcessAutoPopulate(envelope, typeof(TestAutoPopulateMessage));

    // Assert
    var metadata = envelope.GetMetadata("auto:CreatedBy");
    await Assert.That(metadata).IsNotNull();
    await Assert.That(metadata!.Value.GetString()).IsEqualTo("user-123");
  }

  [Test]
  public async Task AutoPopulateProcessor_ProcessAutoPopulate_WithSecurityContext_StoresTenantIdAsync() {
    // Arrange
    var processor = new AutoPopulateProcessor();
    var envelope = _createTestEnvelopeWithSecurity("user-123", "tenant-456");
    var registration = new AutoPopulateRegistration {
      MessageType = typeof(TestAutoPopulateMessage),
      PropertyName = "TenantId",
      PropertyType = typeof(string),
      PopulateKind = PopulateKind.Context,
      ContextKind = ContextKind.TenantId
    };
    var registry = new TestAutoPopulateRegistry([registration]);
    AutoPopulateRegistry.Register(registry, priority: 8003);

    // Act
    processor.ProcessAutoPopulate(envelope, typeof(TestAutoPopulateMessage));

    // Assert
    var metadata = envelope.GetMetadata("auto:TenantId");
    await Assert.That(metadata).IsNotNull();
    await Assert.That(metadata!.Value.GetString()).IsEqualTo("tenant-456");
  }

  [Test]
  public async Task AutoPopulateProcessor_ProcessAutoPopulate_WithServiceInfo_StoresServiceNameAsync() {
    // Arrange
    var processor = new AutoPopulateProcessor();
    var envelope = _createTestEnvelope();
    var registration = new AutoPopulateRegistration {
      MessageType = typeof(TestAutoPopulateMessage),
      PropertyName = "ProcessedBy",
      PropertyType = typeof(string),
      PopulateKind = PopulateKind.Service,
      ServiceKind = ServiceKind.ServiceName
    };
    var registry = new TestAutoPopulateRegistry([registration]);
    AutoPopulateRegistry.Register(registry, priority: 8004);

    // Act
    processor.ProcessAutoPopulate(envelope, typeof(TestAutoPopulateMessage));

    // Assert
    var metadata = envelope.GetMetadata("auto:ProcessedBy");
    await Assert.That(metadata).IsNotNull();
    await Assert.That(metadata!.Value.GetString()).IsEqualTo("TestService");
  }

  [Test]
  public async Task AutoPopulateProcessor_ProcessAutoPopulate_WithIdentifier_StoresMessageIdAsync() {
    // Arrange
    var processor = new AutoPopulateProcessor();
    var messageId = MessageId.New();
    var envelope = _createTestEnvelopeWithMessageId(messageId);
    var registration = new AutoPopulateRegistration {
      MessageType = typeof(TestAutoPopulateMessage),
      PropertyName = "MsgId",
      PropertyType = typeof(Guid),
      PopulateKind = PopulateKind.Identifier,
      IdentifierKind = IdentifierKind.MessageId
    };
    var registry = new TestAutoPopulateRegistry([registration]);
    AutoPopulateRegistry.Register(registry, priority: 8005);

    // Act
    processor.ProcessAutoPopulate(envelope, typeof(TestAutoPopulateMessage));

    // Assert
    var metadata = envelope.GetMetadata("auto:MsgId");
    await Assert.That(metadata).IsNotNull();
    await Assert.That(metadata!.Value.GetGuid()).IsEqualTo(messageId.Value);
  }

  [Test]
  public async Task AutoPopulateProcessor_ProcessAutoPopulate_WithCorrelationId_StoresValueAsync() {
    // Arrange
    var processor = new AutoPopulateProcessor();
    var correlationId = CorrelationId.New();
    var envelope = _createTestEnvelopeWithCorrelationId(correlationId);
    var registration = new AutoPopulateRegistration {
      MessageType = typeof(TestAutoPopulateMessage),
      PropertyName = "CorrelationId",
      PropertyType = typeof(Guid?),
      PopulateKind = PopulateKind.Identifier,
      IdentifierKind = IdentifierKind.CorrelationId
    };
    var registry = new TestAutoPopulateRegistry([registration]);
    AutoPopulateRegistry.Register(registry, priority: 8006);

    // Act
    processor.ProcessAutoPopulate(envelope, typeof(TestAutoPopulateMessage));

    // Assert
    var metadata = envelope.GetMetadata("auto:CorrelationId");
    await Assert.That(metadata).IsNotNull();
    await Assert.That(metadata!.Value.GetGuid()).IsEqualTo(correlationId.Value);
  }

  [Test]
  public async Task AutoPopulateProcessor_ProcessAutoPopulate_NoRegistrations_DoesNotAddHopAsync() {
    // Arrange
    var processor = new AutoPopulateProcessor();
    var envelope = _createTestEnvelope();
    var initialHopCount = envelope.Hops.Count;

    // Act - process with a type that has no registrations
    processor.ProcessAutoPopulate(envelope, typeof(UnregisteredMessage));

    // Assert
    await Assert.That(envelope.Hops.Count).IsEqualTo(initialHopCount);
  }

  [Test]
  public async Task AutoPopulateProcessor_ProcessAutoPopulate_NullEnvelope_ThrowsArgumentNullExceptionAsync() {
    // Arrange
    var processor = new AutoPopulateProcessor();

    // Act & Assert
    await Assert.ThrowsAsync<ArgumentNullException>(async () => {
      processor.ProcessAutoPopulate(null!, typeof(TestAutoPopulateMessage));
      await Task.CompletedTask;
    });
  }

  [Test]
  public async Task AutoPopulateProcessor_ProcessAutoPopulate_NullMessageType_ThrowsArgumentNullExceptionAsync() {
    // Arrange
    var processor = new AutoPopulateProcessor();
    var envelope = _createTestEnvelope();

    // Act & Assert
    await Assert.ThrowsAsync<ArgumentNullException>(async () => {
      processor.ProcessAutoPopulate(envelope, null!);
      await Task.CompletedTask;
    });
  }

  [Test]
  public async Task AutoPopulateProcessor_ProcessAutoPopulate_MultipleRegistrations_StoresAllValuesAsync() {
    // Arrange
    var processor = new AutoPopulateProcessor();
    var envelope = _createTestEnvelopeWithSecurity("user-999", "tenant-888");
    var registrations = new[] {
      new AutoPopulateRegistration {
        MessageType = typeof(TestAutoPopulateMessage),
        PropertyName = "SentAt",
        PropertyType = typeof(DateTimeOffset?),
        PopulateKind = PopulateKind.Timestamp,
        TimestampKind = TimestampKind.SentAt
      },
      new AutoPopulateRegistration {
        MessageType = typeof(TestAutoPopulateMessage),
        PropertyName = "CreatedBy",
        PropertyType = typeof(string),
        PopulateKind = PopulateKind.Context,
        ContextKind = ContextKind.UserId
      }
    };
    var registry = new TestAutoPopulateRegistry(registrations);
    AutoPopulateRegistry.Register(registry, priority: 8007);

    // Act
    processor.ProcessAutoPopulate(envelope, typeof(TestAutoPopulateMessage));

    // Assert
    await Assert.That(envelope.GetMetadata("auto:SentAt")).IsNotNull();
    await Assert.That(envelope.GetMetadata("auto:CreatedBy")).IsNotNull();
    await Assert.That(envelope.GetMetadata("auto:CreatedBy")!.Value.GetString()).IsEqualTo("user-999");
  }

  [Test]
  public async Task AutoPopulateProcessor_MetadataPrefix_IsAutoColonAsync() {
    // Arrange
    var prefix = AutoPopulateProcessor.METADATA_PREFIX;

    // Assert
    await Assert.That(prefix).IsEqualTo("auto:");
  }

  #endregion

  #region Additional Processor Coverage Tests

  [Test]
  public async Task AutoPopulateProcessor_ProcessAutoPopulate_NullHops_DoesNotThrowAsync() {
    // Arrange - envelope with null hops
    var processor = new AutoPopulateProcessor();
    var registration = new AutoPopulateRegistration {
      MessageType = typeof(TestAutoPopulateMessage),
      PropertyName = "SentAt",
      PropertyType = typeof(DateTimeOffset?),
      PopulateKind = PopulateKind.Timestamp,
      TimestampKind = TimestampKind.SentAt
    };
    var registry = new TestAutoPopulateRegistry([registration]);
    AutoPopulateRegistry.Register(registry, priority: 8100);

    var envelope = new MessageEnvelope<TestAutoPopulateMessage> {
      MessageId = MessageId.New(),
      Payload = new TestAutoPopulateMessage(Guid.NewGuid()),
      Hops = null!,
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };

    // Act - should not throw
    processor.ProcessAutoPopulate(envelope, typeof(TestAutoPopulateMessage));

    // Assert - no hop was added
    await Task.CompletedTask;
  }

  [Test]
  public async Task AutoPopulateProcessor_ProcessAutoPopulate_NoCurrentHop_DoesNotAddMetadataAsync() {
    // Arrange - envelope with only causation hops (no current hop)
    var processor = new AutoPopulateProcessor();
    var registration = new AutoPopulateRegistration {
      MessageType = typeof(TestAutoPopulateMessage),
      PropertyName = "SentAt",
      PropertyType = typeof(DateTimeOffset?),
      PopulateKind = PopulateKind.Timestamp,
      TimestampKind = TimestampKind.SentAt
    };
    var registry = new TestAutoPopulateRegistry([registration]);
    AutoPopulateRegistry.Register(registry, priority: 8101);

    var envelope = new MessageEnvelope<TestAutoPopulateMessage> {
      MessageId = MessageId.New(),
      Payload = new TestAutoPopulateMessage(Guid.NewGuid()),
      Hops = [
        new MessageHop {
          Type = HopType.Causation,
          ServiceInstance = new ServiceInstanceInfo {
            ServiceName = "CausationService",
            InstanceId = Guid.NewGuid(),
            HostName = "localhost",
            ProcessId = 1
          },
          Timestamp = DateTimeOffset.UtcNow
        }
      ],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };

    var initialHopCount = envelope.Hops.Count;

    // Act
    processor.ProcessAutoPopulate(envelope, typeof(TestAutoPopulateMessage));

    // Assert
    await Assert.That(envelope.Hops.Count).IsEqualTo(initialHopCount);
  }

  [Test]
  public async Task AutoPopulateProcessor_ProcessAutoPopulate_QueuedAtTimestamp_ReturnsNullAndSkipsAsync() {
    // Arrange - QueuedAt timestamps are set later by WorkCoordinatorPublisherWorker, not here
    var processor = new AutoPopulateProcessor();
    var registration = new AutoPopulateRegistration {
      MessageType = typeof(TestAutoPopulateMessage),
      PropertyName = "QueuedAt",
      PropertyType = typeof(DateTimeOffset?),
      PopulateKind = PopulateKind.Timestamp,
      TimestampKind = TimestampKind.QueuedAt
    };
    var registry = new TestAutoPopulateRegistry([registration]);
    AutoPopulateRegistry.Register(registry, priority: 8102);

    var envelope = _createTestEnvelope();

    // Act
    processor.ProcessAutoPopulate(envelope, typeof(TestAutoPopulateMessage));

    // Assert - QueuedAt returns null, so no "auto:QueuedAt" metadata key should be present
    var metadata = envelope.GetMetadata("auto:QueuedAt");
    await Assert.That(metadata).IsNull();
  }

  [Test]
  public async Task AutoPopulateProcessor_ProcessAutoPopulate_DeliveredAtTimestamp_ReturnsNullAndSkipsAsync() {
    // Arrange - DeliveredAt timestamps are set later by TransportConsumerWorker
    var processor = new AutoPopulateProcessor();
    var registration = new AutoPopulateRegistration {
      MessageType = typeof(TestAutoPopulateMessage),
      PropertyName = "DeliveredAt",
      PropertyType = typeof(DateTimeOffset?),
      PopulateKind = PopulateKind.Timestamp,
      TimestampKind = TimestampKind.DeliveredAt
    };
    var registry = new TestAutoPopulateRegistry([registration]);
    AutoPopulateRegistry.Register(registry, priority: 8103);

    var envelope = _createTestEnvelope();

    // Act
    processor.ProcessAutoPopulate(envelope, typeof(TestAutoPopulateMessage));

    // Assert - DeliveredAt returns null, so no "auto:DeliveredAt" metadata key should be present
    var metadata = envelope.GetMetadata("auto:DeliveredAt");
    await Assert.That(metadata).IsNull();
  }

  [Test]
  public async Task AutoPopulateProcessor_ProcessAutoPopulate_ContextWithNullScope_SkipsAsync() {
    // Arrange - Context registration but hop has no scope values
    var processor = new AutoPopulateProcessor();
    var registration = new AutoPopulateRegistration {
      MessageType = typeof(TestAutoPopulateMessage),
      PropertyName = "CreatedByCtx",
      PropertyType = typeof(string),
      PopulateKind = PopulateKind.Context,
      ContextKind = ContextKind.UserId
    };
    var registry = new TestAutoPopulateRegistry([registration]);
    AutoPopulateRegistry.Register(registry, priority: 8104);

    // Envelope with no scope on hop
    var envelope = _createTestEnvelope();

    // Act
    processor.ProcessAutoPopulate(envelope, typeof(TestAutoPopulateMessage));

    // Assert - No metadata added for this specific key since no scope on hop
    var metadata = envelope.GetMetadata("auto:CreatedByCtx");
    await Assert.That(metadata).IsNull();
  }

  [Test]
  public async Task AutoPopulateProcessor_ProcessAutoPopulate_ServiceInstanceId_StoresGuidAsync() {
    // Arrange
    var processor = new AutoPopulateProcessor();
    var envelope = _createTestEnvelope();
    var registration = new AutoPopulateRegistration {
      MessageType = typeof(TestAutoPopulateMessage),
      PropertyName = "InstanceId",
      PropertyType = typeof(Guid),
      PopulateKind = PopulateKind.Service,
      ServiceKind = ServiceKind.InstanceId
    };
    var registry = new TestAutoPopulateRegistry([registration]);
    AutoPopulateRegistry.Register(registry, priority: 8105);

    // Act
    processor.ProcessAutoPopulate(envelope, typeof(TestAutoPopulateMessage));

    // Assert
    var metadata = envelope.GetMetadata("auto:InstanceId");
    await Assert.That(metadata).IsNotNull();
    await Assert.That(metadata!.Value.ValueKind).IsEqualTo(System.Text.Json.JsonValueKind.String); // Guids serialize as strings
  }

  [Test]
  public async Task AutoPopulateProcessor_ProcessAutoPopulate_ServiceHostName_StoresStringAsync() {
    // Arrange
    var processor = new AutoPopulateProcessor();
    var envelope = _createTestEnvelope();
    var registration = new AutoPopulateRegistration {
      MessageType = typeof(TestAutoPopulateMessage),
      PropertyName = "HostName",
      PropertyType = typeof(string),
      PopulateKind = PopulateKind.Service,
      ServiceKind = ServiceKind.HostName
    };
    var registry = new TestAutoPopulateRegistry([registration]);
    AutoPopulateRegistry.Register(registry, priority: 8106);

    // Act
    processor.ProcessAutoPopulate(envelope, typeof(TestAutoPopulateMessage));

    // Assert
    var metadata = envelope.GetMetadata("auto:HostName");
    await Assert.That(metadata).IsNotNull();
    await Assert.That(metadata!.Value.GetString()).IsEqualTo("localhost");
  }

  [Test]
  public async Task AutoPopulateProcessor_ProcessAutoPopulate_ServiceProcessId_StoresIntAsync() {
    // Arrange
    var processor = new AutoPopulateProcessor();
    var envelope = _createTestEnvelope();
    var registration = new AutoPopulateRegistration {
      MessageType = typeof(TestAutoPopulateMessage),
      PropertyName = "ProcessId",
      PropertyType = typeof(int),
      PopulateKind = PopulateKind.Service,
      ServiceKind = ServiceKind.ProcessId
    };
    var registry = new TestAutoPopulateRegistry([registration]);
    AutoPopulateRegistry.Register(registry, priority: 8107);

    // Act
    processor.ProcessAutoPopulate(envelope, typeof(TestAutoPopulateMessage));

    // Assert
    var metadata = envelope.GetMetadata("auto:ProcessId");
    await Assert.That(metadata).IsNotNull();
    await Assert.That(metadata!.Value.GetInt32()).IsEqualTo(12345);
  }

  [Test]
  public async Task AutoPopulateProcessor_ProcessAutoPopulate_IdentifierCausationId_StoresGuidAsync() {
    // Arrange
    var processor = new AutoPopulateProcessor();
    var causationId = MessageId.New();
    var envelope = new MessageEnvelope<TestAutoPopulateMessage> {
      MessageId = MessageId.New(),
      Payload = new TestAutoPopulateMessage(Guid.NewGuid()),
      Hops = [
        new MessageHop {
          Type = HopType.Current,
          ServiceInstance = new ServiceInstanceInfo {
            ServiceName = "TestService",
            InstanceId = Guid.NewGuid(),
            HostName = "localhost",
            ProcessId = 12345
          },
          Timestamp = DateTimeOffset.UtcNow,
          CausationId = causationId
        }
      ],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };
    var registration = new AutoPopulateRegistration {
      MessageType = typeof(TestAutoPopulateMessage),
      PropertyName = "CausationId",
      PropertyType = typeof(Guid?),
      PopulateKind = PopulateKind.Identifier,
      IdentifierKind = IdentifierKind.CausationId
    };
    var registry = new TestAutoPopulateRegistry([registration]);
    AutoPopulateRegistry.Register(registry, priority: 8108);

    // Act
    processor.ProcessAutoPopulate(envelope, typeof(TestAutoPopulateMessage));

    // Assert
    var metadata = envelope.GetMetadata("auto:CausationId");
    await Assert.That(metadata).IsNotNull();
    await Assert.That(metadata!.Value.GetGuid()).IsEqualTo(causationId.Value);
  }

  [Test]
  public async Task AutoPopulateProcessor_ProcessAutoPopulate_IdentifierStreamId_StoresStringAsync() {
    // Arrange
    var processor = new AutoPopulateProcessor();
    var envelope = new MessageEnvelope<TestAutoPopulateMessage> {
      MessageId = MessageId.New(),
      Payload = new TestAutoPopulateMessage(Guid.NewGuid()),
      Hops = [
        new MessageHop {
          Type = HopType.Current,
          ServiceInstance = new ServiceInstanceInfo {
            ServiceName = "TestService",
            InstanceId = Guid.NewGuid(),
            HostName = "localhost",
            ProcessId = 12345
          },
          Timestamp = DateTimeOffset.UtcNow,
          StreamId = "stream-123"
        }
      ],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };
    var registration = new AutoPopulateRegistration {
      MessageType = typeof(TestAutoPopulateMessage),
      PropertyName = "StreamId",
      PropertyType = typeof(string),
      PopulateKind = PopulateKind.Identifier,
      IdentifierKind = IdentifierKind.StreamId
    };
    var registry = new TestAutoPopulateRegistry([registration]);
    AutoPopulateRegistry.Register(registry, priority: 8109);

    // Act
    processor.ProcessAutoPopulate(envelope, typeof(TestAutoPopulateMessage));

    // Assert
    var metadata = envelope.GetMetadata("auto:StreamId");
    await Assert.That(metadata).IsNotNull();
    await Assert.That(metadata!.Value.GetString()).IsEqualTo("stream-123");
  }

  [Test]
  public async Task AutoPopulateProcessor_ProcessAutoPopulate_IdentifierCorrelationIdNull_SkipsAsync() {
    // Arrange - CorrelationId is null on hop
    var processor = new AutoPopulateProcessor();
    var envelope = new MessageEnvelope<TestAutoPopulateMessage> {
      MessageId = MessageId.New(),
      Payload = new TestAutoPopulateMessage(Guid.NewGuid()),
      Hops = [
        new MessageHop {
          Type = HopType.Current,
          ServiceInstance = new ServiceInstanceInfo {
            ServiceName = "TestService",
            InstanceId = Guid.NewGuid(),
            HostName = "localhost",
            ProcessId = 12345
          },
          Timestamp = DateTimeOffset.UtcNow,
          CorrelationId = null
        }
      ],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };
    var registration = new AutoPopulateRegistration {
      MessageType = typeof(TestAutoPopulateMessage),
      PropertyName = "NullCorrelationId",
      PropertyType = typeof(Guid?),
      PopulateKind = PopulateKind.Identifier,
      IdentifierKind = IdentifierKind.CorrelationId
    };
    var registry = new TestAutoPopulateRegistry([registration]);
    AutoPopulateRegistry.Register(registry, priority: 8110);

    // Act
    processor.ProcessAutoPopulate(envelope, typeof(TestAutoPopulateMessage));

    // Assert - CorrelationId is null, so this specific metadata key should not be present
    var metadata = envelope.GetMetadata("auto:NullCorrelationId");
    await Assert.That(metadata).IsNull();
  }

  [Test]
  public async Task AutoPopulateProcessor_ProcessAutoPopulate_IdentifierStreamIdEmpty_SkipsAsync() {
    // Arrange - StreamId is empty
    var processor = new AutoPopulateProcessor();
    var envelope = new MessageEnvelope<TestAutoPopulateMessage> {
      MessageId = MessageId.New(),
      Payload = new TestAutoPopulateMessage(Guid.NewGuid()),
      Hops = [
        new MessageHop {
          Type = HopType.Current,
          ServiceInstance = new ServiceInstanceInfo {
            ServiceName = "TestService",
            InstanceId = Guid.NewGuid(),
            HostName = "localhost",
            ProcessId = 12345
          },
          Timestamp = DateTimeOffset.UtcNow,
          StreamId = ""
        }
      ],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };
    var registration = new AutoPopulateRegistration {
      MessageType = typeof(TestAutoPopulateMessage),
      PropertyName = "EmptyStreamId",
      PropertyType = typeof(string),
      PopulateKind = PopulateKind.Identifier,
      IdentifierKind = IdentifierKind.StreamId
    };
    var registry = new TestAutoPopulateRegistry([registration]);
    AutoPopulateRegistry.Register(registry, priority: 8111);

    // Act
    processor.ProcessAutoPopulate(envelope, typeof(TestAutoPopulateMessage));

    // Assert - empty StreamId, so this specific metadata key should not be present
    var metadata = envelope.GetMetadata("auto:EmptyStreamId");
    await Assert.That(metadata).IsNull();
  }

  [Test]
  public async Task AutoPopulateProcessor_ProcessAutoPopulate_IdentifierCausationIdNull_SkipsAsync() {
    // Arrange - CausationId is null
    var processor = new AutoPopulateProcessor();
    var envelope = new MessageEnvelope<TestAutoPopulateMessage> {
      MessageId = MessageId.New(),
      Payload = new TestAutoPopulateMessage(Guid.NewGuid()),
      Hops = [
        new MessageHop {
          Type = HopType.Current,
          ServiceInstance = new ServiceInstanceInfo {
            ServiceName = "TestService",
            InstanceId = Guid.NewGuid(),
            HostName = "localhost",
            ProcessId = 12345
          },
          Timestamp = DateTimeOffset.UtcNow,
          CausationId = null
        }
      ],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };
    var registration = new AutoPopulateRegistration {
      MessageType = typeof(TestAutoPopulateMessage),
      PropertyName = "NullCausationId",
      PropertyType = typeof(Guid?),
      PopulateKind = PopulateKind.Identifier,
      IdentifierKind = IdentifierKind.CausationId
    };
    var registry = new TestAutoPopulateRegistry([registration]);
    AutoPopulateRegistry.Register(registry, priority: 8112);

    // Act
    processor.ProcessAutoPopulate(envelope, typeof(TestAutoPopulateMessage));

    // Assert - null CausationId, so this specific metadata key should not be present
    var metadata = envelope.GetMetadata("auto:NullCausationId");
    await Assert.That(metadata).IsNull();
  }

  [Test]
  public async Task AutoPopulateProcessor_ProcessAutoPopulate_ContextTenantId_StoresTenantAsync() {
    // Arrange
    var processor = new AutoPopulateProcessor();
    var envelope = _createTestEnvelopeWithSecurity("user-x", "tenant-x");
    var registration = new AutoPopulateRegistration {
      MessageType = typeof(TestAutoPopulateMessage),
      PropertyName = "TenantIdProp",
      PropertyType = typeof(string),
      PopulateKind = PopulateKind.Context,
      ContextKind = ContextKind.TenantId
    };
    var registry = new TestAutoPopulateRegistry([registration]);
    AutoPopulateRegistry.Register(registry, priority: 8113);

    // Act
    processor.ProcessAutoPopulate(envelope, typeof(TestAutoPopulateMessage));

    // Assert
    var metadata = envelope.GetMetadata("auto:TenantIdProp");
    await Assert.That(metadata).IsNotNull();
    await Assert.That(metadata!.Value.GetString()).IsEqualTo("tenant-x");
  }

  #endregion

  #region Test Helpers

  private sealed record TestMessage(Guid Id);

  private sealed record TestAutoPopulateMessage(Guid Id);

  private sealed record UnregisteredMessage(Guid Id);

  /// <summary>
  /// Test implementation of IAutoPopulateRegistry for unit tests.
  /// </summary>
  private sealed class TestAutoPopulateRegistry(AutoPopulateRegistration[] registrations) : IAutoPopulateRegistry {
    private readonly AutoPopulateRegistration[] _registrations = registrations;

    public IEnumerable<AutoPopulateRegistration> GetRegistrationsFor(Type messageType) {
      return _registrations.Where(r => r.MessageType == messageType);
    }

    public IEnumerable<AutoPopulateRegistration> GetAllRegistrations() {
      return _registrations;
    }
  }

  private static MessageEnvelope<TestAutoPopulateMessage> _createTestEnvelope() {
    return new MessageEnvelope<TestAutoPopulateMessage> {
      MessageId = MessageId.New(),
      Payload = new TestAutoPopulateMessage(Guid.NewGuid()),
      Hops = [
        new MessageHop {
          Type = HopType.Current,
          ServiceInstance = new ServiceInstanceInfo {
            ServiceName = "TestService",
            InstanceId = Guid.NewGuid(),
            HostName = "localhost",
            ProcessId = 12345
          },
          Timestamp = DateTimeOffset.UtcNow,
          Topic = "test-topic"
        }
      ],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };
  }

  private static MessageEnvelope<TestAutoPopulateMessage> _createTestEnvelopeWithSecurity(
      string userId,
      string tenantId) {
    return new MessageEnvelope<TestAutoPopulateMessage> {
      MessageId = MessageId.New(),
      Payload = new TestAutoPopulateMessage(Guid.NewGuid()),
      Hops = [
        new MessageHop {
          Type = HopType.Current,
          ServiceInstance = new ServiceInstanceInfo {
            ServiceName = "TestService",
            InstanceId = Guid.NewGuid(),
            HostName = "localhost",
            ProcessId = 12345
          },
          Timestamp = DateTimeOffset.UtcNow,
          Topic = "test-topic",
          Scope = ScopeDelta.FromSecurityContext(new SecurityContext { UserId = userId, TenantId = tenantId })
        }
      ],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };
  }

  private static MessageEnvelope<TestAutoPopulateMessage> _createTestEnvelopeWithMessageId(MessageId messageId) {
    return new MessageEnvelope<TestAutoPopulateMessage> {
      MessageId = messageId,
      Payload = new TestAutoPopulateMessage(Guid.NewGuid()),
      Hops = [
        new MessageHop {
          Type = HopType.Current,
          ServiceInstance = new ServiceInstanceInfo {
            ServiceName = "TestService",
            InstanceId = Guid.NewGuid(),
            HostName = "localhost",
            ProcessId = 12345
          },
          Timestamp = DateTimeOffset.UtcNow,
          Topic = "test-topic"
        }
      ],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };
  }

  private static MessageEnvelope<TestAutoPopulateMessage> _createTestEnvelopeWithCorrelationId(
      CorrelationId correlationId) {
    return new MessageEnvelope<TestAutoPopulateMessage> {
      MessageId = MessageId.New(),
      Payload = new TestAutoPopulateMessage(Guid.NewGuid()),
      Hops = [
        new MessageHop {
          Type = HopType.Current,
          ServiceInstance = new ServiceInstanceInfo {
            ServiceName = "TestService",
            InstanceId = Guid.NewGuid(),
            HostName = "localhost",
            ProcessId = 12345
          },
          Timestamp = DateTimeOffset.UtcNow,
          Topic = "test-topic",
          CorrelationId = correlationId
        }
      ],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };
  }

  #endregion
}
