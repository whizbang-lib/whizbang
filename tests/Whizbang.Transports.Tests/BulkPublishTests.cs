using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using Whizbang.Core.Observability;
using Whizbang.Core.Transports;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Transports.Tests;

/// <summary>
/// Tests for BulkPublishItem and BulkPublishItemResult record types.
/// Following TDD: These tests are written BEFORE the record implementations.
/// </summary>
public class BulkPublishTests {
  [Test]
  public async Task BulkPublishItem_WithRequiredProperties_SetsAllPropertiesAsync() {
    // Arrange
    var messageId = Guid.NewGuid();
    var envelope = _createTestEnvelope();

    // Act
    var item = new BulkPublishItem {
      Envelope = envelope,
      EnvelopeType = "TestType, TestAssembly",
      MessageId = messageId,
    };

    // Assert
    await Assert.That(item.Envelope).IsEqualTo(envelope);
    await Assert.That(item.EnvelopeType).IsEqualTo("TestType, TestAssembly");
    await Assert.That(item.MessageId).IsEqualTo(messageId);
    await Assert.That(item.RoutingKey).IsNull();
  }

  [Test]
  public async Task BulkPublishItem_WithRoutingKey_SetsRoutingKeyAsync() {
    // Arrange & Act
    var item = new BulkPublishItem {
      Envelope = _createTestEnvelope(),
      EnvelopeType = "TestType, TestAssembly",
      MessageId = Guid.NewGuid(),
      RoutingKey = "orders.created"
    };

    // Assert
    await Assert.That(item.RoutingKey).IsEqualTo("orders.created");
  }

  [Test]
  public async Task BulkPublishItem_WithNullEnvelopeType_AllowsNullAsync() {
    // Arrange & Act
    var item = new BulkPublishItem {
      Envelope = _createTestEnvelope(),
      EnvelopeType = null,
      MessageId = Guid.NewGuid(),
    };

    // Assert
    await Assert.That(item.EnvelopeType).IsNull();
  }

  [Test]
  public async Task BulkPublishItem_RecordEquality_BehavesCorrectlyAsync() {
    // Arrange
    var messageId = Guid.NewGuid();
    var envelope = _createTestEnvelope();

    var item1 = new BulkPublishItem {
      Envelope = envelope,
      EnvelopeType = "TestType, TestAssembly",
      MessageId = messageId,
    };
    var item2 = new BulkPublishItem {
      Envelope = envelope,
      EnvelopeType = "TestType, TestAssembly",
      MessageId = messageId,
    };

    // Assert
    await Assert.That(item1).IsEqualTo(item2);
  }

  [Test]
  public async Task BulkPublishItemResult_Success_SetsPropertiesAsync() {
    // Arrange & Act
    var messageId = Guid.NewGuid();
    var result = new BulkPublishItemResult {
      MessageId = messageId,
      Success = true,
    };

    // Assert
    await Assert.That(result.MessageId).IsEqualTo(messageId);
    await Assert.That(result.Success).IsTrue();
    await Assert.That(result.Error).IsNull();
  }

  [Test]
  public async Task BulkPublishItemResult_Failure_SetsErrorAsync() {
    // Arrange & Act
    var messageId = Guid.NewGuid();
    var result = new BulkPublishItemResult {
      MessageId = messageId,
      Success = false,
      Error = "Connection refused"
    };

    // Assert
    await Assert.That(result.MessageId).IsEqualTo(messageId);
    await Assert.That(result.Success).IsFalse();
    await Assert.That(result.Error).IsEqualTo("Connection refused");
  }

  [Test]
  public async Task BulkPublishItemResult_RecordEquality_BehavesCorrectlyAsync() {
    // Arrange
    var messageId = Guid.NewGuid();
    var result1 = new BulkPublishItemResult { MessageId = messageId, Success = true };
    var result2 = new BulkPublishItemResult { MessageId = messageId, Success = true };

    // Assert
    await Assert.That(result1).IsEqualTo(result2);
  }

  [Test]
  public async Task BulkPublishItem_WithStreamId_SetsStreamIdAsync() {
    // Arrange
    var streamId = Guid.CreateVersion7();

    // Act
    var item = new BulkPublishItem {
      Envelope = _createTestEnvelope(),
      EnvelopeType = "TestType, TestAssembly",
      MessageId = Guid.NewGuid(),
      StreamId = streamId
    };

    // Assert
    await Assert.That(item.StreamId).IsEqualTo(streamId);
  }

  [Test]
  public async Task BulkPublishItem_WithoutStreamId_DefaultsToNullAsync() {
    // Arrange & Act
    var item = new BulkPublishItem {
      Envelope = _createTestEnvelope(),
      EnvelopeType = "TestType, TestAssembly",
      MessageId = Guid.NewGuid(),
    };

    // Assert
    await Assert.That(item.StreamId).IsNull();
  }

  [Test]
  public async Task BulkPublishItem_RecordEquality_WithStreamId_BehavesCorrectlyAsync() {
    // Arrange
    var messageId = Guid.NewGuid();
    var streamId = Guid.CreateVersion7();
    var envelope = _createTestEnvelope();

    var item1 = new BulkPublishItem {
      Envelope = envelope,
      EnvelopeType = "TestType, TestAssembly",
      MessageId = messageId,
      StreamId = streamId
    };
    var item2 = new BulkPublishItem {
      Envelope = envelope,
      EnvelopeType = "TestType, TestAssembly",
      MessageId = messageId,
      StreamId = streamId
    };

    // Assert
    await Assert.That(item1).IsEqualTo(item2);
  }

  // Helper methods
  private static MessageEnvelope<TestMessage> _createTestEnvelope() {
    var message = new TestMessage { Content = "Test" };
    return new MessageEnvelope<TestMessage> {
      MessageId = MessageId.New(),
      Payload = message,
      Hops = [
        new MessageHop {
          ServiceInstance = new ServiceInstanceInfo {
            ServiceName = "TestService",
            InstanceId = Guid.NewGuid(),
            HostName = "test-host",
            ProcessId = 12345
          },
          Timestamp = DateTimeOffset.UtcNow
        }
      ]
    };
  }

  private sealed record TestMessage {
    public string Content { get; init; } = string.Empty;
  }
}
