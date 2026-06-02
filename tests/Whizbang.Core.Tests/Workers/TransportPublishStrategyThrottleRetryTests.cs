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

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Behavioral regression locks for the in-memory throttle-retry loop in
/// <see cref="TransportPublishStrategy"/>. Verifies that broker-side throttle signals are
/// retried in-memory (within the same lease) instead of bouncing back to the failure
/// channel — eliminates the 5-minute orphan-reclaim gap on transient throttling and
/// prevents premature dead-lettering on sustained pressure.
/// </summary>
/// <remarks>
/// All tests use a fast <see cref="ThrottleRetryOptions"/> (1 ms base delay) so the suite
/// stays under hundreds of milliseconds even with 5-attempt budgets.
/// </remarks>
public class TransportPublishStrategyThrottleRetryTests {

  private static ThrottleRetryOptions _fastOpts(int maxAttempts = 5) => new() {
    MaxAttempts = maxAttempts,
    BaseDelay = TimeSpan.FromMilliseconds(1),
    BackoffMultiplier = 1.0,  // flat — keep tests fast
    MaxDelay = TimeSpan.FromMilliseconds(5),
  };

  private static OutboxWork _work(Guid? messageId = null) {
    var id = messageId ?? Guid.CreateVersion7();
    return new OutboxWork {
      MessageId = id,
      Destination = "test-topic",
      Envelope = new MessageEnvelope<JsonElement>(
        messageId: MessageId.From(id),
        payload: JsonDocument.Parse("{}").RootElement,
        hops: []),
      EnvelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[System.Text.Json.JsonElement, System.Text.Json]], Whizbang.Core",
      MessageType = "System.Text.Json.JsonElement, System.Text.Json",
      StreamId = Guid.CreateVersion7(),
      PartitionNumber = 1,
      Attempts = 0,
      Status = MessageProcessingStatus.Stored,
      Flags = WorkBatchOptions.None,
    };
  }

  // Fake ASB transport. Throws a ServiceBusy-style exception for the first N calls, then
  // succeeds. PublishAsync calls are counted so the test can assert "eventually published
  // after N throttles."
  private sealed class ThrottleNTimesTransport(int throttleCount) : ITransport {
    private int _calls;
    public int PublishCalls => _calls;
    public bool IsInitialized => true;
    public TransportCapabilities Capabilities => new();

    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task PublishAsync(IMessageEnvelope envelope, TransportDestination destination, string? envelopeType = null, CancellationToken cancellationToken = default) {
      var n = Interlocked.Increment(ref _calls);
      if (n <= throttleCount) {
        throw new Azure.Messaging.ServiceBus.ServiceBusException(
          "namespace is being throttled. Error code : 50009. (ServiceBusy)");
      }
      return Task.CompletedTask;
    }

    public Task<ISubscription> SubscribeAsync(Func<IMessageEnvelope, string?, CancellationToken, Task> handler, TransportDestination destination, CancellationToken cancellationToken = default)
      => throw new NotSupportedException();
    public Task<ISubscription> SubscribeBatchAsync(Func<IReadOnlyList<TransportMessage>, CancellationToken, Task> batchHandler, TransportDestination destination, TransportBatchOptions batchOptions, CancellationToken cancellationToken = default)
      => throw new NotSupportedException();
    public Task<IMessageEnvelope> SendAsync<TRequest, TResponse>(IMessageEnvelope requestEnvelope, TransportDestination destination, CancellationToken cancellationToken = default)
      where TRequest : notnull where TResponse : notnull => throw new NotSupportedException();
  }

  [Test]
  public async Task PublishAsync_OneThrottleThenSuccess_SucceedsAfterOneRetryAsync() {
    var transport = new ThrottleNTimesTransport(throttleCount: 1);
    var strategy = new TransportPublishStrategy(
      transport, new DefaultTransportReadinessCheck(), "inbox",
      loggerFactory: null,
      throttleRetryOptions: _fastOpts());

    var result = await strategy.PublishAsync(_work(), CancellationToken.None);

    await Assert.That(result.Success).IsTrue()
      .Because("after one throttle + retry, the publish should succeed");
    await Assert.That(transport.PublishCalls).IsEqualTo(2)
      .Because("one throttled call + one successful retry");
  }

  [Test]
  public async Task PublishAsync_FourThrottlesThenSuccess_SucceedsWithinDefaultBudgetAsync() {
    var transport = new ThrottleNTimesTransport(throttleCount: 4);
    var strategy = new TransportPublishStrategy(
      transport, new DefaultTransportReadinessCheck(), "inbox",
      loggerFactory: null,
      throttleRetryOptions: _fastOpts(maxAttempts: 5));

    var result = await strategy.PublishAsync(_work(), CancellationToken.None);

    await Assert.That(result.Success).IsTrue()
      .Because("4 throttles + 5th success = within the 5-attempt budget");
    await Assert.That(transport.PublishCalls).IsEqualTo(5);
  }

  [Test]
  public async Task PublishAsync_AllAttemptsThrottled_ReturnsThrottledReasonAsync() {
    // Throttle 5 times → all 5 attempts exhausted → return Reason=Throttled to caller.
    var transport = new ThrottleNTimesTransport(throttleCount: 100);  // never succeeds
    var strategy = new TransportPublishStrategy(
      transport, new DefaultTransportReadinessCheck(), "inbox",
      loggerFactory: null,
      throttleRetryOptions: _fastOpts(maxAttempts: 5));

    var result = await strategy.PublishAsync(_work(), CancellationToken.None);

    await Assert.That(result.Success).IsFalse();
    await Assert.That(result.Reason).IsEqualTo(MessageFailureReason.Throttled);
    await Assert.That(transport.PublishCalls).IsEqualTo(5)
      .Because("budget=5 → exactly 5 attempts before giving up");
  }

  [Test]
  public async Task PublishAsync_NonThrottleException_NotRetriedAsync() {
    // Generic exception is NOT throttling — should not retry, should return immediately
    // with Reason=Unknown (or TransportException if matched).
    var calls = 0;
    var transport = new ExceptionTransport(() => {
      Interlocked.Increment(ref calls);
      throw new InvalidOperationException("boom");
    });
    var strategy = new TransportPublishStrategy(
      transport, new DefaultTransportReadinessCheck(), "inbox",
      loggerFactory: null,
      throttleRetryOptions: _fastOpts(maxAttempts: 5));

    var result = await strategy.PublishAsync(_work(), CancellationToken.None);

    await Assert.That(result.Success).IsFalse();
    await Assert.That(result.Reason).IsEqualTo(MessageFailureReason.Unknown)
      .Because("InvalidOperationException is not transport-related");
    await Assert.That(calls).IsEqualTo(1)
      .Because("non-throttle failures bypass retry budget");
  }

  [Test]
  public async Task PublishAsync_TransportExceptionNotThrottle_NotRetriedAsync() {
    // A transport-namespaced exception that ISN'T ServiceBusy → should classify as
    // TransportException and NOT retry. Verifies we don't accidentally retry on outages.
    var calls = 0;
    var transport = new ExceptionTransport(() => {
      Interlocked.Increment(ref calls);
      throw new Azure.Messaging.ServiceBus.ServiceBusException("connection lost");
    });
    var strategy = new TransportPublishStrategy(
      transport, new DefaultTransportReadinessCheck(), "inbox",
      loggerFactory: null,
      throttleRetryOptions: _fastOpts(maxAttempts: 5));

    var result = await strategy.PublishAsync(_work(), CancellationToken.None);

    await Assert.That(result.Success).IsFalse();
    await Assert.That(result.Reason).IsEqualTo(MessageFailureReason.TransportException);
    await Assert.That(calls).IsEqualTo(1)
      .Because("outage-like transport exceptions should NOT consume the retry budget");
  }

  [Test]
  public async Task PublishAsync_RabbitMqFlowControl_RetriedAsync() {
    var calls = 0;
    Exception? next = new RabbitMQ.Client.OperationInterruptedException("publisher flow-control nack");
    var transport = new ExceptionTransport(() => {
      var n = Interlocked.Increment(ref calls);
      if (n == 1) {
        throw next!;
      }
      next = null;
      // succeed on the 2nd call
    });
    var strategy = new TransportPublishStrategy(
      transport, new DefaultTransportReadinessCheck(), "inbox",
      loggerFactory: null,
      throttleRetryOptions: _fastOpts());

    var result = await strategy.PublishAsync(_work(), CancellationToken.None);

    await Assert.That(result.Success).IsTrue()
      .Because("RabbitMQ flow-control is classified as Throttled and retried");
    await Assert.That(calls).IsEqualTo(2);
  }

  [Test]
  public async Task PublishAsync_RespectsMaxAttemptsOptionAsync() {
    // Budget=3 → exactly 3 attempts before giving up
    var transport = new ThrottleNTimesTransport(throttleCount: 100);
    var strategy = new TransportPublishStrategy(
      transport, new DefaultTransportReadinessCheck(), "inbox",
      loggerFactory: null,
      throttleRetryOptions: _fastOpts(maxAttempts: 3));

    var result = await strategy.PublishAsync(_work(), CancellationToken.None);

    await Assert.That(result.Success).IsFalse();
    await Assert.That(result.Reason).IsEqualTo(MessageFailureReason.Throttled);
    await Assert.That(transport.PublishCalls).IsEqualTo(3);
  }

  [Test]
  public async Task PublishAsync_SuccessFirstTry_NoSleepAsync() {
    var transport = new ThrottleNTimesTransport(throttleCount: 0);
    var sw = System.Diagnostics.Stopwatch.StartNew();
    var strategy = new TransportPublishStrategy(
      transport, new DefaultTransportReadinessCheck(), "inbox",
      loggerFactory: null,
      throttleRetryOptions: _fastOpts());

    var result = await strategy.PublishAsync(_work(), CancellationToken.None);
    sw.Stop();

    await Assert.That(result.Success).IsTrue();
    await Assert.That(transport.PublishCalls).IsEqualTo(1)
      .Because("first attempt succeeded — no retry, no sleep");
  }

  // Generic exception-throwing transport.
  private sealed class ExceptionTransport(Action throwOrSucceed) : ITransport {
    public bool IsInitialized => true;
    public TransportCapabilities Capabilities => new();
    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task PublishAsync(IMessageEnvelope envelope, TransportDestination destination, string? envelopeType = null, CancellationToken cancellationToken = default) {
      throwOrSucceed();
      return Task.CompletedTask;
    }
    public Task<ISubscription> SubscribeAsync(Func<IMessageEnvelope, string?, CancellationToken, Task> handler, TransportDestination destination, CancellationToken cancellationToken = default)
      => throw new NotSupportedException();
    public Task<ISubscription> SubscribeBatchAsync(Func<IReadOnlyList<TransportMessage>, CancellationToken, Task> batchHandler, TransportDestination destination, TransportBatchOptions batchOptions, CancellationToken cancellationToken = default)
      => throw new NotSupportedException();
    public Task<IMessageEnvelope> SendAsync<TRequest, TResponse>(IMessageEnvelope requestEnvelope, TransportDestination destination, CancellationToken cancellationToken = default)
      where TRequest : notnull where TResponse : notnull => throw new NotSupportedException();
  }

  // ---------- Bulk publish retry tests ----------

  private sealed class BulkThrottleNTimesTransport(int batchThrottleCount) : ITransport {
    private int _batchCalls;
    public int BatchCalls => _batchCalls;
    public bool IsInitialized => true;
    public TransportCapabilities Capabilities => TransportCapabilities.BulkPublish;

    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task PublishAsync(IMessageEnvelope envelope, TransportDestination destination, string? envelopeType = null, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<IReadOnlyList<BulkPublishItemResult>> PublishBatchAsync(IReadOnlyList<BulkPublishItem> items, TransportDestination destination, CancellationToken cancellationToken = default) {
      var n = Interlocked.Increment(ref _batchCalls);
      if (n <= batchThrottleCount) {
        throw new Azure.Messaging.ServiceBus.ServiceBusException(
          "batch send terminated; namespace is being throttled. Error code : 50009. (ServiceBusy)");
      }
      return Task.FromResult<IReadOnlyList<BulkPublishItemResult>>(
        [.. items.Select(i => new BulkPublishItemResult { MessageId = i.MessageId, Success = true })]);
    }

    public Task<ISubscription> SubscribeAsync(Func<IMessageEnvelope, string?, CancellationToken, Task> handler, TransportDestination destination, CancellationToken cancellationToken = default)
      => throw new NotSupportedException();
    public Task<ISubscription> SubscribeBatchAsync(Func<IReadOnlyList<TransportMessage>, CancellationToken, Task> batchHandler, TransportDestination destination, TransportBatchOptions batchOptions, CancellationToken cancellationToken = default)
      => throw new NotSupportedException();
    public Task<IMessageEnvelope> SendAsync<TRequest, TResponse>(IMessageEnvelope requestEnvelope, TransportDestination destination, CancellationToken cancellationToken = default)
      where TRequest : notnull where TResponse : notnull => throw new NotSupportedException();
  }

  [Test]
  public async Task PublishBatchAsync_TwoThrottlesThenSuccess_EveryItemSucceedsAsync() {
    var transport = new BulkThrottleNTimesTransport(batchThrottleCount: 2);
    var strategy = new TransportPublishStrategy(
      transport, new DefaultTransportReadinessCheck(), "inbox",
      loggerFactory: null,
      throttleRetryOptions: _fastOpts());

    var streamId = Guid.CreateVersion7();
    var works = Enumerable.Range(0, 5).Select(_ => _work() with { StreamId = streamId }).ToList();
    var results = await strategy.PublishBatchAsync(works, CancellationToken.None);

    await Assert.That(results.Count).IsEqualTo(5);
    await Assert.That(results.All(r => r.Success)).IsTrue()
      .Because("after the batch finally goes through, every item is published");
    await Assert.That(transport.BatchCalls).IsEqualTo(3)
      .Because("2 batch-throttles + 1 successful batch retry = 3 calls");
  }

  [Test]
  public async Task PublishBatchAsync_AllAttemptsThrottled_AllItemsThrottledAsync() {
    var transport = new BulkThrottleNTimesTransport(batchThrottleCount: 100);
    var strategy = new TransportPublishStrategy(
      transport, new DefaultTransportReadinessCheck(), "inbox",
      loggerFactory: null,
      throttleRetryOptions: _fastOpts(maxAttempts: 5));

    var streamId = Guid.CreateVersion7();
    var works = Enumerable.Range(0, 3).Select(_ => _work() with { StreamId = streamId }).ToList();
    var results = await strategy.PublishBatchAsync(works, CancellationToken.None);

    await Assert.That(results.Count).IsEqualTo(3);
    await Assert.That(results.All(r => !r.Success)).IsTrue();
    await Assert.That(results.All(r => r.Reason == MessageFailureReason.Throttled)).IsTrue()
      .Because("uniform throttle on batch → every item gets Throttled reason");
    await Assert.That(transport.BatchCalls).IsEqualTo(5)
      .Because("budget=5 → exactly 5 batch attempts");
  }
}
