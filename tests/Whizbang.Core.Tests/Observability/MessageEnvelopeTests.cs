#pragma warning disable CA1707

using System.Text.Json;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Observability;
using Whizbang.Core.Policies;
using Whizbang.Core.Security;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Tests.Observability;

/// <summary>
/// Tests for MessageEnvelope covering constructors, property access, hop walking methods,
/// metadata stitching, policy decisions, and edge cases.
/// </summary>
/// <tests>src/Whizbang.Core/Observability/MessageEnvelope.cs</tests>
public class MessageEnvelopeTests {
  #region Test Helpers

  private static ServiceInstanceInfo _createServiceInstance() =>
    new() {
      ServiceName = "TestService",
      InstanceId = Guid.NewGuid(),
      HostName = "localhost",
      ProcessId = 1234
    };

  private static MessageHop _createHop(
      HopType type = HopType.Current,
      string? topic = null,
      string? streamId = null,
      int? partitionIndex = null,
      long? sequenceNumber = null,
      ScopeDelta? scope = null,
      IReadOnlyDictionary<string, JsonElement>? metadata = null,
      PolicyDecisionTrail? trail = null,
      CorrelationId? correlationId = null,
      MessageId? causationId = null) =>
    new() {
      Type = type,
      ServiceInstance = _createServiceInstance(),
      Timestamp = DateTimeOffset.UtcNow,
      Topic = topic ?? string.Empty,
      StreamId = streamId ?? string.Empty,
      PartitionIndex = partitionIndex,
      SequenceNumber = sequenceNumber,
      Scope = scope,
      Metadata = metadata,
      Trail = trail,
      CorrelationId = correlationId,
      CausationId = causationId
    };

  private sealed record TestPayload(string Value);

  #endregion

  #region Constructor Tests

  [Test]
  public async Task ParameterlessConstructor_AllowsObjectInitializerAsync() {
    // Arrange & Act
    var messageId = MessageId.New();
    var payload = new TestPayload("test");
    var hops = new List<MessageHop> { _createHop() };

    var envelope = new MessageEnvelope<TestPayload> {
      MessageId = messageId,
      Payload = payload,
      Hops = hops,
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };

    // Assert
    await Assert.That(envelope.MessageId).IsEqualTo(messageId);
    await Assert.That(envelope.Payload).IsEqualTo(payload);
    await Assert.That(envelope.Hops).Count().IsEqualTo(1);
  }

  [Test]
  public async Task JsonConstructor_SetsAllPropertiesAsync() {
    // Arrange
    var messageId = MessageId.New();
    var payload = new TestPayload("json-ctor");
    var hops = new List<MessageHop> { _createHop(topic: "test-topic") };

    // Act
    var envelope = new MessageEnvelope<TestPayload>(messageId, payload, hops);

    // Assert
    await Assert.That(envelope.MessageId).IsEqualTo(messageId);
    await Assert.That(envelope.Payload).IsEqualTo(payload);
    await Assert.That(envelope.Hops).Count().IsEqualTo(1);
    await Assert.That(envelope.Hops[0].Topic).IsEqualTo("test-topic");
  }

  [Test]
  public async Task NonGenericPayload_ReturnsBoxedPayloadAsync() {
    // Arrange
    var payload = new TestPayload("boxed");
    var envelope = new MessageEnvelope<TestPayload> {
      MessageId = MessageId.New(),
      Payload = payload,
      Hops = [_createHop()],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };

    // Act
    object nonGenericPayload = ((IMessageEnvelope)envelope).Payload;

    // Assert
    await Assert.That(nonGenericPayload).IsEqualTo(payload);
  }

  #endregion

  #region AddHop Tests

  [Test]
  public async Task AddHop_AddsHopToListAsync() {
    // Arrange
    var envelope = new MessageEnvelope<TestPayload> {
      MessageId = MessageId.New(),
      Payload = new TestPayload("test"),
      Hops = [_createHop()],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };
    var newHop = _createHop(topic: "added-hop");

    // Act
    envelope.AddHop(newHop);

    // Assert
    await Assert.That(envelope.Hops).Count().IsEqualTo(2);
    await Assert.That(envelope.Hops[1].Topic).IsEqualTo("added-hop");
  }

