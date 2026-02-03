using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Tests for OrderedStreamProcessor - verifies stream-based ordering guarantees.
/// </summary>
public class OrderedStreamProcessorTests {
  private readonly Uuid7IdProvider _idProvider = new();

  // ========================================
  // Priority 2 Tests: Stream Ordering (Inbox)
  // ========================================

  [Test]
  public async Task ProcessInboxWorkAsync_SingleStream_ProcessesInOrderAsync() {
    // Arrange
    var sut = new OrderedStreamProcessor(parallelizeStreams: false);
    var streamId = _idProvider.NewGuid();
    var processedOrder = new List<Medo.Uuid7>();

    // Create 5 messages with UUIDv7 temporal ordering
    var messages = new List<InboxWork> {
      _createInboxWork(streamId),
      _createInboxWork(streamId),
      _createInboxWork(streamId),
      _createInboxWork(streamId),
      _createInboxWork(streamId)
    };

    // Act
    await sut.ProcessInboxWorkAsync(
      messages,
      processor: async work => {
        processedOrder.Add(work.MessageId);
        return await Task.FromResult(MessageProcessingStatus.EventStored);
      },
      completionHandler: (_, _) => { },
      failureHandler: (_, _, _) => { }
    );

    // Assert - Should process in MessageId (UUIDv7) ascending order
    await Assert.That(processedOrder).Count().IsEqualTo(5);
    // Verify messages were processed in creation order (UUIDv7 provides temporal ordering)
    await Assert.That(processedOrder[0]).IsEqualTo(messages[0].MessageId);
    await Assert.That(processedOrder[1]).IsEqualTo(messages[1].MessageId);
    await Assert.That(processedOrder[2]).IsEqualTo(messages[2].MessageId);
    await Assert.That(processedOrder[3]).IsEqualTo(messages[3].MessageId);
    await Assert.That(processedOrder[4]).IsEqualTo(messages[4].MessageId);
  }

  [Test]
  public async Task ProcessInboxWorkAsync_MultipleStreams_ProcessesConcurrentlyAsync() {
    // Arrange
    var sut = new OrderedStreamProcessor(parallelizeStreams: true);
    var stream1 = _idProvider.NewGuid();
    var stream2 = _idProvider.NewGuid();
    var stream3 = _idProvider.NewGuid();

    var processedStreams = new ConcurrentBag<Guid>();
    var processingStarted = new Dictionary<Guid, DateTimeOffset>();
    var processingCompleted = new Dictionary<Guid, DateTimeOffset>();
    var lockObj = new object();

    // Create messages from 3 different streams
    var messages = new List<InboxWork> {
      _createInboxWork(stream1),
      _createInboxWork(stream1),
      _createInboxWork(stream2),
      _createInboxWork(stream2),
      _createInboxWork(stream3),
      _createInboxWork(stream3)
    };

    // Act
    await sut.ProcessInboxWorkAsync(
      messages,
      processor: async work => {
        var streamId = work.StreamId!.Value;

        lock (lockObj) {
          if (!processingStarted.ContainsKey(streamId)) {
            processingStarted[streamId] = DateTimeOffset.UtcNow;
          }
        }

        processedStreams.Add(streamId);
        await Task.Delay(10);  // Simulate work

        lock (lockObj) {
          processingCompleted[streamId] = DateTimeOffset.UtcNow;
        }

        return MessageProcessingStatus.EventStored;
      },
      completionHandler: (_, _) => { },
      failureHandler: (_, _, _) => { }
    );

    // Assert - All 3 streams processed
    await Assert.That(processedStreams.Distinct()).Count().IsEqualTo(3);
    await Assert.That(processedStreams).Contains(stream1);
    await Assert.That(processedStreams).Contains(stream2);
    await Assert.That(processedStreams).Contains(stream3);

    // Verify concurrent processing (at least 2 streams overlapped)
    var hasOverlap = false;
    foreach (var stream in processingStarted.Keys) {
      var otherStreams = processingStarted.Keys.Where(s => s != stream);
      foreach (var other in otherStreams) {
        if (processingStarted[stream] < processingCompleted[other] &&
            processingCompleted[stream] > processingStarted[other]) {
          hasOverlap = true;
          break;
        }
      }
      if (hasOverlap) {
        break;
      }
    }

    await Assert.That(hasOverlap).IsTrue()
      .Because("With parallelizeStreams=true, different streams should be processed concurrently");
  }

