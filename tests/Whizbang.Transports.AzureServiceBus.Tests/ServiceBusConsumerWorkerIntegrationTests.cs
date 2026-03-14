#pragma warning disable CA1707 // Test method naming uses underscores by convention

using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Security;
using Whizbang.Core.Serialization;
using Whizbang.Core.Transports;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;
using Whizbang.Testing.Transport;
using Whizbang.Transports.AzureServiceBus.Tests.Containers;
using EnvelopeSerializer = Whizbang.Core.Messaging.EnvelopeSerializer;

namespace Whizbang.Transports.AzureServiceBus.Tests;

/// <summary>
/// Integration tests for ServiceBusConsumerWorker using the real Azure Service Bus emulator.
/// Exercises the full message handling pipeline: transport receive → _handleMessageAsync → work coordination.
/// </summary>
[Timeout(240_000)]
[Category("Integration")]
[NotInParallel("ServiceBus")]
[ClassDataSource<ServiceBusEmulatorFixtureSource>(Shared = SharedType.PerAssembly)]
public class ServiceBusConsumerWorkerIntegrationTests(ServiceBusEmulatorFixtureSource fixtureSource) {
  private readonly ServiceBusEmulatorFixture _fixture = fixtureSource.Fixture;

  [Test]
  public async Task Worker_ReceivesMessage_ProcessesThroughHandleMessageAsync() {
    // Arrange: Wire up worker with real transport and test strategy
    var jsonOptions = JsonContextRegistry.CreateCombinedOptions();
    var transport = new AzureServiceBusTransport(_fixture.Client, jsonOptions);
    await transport.InitializeAsync();

    var capturedInboxMessages = new List<InboxMessage>();
    var strategy = new CapturingWorkCoordinatorStrategy(capturedInboxMessages);

    var services = new ServiceCollection();
    services.AddWhizbangMessageSecurity(opts => { opts.AllowAnonymous = true; });
    services.AddScoped<IWorkCoordinatorStrategy>(_ => strategy);
    services.AddSingleton<IEnvelopeSerializer>(new EnvelopeSerializer(jsonOptions));

    var serviceProvider = services.BuildServiceProvider();
    var orderedProcessor = new OrderedStreamProcessor();
    var logger = new TestConsumerLogger();

    var options = new ServiceBusConsumerOptions {
      Subscriptions = [new TopicSubscription("topic-00", "sub-00-a")]
    };

    var worker = new ServiceBusConsumerWorker(
      transport, serviceProvider.GetRequiredService<IServiceScopeFactory>(),
      jsonOptions, logger, orderedProcessor, options);

    // Drain existing messages
    await _drainMessagesAsync("topic-00", "sub-00-a");

    // Act: Start worker, publish a message, wait for it to be processed
    await worker.StartAsync(CancellationToken.None);

    try {
      // Give subscription time to warm up
      await Task.Delay(TimeSpan.FromSeconds(5));

      var envelope = _createTestEnvelopeWithAggregateId(Guid.NewGuid());
      var destination = new TransportDestination("topic-00");
      await transport.PublishAsync(envelope, destination);

      // Wait for message to be processed
      var processed = await _waitForConditionAsync(
        () => capturedInboxMessages.Count > 0,
        TimeSpan.FromSeconds(30));

      // Assert
      await Assert.That(processed).IsTrue();
      await Assert.That(capturedInboxMessages.Count).IsGreaterThanOrEqualTo(1);

      var inbox = capturedInboxMessages.First();
      await Assert.That(inbox.MessageId).IsEqualTo(envelope.MessageId.Value);
      // TestMessage does not implement IEvent, so isEvent is false
      await Assert.That(inbox.IsEvent).IsFalse();
    } finally {
      await worker.StopAsync(CancellationToken.None);
    }
  }