  [Test]
  public async Task AddHop_MaintainsOrderAsync() {
    // Arrange
    var envelope = new MessageEnvelope<TestPayload> {
      MessageId = MessageId.New(),
      Payload = new TestPayload("test"),
      Hops = [_createHop(topic: "first")],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };

    // Act
    envelope.AddHop(_createHop(topic: "second"));
    envelope.AddHop(_createHop(topic: "third"));

    // Assert
    await Assert.That(envelope.Hops).Count().IsEqualTo(3);
    await Assert.That(envelope.Hops[0].Topic).IsEqualTo("first");
    await Assert.That(envelope.Hops[1].Topic).IsEqualTo("second");
    await Assert.That(envelope.Hops[2].Topic).IsEqualTo("third");
  }

  #endregion

  #region GetCurrentTopic Tests

  [Test]
  public async Task GetCurrentTopic_ReturnsNull_WhenNoHopsAsync() {
    // Arrange
    var envelope = new MessageEnvelope<TestPayload> {
      MessageId = MessageId.New(),
      Payload = new TestPayload("test"),
      Hops = [],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };

    // Act
    var result = envelope.GetCurrentTopic();

    // Assert
    await Assert.That(result).IsNull();
  }

  [Test]
  public async Task GetCurrentTopic_ReturnsNull_WhenNoHopsHaveTopicAsync() {
    // Arrange
    var envelope = new MessageEnvelope<TestPayload> {
      MessageId = MessageId.New(),
      Payload = new TestPayload("test"),
      Hops = [_createHop(), _createHop()],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };

    // Act
    var result = envelope.GetCurrentTopic();

    // Assert
    await Assert.That(result).IsNull();
  }

  [Test]
  public async Task GetCurrentTopic_ReturnsMostRecentNonNullTopicAsync() {
    // Arrange
    var envelope = new MessageEnvelope<TestPayload> {
      MessageId = MessageId.New(),
      Payload = new TestPayload("test"),
      Hops = [
        _createHop(topic: "first-topic"),
        _createHop(),
        _createHop(topic: "latest-topic")
      ],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };

    // Act
    var result = envelope.GetCurrentTopic();

    // Assert
    await Assert.That(result).IsEqualTo("latest-topic");
  }

  [Test]
  public async Task GetCurrentTopic_IgnoresCausationHopsAsync() {
    // Arrange
    var envelope = new MessageEnvelope<TestPayload> {
      MessageId = MessageId.New(),
      Payload = new TestPayload("test"),
      Hops = [
        _createHop(topic: "current-topic"),
        _createHop(type: HopType.Causation, topic: "causation-topic")
      ],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };

    // Act
    var result = envelope.GetCurrentTopic();

    // Assert
    await Assert.That(result).IsEqualTo("current-topic");
  }

  #endregion

  #region GetCurrentStreamId Tests

  [Test]
  public async Task GetCurrentStreamId_ReturnsNull_WhenNoHopsAsync() {
    // Arrange
    var envelope = new MessageEnvelope<TestPayload> {
      MessageId = MessageId.New(),
      Payload = new TestPayload("test"),
      Hops = [],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };

    // Act
    var result = envelope.GetCurrentStreamId();

    // Assert
    await Assert.That(result).IsNull();
  }

  [Test]
  public async Task GetCurrentStreamId_ReturnsMostRecentNonNullStreamIdAsync() {
    // Arrange
    var envelope = new MessageEnvelope<TestPayload> {
      MessageId = MessageId.New(),
      Payload = new TestPayload("test"),
      Hops = [
        _createHop(streamId: "stream-1"),
        _createHop(streamId: "stream-2")
      ],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };

    // Act
    var result = envelope.GetCurrentStreamId();

    // Assert
    await Assert.That(result).IsEqualTo("stream-2");
  }