  [Test]
  public async Task ProcessInboxWorkAsync_StreamWithError_ContinuesOtherStreamsAsync() {
    // Arrange
    var sut = new OrderedStreamProcessor(parallelizeStreams: true);
    var stream1 = _idProvider.NewGuid();  // Will fail
    var stream2 = _idProvider.NewGuid();  // Should continue

    var processedMessages = new ConcurrentBag<Guid>();
    var failedMessages = new ConcurrentBag<Guid>();

    // Create messages from 2 streams (UUIDv7 provides temporal ordering)
    var stream1msg1 = _createInboxWork(stream1);
    var stream1msg2 = _createInboxWork(stream1);  // Won't be processed (stream stopped)
    var stream2msg1 = _createInboxWork(stream2);
    var stream2msg2 = _createInboxWork(stream2);

    var messages = new List<InboxWork> { stream1msg1, stream1msg2, stream2msg1, stream2msg2 };

    // Act
    await sut.ProcessInboxWorkAsync(
      messages,
      processor: async work => {
        // Fail first message of stream1
        if (work.MessageId == stream1msg1.MessageId) {
          throw new InvalidOperationException("Simulated failure");
        }

        processedMessages.Add(work.MessageId);
        return await Task.FromResult(MessageProcessingStatus.EventStored);
      },
      completionHandler: (_, _) => { },
      failureHandler: (messageId, _, _) => {
        failedMessages.Add(messageId);
      }
    );

    // Assert
    await Assert.That(failedMessages).Count().IsEqualTo(1)
      .Because("Only 1 message should fail");

    await Assert.That(processedMessages).Count().IsEqualTo(2)
      .Because("Stream 2 should continue processing despite stream 1 failure");

    // Verify stream2 messages were processed
    var stream2Messages = messages.Where(m => m.StreamId == stream2).Select(m => m.MessageId);
    foreach (var messageId in stream2Messages) {
      await Assert.That(processedMessages).Contains(messageId)
        .Because("Stream 2 should process all its messages despite stream 1 failure");
    }
  }

  [Test]
  public async Task ProcessInboxWorkAsync_PartialFailure_ReportsCorrectStatusAsync() {
    // Arrange
    var sut = new OrderedStreamProcessor(parallelizeStreams: false);
    var streamId = _idProvider.NewGuid();

    MessageProcessingStatus? reportedPartialStatus = null;
    string? reportedError = null;

    // Create work item with only Stored status (processing will fail before EventStored)
    var message = _createInboxWork(streamId);
    message = message with {
      Status = MessageProcessingStatus.Stored
    };

    // Act
    await sut.ProcessInboxWorkAsync(
      [message],
      processor: async _ => {
        // Simulate failure at receptor processing stage
        throw new InvalidOperationException("Receptor processing failed");
      },
      completionHandler: (_, _) => { },
      failureHandler: (_, partialStatus, error) => {
        reportedPartialStatus = partialStatus;
        reportedError = error;
      }
    );

    // Assert - Partial status should reflect what completed before failure
    await Assert.That(reportedPartialStatus).IsNotNull();
    await Assert.That((reportedPartialStatus!.Value & MessageProcessingStatus.Stored) == MessageProcessingStatus.Stored).IsTrue()
      .Because("Partial completion should include Stored flag");
    // EventStored flag should NOT be set since that's where processing failed
    await Assert.That((reportedPartialStatus.Value & MessageProcessingStatus.EventStored) != MessageProcessingStatus.EventStored).IsTrue()
      .Because("Partial completion should NOT include EventStored flag (processing failed before this stage)");
    await Assert.That(reportedError).Contains("Receptor processing failed");
  }

  // ========================================
  // Priority 2 Tests: Stream Ordering (Outbox)
  // ========================================