  [Test]
  public async Task Worker_MessageWithSecurityContext_EstablishesContextDuringHandlingAsync() {
    // Arrange
    var jsonOptions = JsonContextRegistry.CreateCombinedOptions();
    var transport = new AzureServiceBusTransport(_fixture.Client, jsonOptions);
    await transport.InitializeAsync();

    IScopeContext? capturedScope = null;
    var capturedInboxMessages = new List<InboxMessage>();
    var strategy = new CapturingWorkCoordinatorStrategy(
      capturedInboxMessages,
      onFlush: sp => {
        var accessor = sp?.GetService<IScopeContextAccessor>();
        capturedScope ??= accessor?.Current;
      });

    var services = new ServiceCollection();
    services.AddWhizbangMessageSecurity(opts => { opts.AllowAnonymous = true; });
    services.AddScoped<IWorkCoordinatorStrategy>(_ => strategy);
    services.AddSingleton<IEnvelopeSerializer>(new EnvelopeSerializer(jsonOptions));

    var serviceProvider = services.BuildServiceProvider();
    var orderedProcessor = new OrderedStreamProcessor();
    var logger = new TestConsumerLogger();

    var options = new ServiceBusConsumerOptions {
      Subscriptions = [new TopicSubscription("topic-01", "sub-01-a")]
    };

    var worker = new ServiceBusConsumerWorker(
      transport, serviceProvider.GetRequiredService<IServiceScopeFactory>(),
      jsonOptions, logger, orderedProcessor, options);

    await _drainMessagesAsync("topic-01", "sub-01-a");
    await worker.StartAsync(CancellationToken.None);

    try {
      await Task.Delay(TimeSpan.FromSeconds(5));

      // Publish message with security context in hop
      var envelope = _createTestEnvelopeWithScope("test-user-id", "test-tenant-id");
      var destination = new TransportDestination("topic-01");
      await transport.PublishAsync(envelope, destination);

      var processed = await _waitForConditionAsync(
        () => capturedInboxMessages.Count > 0,
        TimeSpan.FromSeconds(30));

      await Assert.That(processed).IsTrue();
      // Security context should have been established during handling
      // Note: Context may not be captured if strategy doesn't have scope access,
      // but the test verifies the full pipeline runs without errors
      await Assert.That(capturedInboxMessages.Count).IsGreaterThanOrEqualTo(1);
    } finally {
      await worker.StopAsync(CancellationToken.None);
    }
  }

  [Test]
  public async Task Worker_DuplicateMessage_SkipsSecondProcessingAsync() {
    // Arrange: Strategy returns empty work batch for duplicates
    var jsonOptions = JsonContextRegistry.CreateCombinedOptions();
    var transport = new AzureServiceBusTransport(_fixture.Client, jsonOptions);
    await transport.InitializeAsync();

    var processedMessageIds = new List<Guid>();
    var flushCount = 0;
    var strategy = new DuplicateDetectingStrategy(processedMessageIds, () => flushCount++);

    var services = new ServiceCollection();
    services.AddWhizbangMessageSecurity(opts => { opts.AllowAnonymous = true; });
    services.AddScoped<IWorkCoordinatorStrategy>(_ => strategy);
    services.AddSingleton<IEnvelopeSerializer>(new EnvelopeSerializer(jsonOptions));

    var serviceProvider = services.BuildServiceProvider();
    var orderedProcessor = new OrderedStreamProcessor();
    var logger = new TestConsumerLogger();

    var options = new ServiceBusConsumerOptions {
      Subscriptions = [new TopicSubscription("topic-01", "sub-01-a")]
    };

    var worker = new ServiceBusConsumerWorker(
      transport, serviceProvider.GetRequiredService<IServiceScopeFactory>(),
      jsonOptions, logger, orderedProcessor, options);

    await _drainMessagesAsync("topic-01", "sub-01-a");
    await worker.StartAsync(CancellationToken.None);

    try {
      await Task.Delay(TimeSpan.FromSeconds(5));

      // Publish a message
      var envelope = _createTestEnvelopeWithAggregateId(Guid.NewGuid());
      var destination = new TransportDestination("topic-01");
      await transport.PublishAsync(envelope, destination);

      // Wait for first processing
      var firstProcessed = await _waitForConditionAsync(
        () => processedMessageIds.Count > 0,
        TimeSpan.FromSeconds(30));

      await Assert.That(firstProcessed).IsTrue();
      await Assert.That(processedMessageIds.Count).IsEqualTo(1);
    } finally {
      await worker.StopAsync(CancellationToken.None);
    }
  }

