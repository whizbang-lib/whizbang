using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Resilience;
using Whizbang.Core.Routing;
using Whizbang.Core.Security;
using Whizbang.Core.Transports;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

#pragma warning disable CS0067 // Event is never used (test doubles)
#pragma warning disable CA1822 // Member does not access instance data (test doubles)

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Tests for the batch handler in TransportConsumerWorker.
/// The batch handler processes N messages with: self-echo check → OTEL activity →
/// security context → serialize → PreInbox → bulk insert → Process → PostInbox → completion.
/// TDD RED phase: these tests define required behavior before implementation.
/// </summary>
[Category("Workers")]
public class TransportConsumerWorkerBatchHandlerTests {

  // ========================================
  // Batch handler uses SubscribeBatchAsync
  // ========================================

  [Test]
  public async Task Worker_SubscribesViaBatchAsync() {
    // Arrange
    var transport = new BatchTestTransport();
    var options = new TransportConsumerOptions();
    options.Destinations.Add(new TransportDestination("test-topic"));

    var worker = _createWorker(transport, options);

    using var cts = new CancellationTokenSource();
    _ = worker.StartAsync(cts.Token);
    await Task.Delay(200);
    cts.Cancel();

    // Assert — worker should use SubscribeBatchAsync
    await Assert.That(transport.BatchSubscribeCallCount).IsGreaterThanOrEqualTo(1)
      .Because("TransportConsumerWorker should subscribe via SubscribeBatchAsync");
  }

  // ========================================
  // Batch handler — inbox insert + processing
  // ========================================

  [Test]
  public async Task BatchHandler_InsertsAllMessagesInOneFlushAsync() {
    // Arrange
    var messageId1 = MessageId.New();
    var messageId2 = MessageId.New();
    var transport = new BatchTestTransport();
    var options = new TransportConsumerOptions();
    options.Destinations.Add(new TransportDestination("test-topic"));

    var workStrategy = new TrackingBatchWorkStrategy();

    var services = new ServiceCollection();
    services.AddScoped<IWorkCoordinatorStrategy>(_ => workStrategy);
    services.AddWhizbangMessageSecurity(opts => { opts.AllowAnonymous = true; });
    var sp = services.BuildServiceProvider();

    var worker = _createWorkerWithScope(transport, options, sp.GetRequiredService<IServiceScopeFactory>());

    using var cts = new CancellationTokenSource();
    _ = worker.StartAsync(cts.Token);
    await Task.Delay(200);

    // Act — simulate batch of 2 messages
    var envelope1 = _createJsonEnvelope(messageId1);
    var envelope2 = _createJsonEnvelope(messageId2);
    const string envelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[TestApp.TestMessage, TestApp]], Whizbang.Core";

    await transport.SimulateBatchReceivedAsync([
      new TransportMessage(envelope1, envelopeType),
      new TransportMessage(envelope2, envelopeType)
    ]);

    cts.Cancel();