  [Test]
  public async Task ProcessOutboxWorkAsync_SameStreamSameOrder_ProcessesSequentiallyAsync() {
    // Arrange
    var sut = new OrderedStreamProcessor(parallelizeStreams: false);
    var streamId = _idProvider.NewGuid();
    var processedOrder = new List<Medo.Uuid7>();

    // Create 5 messages with UUIDv7 temporal ordering
    var messages = new List<OutboxWork> {
      _createOutboxWork(streamId),
      _createOutboxWork(streamId),
      _createOutboxWork(streamId),
      _createOutboxWork(streamId),
      _createOutboxWork(streamId)
    };

    // Act
    await sut.ProcessOutboxWorkAsync(
      messages,
      processor: async work => {
        processedOrder.Add(work.MessageId);
        return await Task.FromResult(MessageProcessingStatus.Published);
      },
      completionHandler: (_, _) => { },
      failureHandler: (_, _, _) => { }
    );

    // Assert - Should process in MessageId (UUIDv7) ascending order
    await Assert.That(processedOrder).Count().IsEqualTo(5);
    // Verify messages were processed in creation order (UUIDv7 provides temporal ordering)
    await Assert.That(processedOrder[0]).IsEqualTo(messages[0].MessageId);
    await Assert.That(processedOrder[1]).IsEqualTo(messages[1].MessageId);
    await Assert.That(processedOrder[2]).IsEqualTo(messages[2].MessageId);
    await Assert.That(processedOrder[3]).IsEqualTo(messages[3].MessageId);
    await Assert.That(processedOrder[4]).IsEqualTo(messages[4].MessageId);
  }

  // ========================================
  // Null/Empty Input Tests
  // ========================================

  [Test]
  public async Task ProcessInboxWorkAsync_WithNullList_ReturnsEarlyAsync() {
    // Arrange
    var sut = new OrderedStreamProcessor(parallelizeStreams: false);
    var processorCalled = false;

    // Act
    await sut.ProcessInboxWorkAsync(
      null!,
      processor: async _ => {
        processorCalled = true;
        return await Task.FromResult(MessageProcessingStatus.EventStored);
      },
      completionHandler: (_, _) => { },
      failureHandler: (_, _, _) => { }
    );

    // Assert - processor should never be called
    await Assert.That(processorCalled).IsFalse();
  }

  [Test]
  public async Task ProcessInboxWorkAsync_WithEmptyList_ReturnsEarlyAsync() {
    // Arrange
    var sut = new OrderedStreamProcessor(parallelizeStreams: false);
    var processorCalled = false;

    // Act
    await sut.ProcessInboxWorkAsync(
      [],
      processor: async _ => {
        processorCalled = true;
        return await Task.FromResult(MessageProcessingStatus.EventStored);
      },
      completionHandler: (_, _) => { },
      failureHandler: (_, _, _) => { }
    );

    // Assert - processor should never be called
    await Assert.That(processorCalled).IsFalse();
  }

  [Test]
  public async Task ProcessOutboxWorkAsync_WithNullList_ReturnsEarlyAsync() {
    // Arrange
    var sut = new OrderedStreamProcessor(parallelizeStreams: false);
    var processorCalled = false;

    // Act
    await sut.ProcessOutboxWorkAsync(
      null!,
      processor: async _ => {
        processorCalled = true;
        return await Task.FromResult(MessageProcessingStatus.Published);
      },
      completionHandler: (_, _) => { },
      failureHandler: (_, _, _) => { }
    );

    // Assert - processor should never be called
    await Assert.That(processorCalled).IsFalse();
  }

  [Test]
  public async Task ProcessOutboxWorkAsync_WithEmptyList_ReturnsEarlyAsync() {
    // Arrange
    var sut = new OrderedStreamProcessor(parallelizeStreams: false);
    var processorCalled = false;

    // Act
    await sut.ProcessOutboxWorkAsync(
      [],
      processor: async _ => {
        processorCalled = true;
        return await Task.FromResult(MessageProcessingStatus.Published);
      },
      completionHandler: (_, _) => { },
      failureHandler: (_, _, _) => { }
    );

    // Assert - processor should never be called
    await Assert.That(processorCalled).IsFalse();
  }

  // ========================================
  // Cancellation Tests
  // ========================================

  [Test]
  public async Task ProcessInboxWorkAsync_WithCancellation_StopsProcessingAsync() {
    // Arrange
    var sut = new OrderedStreamProcessor(parallelizeStreams: false);
    var streamId = _idProvider.NewGuid();
    var cts = new CancellationTokenSource();
    var processedCount = 0;

    var messages = new List<InboxWork> {
      _createInboxWork(streamId),
      _createInboxWork(streamId),
      _createInboxWork(streamId),
      _createInboxWork(streamId),
      _createInboxWork(streamId)
    };

    // Act
    await sut.ProcessInboxWorkAsync(
      messages,
      processor: async _ => {
        processedCount++;
        if (processedCount == 2) {
          // Cancel after processing 2 messages
          await cts.CancelAsync();
        }
        return await Task.FromResult(MessageProcessingStatus.EventStored);
      },
      completionHandler: (_, _) => { },
      failureHandler: (_, _, _) => { },
      ct: cts.Token
    );

    // Assert - should stop after cancellation
    await Assert.That(processedCount).IsLessThanOrEqualTo(3)
      .Because("Processing should stop after cancellation requested");
  }