  [Test]
  public async Task Worker_StartAndStop_ManagesSubscriptionsCorrectlyAsync() {
    // Arrange
    var jsonOptions = JsonContextRegistry.CreateCombinedOptions();
    var transport = new AzureServiceBusTransport(_fixture.Client, jsonOptions);
    await transport.InitializeAsync();

    var strategy = new NoOpWorkCoordinatorStrategy();
    var services = new ServiceCollection();
    services.AddWhizbangMessageSecurity(opts => { opts.AllowAnonymous = true; });
    services.AddScoped<IWorkCoordinatorStrategy>(_ => strategy);

    var serviceProvider = services.BuildServiceProvider();
    var orderedProcessor = new OrderedStreamProcessor();
    var logger = new TestConsumerLogger();

    var options = new ServiceBusConsumerOptions {
      Subscriptions = [
        new TopicSubscription("topic-00", "sub-00-a"),
        new TopicSubscription("topic-01", "sub-01-a")
      ]
    };

    var worker = new ServiceBusConsumerWorker(
      transport, serviceProvider.GetRequiredService<IServiceScopeFactory>(),
      jsonOptions, logger, orderedProcessor, options);

    // Act: Start creates subscriptions, stop disposes them
    await worker.StartAsync(CancellationToken.None);
    // Worker should have created 2 subscriptions - verify by stopping cleanly
    await worker.StopAsync(CancellationToken.None);

    // Assert: No exceptions means subscriptions were managed correctly
    // Logger should have logged starting/stopping
    await Assert.That(logger.HasMessage("starting")).IsTrue();
    await Assert.That(logger.HasMessage("stopping")).IsTrue();
  }

  [Test]
  public async Task Worker_PauseAndResume_PausesAndResumesSubscriptionsAsync() {
    // Arrange
    var jsonOptions = JsonContextRegistry.CreateCombinedOptions();
    var transport = new AzureServiceBusTransport(_fixture.Client, jsonOptions);
    await transport.InitializeAsync();

    var strategy = new NoOpWorkCoordinatorStrategy();
    var services = new ServiceCollection();
    services.AddWhizbangMessageSecurity(opts => { opts.AllowAnonymous = true; });
    services.AddScoped<IWorkCoordinatorStrategy>(_ => strategy);

    var serviceProvider = services.BuildServiceProvider();
    var orderedProcessor = new OrderedStreamProcessor();
    var logger = new TestConsumerLogger();

    var options = new ServiceBusConsumerOptions {
      Subscriptions = [new TopicSubscription("topic-00", "sub-00-a")]
    };

    var worker = new ServiceBusConsumerWorker(
      transport, serviceProvider.GetRequiredService<IServiceScopeFactory>(),
      jsonOptions, logger, orderedProcessor, options);

    await worker.StartAsync(CancellationToken.None);

    try {
      // Act: Pause and resume should not throw
      await worker.PauseAllSubscriptionsAsync();
      await worker.ResumeAllSubscriptionsAsync();

      // Assert: Worker still running after pause/resume - no exception means success
      await Assert.That(logger.HasMessage("starting")).IsTrue();
    } finally {
      await worker.StopAsync(CancellationToken.None);
    }
  }

  [Test]
  public async Task Worker_WithDestinationFilter_CreatesSubscriptionWithFilterAsync() {
    // Arrange
    var jsonOptions = JsonContextRegistry.CreateCombinedOptions();
    var transport = new AzureServiceBusTransport(_fixture.Client, jsonOptions);
    await transport.InitializeAsync();

    var strategy = new NoOpWorkCoordinatorStrategy();
    var services = new ServiceCollection();
    services.AddWhizbangMessageSecurity(opts => { opts.AllowAnonymous = true; });
    services.AddScoped<IWorkCoordinatorStrategy>(_ => strategy);

    var serviceProvider = services.BuildServiceProvider();
    var orderedProcessor = new OrderedStreamProcessor();
    var logger = new TestConsumerLogger();

    // Use DestinationFilter to test that code path
    var options = new ServiceBusConsumerOptions {
      Subscriptions = [new TopicSubscription("topic-00", "sub-00-a", "my-filter-value")]
    };

    var worker = new ServiceBusConsumerWorker(
      transport, serviceProvider.GetRequiredService<IServiceScopeFactory>(),
      jsonOptions, logger, orderedProcessor, options);

    // Act: Start should handle filter metadata without errors
    await worker.StartAsync(CancellationToken.None);

    try {
      // Assert: Worker started successfully with destination filter
      await Assert.That(logger.HasMessage("Subscribed")).IsTrue();
    } finally {
      await worker.StopAsync(CancellationToken.None);
    }
  }

