using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.Logging;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Offloads;
using Whizbang.Core.Routing;
using Whizbang.Core.Transports;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Coverage-round-23 targets in <see cref="TransportPublishStrategy"/>: the transport-tag switch
/// that names the "transport" OTEL dimension on throttle telemetry/logging (asb/rabbitmq/inmemory),
/// the in-memory retry loop's behavior when a throttle is immediately followed by a genuinely
/// different failure, the runtime message-kind classifier's defensive null-type-name guard, and
/// the post-serialize hook chain's destination-metadata merge loop.
/// </summary>
[Category("Workers")]
public class TransportPublishStrategyCoverageTests {

  private static ThrottleRetryOptions _fastOpts(int maxAttempts = 5) => new() {
    MaxAttempts = maxAttempts,
    BaseDelay = TimeSpan.FromMilliseconds(1),
    BackoffMultiplier = 1.0, // flat — keep tests fast
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

  // ============================================================
  // _transportTag switch: GetType().Name is the dispatch key, so each fake's class NAME
  // (not its behavior) is what exercises the corresponding switch arm.
  // ============================================================

  // If this case stops matching "AzureServiceBusTransport" by name, throttle retry logging and
  // the OutboxPublishThrottled metric silently fall back to the raw CLR type name for the
  // "transport" dimension — ASB-specific dashboards and alerts go blind with no error anywhere.
  [Test]
  public async Task PublishAsync_AzureServiceBusTransportThrottledOnce_LogsAsbTransportTagAsync() {
    var transport = new AzureServiceBusTransport();
    var logger = new _capturingLogger();
    var strategy = new TransportPublishStrategy(
      transport, new DefaultTransportReadinessCheck(), "inbox",
      loggerFactory: new _loggerFactoryReturning(logger),
      throttleRetryOptions: _fastOpts());

    var result = await strategy.PublishAsync(_work(), CancellationToken.None);

    await Assert.That(result.Success).IsTrue()
      .Because("the transport recovers on the second attempt after one throttle");
    await Assert.That(transport.PublishCalls).IsEqualTo(2)
      .Because("one throttled call + one successful retry");
    await Assert.That(logger.Messages.Any(m => m.Contains("(asb)", StringComparison.Ordinal))).IsTrue()
      .Because("the retry log must tag AzureServiceBusTransport as 'asb', not the raw type name");
  }

  // Same regression, RabbitMQ's dimension.
  [Test]
  public async Task PublishAsync_RabbitMqTransportThrottledOnce_LogsRabbitmqTransportTagAsync() {
    var transport = new RabbitMQTransport();
    var logger = new _capturingLogger();
    var strategy = new TransportPublishStrategy(
      transport, new DefaultTransportReadinessCheck(), "inbox",
      loggerFactory: new _loggerFactoryReturning(logger),
      throttleRetryOptions: _fastOpts());

    var result = await strategy.PublishAsync(_work(), CancellationToken.None);

    await Assert.That(result.Success).IsTrue()
      .Because("the transport recovers on the second attempt after one throttle");
    await Assert.That(transport.PublishCalls).IsEqualTo(2);
    await Assert.That(logger.Messages.Any(m => m.Contains("(rabbitmq)", StringComparison.Ordinal))).IsTrue()
      .Because("the retry log must tag RabbitMQTransport as 'rabbitmq', not the raw type name");
  }

  // Same regression, the in-memory dimension used by in-process/dev/test hosts.
  [Test]
  public async Task PublishAsync_InMemoryTransportThrottledOnce_LogsInmemoryTransportTagAsync() {
    var transport = new InMemoryTransport();
    var logger = new _capturingLogger();
    var strategy = new TransportPublishStrategy(
      transport, new DefaultTransportReadinessCheck(), "inbox",
      loggerFactory: new _loggerFactoryReturning(logger),
      throttleRetryOptions: _fastOpts());

    var result = await strategy.PublishAsync(_work(), CancellationToken.None);

    await Assert.That(result.Success).IsTrue()
      .Because("the transport recovers on the second attempt after one throttle");
    await Assert.That(transport.PublishCalls).IsEqualTo(2);
    await Assert.That(logger.Messages.Any(m => m.Contains("(inmemory)", StringComparison.Ordinal))).IsTrue()
      .Because("the retry log must tag InMemoryTransport as 'inmemory', not the raw type name");
  }

  // ============================================================
  // In-memory retry loop: a throttle immediately followed by a different, real failure
  // ============================================================

  // If the loop mishandled the transition out of a throttled attempt, a real outage arriving
  // right after a throttle could either keep retrying the outage as if it were transient
  // pressure (burning the whole budget on an error that will never clear) or lose its own
  // classification and report the wrong reason to the failure channel.
  [Test]
  public async Task PublishAsync_ThrottledOnceThenHardFailure_ReturnsHardFailureReasonAsync() {
    var calls = 0;
    var transport = new _switchingFailureTransport(() => {
      var n = Interlocked.Increment(ref calls);
      if (n == 1) {
        throw new Azure.Messaging.ServiceBus.ServiceBusException(
          "namespace is being throttled. Error code : 50009. (ServiceBusy)");
      }
      throw new InvalidOperationException("broker outage");
    });
    var strategy = new TransportPublishStrategy(
      transport, new DefaultTransportReadinessCheck(), "inbox",
      loggerFactory: null,
      throttleRetryOptions: _fastOpts());

    var result = await strategy.PublishAsync(_work(), CancellationToken.None);

    await Assert.That(result.Success).IsFalse();
    await Assert.That(result.Reason).IsEqualTo(MessageFailureReason.Unknown)
      .Because("the second, non-throttle failure must be classified on its own terms after the " +
               "loop continues past the first throttle");
    await Assert.That(calls).IsEqualTo(2)
      .Because("one throttled attempt retried in-memory, then the real failure stops the loop " +
               "immediately — no third attempt burning the retry budget on an outage");
  }

  // ============================================================
  // Runtime message-kind classifier — defensive null-type-name guard
  // ============================================================

  // If this guard regressed, a caller handing the classifier a genuinely unresolvable type name
  // (distinct from the empty string the normal fallback chain already produces) would
  // NullReferenceException instead of degrading to Unknown — turning a classification miss into
  // a publish-path crash instead of a routing decision.
  [Test]
  public async Task DetectMessageKindForTest_NullTypeFullName_ReturnsUnknownAsync() {
    var kind = TransportPublishStrategy.DetectMessageKindForTest(null!);

    await Assert.That(kind).IsEqualTo(MessageKind.Unknown)
      .Because("a type name that cannot even produce a simple name must degrade to Unknown, not throw");
  }

  // ============================================================
  // Post-serialize hook chain — destination-metadata merge loop
  // ============================================================

  // If this merge loop stopped copying a hook's AdditionalDestinationMetadata into the final
  // destination, every header a hook contributes (claim-check markers, compression flags, audit
  // tags) would silently vanish from the wire even though the hook itself ran and reported success.
  [Test]
  public async Task PublishAsync_HookAddsDestinationMetadata_SurvivesIntoFinalDestinationAsync() {
    var transport = new _captureTransport();
    var chain = new PostSerializeHookChain([new _metadataAddingHook("custom-header", "custom-value")]);
    var strategy = new TransportPublishStrategy(
      transport: transport,
      readinessCheck: new DefaultTransportReadinessCheck(),
      inboxTopic: "test-inbox",
      postSerializeHookChain: chain,
      jsonOptions: _buildJsonOptions());

    var result = await strategy.PublishAsync(_hookWork(), CancellationToken.None);

    await Assert.That(result.Success).IsTrue();
    await Assert.That(transport.LastDestination).IsNotNull();
    await Assert.That(transport.LastDestination!.Metadata).IsNotNull();
    await Assert.That(transport.LastDestination!.Metadata!.ContainsKey("custom-header")).IsTrue()
      .Because("a hook's AdditionalDestinationMetadata must survive the chain's merge into the " +
               "destination the transport receives");
    await Assert.That(transport.LastDestination!.Metadata!["custom-header"].GetString())
      .IsEqualTo("custom-value");
    await Assert.That(transport.LastDestination!.Metadata!.ContainsKey(TransportPublishStrategy.BODY_SIZE_METADATA_KEY)).IsTrue()
      .Because("the body-size stamp and a hook's own metadata must coexist — the merge must not " +
               "clobber one with the other");
  }

  private static JsonSerializerOptions _buildJsonOptions() =>
    new() { TypeInfoResolver = new DefaultJsonTypeInfoResolver() };

  private static OutboxWork _hookWork() {
    var envelope = new MessageEnvelope<JsonElement> {
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Outbox },
      MessageId = MessageId.New(),
      Payload = JsonDocument.Parse("{\"x\":\"hello\"}").RootElement,
      Hops = [
        new MessageHop { Type = HopType.Current, Timestamp = DateTimeOffset.UtcNow, ServiceInstance = ServiceInstanceInfo.Unknown }
      ]
    };
    return new OutboxWork {
      MessageId = Guid.NewGuid(),
      MessageType = "MyApp.Events.SomeEvent, MyApp",
      Destination = "test-topic",
      EnvelopeType = envelope.GetType().AssemblyQualifiedName!,
      Envelope = envelope,
      Status = MessageProcessingStatus.Stored,
      Attempts = 0,
    };
  }