  [Test]
  public async Task ProcessOutboxWorkAsync_WithCancellation_StopsProcessingAsync() {
    // Arrange
    var sut = new OrderedStreamProcessor(parallelizeStreams: false);
    var streamId = _idProvider.NewGuid();
    var cts = new CancellationTokenSource();
    var processedCount = 0;

    var messages = new List<OutboxWork> {
      _createOutboxWork(streamId),
      _createOutboxWork(streamId),
      _createOutboxWork(streamId),
      _createOutboxWork(streamId),
      _createOutboxWork(streamId)
    };

    // Act
    await sut.ProcessOutboxWorkAsync(
      messages,
      processor: async _ => {
        processedCount++;
        if (processedCount == 2) {
          await cts.CancelAsync();
        }
        return await Task.FromResult(MessageProcessingStatus.Published);
      },
      completionHandler: (_, _) => { },
      failureHandler: (_, _, _) => { },
      ct: cts.Token
    );

    // Assert - should stop after cancellation
    await Assert.That(processedCount).IsLessThanOrEqualTo(3)
      .Because("Processing should stop after cancellation requested");
  }

  // ========================================
  // Null Stream ID Tests
  // ========================================

  [Test]
  public async Task ProcessInboxWorkAsync_WithNullStreamId_GroupsTogetherAsync() {
    // Arrange
    var sut = new OrderedStreamProcessor(parallelizeStreams: false);
    var processedOrder = new List<Medo.Uuid7>();

    // Create messages without stream IDs (null)
    var messages = new List<InboxWork> {
      _createInboxWorkWithoutStream(),
      _createInboxWorkWithoutStream(),
      _createInboxWorkWithoutStream()
    };

    // Act
    await sut.ProcessInboxWorkAsync(
      messages,
      processor: async work => {
        processedOrder.Add(work.MessageId);
        return await Task.FromResult(MessageProcessingStatus.EventStored);
      },
      completionHandler: (_, _) => { },
      failureHandler: (_, _, _) => { }
    );

    // Assert - all should be processed (grouped into empty stream)
    await Assert.That(processedOrder).Count().IsEqualTo(3);
  }

  [Test]
  public async Task ProcessOutboxWorkAsync_WithNullStreamId_GroupsTogetherAsync() {
    // Arrange
    var sut = new OrderedStreamProcessor(parallelizeStreams: false);
    var processedOrder = new List<Medo.Uuid7>();

    // Create messages without stream IDs
    var messages = new List<OutboxWork> {
      _createOutboxWorkWithoutStream(),
      _createOutboxWorkWithoutStream(),
      _createOutboxWorkWithoutStream()
    };

    // Act
    await sut.ProcessOutboxWorkAsync(
      messages,
      processor: async work => {
        processedOrder.Add(work.MessageId);
        return await Task.FromResult(MessageProcessingStatus.Published);
      },
      completionHandler: (_, _) => { },
      failureHandler: (_, _, _) => { }
    );

    // Assert - all should be processed (grouped into empty stream)
    await Assert.That(processedOrder).Count().IsEqualTo(3);
  }

  // ========================================
  // Additional Outbox Tests
  // ========================================