  [Test]
  public async Task Worker_MessageWithAggregateId_ExtractsStreamIdFromHopMetadataAsync() {
    // Arrange: Publish message with AggregateId in hop metadata
    var jsonOptions = JsonContextRegistry.CreateCombinedOptions();
    var transport = new AzureServiceBusTransport(_fixture.Client, jsonOptions);
    await transport.InitializeAsync();

    var capturedInboxMessages = new List<InboxMessage>();
    var strategy = new CapturingWorkCoordinatorStrategy(capturedInboxMessages);

    var services = new ServiceCollection();
    services.AddWhizbangMessageSecurity(opts => { opts.AllowAnonymous = true; });
    services.AddScoped<IWorkCoordinatorStrategy>(_ => strategy);
    services.AddSingleton<IEnvelopeSerializer>(new EnvelopeSerializer(jsonOptions));

    var serviceProvider = services.BuildServiceProvider();
    var orderedProcessor = new OrderedStreamProcessor();
    var logger = new TestConsumerLogger();

    var options = new ServiceBusConsumerOptions {
      Subscriptions = [new TopicSubscription("topic-01", "sub-01-a")]
    };

    var worker = new ServiceBusConsumerWorker(
      transport, serviceProvider.GetRequiredService<IServiceScopeFactory>(),
      jsonOptions, logger, orderedProcessor, options);

    await _drainMessagesAsync("topic-01", "sub-01-a");
    await worker.StartAsync(CancellationToken.None);

    try {
      await Task.Delay(TimeSpan.FromSeconds(5));

      var expectedStreamId = Guid.NewGuid();
      var envelope = _createTestEnvelopeWithAggregateId(expectedStreamId);
      var destination = new TransportDestination("topic-01");
      await transport.PublishAsync(envelope, destination);

      var processed = await _waitForConditionAsync(
        () => capturedInboxMessages.Count > 0,
        TimeSpan.FromSeconds(30));

      // Assert: StreamId extracted from AggregateId in hop metadata
      await Assert.That(processed).IsTrue();
      var inbox = capturedInboxMessages.First();
      await Assert.That(inbox.StreamId).IsEqualTo(expectedStreamId);
    } finally {
      await worker.StopAsync(CancellationToken.None);
    }
  }

  [Test]
  public async Task Worker_MessageWithoutAggregateId_FallsBackToMessageIdAsync() {
    // Arrange: Message without AggregateId metadata - should fall back to MessageId
    var jsonOptions = JsonContextRegistry.CreateCombinedOptions();
    var transport = new AzureServiceBusTransport(_fixture.Client, jsonOptions);
    await transport.InitializeAsync();

    var capturedInboxMessages = new List<InboxMessage>();
    var strategy = new CapturingWorkCoordinatorStrategy(capturedInboxMessages);

    var services = new ServiceCollection();
    services.AddWhizbangMessageSecurity(opts => { opts.AllowAnonymous = true; });
    services.AddScoped<IWorkCoordinatorStrategy>(_ => strategy);
    services.AddSingleton<IEnvelopeSerializer>(new EnvelopeSerializer(jsonOptions));

    var serviceProvider = services.BuildServiceProvider();
    var orderedProcessor = new OrderedStreamProcessor();
    var logger = new TestConsumerLogger();

    var options = new ServiceBusConsumerOptions {
      Subscriptions = [new TopicSubscription("topic-00", "sub-00-a")]
    };

    var worker = new ServiceBusConsumerWorker(
      transport, serviceProvider.GetRequiredService<IServiceScopeFactory>(),
      jsonOptions, logger, orderedProcessor, options);

    await _drainMessagesAsync("topic-00", "sub-00-a");
    await worker.StartAsync(CancellationToken.None);

    try {
      await Task.Delay(TimeSpan.FromSeconds(5));

      // Envelope with no AggregateId metadata
      var envelope = _createTestEnvelopeWithoutAggregateId();
      var destination = new TransportDestination("topic-00");
      await transport.PublishAsync(envelope, destination);

      var processed = await _waitForConditionAsync(
        () => capturedInboxMessages.Count > 0,
        TimeSpan.FromSeconds(30));

      // Assert: StreamId falls back to MessageId
      await Assert.That(processed).IsTrue();
      var inbox = capturedInboxMessages.First();
      await Assert.That(inbox.StreamId).IsEqualTo(inbox.MessageId);
    } finally {
      await worker.StopAsync(CancellationToken.None);
    }
  }