  [Test]
  public async Task GetCurrentStreamId_IgnoresCausationHopsAsync() {
    // Arrange
    var envelope = new MessageEnvelope<TestPayload> {
      MessageId = MessageId.New(),
      Payload = new TestPayload("test"),
      Hops = [
        _createHop(streamId: "current-stream"),
        _createHop(type: HopType.Causation, streamId: "causation-stream")
      ],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };

    // Act
    var result = envelope.GetCurrentStreamId();

    // Assert
    await Assert.That(result).IsEqualTo("current-stream");
  }

  #endregion

  #region GetCurrentPartitionIndex Tests

  [Test]
  public async Task GetCurrentPartitionIndex_ReturnsNull_WhenNoHopsAsync() {
    // Arrange
    var envelope = new MessageEnvelope<TestPayload> {
      MessageId = MessageId.New(),
      Payload = new TestPayload("test"),
      Hops = [],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };

    // Act
    var result = envelope.GetCurrentPartitionIndex();

    // Assert
    await Assert.That(result).IsNull();
  }

  [Test]
  public async Task GetCurrentPartitionIndex_ReturnsMostRecentNonNullValueAsync() {
    // Arrange
    var envelope = new MessageEnvelope<TestPayload> {
      MessageId = MessageId.New(),
      Payload = new TestPayload("test"),
      Hops = [
        _createHop(partitionIndex: 1),
        _createHop(),
        _createHop(partitionIndex: 5)
      ],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };

    // Act
    var result = envelope.GetCurrentPartitionIndex();

    // Assert
    await Assert.That(result).IsEqualTo(5);
  }

  [Test]
  public async Task GetCurrentPartitionIndex_IgnoresCausationHopsAsync() {
    // Arrange
    var envelope = new MessageEnvelope<TestPayload> {
      MessageId = MessageId.New(),
      Payload = new TestPayload("test"),
      Hops = [
        _createHop(partitionIndex: 3),
        _createHop(type: HopType.Causation, partitionIndex: 99)
      ],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };

    // Act
    var result = envelope.GetCurrentPartitionIndex();

    // Assert
    await Assert.That(result).IsEqualTo(3);
  }

  #endregion

  #region GetCurrentSequenceNumber Tests

  [Test]
  public async Task GetCurrentSequenceNumber_ReturnsNull_WhenNoHopsAsync() {
    // Arrange
    var envelope = new MessageEnvelope<TestPayload> {
      MessageId = MessageId.New(),
      Payload = new TestPayload("test"),
      Hops = [],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };

    // Act
    var result = envelope.GetCurrentSequenceNumber();

    // Assert
    await Assert.That(result).IsNull();
  }

  [Test]
  public async Task GetCurrentSequenceNumber_ReturnsMostRecentNonNullValueAsync() {
    // Arrange
    var envelope = new MessageEnvelope<TestPayload> {
      MessageId = MessageId.New(),
      Payload = new TestPayload("test"),
      Hops = [
        _createHop(sequenceNumber: 10L),
        _createHop(sequenceNumber: 42L)
      ],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };

    // Act
    var result = envelope.GetCurrentSequenceNumber();

    // Assert
    await Assert.That(result).IsEqualTo(42L);
  }

  [Test]
  public async Task GetCurrentSequenceNumber_IgnoresCausationHopsAsync() {
    // Arrange
    var envelope = new MessageEnvelope<TestPayload> {
      MessageId = MessageId.New(),
      Payload = new TestPayload("test"),
      Hops = [
        _createHop(sequenceNumber: 7L),
        _createHop(type: HopType.Causation, sequenceNumber: 999L)
      ],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };

    // Act
    var result = envelope.GetCurrentSequenceNumber();

    // Assert
    await Assert.That(result).IsEqualTo(7L);
  }

  #endregion

  #region GetCurrentScope Tests

  [Test]
  public async Task GetCurrentScope_ReturnsNull_WhenNoHopsAsync() {
    // Arrange
    var envelope = new MessageEnvelope<TestPayload> {
      MessageId = MessageId.New(),
      Payload = new TestPayload("test"),
      Hops = [],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };

    // Act
    var result = envelope.GetCurrentScope();

    // Assert
    await Assert.That(result).IsNull();
  }