  [Test]
  public async Task ProcessOutboxWorkAsync_StreamWithError_ContinuesOtherStreamsAsync() {
    // Arrange
    var sut = new OrderedStreamProcessor(parallelizeStreams: true);
    var stream1 = _idProvider.NewGuid();  // Will fail
    var stream2 = _idProvider.NewGuid();  // Should continue

    var processedMessages = new ConcurrentBag<Guid>();
    var failedMessages = new ConcurrentBag<Guid>();

    var stream1msg1 = _createOutboxWork(stream1);
    var stream1msg2 = _createOutboxWork(stream1);
    var stream2msg1 = _createOutboxWork(stream2);
    var stream2msg2 = _createOutboxWork(stream2);

    var messages = new List<OutboxWork> { stream1msg1, stream1msg2, stream2msg1, stream2msg2 };

    // Act
    await sut.ProcessOutboxWorkAsync(
      messages,
      processor: async work => {
        // Fail first message of stream1
        if (work.MessageId == stream1msg1.MessageId) {
          throw new InvalidOperationException("Simulated failure");
        }

        processedMessages.Add(work.MessageId);
        return await Task.FromResult(MessageProcessingStatus.Published);
      },
      completionHandler: (_, _) => { },
      failureHandler: (messageId, _, _) => {
        failedMessages.Add(messageId);
      }
    );

    // Assert
    await Assert.That(failedMessages).Count().IsEqualTo(1)
      .Because("Only 1 message should fail");

    await Assert.That(processedMessages).Count().IsEqualTo(2)
      .Because("Stream 2 should continue processing despite stream 1 failure");
  }

  [Test]
  public async Task ProcessOutboxWorkAsync_MultipleStreams_ProcessesConcurrentlyAsync() {
    // Arrange
    var sut = new OrderedStreamProcessor(parallelizeStreams: true);
    var stream1 = _idProvider.NewGuid();
    var stream2 = _idProvider.NewGuid();
    var stream3 = _idProvider.NewGuid();

    var processedStreams = new ConcurrentBag<Guid>();
    var processingStarted = new Dictionary<Guid, DateTimeOffset>();
    var processingCompleted = new Dictionary<Guid, DateTimeOffset>();
    var lockObj = new object();

    var messages = new List<OutboxWork> {
      _createOutboxWork(stream1),
      _createOutboxWork(stream1),
      _createOutboxWork(stream2),
      _createOutboxWork(stream2),
      _createOutboxWork(stream3),
      _createOutboxWork(stream3)
    };

    // Act
    await sut.ProcessOutboxWorkAsync(
      messages,
      processor: async work => {
        var streamId = work.StreamId!.Value;

        lock (lockObj) {
          if (!processingStarted.ContainsKey(streamId)) {
            processingStarted[streamId] = DateTimeOffset.UtcNow;
          }
        }

        processedStreams.Add(streamId);
        await Task.Delay(10);  // Simulate work

        lock (lockObj) {
          processingCompleted[streamId] = DateTimeOffset.UtcNow;
        }

        return MessageProcessingStatus.Published;
      },
      completionHandler: (_, _) => { },
      failureHandler: (_, _, _) => { }
    );

    // Assert - All 3 streams processed
    await Assert.That(processedStreams.Distinct()).Count().IsEqualTo(3);
  }

  // ========================================
  // Completion Handler Tests
  // ========================================

  [Test]
  public async Task ProcessInboxWorkAsync_OnSuccess_CallsCompletionHandlerAsync() {
    // Arrange
    var sut = new OrderedStreamProcessor(parallelizeStreams: false);
    var streamId = _idProvider.NewGuid();
    var completedMessages = new List<(Guid MessageId, MessageProcessingStatus Status)>();

    var messages = new List<InboxWork> {
      _createInboxWork(streamId),
      _createInboxWork(streamId)
    };

    // Act
    await sut.ProcessInboxWorkAsync(
      messages,
      processor: async _ => {
        return await Task.FromResult(MessageProcessingStatus.EventStored);
      },
      completionHandler: (messageId, status) => {
        completedMessages.Add((messageId, status));
      },
      failureHandler: (_, _, _) => { }
    );

    // Assert - completion handler should be called for each message
    await Assert.That(completedMessages).Count().IsEqualTo(2);
    await Assert.That(completedMessages[0].Status).IsEqualTo(MessageProcessingStatus.EventStored);
    await Assert.That(completedMessages[1].Status).IsEqualTo(MessageProcessingStatus.EventStored);
  }

