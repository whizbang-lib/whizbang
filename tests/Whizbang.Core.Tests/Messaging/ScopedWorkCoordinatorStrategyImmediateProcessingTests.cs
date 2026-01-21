using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Tests for ScopedWorkCoordinatorStrategy immediate processing of returned work.
/// Verifies that work returned from ProcessWorkBatchAsync is written to channel immediately.
/// </summary>
public class ScopedWorkCoordinatorStrategyImmediateProcessingTests {
  // Helper method to create test envelope
  private static TestMessageEnvelope _createTestEnvelope(System.Guid messageId) {
    return new TestMessageEnvelope {
      MessageId = MessageId.From(messageId),
      Hops = []
    };
  }

  [Test]
  public async Task FlushAsync_WithReturnedWork_WritesToChannelImmediatelyAsync(CancellationToken cancellationToken) {
    // Arrange
    var channelWriter = new TestWorkChannelWriter();
    var messageId1 = System.Guid.CreateVersion7();
    var messageId2 = System.Guid.CreateVersion7();
    var coordinator = new TestWorkCoordinator {
      WorkToReturn = [
        new OutboxWork {
          MessageId = messageId1,
          Destination = "test-topic",
          EnvelopeType = "Whizbang.Core.Tests.Messaging.ScopedWorkCoordinatorStrategyImmediateProcessingTests+TestMessageEnvelope, Whizbang.Core.Tests",
      MessageType = "System.Text.Json.JsonElement, System.Text.Json",
          Envelope = _createTestEnvelope(messageId1),
          Attempts = 0,
          Status = MessageProcessingStatus.None
        },
        new OutboxWork {
          MessageId = messageId2,
          Destination = "test-topic",
          EnvelopeType = "Whizbang.Core.Tests.Messaging.ScopedWorkCoordinatorStrategyImmediateProcessingTests+TestMessageEnvelope, Whizbang.Core.Tests",
      MessageType = "System.Text.Json.JsonElement, System.Text.Json",
          Envelope = _createTestEnvelope(messageId2),
          Attempts = 0,
          Status = MessageProcessingStatus.None
        }
      ]
    };

    var instanceProvider = new TestServiceInstanceProvider();
    var options = new WorkCoordinatorOptions();
    var strategy = new ScopedWorkCoordinatorStrategy(coordinator, instanceProvider, channelWriter, options);

    // Queue a message to trigger flush
    var queuedMessageId = System.Guid.CreateVersion7();
    strategy.QueueOutboxMessage(new OutboxMessage {
      MessageId = queuedMessageId,
      Destination = "test-topic",
      EnvelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[System.Object, System.Private.CoreLib]], Whizbang.Core",
      Envelope = _createTestEnvelope(queuedMessageId),
      IsEvent = false,
      MessageType = "TestMessage, TestAssembly",
      Metadata = new EnvelopeMetadata {
        MessageId = MessageId.From(queuedMessageId),
        Hops = new List<MessageHop>()
      }
    });

    // Act
    var result = await strategy.FlushAsync(WorkBatchFlags.None, cancellationToken);