  [Test]
  public async Task GetCurrentScope_ReturnsNull_WhenNoHopsHaveScopeAsync() {
    // Arrange
    var envelope = new MessageEnvelope<TestPayload> {
      MessageId = MessageId.New(),
      Payload = new TestPayload("test"),
      Hops = [_createHop(), _createHop()],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };

    // Act
    var result = envelope.GetCurrentScope();

    // Assert
    await Assert.That(result).IsNull();
  }

  [Test]
  public async Task GetCurrentScope_IgnoresCausationHopsAsync() {
    // Arrange
    var causationScope = new ScopeDelta {
      Values = new Dictionary<ScopeProp, JsonElement> {
        [ScopeProp.Scope] = JsonSerializer.SerializeToElement(new { t = "causation-tenant", u = "causation-user" })
      }
    };
    var envelope = new MessageEnvelope<TestPayload> {
      MessageId = MessageId.New(),
      Payload = new TestPayload("test"),
      Hops = [
        _createHop(type: HopType.Causation, scope: causationScope)
      ],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };

    // Act
    var result = envelope.GetCurrentScope();

    // Assert
    await Assert.That(result).IsNull();
  }

  #endregion

  #region GetCurrentSecurityContext Tests (Obsolete)

#pragma warning disable CS0618 // Obsolete member usage

  [Test]
  public async Task GetCurrentSecurityContext_ReturnsNull_WhenNoHopsAsync() {
    // Arrange
    var envelope = new MessageEnvelope<TestPayload> {
      MessageId = MessageId.New(),
      Payload = new TestPayload("test"),
      Hops = [],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };

    // Act
    var result = envelope.GetCurrentSecurityContext();

    // Assert
    await Assert.That(result).IsNull();
  }

  [Test]
  public async Task GetCurrentSecurityContext_ReturnsNull_WhenNoHopsHaveScopeAsync() {
    // Arrange
    var envelope = new MessageEnvelope<TestPayload> {
      MessageId = MessageId.New(),
      Payload = new TestPayload("test"),
      Hops = [_createHop(), _createHop()],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };

    // Act
    var result = envelope.GetCurrentSecurityContext();

    // Assert
    await Assert.That(result).IsNull();
  }

  [Test]
  public async Task GetCurrentSecurityContext_ReturnsSecurityContext_WhenScopeExistsAsync() {
    // Arrange
    var scope = new ScopeDelta {
      Values = new Dictionary<ScopeProp, JsonElement> {
        [ScopeProp.Scope] = JsonSerializer.SerializeToElement(new { t = "tenant-1", u = "user-1" })
      }
    };
    var envelope = new MessageEnvelope<TestPayload> {
      MessageId = MessageId.New(),
      Payload = new TestPayload("test"),
      Hops = [_createHop(scope: scope)],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };

    // Act
    var result = envelope.GetCurrentSecurityContext();

    // Assert
    await Assert.That(result).IsNotNull();
    await Assert.That(result!.TenantId).IsEqualTo("tenant-1");
    await Assert.That(result.UserId).IsEqualTo("user-1");
  }

  [Test]
  public async Task GetCurrentSecurityContext_IgnoresCausationHopsAsync() {
    // Arrange
    var causationScope = new ScopeDelta {
      Values = new Dictionary<ScopeProp, JsonElement> {
        [ScopeProp.Scope] = JsonSerializer.SerializeToElement(new { t = "causation-tenant", u = "causation-user" })
      }
    };
    var envelope = new MessageEnvelope<TestPayload> {
      MessageId = MessageId.New(),
      Payload = new TestPayload("test"),
      Hops = [
        _createHop(type: HopType.Causation, scope: causationScope)
      ],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };

    // Act
    var result = envelope.GetCurrentSecurityContext();

    // Assert
    await Assert.That(result).IsNull();
  }

#pragma warning restore CS0618

  #endregion

  #region GetMessageTimestamp Tests