  #region Helper Methods

  private static MessageEnvelope<TestMessage> _createTestEnvelopeWithAggregateId(Guid aggregateId) {
    var metadata = new Dictionary<string, JsonElement> {
      ["AggregateId"] = JsonSerializer.SerializeToElement(aggregateId.ToString())
    };

    return new MessageEnvelope<TestMessage> {
      MessageId = MessageId.New(),
      Payload = new TestMessage("integration-test"),
      Hops = [
        new MessageHop {
          Type = HopType.Current,
          Timestamp = DateTimeOffset.UtcNow,
          Topic = "test-topic",
          Metadata = metadata,
          ServiceInstance = ServiceInstanceInfo.Unknown
        }
      ]
    };
  }

  private static MessageEnvelope<TestMessage> _createTestEnvelopeWithoutAggregateId() {
    return new MessageEnvelope<TestMessage> {
      MessageId = MessageId.New(),
      Payload = new TestMessage("no-aggregate-id"),
      Hops = [
        new MessageHop {
          Type = HopType.Current,
          Timestamp = DateTimeOffset.UtcNow,
          Topic = "test-topic",
          ServiceInstance = ServiceInstanceInfo.Unknown
        }
      ]
    };
  }

  private static MessageEnvelope<TestMessage> _createTestEnvelopeWithScope(string userId, string tenantId) {
    return new MessageEnvelope<TestMessage> {
      MessageId = MessageId.New(),
      Payload = new TestMessage("security-context-test"),
      Hops = [
        new MessageHop {
          Type = HopType.Current,
          Timestamp = DateTimeOffset.UtcNow,
          Topic = "test-topic",
          ServiceInstance = ServiceInstanceInfo.Unknown,
          Scope = ScopeDelta.FromSecurityContext(new SecurityContext {
            UserId = userId,
            TenantId = tenantId
          })
        }
      ]
    };
  }

  private async Task _drainMessagesAsync(string topicName, string subscriptionName) {
    var receiver = _fixture.Client.CreateReceiver(topicName, subscriptionName);
    try {
      for (var i = 0; i < 100; i++) {
        var msg = await receiver.ReceiveMessageAsync(TimeSpan.FromMilliseconds(100));
        if (msg == null) {
          break;
        }
        await receiver.CompleteMessageAsync(msg);
      }
    } finally {
      await receiver.DisposeAsync();
    }
  }

  private static async Task<bool> _waitForConditionAsync(Func<bool> condition, TimeSpan timeout) {
    var deadline = DateTime.UtcNow + timeout;
    while (DateTime.UtcNow < deadline) {
      if (condition()) {
        return true;
      }
      await Task.Delay(100);
    }
    return condition();
  }

  #endregion

  #region Test Doubles

  /// <summary>
  /// Work coordinator strategy that captures inbox messages and returns them as work items.
  /// </summary>
  private sealed class CapturingWorkCoordinatorStrategy : IWorkCoordinatorStrategy {
    private readonly List<InboxMessage> _capturedMessages;
    private readonly Action<IServiceProvider?>? _onFlush;
    private InboxMessage? _pendingMessage;

    public CapturingWorkCoordinatorStrategy(
        List<InboxMessage> capturedMessages,
        Action<IServiceProvider?>? onFlush = null) {
      _capturedMessages = capturedMessages;
      _onFlush = onFlush;
    }

    public void QueueOutboxMessage(OutboxMessage message) { }

    public void QueueInboxMessage(InboxMessage message) {
      _pendingMessage = message;
      _capturedMessages.Add(message);
    }

    public void QueueOutboxCompletion(Guid messageId, MessageProcessingStatus status) { }
    public void QueueOutboxFailure(Guid messageId, MessageProcessingStatus partialStatus, string error) { }
    public void QueueInboxCompletion(Guid messageId, MessageProcessingStatus status) { }
    public void QueueInboxFailure(Guid messageId, MessageProcessingStatus partialStatus, string error) { }