  // ============================================================
  // Test doubles
  // ============================================================

  // Named EXACTLY to match the "AzureServiceBusTransport" case label in
  // TransportPublishStrategy._transportTag — GetType().Name is the dispatch key here, so the
  // fake's class name is load-bearing, not cosmetic. Throttles the first call, then succeeds.
  private sealed class AzureServiceBusTransport : ITransport {
    private int _calls;
    public int PublishCalls => _calls;
    public bool IsInitialized => true;
    public TransportCapabilities Capabilities => new();
    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task PublishAsync(IMessageEnvelope envelope, TransportDestination destination, string? envelopeType = null, ReadOnlyMemory<byte>? preSerializedBytes = null, CancellationToken cancellationToken = default) {
      var n = Interlocked.Increment(ref _calls);
      if (n == 1) {
        throw new Azure.Messaging.ServiceBus.ServiceBusException(
          "namespace is being throttled. Error code : 50009. (ServiceBusy)");
      }
      return Task.CompletedTask;
    }

    public Task<ISubscription> SubscribeBatchAsync(Func<IReadOnlyList<TransportMessage>, CancellationToken, Task> batchHandler, TransportDestination destination, TransportBatchOptions batchOptions, CancellationToken cancellationToken = default)
      => throw new NotSupportedException();
    public Task<IMessageEnvelope> SendAsync<TRequest, TResponse>(IMessageEnvelope requestEnvelope, TransportDestination destination, CancellationToken cancellationToken = default)
      where TRequest : notnull where TResponse : notnull => throw new NotSupportedException();
  }