  [Test]
  public async Task GetMessageTimestamp_ReturnsFirstHopTimestampAsync() {
    // Arrange
    var firstTimestamp = new DateTimeOffset(2025, 1, 1, 12, 0, 0, TimeSpan.Zero);
    var hop = new MessageHop {
      ServiceInstance = _createServiceInstance(),
      Timestamp = firstTimestamp
    };
    var envelope = new MessageEnvelope<TestPayload> {
      MessageId = MessageId.New(),
      Payload = new TestPayload("test"),
      Hops = [hop, _createHop()],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };

    // Act
    var result = envelope.GetMessageTimestamp();

    // Assert
    await Assert.That(result).IsEqualTo(firstTimestamp);
  }

  [Test]
  public async Task GetMessageTimestamp_ReturnsFallback_WhenNoHopsAsync() {
    // Arrange
    var before = DateTimeOffset.UtcNow;
    var envelope = new MessageEnvelope<TestPayload> {
      MessageId = MessageId.New(),
      Payload = new TestPayload("test"),
      Hops = [],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };

    // Act
    var result = envelope.GetMessageTimestamp();
    var after = DateTimeOffset.UtcNow;

    // Assert - should return approximately "now" as fallback
    await Assert.That(result).IsGreaterThanOrEqualTo(before);
    await Assert.That(result).IsLessThanOrEqualTo(after);
  }

  #endregion

  #region GetCorrelationId Tests

  [Test]
  public async Task GetCorrelationId_ReturnsFirstHopCorrelationIdAsync() {
    // Arrange
    var correlationId = CorrelationId.New();
    var envelope = new MessageEnvelope<TestPayload> {
      MessageId = MessageId.New(),
      Payload = new TestPayload("test"),
      Hops = [_createHop(correlationId: correlationId)],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };

    // Act
    var result = envelope.GetCorrelationId();

    // Assert
    await Assert.That(result).IsEqualTo(correlationId);
  }

  [Test]
  public async Task GetCorrelationId_ReturnsNull_WhenNoHopsAsync() {
    // Arrange
    var envelope = new MessageEnvelope<TestPayload> {
      MessageId = MessageId.New(),
      Payload = new TestPayload("test"),
      Hops = [],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };

    // Act
    var result = envelope.GetCorrelationId();

    // Assert
    await Assert.That(result).IsNull();
  }

  [Test]
  public async Task GetCorrelationId_ReturnsNull_WhenFirstHopHasNoCorrelationIdAsync() {
    // Arrange
    var envelope = new MessageEnvelope<TestPayload> {
      MessageId = MessageId.New(),
      Payload = new TestPayload("test"),
      Hops = [_createHop()],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };

    // Act
    var result = envelope.GetCorrelationId();

    // Assert
    await Assert.That(result).IsNull();
  }

  #endregion

  #region GetCausationId Tests

  [Test]
  public async Task GetCausationId_ReturnsFirstHopCausationIdAsync() {
    // Arrange
    var causationId = MessageId.New();
    var envelope = new MessageEnvelope<TestPayload> {
      MessageId = MessageId.New(),
      Payload = new TestPayload("test"),
      Hops = [_createHop(causationId: causationId)],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };

    // Act
    var result = envelope.GetCausationId();

    // Assert
    await Assert.That(result).IsEqualTo(causationId);
  }

  [Test]
  public async Task GetCausationId_ReturnsNull_WhenNoHopsAsync() {
    // Arrange
    var envelope = new MessageEnvelope<TestPayload> {
      MessageId = MessageId.New(),
      Payload = new TestPayload("test"),
      Hops = [],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };

    // Act
    var result = envelope.GetCausationId();

    // Assert
    await Assert.That(result).IsNull();
  }

  #endregion

  #region GetMetadata Tests

  [Test]
  public async Task GetMetadata_ReturnsNull_WhenNoHopsAsync() {
    // Arrange
    var envelope = new MessageEnvelope<TestPayload> {
      MessageId = MessageId.New(),
      Payload = new TestPayload("test"),
      Hops = [],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };

    // Act
    var result = envelope.GetMetadata("any-key");

    // Assert
    await Assert.That(result).IsNull();
  }