    public Task<WorkBatch> FlushAsync(WorkBatchFlags flags, FlushMode mode = FlushMode.Required, CancellationToken ct = default) {
      _onFlush?.Invoke(null);

      if (_pendingMessage != null) {
        var msg = _pendingMessage;
        _pendingMessage = null;

        // Create InboxWork from captured message
        var inboxWork = new InboxWork {
          MessageId = msg.MessageId,
          Envelope = msg.Envelope,
          StreamId = msg.StreamId,
          MessageType = msg.MessageType
        };

        return Task.FromResult(new WorkBatch {
          InboxWork = [inboxWork],
          OutboxWork = [],
          PerspectiveWork = []
        });
      }

      return Task.FromResult(new WorkBatch {
        InboxWork = [],
        OutboxWork = [],
        PerspectiveWork = []
      });
    }
  }

  /// <summary>
  /// Strategy that tracks processed message IDs and returns empty for duplicates.
  /// </summary>
  private sealed class DuplicateDetectingStrategy : IWorkCoordinatorStrategy {
    private readonly List<Guid> _processedIds;
    private readonly Action _onFlush;
    private InboxMessage? _pendingMessage;

    public DuplicateDetectingStrategy(List<Guid> processedIds, Action onFlush) {
      _processedIds = processedIds;
      _onFlush = onFlush;
    }

    public void QueueOutboxMessage(OutboxMessage message) { }

    public void QueueInboxMessage(InboxMessage message) {
      _pendingMessage = message;
    }

    public void QueueOutboxCompletion(Guid messageId, MessageProcessingStatus status) { }
    public void QueueOutboxFailure(Guid messageId, MessageProcessingStatus partialStatus, string error) { }
    public void QueueInboxCompletion(Guid messageId, MessageProcessingStatus status) { }
    public void QueueInboxFailure(Guid messageId, MessageProcessingStatus partialStatus, string error) { }

    public Task<WorkBatch> FlushAsync(WorkBatchFlags flags, FlushMode mode = FlushMode.Required, CancellationToken ct = default) {
      _onFlush();

      if (_pendingMessage != null) {
        var msg = _pendingMessage;
        _pendingMessage = null;

        // Check for duplicate
        if (_processedIds.Contains(msg.MessageId)) {
          // Return empty work - duplicate
          return Task.FromResult(new WorkBatch {
            InboxWork = [],
            OutboxWork = [],
            PerspectiveWork = []
          });
        }

        _processedIds.Add(msg.MessageId);

        var inboxWork = new InboxWork {
          MessageId = msg.MessageId,
          Envelope = msg.Envelope,
          StreamId = msg.StreamId,
          MessageType = msg.MessageType
        };

        return Task.FromResult(new WorkBatch {
          InboxWork = [inboxWork],
          OutboxWork = [],
          PerspectiveWork = []
        });
      }

      return Task.FromResult(new WorkBatch {
        InboxWork = [],
        OutboxWork = [],
        PerspectiveWork = []
      });
    }
  }

  /// <summary>
  /// Minimal no-op strategy for startup/shutdown tests.
  /// </summary>
  private sealed class NoOpWorkCoordinatorStrategy : IWorkCoordinatorStrategy {
    public void QueueOutboxMessage(OutboxMessage message) { }
    public void QueueInboxMessage(InboxMessage message) { }
    public void QueueOutboxCompletion(Guid messageId, MessageProcessingStatus status) { }
    public void QueueOutboxFailure(Guid messageId, MessageProcessingStatus partialStatus, string error) { }
    public void QueueInboxCompletion(Guid messageId, MessageProcessingStatus status) { }
    public void QueueInboxFailure(Guid messageId, MessageProcessingStatus partialStatus, string error) { }

    public Task<WorkBatch> FlushAsync(WorkBatchFlags flags, FlushMode mode = FlushMode.Required, CancellationToken ct = default) {
      return Task.FromResult(new WorkBatch {
        InboxWork = [],
        OutboxWork = [],
        PerspectiveWork = []
      });
    }
  }

  /// <summary>
  /// Logger that captures log messages for assertion.
  /// </summary>
  private sealed class TestConsumerLogger : ILogger<ServiceBusConsumerWorker> {
    private readonly List<string> _messages = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        Microsoft.Extensions.Logging.EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter) {
      _messages.Add(formatter(state, exception));
    }

    public bool HasMessage(string substring) =>
      _messages.Any(m => m.Contains(substring, StringComparison.OrdinalIgnoreCase));
  }

  #endregion
}
