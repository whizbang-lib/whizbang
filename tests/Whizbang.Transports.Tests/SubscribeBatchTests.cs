using System.Text.Json;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Transports;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Transports.Tests;

/// <summary>
/// Tests for ITransport.SubscribeBatchAsync and TransportMessage.
/// Defines the contract that all transports must follow for batch subscribe.
/// TDD RED phase: these tests fail until the interface and implementations are added.
/// </summary>
public class SubscribeBatchTests {

  // ========================================
  // TransportMessage record struct
  // ========================================

  [Test]
  public async Task TransportMessage_IsValueTypeAsync() {
    // TransportMessage should be a readonly record struct (no heap allocation per message)
    var msg = new TransportMessage(_createTestEnvelope(), "TestEnvelopeType");

    await Assert.That(msg.GetType().IsValueType).IsTrue()
      .Because("TransportMessage should be a value type for batch performance");
  }

  [Test]
  public async Task TransportMessage_CarriesEnvelopeAndTypeAsync() {
    var envelope = _createTestEnvelope();
    const string envelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[Test, Test]], Whizbang.Core";

    var msg = new TransportMessage(envelope, envelopeType);

    await Assert.That(msg.Envelope).IsEqualTo(envelope);
    await Assert.That(msg.EnvelopeType).IsEqualTo(envelopeType);
  }

  [Test]
  public async Task TransportMessage_EnvelopeType_CanBeNullAsync() {
    var envelope = _createTestEnvelope();
    var msg = new TransportMessage(envelope, null);

    await Assert.That(msg.EnvelopeType).IsNull();
  }

  // ========================================
  // SubscribeBatchAsync — InProcessTransport
  // ========================================

  [Test]
  public async Task SubscribeBatchAsync_ReturnsActiveSubscriptionAsync() {
    // Arrange
    var transport = new InProcessTransport();
    var destination = new TransportDestination("test-topic");
    var batchOptions = new TransportBatchOptions { BatchSize = 10, SlideMs = 50, MaxWaitMs = 1000 };

    // Act
    var subscription = await transport.SubscribeBatchAsync(
      (batch, ct) => Task.CompletedTask,
      destination,
      batchOptions
    );

    // Assert
    await Assert.That(subscription).IsNotNull();
    await Assert.That(subscription.IsActive).IsTrue();

    subscription.Dispose();
  }

  [Test]
  public async Task SubscribeBatchAsync_BatchHandlerReceivesPublishedMessagesAsync() {
    // Arrange
    var transport = new InProcessTransport();
    var destination = new TransportDestination("test-topic");
    var batchOptions = new TransportBatchOptions { BatchSize = 3, SlideMs = 5000, MaxWaitMs = 10000 };
    var receivedBatches = new List<IReadOnlyList<TransportMessage>>();
    var batchReceived = new TaskCompletionSource();

    var subscription = await transport.SubscribeBatchAsync(
      (batch, ct) => {
        receivedBatches.Add(batch);
        batchReceived.TrySetResult();
        return Task.CompletedTask;
      },
      destination,
      batchOptions
    );

    // Act — publish exactly batchSize messages
    var envelope1 = _createTestEnvelope();
    var envelope2 = _createTestEnvelope();
    var envelope3 = _createTestEnvelope();
    await transport.PublishAsync(envelope1, destination, "Type1");
    await transport.PublishAsync(envelope2, destination, "Type2");
    await transport.PublishAsync(envelope3, destination, "Type3");

    await batchReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));

    // Assert — batch handler received all 3 messages
    await Assert.That(receivedBatches).Count().IsEqualTo(1);
    await Assert.That(receivedBatches[0]).Count().IsEqualTo(3);
    await Assert.That(receivedBatches[0][0].Envelope.MessageId).IsEqualTo(envelope1.MessageId);
    await Assert.That(receivedBatches[0][1].Envelope.MessageId).IsEqualTo(envelope2.MessageId);
    await Assert.That(receivedBatches[0][2].Envelope.MessageId).IsEqualTo(envelope3.MessageId);

    subscription.Dispose();
  }

  [Test]
  public async Task SubscribeBatchAsync_SlideWindowFlushesPartialBatchAsync() {
    // Arrange
    var transport = new InProcessTransport();
    var destination = new TransportDestination("test-topic");
    var batchOptions = new TransportBatchOptions { BatchSize = 100, SlideMs = 50, MaxWaitMs = 10000 };
    var receivedBatches = new List<IReadOnlyList<TransportMessage>>();
    var batchReceived = new TaskCompletionSource();

    var subscription = await transport.SubscribeBatchAsync(
      (batch, ct) => {
        receivedBatches.Add(batch);
        batchReceived.TrySetResult();
        return Task.CompletedTask;
      },
      destination,
      batchOptions
    );

    // Act — publish fewer than batchSize, let slide window flush
    await transport.PublishAsync(_createTestEnvelope(), destination, "Type1");
    await transport.PublishAsync(_createTestEnvelope(), destination, "Type2");

    await batchReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));

    // Assert — partial batch flushed after slide window
    await Assert.That(receivedBatches).Count().IsEqualTo(1);
    await Assert.That(receivedBatches[0]).Count().IsEqualTo(2);

    subscription.Dispose();
  }

  [Test]
  public async Task SubscribeBatchAsync_WithCancelledToken_ThrowsAsync() {
    // Arrange
    var transport = new InProcessTransport();
    var destination = new TransportDestination("test-topic");
    var batchOptions = new TransportBatchOptions();
    using var cts = new CancellationTokenSource();
    cts.Cancel();

    // Act & Assert
    await Assert.ThrowsAsync<OperationCanceledException>(async () =>
      await transport.SubscribeBatchAsync(
        (batch, ct) => Task.CompletedTask,
        destination,
        batchOptions,
        cts.Token
      )
    );
  }

  [Test]
  public async Task SubscribeBatchAsync_DisposedSubscription_StopsReceivingAsync() {
    // Arrange
    var transport = new InProcessTransport();
    var destination = new TransportDestination("test-topic");
    var batchOptions = new TransportBatchOptions { BatchSize = 1, SlideMs = 5000, MaxWaitMs = 10000 };
    var batchCount = 0;

    var subscription = await transport.SubscribeBatchAsync(
      (batch, ct) => {
        Interlocked.Increment(ref batchCount);
        return Task.CompletedTask;
      },
      destination,
      batchOptions
    );

    // Act — publish one message, then dispose, then publish another
    await transport.PublishAsync(_createTestEnvelope(), destination, "Type1");
    await Task.Delay(100); // Let batch flush
    subscription.Dispose();

    var countAfterDispose = batchCount;
    await transport.PublishAsync(_createTestEnvelope(), destination, "Type2");
    await Task.Delay(100);

    // Assert — no new batches after dispose
    await Assert.That(batchCount).IsEqualTo(countAfterDispose)
      .Because("Disposed subscription should not receive more messages");
  }

  [Test]
  public async Task SubscribeBatchAsync_BatchHandlerError_DoesNotLoseMessagesAsync() {
    // Arrange
    var transport = new InProcessTransport();
    var destination = new TransportDestination("test-topic");
    var batchOptions = new TransportBatchOptions { BatchSize = 1, SlideMs = 20, MaxWaitMs = 1000 };
    var callCount = 0;
    var secondCallReceived = new TaskCompletionSource();

    var subscription = await transport.SubscribeBatchAsync(
      (batch, ct) => {
        var count = Interlocked.Increment(ref callCount);
        if (count == 1) {
          throw new InvalidOperationException("Simulated handler failure");
        }
        // Second call should receive the retried batch
        secondCallReceived.TrySetResult();
        return Task.CompletedTask;
      },
      destination,
      batchOptions
    );

    // Act
    await transport.PublishAsync(_createTestEnvelope(), destination, "Type1");

    await secondCallReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));

    // Assert — handler was called at least twice (initial + retry)
    await Assert.That(callCount).IsGreaterThanOrEqualTo(2)
      .Because("Failed batch should be re-queued and retried");

    subscription.Dispose();
  }

  // ========================================
  // SubscribeAsync removed from interface
  // ========================================

  [Test]
  public async Task ITransport_DoesNotHaveSubscribeAsyncMethodAsync() {
    // SubscribeAsync should be replaced by SubscribeBatchAsync
    var methods = typeof(ITransport).GetMethods();
    var hasSubscribeAsync = methods.Any(m => m.Name == "SubscribeAsync");

    await Assert.That(hasSubscribeAsync).IsFalse()
      .Because("SubscribeAsync should be replaced by SubscribeBatchAsync on ITransport");
  }

  // ========================================
  // Helpers
  // ========================================

  private static MessageEnvelope<TestPayload> _createTestEnvelope() {
    return new MessageEnvelope<TestPayload> {
      MessageId = MessageId.New(),
      Payload = new TestPayload { Value = "test" },
      Hops = [
        new MessageHop {
          Type = HopType.Current,
          Timestamp = DateTimeOffset.UtcNow,
          ServiceInstance = ServiceInstanceInfo.Unknown
        }
      ],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };
  }

  private sealed record TestPayload {
    public string Value { get; init; } = string.Empty;
  }
}