  [Test]
  public async Task ProcessOutboxWorkAsync_OnSuccess_CallsCompletionHandlerAsync() {
    // Arrange
    var sut = new OrderedStreamProcessor(parallelizeStreams: false);
    var streamId = _idProvider.NewGuid();
    var completedMessages = new List<(Guid MessageId, MessageProcessingStatus Status)>();

    var messages = new List<OutboxWork> {
      _createOutboxWork(streamId),
      _createOutboxWork(streamId)
    };

    // Act
    await sut.ProcessOutboxWorkAsync(
      messages,
      processor: async _ => {
        return await Task.FromResult(MessageProcessingStatus.Published);
      },
      completionHandler: (messageId, status) => {
        completedMessages.Add((messageId, status));
      },
      failureHandler: (_, _, _) => { }
    );

    // Assert - completion handler should be called for each message
    await Assert.That(completedMessages).Count().IsEqualTo(2);
    await Assert.That(completedMessages[0].Status).IsEqualTo(MessageProcessingStatus.Published);
  }

  [Test]
  public async Task ProcessOutboxWorkAsync_OnFailure_ReportsPartialStatusAsync() {
    // Arrange
    var sut = new OrderedStreamProcessor(parallelizeStreams: false);
    var streamId = _idProvider.NewGuid();

    MessageProcessingStatus? reportedPartialStatus = null;
    string? reportedError = null;

    var message = _createOutboxWork(streamId);
    message = message with {
      Status = MessageProcessingStatus.Stored
    };

    // Act
    await sut.ProcessOutboxWorkAsync(
      [message],
      processor: async _ => {
        throw new InvalidOperationException("Publishing failed");
      },
      completionHandler: (_, _) => { },
      failureHandler: (_, partialStatus, error) => {
        reportedPartialStatus = partialStatus;
        reportedError = error;
      }
    );

    // Assert
    await Assert.That(reportedPartialStatus).IsNotNull();
    await Assert.That((reportedPartialStatus!.Value & MessageProcessingStatus.Stored) == MessageProcessingStatus.Stored).IsTrue()
      .Because("Partial completion should include Stored flag");
    await Assert.That(reportedError).Contains("Publishing failed");
  }

  // Helper methods

  private InboxWork _createInboxWorkWithoutStream() {
    var messageId = _idProvider.NewGuid();
    var envelope = _createTestEnvelope(messageId);
    return new InboxWork {
      MessageId = messageId,
      Envelope = envelope,
      MessageType = "Whizbang.Core.Tests.Messaging.TestMessage, Whizbang.Core.Tests",
      StreamId = null,  // No stream ID
      PartitionNumber = 0,
      Status = MessageProcessingStatus.Stored,
      Flags = WorkBatchFlags.None
    };
  }

  private OutboxWork _createOutboxWorkWithoutStream() {
    var messageId = _idProvider.NewGuid();
    var envelope = _createTestEnvelope(messageId);
    return new OutboxWork {
      MessageId = messageId,
      Destination = "test-topic",
      Envelope = envelope,
      EnvelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[System.Text.Json.JsonElement, System.Text.Json]], Whizbang.Core",
      MessageType = "System.Text.Json.JsonElement, System.Text.Json",
      StreamId = null,  // No stream ID
      PartitionNumber = 0,
      Attempts = 0,
      Status = MessageProcessingStatus.Stored,
      Flags = WorkBatchFlags.None
    };
  }

  private InboxWork _createInboxWork(Guid streamId) {
    var messageId = _idProvider.NewGuid();  // UUIDv7 with temporal ordering
    var envelope = _createTestEnvelope(messageId);
    return new InboxWork {
      MessageId = messageId,
      Envelope = envelope,
      MessageType = "Whizbang.Core.Tests.Messaging.TestMessage, Whizbang.Core.Tests",
      StreamId = streamId,
      PartitionNumber = 0,
      Status = MessageProcessingStatus.Stored,
      Flags = WorkBatchFlags.None
    };
  }

  private OutboxWork _createOutboxWork(Guid streamId) {
    var messageId = _idProvider.NewGuid();  // UUIDv7 with temporal ordering
    var envelope = _createTestEnvelope(messageId);
    return new OutboxWork {
      MessageId = messageId,
      Destination = "test-topic",
      Envelope = envelope,
      EnvelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[System.Text.Json.JsonElement, System.Text.Json]], Whizbang.Core",
      MessageType = "System.Text.Json.JsonElement, System.Text.Json",
      StreamId = streamId,
      PartitionNumber = 0,
      Attempts = 0,
      Status = MessageProcessingStatus.Stored,
      Flags = WorkBatchFlags.None
    };
  }

  private static TestMessageEnvelope _createTestEnvelope(Guid messageId) {
    return new TestMessageEnvelope {
      MessageId = MessageId.From(messageId),
      Hops = []
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