  [Test]
  public async Task GetMetadata_ReturnsNull_WhenKeyNotFoundAsync() {
    // Arrange
    var metadata = new Dictionary<string, JsonElement> {
      ["existing-key"] = JsonSerializer.SerializeToElement("value")
    };
    var envelope = new MessageEnvelope<TestPayload> {
      MessageId = MessageId.New(),
      Payload = new TestPayload("test"),
      Hops = [_createHop(metadata: metadata)],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };

    // Act
    var result = envelope.GetMetadata("missing-key");

    // Assert
    await Assert.That(result).IsNull();
  }

  [Test]
  public async Task GetMetadata_ReturnsLatestValue_WhenKeyExistsInMultipleHopsAsync() {
    // Arrange
    var metadata1 = new Dictionary<string, JsonElement> {
      ["key"] = JsonSerializer.SerializeToElement("first-value")
    };
    var metadata2 = new Dictionary<string, JsonElement> {
      ["key"] = JsonSerializer.SerializeToElement("latest-value")
    };
    var envelope = new MessageEnvelope<TestPayload> {
      MessageId = MessageId.New(),
      Payload = new TestPayload("test"),
      Hops = [
        _createHop(metadata: metadata1),
        _createHop(metadata: metadata2)
      ],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };

    // Act
    var result = envelope.GetMetadata("key");

    // Assert
    await Assert.That(result).IsNotNull();
    await Assert.That(result!.Value.GetString()).IsEqualTo("latest-value");
  }

  [Test]
  public async Task GetMetadata_IgnoresCausationHopsAsync() {
    // Arrange
    var causationMetadata = new Dictionary<string, JsonElement> {
      ["key"] = JsonSerializer.SerializeToElement("causation-value")
    };
    var envelope = new MessageEnvelope<TestPayload> {
      MessageId = MessageId.New(),
      Payload = new TestPayload("test"),
      Hops = [
        _createHop(type: HopType.Causation, metadata: causationMetadata)
      ],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };

    // Act
    var result = envelope.GetMetadata("key");

    // Assert
    await Assert.That(result).IsNull();
  }

  [Test]
  public async Task GetMetadata_ReturnsNull_WhenHopsHaveNullMetadataAsync() {
    // Arrange
    var envelope = new MessageEnvelope<TestPayload> {
      MessageId = MessageId.New(),
      Payload = new TestPayload("test"),
      Hops = [_createHop(), _createHop()],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };

    // Act
    var result = envelope.GetMetadata("any-key");

    // Assert
    await Assert.That(result).IsNull();
  }

  #endregion

  #region GetAllMetadata Tests

  [Test]
  public async Task GetAllMetadata_ReturnsEmpty_WhenNoHopsAsync() {
    // Arrange
    var envelope = new MessageEnvelope<TestPayload> {
      MessageId = MessageId.New(),
      Payload = new TestPayload("test"),
      Hops = [],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };

    // Act
    var result = envelope.GetAllMetadata();

    // Assert
    await Assert.That(result.Count).IsEqualTo(0);
  }

  [Test]
  public async Task GetAllMetadata_StitchesMetadataAcrossHopsAsync() {
    // Arrange
    var metadata1 = new Dictionary<string, JsonElement> {
      ["key1"] = JsonSerializer.SerializeToElement("value1"),
      ["key2"] = JsonSerializer.SerializeToElement("original")
    };
    var metadata2 = new Dictionary<string, JsonElement> {
      ["key2"] = JsonSerializer.SerializeToElement("overridden"),
      ["key3"] = JsonSerializer.SerializeToElement("value3")
    };
    var envelope = new MessageEnvelope<TestPayload> {
      MessageId = MessageId.New(),
      Payload = new TestPayload("test"),
      Hops = [
        _createHop(metadata: metadata1),
        _createHop(metadata: metadata2)
      ],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };

    // Act
    var result = envelope.GetAllMetadata();

    // Assert
    await Assert.That(result.Count).IsEqualTo(3);
    await Assert.That(result["key1"].GetString()).IsEqualTo("value1");
    await Assert.That(result["key2"].GetString()).IsEqualTo("overridden");
    await Assert.That(result["key3"].GetString()).IsEqualTo("value3");
  }