    // Assert — both messages queued, ONE flush
    await Assert.That(workStrategy.QueuedInboxCount).IsEqualTo(2)
      .Because("Both messages should be queued for inbox insert");
    await Assert.That(workStrategy.FlushCount).IsEqualTo(1)
      .Because("Exactly one flush for inbox insert — no completion flush (processing deferred)");
  }

  [Test]
  public async Task BatchHandler_DoesNotProcessInline_DefersToPublisherWorkerAsync() {
    // Arrange
    var messageId = MessageId.New();
    var transport = new BatchTestTransport();
    var options = new TransportConsumerOptions();
    options.Destinations.Add(new TransportDestination("test-topic"));

    var workStrategy = new TrackingBatchWorkStrategy(messageId.Value);

    var services = new ServiceCollection();
    services.AddScoped<IWorkCoordinatorStrategy>(_ => workStrategy);
    services.AddWhizbangMessageSecurity(opts => { opts.AllowAnonymous = true; });
    var sp = services.BuildServiceProvider();

    var worker = _createWorkerWithScope(transport, options, sp.GetRequiredService<IServiceScopeFactory>());

    using var cts = new CancellationTokenSource();
    _ = worker.StartAsync(cts.Token);
    await Task.Delay(200);

    var envelope = _createJsonEnvelope(messageId);
    const string envelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[TestApp.TestMessage, TestApp]], Whizbang.Core";

    // Act
    await transport.SimulateBatchReceivedAsync([new TransportMessage(envelope, envelopeType)]);
    cts.Cancel();

    // Assert — NO inline processing. Processing deferred to WorkCoordinatorPublisherWorker.
    await Assert.That(workStrategy.InboxCompletionCount).IsEqualTo(0)
      .Because("Batch handler should NOT process inline — processing is deferred to WorkCoordinatorPublisherWorker");
  }

  // ========================================
  // Self-echo discard
  // ========================================

  [Test]
  public async Task BatchHandler_SkipsSelfEchoMessagesAsync() {
    // Arrange
    const string serviceName = "TestService";
    const string ownedNamespace = "TestApp";
    var messageId = MessageId.New();
    var transport = new BatchTestTransport();
    var options = new TransportConsumerOptions();
    options.Destinations.Add(new TransportDestination("test-topic"));

    var workStrategy = new TrackingBatchWorkStrategy();

    var services = new ServiceCollection();
    services.AddScoped<IWorkCoordinatorStrategy>(_ => workStrategy);
    services.AddWhizbangMessageSecurity(opts => { opts.AllowAnonymous = true; });
    services.Configure<RoutingOptions>(opts => { opts.OwnDomains([ownedNamespace]); });
    var sp = services.BuildServiceProvider();

    var worker = new TransportConsumerWorker(
      transport, options, new SubscriptionResilienceOptions(),
      sp.GetRequiredService<IServiceScopeFactory>(), new JsonSerializerOptions(),
      new OrderedStreamProcessor(parallelizeStreams: false, logger: null),
      lifecycleMessageDeserializer: null, metrics: null,
      NullLogger<TransportConsumerWorker>.Instance,
      routingOptions: sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<RoutingOptions>>(),
      serviceInstanceProvider: new StubServiceInstanceProvider(serviceName)
    );

    using var cts = new CancellationTokenSource();
    _ = worker.StartAsync(cts.Token);
    await Task.Delay(200);

    // Create self-echo envelope (last hop matches this service, owned namespace)
    var selfEchoEnvelope = _createSelfEchoEnvelope(messageId, serviceName);
    const string envelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[TestApp.TestMessage, TestApp]], Whizbang.Core";

    // Act
    await transport.SimulateBatchReceivedAsync([new TransportMessage(selfEchoEnvelope, envelopeType)]);
    cts.Cancel();

    // Assert — self-echo should be discarded before inbox insert
    await Assert.That(workStrategy.QueuedInboxCount).IsEqualTo(0)
      .Because("Self-echo messages should be discarded in batch handler");
  }

  // ========================================
  // Per-message error isolation
  // ========================================

  [Test]
  public async Task BatchHandler_OneFailedMessageDoesNotPoisonBatchAsync() {
    // Arrange — send 2 messages, one with invalid envelope type (will fail serialization)
    var goodMessageId = MessageId.New();
    var transport = new BatchTestTransport();
    var options = new TransportConsumerOptions();
    options.Destinations.Add(new TransportDestination("test-topic"));

    var workStrategy = new TrackingBatchWorkStrategy();

    var services = new ServiceCollection();
    services.AddScoped<IWorkCoordinatorStrategy>(_ => workStrategy);
    services.AddWhizbangMessageSecurity(opts => { opts.AllowAnonymous = true; });
    var sp = services.BuildServiceProvider();

    var worker = _createWorkerWithScope(transport, options, sp.GetRequiredService<IServiceScopeFactory>());

    using var cts = new CancellationTokenSource();
    _ = worker.StartAsync(cts.Token);
    await Task.Delay(200);

    var goodEnvelope = _createJsonEnvelope(goodMessageId);
    var badEnvelope = _createJsonEnvelope(MessageId.New());

    // Act — batch with one good message and one with null envelope type (will throw)
    await transport.SimulateBatchReceivedAsync([
      new TransportMessage(goodEnvelope, "Whizbang.Core.Observability.MessageEnvelope`1[[TestApp.TestMessage, TestApp]], Whizbang.Core"),
      new TransportMessage(badEnvelope, null) // null envelope type → serialization error
    ]);
    cts.Cancel();

    // Assert — good message should still be processed
    await Assert.That(workStrategy.QueuedInboxCount).IsGreaterThanOrEqualTo(1)
      .Because("Good messages should be processed even when another message in the batch fails");
  }

  // ========================================
  // Duplicate detection
  // ========================================

  [Test]
  public async Task BatchHandler_DuplicateMessages_SkipsProcessingAsync() {
    // Arrange — strategy returns empty InboxWork (simulating duplicate)
    var messageId = MessageId.New();
    var transport = new BatchTestTransport();
    var options = new TransportConsumerOptions();
    options.Destinations.Add(new TransportDestination("test-topic"));

    var workStrategy = new TrackingBatchWorkStrategy(returnEmptyInboxWork: true);

    var services = new ServiceCollection();
    services.AddScoped<IWorkCoordinatorStrategy>(_ => workStrategy);
    services.AddWhizbangMessageSecurity(opts => { opts.AllowAnonymous = true; });
    var sp = services.BuildServiceProvider();

    var worker = _createWorkerWithScope(transport, options, sp.GetRequiredService<IServiceScopeFactory>());

    using var cts = new CancellationTokenSource();
    _ = worker.StartAsync(cts.Token);
    await Task.Delay(200);

    var envelope = _createJsonEnvelope(messageId);
    const string envelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[TestApp.TestMessage, TestApp]], Whizbang.Core";

    // Act
    await transport.SimulateBatchReceivedAsync([new TransportMessage(envelope, envelopeType)]);
    cts.Cancel();

    // Assert — message queued but no completions (duplicate detected, processing skipped)
    await Assert.That(workStrategy.QueuedInboxCount).IsEqualTo(1);
    await Assert.That(workStrategy.InboxCompletionCount).IsEqualTo(0)
      .Because("Duplicate messages should not be processed");
  }

  // ========================================
  // Helper Methods
  // ========================================

  private static TransportConsumerWorker _createWorker(
      ITransport transport, TransportConsumerOptions options) {
    return new TransportConsumerWorker(
      transport, options, new SubscriptionResilienceOptions(),
      _buildScopeFactory(), new JsonSerializerOptions(),
      new OrderedStreamProcessor(parallelizeStreams: false, logger: null),
      lifecycleMessageDeserializer: null, metrics: null,
      NullLogger<TransportConsumerWorker>.Instance
    );
  }

  private static TransportConsumerWorker _createWorkerWithScope(
      ITransport transport, TransportConsumerOptions options, IServiceScopeFactory scopeFactory) {
    return new TransportConsumerWorker(
      transport, options, new SubscriptionResilienceOptions(),
      scopeFactory, new JsonSerializerOptions(),
      new OrderedStreamProcessor(parallelizeStreams: false, logger: null),
      lifecycleMessageDeserializer: null, metrics: null,
      NullLogger<TransportConsumerWorker>.Instance
    );
  }

  private static IServiceScopeFactory _buildScopeFactory() {
    var services = new ServiceCollection();
    return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
  }

  private static MessageEnvelope<JsonElement> _createJsonEnvelope(MessageId messageId) {
    return new MessageEnvelope<JsonElement> {
      MessageId = messageId,
      Payload = JsonDocument.Parse("{}").RootElement,
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

  private static MessageEnvelope<JsonElement> _createSelfEchoEnvelope(MessageId messageId, string sourceServiceName) {
    return new MessageEnvelope<JsonElement> {
      MessageId = messageId,
      Payload = JsonDocument.Parse("{}").RootElement,
      Hops = [
        new MessageHop {
          Type = HopType.Current,
          Timestamp = DateTimeOffset.UtcNow,
          ServiceInstance = new ServiceInstanceInfo {
            ServiceName = sourceServiceName,
            InstanceId = Guid.NewGuid(),
            HostName = "test-host",
            ProcessId = 1234
          }
        }
      ],
      DispatchContext = new MessageDispatchContext {
        Mode = DispatchModes.Outbox,
        Source = MessageSource.Outbox
      }
    };
  }

  // ========================================
  // Test Doubles
  // ========================================

  /// <summary>
  /// Transport that captures the batch handler and allows simulating batch receives.
  /// </summary>
  private sealed class BatchTestTransport : ITransport {
    private Func<IReadOnlyList<TransportMessage>, CancellationToken, Task>? _batchHandler;

    public int BatchSubscribeCallCount { get; private set; }
    public bool IsInitialized => true;
    public TransportCapabilities Capabilities => TransportCapabilities.PublishSubscribe | TransportCapabilities.Reliable;

    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task PublishAsync(
        IMessageEnvelope envelope, TransportDestination destination,
        string? envelopeType = null, CancellationToken cancellationToken = default) =>
      Task.CompletedTask;

    public Task<ISubscription> SubscribeBatchAsync(
        Func<IReadOnlyList<TransportMessage>, CancellationToken, Task> batchHandler,
        TransportDestination destination, TransportBatchOptions batchOptions,
        CancellationToken cancellationToken = default) {
      BatchSubscribeCallCount++;
      _batchHandler = batchHandler;
      return Task.FromResult<ISubscription>(new BatchTestSubscription());
    }

    public Task<IMessageEnvelope> SendAsync<TRequest, TResponse>(
        IMessageEnvelope requestEnvelope, TransportDestination destination,
        CancellationToken cancellationToken = default)
        where TRequest : notnull where TResponse : notnull =>
      throw new NotSupportedException();

    public async Task SimulateBatchReceivedAsync(IReadOnlyList<TransportMessage> batch) {
      if (_batchHandler != null) {
        await _batchHandler(batch, CancellationToken.None);
      }
    }
  }

  private sealed class BatchTestSubscription : ISubscription {
    public bool IsActive { get; private set; } = true;
    public bool IsDisposed { get; private set; }

#pragma warning disable CS0067
    public event EventHandler<SubscriptionDisconnectedEventArgs>? OnDisconnected;
#pragma warning restore CS0067

    public Task PauseAsync() { IsActive = false; return Task.CompletedTask; }
    public Task ResumeAsync() { IsActive = true; return Task.CompletedTask; }
    public void Dispose() { IsDisposed = true; }
  }

  private sealed class StubServiceInstanceProvider(string serviceName) : IServiceInstanceProvider {
    public Guid InstanceId { get; } = Guid.NewGuid();
    public string ServiceName { get; } = serviceName;
    public string HostName => "test-host";
    public int ProcessId => 1234;
    public ServiceInstanceInfo ToInfo() => new() {
      ServiceName = ServiceName,
      InstanceId = InstanceId,
      HostName = HostName,
      ProcessId = ProcessId
    };
  }

  /// <summary>
  /// Work coordinator strategy that tracks calls for batch handler assertions.
  /// </summary>
  private sealed class TrackingBatchWorkStrategy : IWorkCoordinatorStrategy {
    private readonly Guid _expectedMessageId;
    private readonly bool _returnEmptyInboxWork;

    public TrackingBatchWorkStrategy(Guid? expectedMessageId = null, bool returnEmptyInboxWork = false) {
      _expectedMessageId = expectedMessageId ?? Guid.Empty;
      _returnEmptyInboxWork = returnEmptyInboxWork;
    }

    public int QueuedInboxCount { get; private set; }
    public int FlushCount { get; private set; }
    public int InboxCompletionCount { get; private set; }
    public Action? OnCompletionQueued { get; set; }

    public void QueueInboxMessage(InboxMessage message) {
      QueuedInboxCount++;
    }

    public void QueueInboxCompletion(Guid messageId, MessageProcessingStatus status) {
      InboxCompletionCount++;
      OnCompletionQueued?.Invoke();
    }

    public void QueueInboxFailure(Guid messageId, MessageProcessingStatus status, string errorDetails) { }
    public void QueueOutboxMessage(OutboxMessage message) { }
    public void QueueOutboxCompletion(Guid messageId, MessageProcessingStatus status) { }
    public void QueueOutboxFailure(Guid messageId, MessageProcessingStatus status, string errorDetails) { }

    public Task<WorkBatch> FlushAsync(WorkBatchOptions flags, FlushMode mode = FlushMode.Required, CancellationToken ct = default) {
      FlushCount++;

      if (_returnEmptyInboxWork) {
        return Task.FromResult(new WorkBatch {
          InboxWork = [],
          OutboxWork = [],
          PerspectiveWork = []
        });
      }

      // Return inbox work for the expected message, or for any queued messages
      var workMessageId = _expectedMessageId == Guid.Empty ? Guid.CreateVersion7() : _expectedMessageId;
      var inboxWork = new InboxWork {
        MessageId = workMessageId,
        Envelope = new MessageEnvelope<JsonElement> {
          MessageId = MessageId.From(workMessageId),
          Payload = JsonDocument.Parse("{}").RootElement,
          Hops = [
            new MessageHop {
              Type = HopType.Current,
              Timestamp = DateTimeOffset.UtcNow,
              ServiceInstance = ServiceInstanceInfo.Unknown
            }
          ],
          DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
        },
        MessageType = "TestApp.TestMessage, TestApp",
        StreamId = workMessageId
      };

      return Task.FromResult(new WorkBatch {
        InboxWork = [inboxWork],
        OutboxWork = [],
        PerspectiveWork = []
      });
    }
  }
}
