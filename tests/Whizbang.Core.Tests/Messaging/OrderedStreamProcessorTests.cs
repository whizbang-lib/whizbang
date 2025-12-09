using System.Collections.Concurrent;
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
  private readonly IWhizbangIdProvider _idProvider = new Uuid7IdProvider();

  // ========================================
  // Priority 2 Tests: Stream Ordering (Inbox)
  // ========================================

  [Test]
  public async Task ProcessInboxWorkAsync_SingleStream_ProcessesInOrderAsync() {
    // Arrange
    var sut = new OrderedStreamProcessor(parallelizeStreams: false);
    var streamId = _idProvider.NewGuid();
    var processedOrder = new List<long>();

    // Create 5 messages with different SequenceOrder
    var messages = new List<InboxWork> {
      CreateInboxWork(streamId, sequenceOrder: 100),
      CreateInboxWork(streamId, sequenceOrder: 300),
      CreateInboxWork(streamId, sequenceOrder: 200),
      CreateInboxWork(streamId, sequenceOrder: 500),
      CreateInboxWork(streamId, sequenceOrder: 400)
    };

    // Act
    await sut.ProcessInboxWorkAsync(
      messages,
      processor: async work => {
        processedOrder.Add(work.SequenceOrder);
        return await Task.FromResult(MessageProcessingStatus.ReceptorProcessed);
      },
      completionHandler: (_, _) => { },
      failureHandler: (_, _, _) => { }
    );

    // Assert - Should process in SequenceOrder ascending order
    await Assert.That(processedOrder).HasCount().EqualTo(5);
    await Assert.That(processedOrder[0]).IsEqualTo(100L);
    await Assert.That(processedOrder[1]).IsEqualTo(200L);
    await Assert.That(processedOrder[2]).IsEqualTo(300L);
    await Assert.That(processedOrder[3]).IsEqualTo(400L);
    await Assert.That(processedOrder[4]).IsEqualTo(500L);
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
      CreateInboxWork(stream1, sequenceOrder: 100),
      CreateInboxWork(stream1, sequenceOrder: 200),
      CreateInboxWork(stream2, sequenceOrder: 100),
      CreateInboxWork(stream2, sequenceOrder: 200),
      CreateInboxWork(stream3, sequenceOrder: 100),
      CreateInboxWork(stream3, sequenceOrder: 200)
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

        return MessageProcessingStatus.ReceptorProcessed;
      },
      completionHandler: (_, _) => { },
      failureHandler: (_, _, _) => { }
    );

    // Assert - All 3 streams processed
    await Assert.That(processedStreams.Distinct()).HasCount().EqualTo(3);
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

    // Create messages from 2 streams
    var messages = new List<InboxWork> {
      CreateInboxWork(stream1, sequenceOrder: 100),
      CreateInboxWork(stream1, sequenceOrder: 200),  // Won't be processed (stream stopped)
      CreateInboxWork(stream2, sequenceOrder: 100),
      CreateInboxWork(stream2, sequenceOrder: 200)
    };

    // Act
    await sut.ProcessInboxWorkAsync(
      messages,
      processor: async work => {
        // Fail first message of stream1
        if (work.StreamId == stream1 && work.SequenceOrder == 100) {
          throw new InvalidOperationException("Simulated failure");
        }

        processedMessages.Add(work.MessageId);
        return await Task.FromResult(MessageProcessingStatus.ReceptorProcessed);
      },
      completionHandler: (_, _) => { },
      failureHandler: (messageId, _, _) => {
        failedMessages.Add(messageId);
      }
    );

    // Assert
    await Assert.That(failedMessages).HasCount().EqualTo(1)
      .Because("Only 1 message should fail");

    await Assert.That(processedMessages).HasCount().EqualTo(2)
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

    // Create work item with partial completion status
    var message = CreateInboxWork(streamId, sequenceOrder: 100);
    message = message with {
      Status = MessageProcessingStatus.Stored | MessageProcessingStatus.EventStored
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
    await Assert.That((reportedPartialStatus.Value & MessageProcessingStatus.EventStored) == MessageProcessingStatus.EventStored).IsTrue()
      .Because("Partial completion should include EventStored flag");
    await Assert.That((reportedPartialStatus.Value & MessageProcessingStatus.ReceptorProcessed) != MessageProcessingStatus.ReceptorProcessed).IsTrue()
      .Because("Partial completion should NOT include ReceptorProcessed flag (this is where it failed)");
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
    var processedOrder = new List<long>();

    // Create 5 messages with different SequenceOrder
    var messages = new List<OutboxWork> {
      CreateOutboxWork(streamId, sequenceOrder: 100),
      CreateOutboxWork(streamId, sequenceOrder: 300),
      CreateOutboxWork(streamId, sequenceOrder: 200),
      CreateOutboxWork(streamId, sequenceOrder: 500),
      CreateOutboxWork(streamId, sequenceOrder: 400)
    };

    // Act
    await sut.ProcessOutboxWorkAsync(
      messages,
      processor: async work => {
        processedOrder.Add(work.SequenceOrder);
        return await Task.FromResult(MessageProcessingStatus.Published);
      },
      completionHandler: (_, _) => { },
      failureHandler: (_, _, _) => { }
    );

    // Assert - Should process in SequenceOrder ascending order
    await Assert.That(processedOrder).HasCount().EqualTo(5);
    await Assert.That(processedOrder[0]).IsEqualTo(100L);
    await Assert.That(processedOrder[1]).IsEqualTo(200L);
    await Assert.That(processedOrder[2]).IsEqualTo(300L);
    await Assert.That(processedOrder[3]).IsEqualTo(400L);
    await Assert.That(processedOrder[4]).IsEqualTo(500L);
  }

  // Helper methods

  private InboxWork CreateInboxWork(Guid streamId, long sequenceOrder) {
    var messageId = _idProvider.NewGuid();
    var envelope = CreateTestEnvelope(messageId);
    return new InboxWork {
      MessageId = messageId,
      Envelope = envelope,
      StreamId = streamId,
      PartitionNumber = 0,
      Status = MessageProcessingStatus.Stored,
      Flags = WorkBatchFlags.None,
      SequenceOrder = sequenceOrder
    };
  }

  private OutboxWork CreateOutboxWork(Guid streamId, long sequenceOrder) {
    var messageId = _idProvider.NewGuid();
    var envelope = CreateTestEnvelope(messageId);
    return new OutboxWork {
      MessageId = messageId,
      Destination = "test-topic",
      Envelope = envelope,
      StreamId = streamId,
      PartitionNumber = 0,
      Attempts = 0,
      Status = MessageProcessingStatus.Stored,
      Flags = WorkBatchFlags.None,
      SequenceOrder = sequenceOrder
    };
  }

  private static TestMessageEnvelope CreateTestEnvelope(Guid messageId) {
    return new TestMessageEnvelope {
      MessageId = MessageId.From(messageId),
      Hops = []
    };
  }

  // Test envelope implementation
  private class TestMessageEnvelope : IMessageEnvelope {
    public required MessageId MessageId { get; init; }
    public required List<MessageHop> Hops { get; init; }

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

    public object? GetMetadata(string key) {
      for (var i = Hops.Count - 1; i >= 0; i--) {
        if (Hops[i].Type == HopType.Current && Hops[i].Metadata?.ContainsKey(key) == true) {
          return Hops[i].Metadata[key];
        }
      }
      return null;
    }

    public object GetPayload() {
      return new { };
    }
  }
}