  [Test]
  public async Task GetAllMetadata_IgnoresCausationHopsAsync() {
    // Arrange
    var currentMetadata = new Dictionary<string, JsonElement> {
      ["current-key"] = JsonSerializer.SerializeToElement("current-value")
    };
    var causationMetadata = new Dictionary<string, JsonElement> {
      ["causation-key"] = JsonSerializer.SerializeToElement("causation-value")
    };
    var envelope = new MessageEnvelope<TestPayload> {
      MessageId = MessageId.New(),
      Payload = new TestPayload("test"),
      Hops = [
        _createHop(metadata: currentMetadata),
        _createHop(type: HopType.Causation, metadata: causationMetadata)
      ],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };

    // Act
    var result = envelope.GetAllMetadata();

    // Assert
    await Assert.That(result.Count).IsEqualTo(1);
    await Assert.That(result.ContainsKey("current-key")).IsTrue();
    await Assert.That(result.ContainsKey("causation-key")).IsFalse();
  }

  [Test]
  public async Task GetAllMetadata_ReturnsEmpty_WhenNoHopsHaveMetadataAsync() {
    // Arrange
    var envelope = new MessageEnvelope<TestPayload> {
      MessageId = MessageId.New(),
      Payload = new TestPayload("test"),
      Hops = [_createHop(), _createHop()],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };

    // Act
    var result = envelope.GetAllMetadata();

    // Assert
    await Assert.That(result.Count).IsEqualTo(0);
  }

  #endregion

  #region GetAllPolicyDecisions Tests

  [Test]
  public async Task GetAllPolicyDecisions_ReturnsEmpty_WhenNoHopsAsync() {
    // Arrange
    var envelope = new MessageEnvelope<TestPayload> {
      MessageId = MessageId.New(),
      Payload = new TestPayload("test"),
      Hops = [],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };

    // Act
    var result = envelope.GetAllPolicyDecisions();

    // Assert
    await Assert.That(result.Count).IsEqualTo(0);
  }

  [Test]
  public async Task GetAllPolicyDecisions_ReturnsEmpty_WhenNoHopsHaveTrailsAsync() {
    // Arrange
    var envelope = new MessageEnvelope<TestPayload> {
      MessageId = MessageId.New(),
      Payload = new TestPayload("test"),
      Hops = [_createHop(), _createHop()],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };

    // Act
    var result = envelope.GetAllPolicyDecisions();

    // Assert
    await Assert.That(result.Count).IsEqualTo(0);
  }

  [Test]
  public async Task GetAllPolicyDecisions_StitchesDecisionsAcrossHopsAsync() {
    // Arrange
    var trail1 = new PolicyDecisionTrail();
    trail1.RecordDecision("Policy1", "Rule1", true, null, "matched");

    var trail2 = new PolicyDecisionTrail();
    trail2.RecordDecision("Policy2", "Rule2", false, null, "not matched");

    var envelope = new MessageEnvelope<TestPayload> {
      MessageId = MessageId.New(),
      Payload = new TestPayload("test"),
      Hops = [
        _createHop(trail: trail1),
        _createHop(trail: trail2)
      ],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };

    // Act
    var result = envelope.GetAllPolicyDecisions();

    // Assert
    await Assert.That(result.Count).IsEqualTo(2);
    await Assert.That(result[0].PolicyName).IsEqualTo("Policy1");
    await Assert.That(result[1].PolicyName).IsEqualTo("Policy2");
  }

  [Test]
  public async Task GetAllPolicyDecisions_IgnoresCausationHopsAsync() {
    // Arrange
    var currentTrail = new PolicyDecisionTrail();
    currentTrail.RecordDecision("CurrentPolicy", "Rule1", true, null, "current");

    var causationTrail = new PolicyDecisionTrail();
    causationTrail.RecordDecision("CausationPolicy", "Rule1", true, null, "causation");

    var envelope = new MessageEnvelope<TestPayload> {
      MessageId = MessageId.New(),
      Payload = new TestPayload("test"),
      Hops = [
        _createHop(trail: currentTrail),
        _createHop(type: HopType.Causation, trail: causationTrail)
      ],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };

    // Act
    var result = envelope.GetAllPolicyDecisions();

    // Assert
    await Assert.That(result.Count).IsEqualTo(1);
    await Assert.That(result[0].PolicyName).IsEqualTo("CurrentPolicy");
  }