  // Named EXACTLY to match the "RabbitMQTransport" case label — same shape as
  // AzureServiceBusTransport above, distinguished only by GetType().Name.
  private sealed class RabbitMQTransport : ITransport {
    private int _calls;
    public int PublishCalls => _calls;
    public bool IsInitialized => true;
    public TransportCapabilities Capabilities => new();
    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task PublishAsync(IMessageEnvelope envelope, TransportDestination destination, string? envelopeType = null, ReadOnlyMemory<byte>? preSerializedBytes = null, CancellationToken cancellationToken = default) {
      var n = Interlocked.Increment(ref _calls);
      if (n == 1) {
        throw new Azure.Messaging.ServiceBus.ServiceBusException(
          "namespace is being throttled. Error code : 50009. (ServiceBusy)");
      }
      return Task.CompletedTask;
    }

    public Task<ISubscription> SubscribeBatchAsync(Func<IReadOnlyList<TransportMessage>, CancellationToken, Task> batchHandler, TransportDestination destination, TransportBatchOptions batchOptions, CancellationToken cancellationToken = default)
      => throw new NotSupportedException();
    public Task<IMessageEnvelope> SendAsync<TRequest, TResponse>(IMessageEnvelope requestEnvelope, TransportDestination destination, CancellationToken cancellationToken = default)
      where TRequest : notnull where TResponse : notnull => throw new NotSupportedException();
  }

  // Named EXACTLY to match the "InMemoryTransport" case label — same shape as the two above,
  // distinguished only by GetType().Name.
  private sealed class InMemoryTransport : ITransport {
    private int _calls;
    public int PublishCalls => _calls;
    public bool IsInitialized => true;
    public TransportCapabilities Capabilities => new();
    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task PublishAsync(IMessageEnvelope envelope, TransportDestination destination, string? envelopeType = null, ReadOnlyMemory<byte>? preSerializedBytes = null, CancellationToken cancellationToken = default) {
      var n = Interlocked.Increment(ref _calls);
      if (n == 1) {
        throw new Azure.Messaging.ServiceBus.ServiceBusException(
          "namespace is being throttled. Error code : 50009. (ServiceBusy)");
      }
      return Task.CompletedTask;
    }

