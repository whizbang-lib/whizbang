using System.Text.Json;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Security;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Coverage round 23 tail: the sequential (non-parallel) branches' pre-stream cancellation checks
/// in <see cref="OrderedStreamProcessor.ProcessInboxWorkAsync"/> and
/// <see cref="OrderedStreamProcessor.ProcessOutboxWorkAsync"/>. Every existing test either passes
/// a live token or an empty work list; none passes a cancelled token alongside real work, so the
/// <c>break</c> that stops the stream loop before touching the first stream batch has never run.
/// </summary>
[Category("Messaging")]
public class OrderedStreamProcessorCoverageTests {
  private readonly Uuid7IdProvider _idProvider = new();

  /// <summary>
  /// Operator impact: a shutdown-in-progress token handed to the sequential path must stop the
  /// processor from starting ANY stream, not just from finishing all of them — otherwise a
  /// graceful-shutdown caller would see partial, non-deterministic processing depending on how
  /// many streams happened to exist that tick.
  /// </summary>
  [Test]
  public async Task ProcessInboxWorkAsync_CancelledBeforeStart_ProcessesNoStreamsAsync() {
    var sut = new OrderedStreamProcessor(parallelizeStreams: false);
    var messages = new List<InboxWork> {
      _createInboxWork(_idProvider.NewGuid()),
      _createInboxWork(_idProvider.NewGuid()),
    };
    var processedCount = 0;
    using var cts = new CancellationTokenSource();
    await cts.CancelAsync();

    await sut.ProcessInboxWorkAsync(
      messages,
      processor: async work => {
        Interlocked.Increment(ref processedCount);
        return await Task.FromResult(MessageProcessingStatus.EventStored);
      },
      completionHandler: (_, _) => { },
      failureHandler: (_, _, _) => { },
      ct: cts.Token);

    await Assert.That(processedCount).IsEqualTo(0)
      .Because("a token already cancelled before the sequential loop starts must stop the "
             + "processor before it enters the first stream batch — work items exist (this isn't "
             + "the empty-list early return), so reaching zero processed proves the cancellation "
             + "check, not the null/empty guard, is what stopped it");
  }

  /// <summary>
  /// Operator impact: same guarantee as the inbox path, mirrored for outbox publishing — a
  /// shutdown token must not let even one more stream start publishing.
  /// </summary>
  [Test]
  public async Task ProcessOutboxWorkAsync_CancelledBeforeStart_ProcessesNoStreamsAsync() {
    var sut = new OrderedStreamProcessor(parallelizeStreams: false);
    var messages = new List<OutboxWork> {
      _createOutboxWork(_idProvider.NewGuid()),
      _createOutboxWork(_idProvider.NewGuid()),
    };
    var processedCount = 0;
    using var cts = new CancellationTokenSource();
    await cts.CancelAsync();

    await sut.ProcessOutboxWorkAsync(
      messages,
      processor: async work => {
        Interlocked.Increment(ref processedCount);
        return await Task.FromResult(MessageProcessingStatus.Published);
      },
      completionHandler: (_, _) => { },
      failureHandler: (_, _, _) => { },
      ct: cts.Token);

    await Assert.That(processedCount).IsEqualTo(0)
      .Because("a token already cancelled before the sequential outbox loop starts must stop the "
             + "processor before it enters the first stream batch, mirroring the inbox guarantee");
  }

  // Helper methods

  private InboxWork _createInboxWork(Guid streamId) {
    var messageId = _idProvider.NewGuid();
    var envelope = _createTestEnvelope(messageId);
    return new InboxWork {
      MessageId = messageId,
      Envelope = envelope,
      MessageType = "Whizbang.Core.Tests.Messaging.TestMessage, Whizbang.Core.Tests",
      StreamId = streamId,
      PartitionNumber = 0,
      Status = MessageProcessingStatus.Stored,
      Flags = WorkBatchOptions.None
    };
  }

  private OutboxWork _createOutboxWork(Guid streamId) {
    var messageId = _idProvider.NewGuid();
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
      Flags = WorkBatchOptions.None
    };
  }

  private static TestMessageEnvelope _createTestEnvelope(Guid messageId) {
    return new TestMessageEnvelope {
      MessageId = MessageId.From(messageId),
      Hops = []
    };
  }

  // Minimal test envelope implementation — mirrors OrderedStreamProcessorTests.cs's own private
  // envelope, duplicated here because coverage files stay self-contained (no shared private state
  // across sibling *Tests.cs files).
  private sealed class TestMessageEnvelope : IMessageEnvelope<JsonElement> {
    public int Version => 1;
    public MessageDispatchContext DispatchContext { get; } = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local };
    public required MessageId MessageId { get; init; }
    public required List<MessageHop> Hops { get; init; }
    public JsonElement Payload { get; init; } = JsonDocument.Parse("{}").RootElement;
    object IMessageEnvelope.Payload => Payload;

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

    public SecurityContext? GetCurrentSecurityContext() => null;
    public ScopeContext? GetCurrentScope() => null;
  }
}