  [Test]
  public async Task GetAllPolicyDecisions_SkipsHopsWithoutTrailsAsync() {
    // Arrange
    var trail = new PolicyDecisionTrail();
    trail.RecordDecision("OnlyPolicy", "Rule1", true, null, "only one");

    var envelope = new MessageEnvelope<TestPayload> {
      MessageId = MessageId.New(),
      Payload = new TestPayload("test"),
      Hops = [
        _createHop(),
        _createHop(trail: trail),
        _createHop()
      ],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };

    // Act
    var result = envelope.GetAllPolicyDecisions();

    // Assert
    await Assert.That(result.Count).IsEqualTo(1);
    await Assert.That(result[0].PolicyName).IsEqualTo("OnlyPolicy");
  }

  #endregion

  #region GetCausationHops Tests

  [Test]
  public async Task GetCausationHops_ReturnsEmpty_WhenNoHopsAsync() {
    // Arrange
    var envelope = new MessageEnvelope<TestPayload> {
      MessageId = MessageId.New(),
      Payload = new TestPayload("test"),
      Hops = [],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };

    // Act
    var result = envelope.GetCausationHops();

    // Assert
    await Assert.That(result.Count).IsEqualTo(0);
  }

  [Test]
  public async Task GetCausationHops_ReturnsEmpty_WhenNoCausationHopsExistAsync() {
    // Arrange
    var envelope = new MessageEnvelope<TestPayload> {
      MessageId = MessageId.New(),
      Payload = new TestPayload("test"),
      Hops = [_createHop(), _createHop()],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };

    // Act
    var result = envelope.GetCausationHops();

    // Assert
    await Assert.That(result.Count).IsEqualTo(0);
  }

  [Test]
  public async Task GetCausationHops_ReturnsOnlyCausationHopsAsync() {
    // Arrange
    var envelope = new MessageEnvelope<TestPayload> {
      MessageId = MessageId.New(),
      Payload = new TestPayload("test"),
      Hops = [
        _createHop(topic: "current-1"),
        _createHop(type: HopType.Causation, topic: "causation-1"),
        _createHop(topic: "current-2"),
        _createHop(type: HopType.Causation, topic: "causation-2")
      ],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };

    // Act
    var result = envelope.GetCausationHops();

    // Assert
    await Assert.That(result.Count).IsEqualTo(2);
    await Assert.That(result[0].Topic).IsEqualTo("causation-1");
    await Assert.That(result[1].Topic).IsEqualTo("causation-2");
  }

  #endregion

  #region GetCurrentHops Tests

  [Test]
  public async Task GetCurrentHops_ReturnsEmpty_WhenNoHopsAsync() {
    // Arrange
    var envelope = new MessageEnvelope<TestPayload> {
      MessageId = MessageId.New(),
      Payload = new TestPayload("test"),
      Hops = [],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };

    // Act
    var result = envelope.GetCurrentHops();

    // Assert
    await Assert.That(result.Count).IsEqualTo(0);
  }

  [Test]
  public async Task GetCurrentHops_ReturnsOnlyCurrentHopsAsync() {
    // Arrange
    var envelope = new MessageEnvelope<TestPayload> {
      MessageId = MessageId.New(),
      Payload = new TestPayload("test"),
      Hops = [
        _createHop(topic: "current-1"),
        _createHop(type: HopType.Causation, topic: "causation-1"),
        _createHop(topic: "current-2")
      ],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };

    // Act
    var result = envelope.GetCurrentHops();

    // Assert
    await Assert.That(result.Count).IsEqualTo(2);
    await Assert.That(result[0].Topic).IsEqualTo("current-1");
    await Assert.That(result[1].Topic).IsEqualTo("current-2");
  }

  #endregion
}