    // Assert - Work should be written to channel immediately
    await Assert.That(channelWriter.WrittenWork).Count().IsEqualTo(2);
    await Assert.That(result.OutboxWork).Count().IsEqualTo(2);
  }

  [Test]
  public async Task FlushAsync_NoReturnedWork_DoesNotWriteToChannelAsync(CancellationToken cancellationToken) {
    // Arrange
    var channelWriter = new TestWorkChannelWriter();
    var coordinator = new TestWorkCoordinator {
      WorkToReturn = []  // No work returned
    };

    var instanceProvider = new TestServiceInstanceProvider();
    var options = new WorkCoordinatorOptions();
    var strategy = new ScopedWorkCoordinatorStrategy(coordinator, instanceProvider, channelWriter, options);

    // Queue a message to trigger flush
    var queuedMessageId = System.Guid.CreateVersion7();
    strategy.QueueOutboxMessage(new OutboxMessage {
      MessageId = queuedMessageId,
      Destination = "test-topic",
      EnvelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[System.Object, System.Private.CoreLib]], Whizbang.Core",
      Envelope = _createTestEnvelope(queuedMessageId),
      IsEvent = false,
      MessageType = "TestMessage, TestAssembly",
      Metadata = new EnvelopeMetadata {
        MessageId = MessageId.From(queuedMessageId),
        Hops = new List<MessageHop>()
      }
    });

    // Act
    await strategy.FlushAsync(WorkBatchFlags.None, cancellationToken);

    // Assert - Nothing written to channel
    await Assert.That(channelWriter.WrittenWork).Count().IsEqualTo(0);
  }

  [Test]
  public async Task FlushAsync_MultipleFlushes_AllWorkWrittenToChannelAsync(CancellationToken cancellationToken) {
    // Arrange
    var channelWriter = new TestWorkChannelWriter();
    var coordinator = new TestWorkCoordinator();
    var instanceProvider = new TestServiceInstanceProvider();
    var options = new WorkCoordinatorOptions();
    var strategy = new ScopedWorkCoordinatorStrategy(coordinator, instanceProvider, channelWriter, options);

    // Act - First flush with 2 messages
    var msg1 = System.Guid.CreateVersion7();
    var msg2 = System.Guid.CreateVersion7();
    coordinator.WorkToReturn = [
      new OutboxWork { MessageId = msg1, Destination = "topic1", EnvelopeType = "Whizbang.Core.Tests.Messaging.ScopedWorkCoordinatorStrategyImmediateProcessingTests+TestMessageEnvelope, Whizbang.Core.Tests",
      MessageType = "System.Text.Json.JsonElement, System.Text.Json", Envelope = _createTestEnvelope(msg1), Attempts = 0, Status = MessageProcessingStatus.None },
      new OutboxWork { MessageId = msg2, Destination = "topic1", EnvelopeType = "Whizbang.Core.Tests.Messaging.ScopedWorkCoordinatorStrategyImmediateProcessingTests+TestMessageEnvelope, Whizbang.Core.Tests",
      MessageType = "System.Text.Json.JsonElement, System.Text.Json", Envelope = _createTestEnvelope(msg2), Attempts = 0, Status = MessageProcessingStatus.None }
    ];
    var queued1 = System.Guid.CreateVersion7();
    strategy.QueueOutboxMessage(new OutboxMessage {
      MessageId = queued1,
      Destination = "topic1",
      Envelope = _createTestEnvelope(queued1),
      EnvelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[System.Object, System.Private.CoreLib]], Whizbang.Core",
      IsEvent = false,
      MessageType = "TestMessage, TestAssembly",
      Metadata = new EnvelopeMetadata {
        MessageId = MessageId.From(queued1),
        Hops = new List<MessageHop>()
      }
    });
    await strategy.FlushAsync(WorkBatchFlags.None, cancellationToken);

    // Act - Second flush with 3 messages
    var msg3 = System.Guid.CreateVersion7();
    var msg4 = System.Guid.CreateVersion7();
    var msg5 = System.Guid.CreateVersion7();
    coordinator.WorkToReturn = [
      new OutboxWork { MessageId = msg3, Destination = "topic2", EnvelopeType = "Whizbang.Core.Tests.Messaging.ScopedWorkCoordinatorStrategyImmediateProcessingTests+TestMessageEnvelope, Whizbang.Core.Tests",
      MessageType = "System.Text.Json.JsonElement, System.Text.Json", Envelope = _createTestEnvelope(msg3), Attempts = 0, Status = MessageProcessingStatus.None },
      new OutboxWork { MessageId = msg4, Destination = "topic2", EnvelopeType = "Whizbang.Core.Tests.Messaging.ScopedWorkCoordinatorStrategyImmediateProcessingTests+TestMessageEnvelope, Whizbang.Core.Tests",
      MessageType = "System.Text.Json.JsonElement, System.Text.Json", Envelope = _createTestEnvelope(msg4), Attempts = 0, Status = MessageProcessingStatus.None },
      new OutboxWork { MessageId = msg5, Destination = "topic2", EnvelopeType = "Whizbang.Core.Tests.Messaging.ScopedWorkCoordinatorStrategyImmediateProcessingTests+TestMessageEnvelope, Whizbang.Core.Tests",
      MessageType = "System.Text.Json.JsonElement, System.Text.Json", Envelope = _createTestEnvelope(msg5), Attempts = 0, Status = MessageProcessingStatus.None }
    ];
    var queued2 = System.Guid.CreateVersion7();
    strategy.QueueOutboxMessage(new OutboxMessage {
      MessageId = queued2,
      Destination = "topic2",
      Envelope = _createTestEnvelope(queued2),
      EnvelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[System.Object, System.Private.CoreLib]], Whizbang.Core",
      IsEvent = false,
      MessageType = "TestMessage, TestAssembly",
      Metadata = new EnvelopeMetadata {
        MessageId = MessageId.From(queued2),
        Hops = new List<MessageHop>()
      }
    });
    await strategy.FlushAsync(WorkBatchFlags.None, cancellationToken);

    // Assert - All 5 messages written to channel
    await Assert.That(channelWriter.WrittenWork).Count().IsEqualTo(5);
  }

  [Test]
  public async Task FlushAsync_WithOrderedWork_MaintainsOrderInChannelAsync(CancellationToken cancellationToken) {
    // Arrange
    var channelWriter = new TestWorkChannelWriter();
    var messageId1 = System.Guid.CreateVersion7();
    var messageId2 = System.Guid.CreateVersion7();
    var messageId3 = System.Guid.CreateVersion7();

    var coordinator = new TestWorkCoordinator {
      WorkToReturn = [
        new OutboxWork { MessageId = messageId1, Destination = "topic", EnvelopeType = "Whizbang.Core.Tests.Messaging.ScopedWorkCoordinatorStrategyImmediateProcessingTests+TestMessageEnvelope, Whizbang.Core.Tests",
      MessageType = "System.Text.Json.JsonElement, System.Text.Json", Envelope = _createTestEnvelope(messageId1), Attempts = 0, Status = MessageProcessingStatus.None },
        new OutboxWork { MessageId = messageId2, Destination = "topic", EnvelopeType = "Whizbang.Core.Tests.Messaging.ScopedWorkCoordinatorStrategyImmediateProcessingTests+TestMessageEnvelope, Whizbang.Core.Tests",
      MessageType = "System.Text.Json.JsonElement, System.Text.Json", Envelope = _createTestEnvelope(messageId2), Attempts = 0, Status = MessageProcessingStatus.None },
        new OutboxWork { MessageId = messageId3, Destination = "topic", EnvelopeType = "Whizbang.Core.Tests.Messaging.ScopedWorkCoordinatorStrategyImmediateProcessingTests+TestMessageEnvelope, Whizbang.Core.Tests",
      MessageType = "System.Text.Json.JsonElement, System.Text.Json", Envelope = _createTestEnvelope(messageId3), Attempts = 0, Status = MessageProcessingStatus.None }
      ]
    };

    var instanceProvider = new TestServiceInstanceProvider();
    var options = new WorkCoordinatorOptions();
    var strategy = new ScopedWorkCoordinatorStrategy(coordinator, instanceProvider, channelWriter, options);

    var queuedMessageId = System.Guid.CreateVersion7();
    strategy.QueueOutboxMessage(new OutboxMessage {
      MessageId = queuedMessageId,
      Destination = "topic",
      Envelope = _createTestEnvelope(queuedMessageId),
      EnvelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[System.Object, System.Private.CoreLib]], Whizbang.Core",
      IsEvent = false,
      MessageType = "TestMessage, TestAssembly",
      Metadata = new EnvelopeMetadata {
        MessageId = MessageId.From(queuedMessageId),
        Hops = new List<MessageHop>()
      }
    });

    // Act
    await strategy.FlushAsync(WorkBatchFlags.None, cancellationToken);

    // Assert - Work written in same order
    await Assert.That(channelWriter.WrittenWork[0].MessageId).IsEqualTo(messageId1);
    await Assert.That(channelWriter.WrittenWork[1].MessageId).IsEqualTo(messageId2);
    await Assert.That(channelWriter.WrittenWork[2].MessageId).IsEqualTo(messageId3);
  }

  // Test helper - Mock work channel writer
  private sealed class TestWorkChannelWriter : IWorkChannelWriter {
    public List<OutboxWork> WrittenWork { get; } = [];

    public System.Threading.Channels.ChannelReader<OutboxWork> Reader =>
      throw new System.NotImplementedException("Reader not needed for tests");

    public ValueTask WriteAsync(OutboxWork work, CancellationToken ct) {
      WrittenWork.Add(work);
      return ValueTask.CompletedTask;
    }

    public bool TryWrite(OutboxWork work) {
      WrittenWork.Add(work);
      return true;
    }

    public void Complete() {
      // No-op for testing
    }
  }

  // Test helper - Mock work coordinator
  private sealed class TestWorkCoordinator : IWorkCoordinator {
    public List<OutboxWork> WorkToReturn { get; set; } = [];

    public Task<WorkBatch> ProcessWorkBatchAsync(
      ProcessWorkBatchRequest request,
      CancellationToken cancellationToken = default) {
      return Task.FromResult(new WorkBatch {
        OutboxWork = WorkToReturn,
        InboxWork = [],
        PerspectiveWork = []
      });
    }

    public Task ReportPerspectiveCompletionAsync(
      PerspectiveCheckpointCompletion completion,
      CancellationToken cancellationToken = default) {
      return Task.CompletedTask;
    }

    public Task ReportPerspectiveFailureAsync(
      PerspectiveCheckpointFailure failure,
      CancellationToken cancellationToken = default) {
      return Task.CompletedTask;
    }

    public Task<PerspectiveCheckpointInfo?> GetPerspectiveCheckpointAsync(
      Guid streamId,
      string perspectiveName,
      CancellationToken cancellationToken = default) {
      return Task.FromResult<PerspectiveCheckpointInfo?>(null);
    }
  }

  // Test helper - Mock service instance provider
  private sealed class TestServiceInstanceProvider : IServiceInstanceProvider {
    public System.Guid InstanceId { get; } = System.Guid.CreateVersion7();
    public string ServiceName { get; } = "TestService";
    public string HostName { get; } = "test-host";
    public int ProcessId { get; } = 12345;

    public ServiceInstanceInfo ToInfo() => new() {
      InstanceId = InstanceId,
      ServiceName = ServiceName,
      HostName = HostName,
      ProcessId = ProcessId
    };
  }

  // Test envelope implementation
  private sealed class TestMessageEnvelope : IMessageEnvelope<JsonElement> {
    public required MessageId MessageId { get; init; }
    public required List<MessageHop> Hops { get; init; }
    public JsonElement Payload { get; init; } = JsonDocument.Parse("{}").RootElement;  // Test payload
    object IMessageEnvelope.Payload => Payload;  // Explicit interface implementation

    public void AddHop(MessageHop hop) {
      Hops.Add(hop);
    }

    public DateTimeOffset GetMessageTimestamp() {
      return Hops.Count > 0 ? Hops[0].Timestamp : DateTimeOffset.UtcNow;
    }

    public CorrelationId? GetCorrelationId() {
      return Hops.Count > 0 ? Hops[0].CorrelationId : null;
    }

    public MessageId? GetCausationId() {
      return Hops.Count > 0 ? Hops[0].CausationId : null;
    }

    public JsonElement? GetMetadata(string key) {
      for (var i = Hops.Count - 1; i >= 0; i--) {
        if (Hops[i].Type == HopType.Current && Hops[i].Metadata?.ContainsKey(key) == true) {
          return Hops[i].Metadata![key];
        }
      }
      return null;
    }
  }
}