    public Task<ISubscription> SubscribeBatchAsync(Func<IReadOnlyList<TransportMessage>, CancellationToken, Task> batchHandler, TransportDestination destination, TransportBatchOptions batchOptions, CancellationToken cancellationToken = default)
      => throw new NotSupportedException();
    public Task<IMessageEnvelope> SendAsync<TRequest, TResponse>(IMessageEnvelope requestEnvelope, TransportDestination destination, CancellationToken cancellationToken = default)
      where TRequest : notnull where TResponse : notnull => throw new NotSupportedException();
  }

  // Runs a caller-supplied action on every PublishAsync call; the action decides per-call
  // whether (and how) to fail, via its own closure-captured counter.
  private sealed class _switchingFailureTransport(Action onPublish) : ITransport {
    public bool IsInitialized => true;
    public TransportCapabilities Capabilities => new();
    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task PublishAsync(IMessageEnvelope envelope, TransportDestination destination, string? envelopeType = null, ReadOnlyMemory<byte>? preSerializedBytes = null, CancellationToken cancellationToken = default) {
      onPublish();
      return Task.CompletedTask;
    }

    public Task<ISubscription> SubscribeBatchAsync(Func<IReadOnlyList<TransportMessage>, CancellationToken, Task> batchHandler, TransportDestination destination, TransportBatchOptions batchOptions, CancellationToken cancellationToken = default)
      => throw new NotSupportedException();
    public Task<IMessageEnvelope> SendAsync<TRequest, TResponse>(IMessageEnvelope requestEnvelope, TransportDestination destination, CancellationToken cancellationToken = default)
      where TRequest : notnull where TResponse : notnull => throw new NotSupportedException();
  }

  // Captures the destination the strategy hands to the transport, so tests can inspect the
  // final merged metadata post-hook-chain.
  private sealed class _captureTransport : ITransport {
    public bool IsInitialized => true;
    public TransportCapabilities Capabilities => TransportCapabilities.PublishSubscribe;
    public TransportDestination? LastDestination { get; private set; }

    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task PublishAsync(IMessageEnvelope envelope, TransportDestination destination, string? envelopeType = null, ReadOnlyMemory<byte>? preSerializedBytes = null, CancellationToken cancellationToken = default) {
      LastDestination = destination;
      return Task.CompletedTask;
    }

    public Task<ISubscription> SubscribeBatchAsync(Func<IReadOnlyList<TransportMessage>, CancellationToken, Task> batchHandler, TransportDestination destination, TransportBatchOptions batchOptions, CancellationToken cancellationToken = default)
      => throw new NotSupportedException();
    public Task<IMessageEnvelope> SendAsync<TRequest, TResponse>(IMessageEnvelope requestEnvelope, TransportDestination destination, CancellationToken cancellationToken = default)
      where TRequest : notnull where TResponse : notnull => throw new NotSupportedException();
  }

  // A post-serialize hook contributing exactly one destination-metadata entry — the minimal
  // shape needed to drive the chain's merge loop through at least one iteration.
  private sealed class _metadataAddingHook(string key, string value) : IPostSerializeHook {
    public int Order => 100;
    public Task<PostSerializeResult> RunAsync(PostSerializeContext context, CancellationToken cancellationToken) {
      return Task.FromResult(new PostSerializeResult {
        AdditionalDestinationMetadata = new Dictionary<string, JsonElement> {
          [key] = JsonDocument.Parse($"\"{value}\"").RootElement
        }
      });
    }
  }

  private sealed class _capturingLogger : ILogger {
    public List<string> Messages { get; } = [];
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(
        LogLevel logLevel,
        Microsoft.Extensions.Logging.EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter) {
      Messages.Add(formatter(state, exception));
    }
  }

  private sealed class _loggerFactoryReturning(ILogger logger) : ILoggerFactory {
    public void AddProvider(ILoggerProvider provider) { }
    public ILogger CreateLogger(string categoryName) => logger;
    public void Dispose() { }
  }
}
