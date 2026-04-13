using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.SystemEvents;
using Whizbang.Core.Validation;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Additional coverage tests for ScopedWorkCoordinatorStrategy targeting remaining uncovered branches.
/// Focuses on: DisposeAsync with pending audit messages, dispose with inbox-only + logger,
/// dispose without logger + unflushed items, flush with logger + non-event outbox (no audit),
/// null pending audit messages in dispose, and other edge combinations.
/// </summary>
public class ScopedWorkCoordinatorStrategyFullCoverageTests {
  private readonly Uuid7IdProvider _idProvider = new();

  // Test events
  public record _coverageEvent1([StreamId] string Id = "coverage-1") : IEvent { }
  public record _coverageEvent2([StreamId] string Id = "coverage-2") : IEvent { }

  // ========================================
  // DISPOSE: unflushed items WITHOUT logger — exercises null-logger path
  // in DisposeAsync (lines 203-212: !_queues.IsEmpty with _logger == null)
  // ========================================

  [Test]
  public async Task DisposeAsync_WithUnflushedItems_WithoutLogger_StillFlushesAsync() {
    // Arrange
    var coordinator = new ScopedCoverageCoordinator();
    var instanceProvider = new ScopedCoverageInstanceProvider();
    var options = new WorkCoordinatorOptions();

    var sut = new ScopedWorkCoordinatorStrategy(
      coordinator, instanceProvider, null, options
    );

    _queueOutboxMessage(sut);
    _queueInboxMessage(sut);
    sut.QueueOutboxCompletion(Guid.NewGuid(), MessageProcessingStatus.Published);
    sut.QueueInboxCompletion(Guid.NewGuid(), MessageProcessingStatus.Stored);
    sut.QueueOutboxFailure(Guid.NewGuid(), MessageProcessingStatus.Failed, "err1");
    sut.QueueInboxFailure(Guid.NewGuid(), MessageProcessingStatus.Failed, "err2");

    // Act
    await sut.DisposeAsync();

    // Assert — all items flushed even without logger
    await Assert.That(coordinator.ProcessWorkBatchCallCount).IsEqualTo(1);
  }

  // ========================================
  // DISPOSE: unflushed inbox-only items with logger
  // ========================================

  [Test]
  public async Task DisposeAsync_InboxOnly_WithLogger_LogsWarningAndFlushesAsync() {
    // Arrange
    var logger = new ScopedCoverageLogger();
    var coordinator = new ScopedCoverageCoordinator();
    var instanceProvider = new ScopedCoverageInstanceProvider();
    var options = new WorkCoordinatorOptions();

    var sut = new ScopedWorkCoordinatorStrategy(
      coordinator, instanceProvider, null, options, logger: logger
    );

    _queueInboxMessage(sut);

    // Act
    await sut.DisposeAsync();

    // Assert
    await Assert.That(coordinator.ProcessWorkBatchCallCount).IsEqualTo(1);
    await Assert.That(logger.LogCount).IsGreaterThan(0);
  }

  // ========================================
  // DISPOSE: pending audit messages in dispose path
  // ========================================

  [Test]
  public async Task DisposeAsync_WithAuditEnabled_UnflushedEvent_IncludesAuditMessagesAsync() {
    // Arrange
    var coordinator = new ScopedCoverageCoordinator();
    var instanceProvider = new ScopedCoverageInstanceProvider();
    var options = new WorkCoordinatorOptions();
    var systemEventOptions = new SystemEventOptions();
    systemEventOptions.EnableEventAudit();

    var sut = new ScopedWorkCoordinatorStrategy(
      coordinator, instanceProvider, null, options,
      dependencies: new ScopedWorkCoordinatorDependencies {
        SystemEventOptions = systemEventOptions
      }
    );

    // Queue an event (generates audit message internally)
    _queueOutboxMessage(sut);

    // Act — dispose should include pending audit messages
    await sut.DisposeAsync();

    // Assert
    await Assert.That(coordinator.ProcessWorkBatchCallCount).IsEqualTo(1);
    // Audit message should have been merged into the outbox batch
    await Assert.That(coordinator.LastNewOutboxMessages.Length).IsGreaterThanOrEqualTo(1);
  }

  // ========================================
  // FLUSH: non-event outbox message (no audit generated)
  // ========================================

  [Test]
  public async Task FlushAsync_NonEventOutbox_WithAuditEnabled_NoAuditMessageGeneratedAsync() {
    // Arrange
    var coordinator = new ScopedCoverageCoordinator();
    var instanceProvider = new ScopedCoverageInstanceProvider();
    var options = new WorkCoordinatorOptions();
    var systemEventOptions = new SystemEventOptions();
    systemEventOptions.EnableEventAudit();

    var sut = new ScopedWorkCoordinatorStrategy(
      coordinator, instanceProvider, null, options,
      dependencies: new ScopedWorkCoordinatorDependencies {
        SystemEventOptions = systemEventOptions
      }
    );

    // Queue a NON-event outbox message (IsEvent = false)
    _queueNonEventOutboxMessage(sut);

    // Act
    await sut.FlushAsync(WorkBatchOptions.None);

    // Assert — only the original message, no audit
    await Assert.That(coordinator.ProcessWorkBatchCallCount).IsEqualTo(1);
    await Assert.That(coordinator.LastNewOutboxMessages.Length).IsEqualTo(1);

    // Cleanup
    await sut.DisposeAsync();
  }

  // ========================================
  // FLUSH: with logger, inbox messages but 0 outbox work returned
  // exercises the "else if (outboxMessages.Length > 0)" at line 181
  // ========================================

  [Test]
  public async Task FlushAsync_WithLogger_InboxOnlyQueued_NoOutboxWorkReturned_DoesNotLogNoWorkAsync() {
    // Arrange
    var logger = new ScopedCoverageLogger();
    var coordinator = new ScopedCoverageCoordinator();
    var instanceProvider = new ScopedCoverageInstanceProvider();
    var options = new WorkCoordinatorOptions();

    var sut = new ScopedWorkCoordinatorStrategy(
      coordinator, instanceProvider, null, options, logger: logger
    );

    _queueInboxMessage(sut);

    // Act
    await sut.FlushAsync(WorkBatchOptions.None);

    // Assert
    await Assert.That(coordinator.ProcessWorkBatchCallCount).IsEqualTo(1);

    // Cleanup
    await sut.DisposeAsync();
  }

  // ========================================
  // FLUSH: with logger and metrics, empty queue
  // ========================================

  [Test]
  public async Task FlushAsync_EmptyQueues_WithLoggerAndMetrics_ReturnsEmptyBatchAsync() {
    // Arrange
    var logger = new ScopedCoverageLogger();
    var metrics = new WorkCoordinatorMetrics(new WhizbangMetrics());
    var coordinator = new ScopedCoverageCoordinator();
    var instanceProvider = new ScopedCoverageInstanceProvider();
    var options = new WorkCoordinatorOptions();

    var sut = new ScopedWorkCoordinatorStrategy(
      coordinator, instanceProvider, null, options, logger: logger, metrics: metrics
    );

    // Act
    var result = await sut.FlushAsync(WorkBatchOptions.None);

    // Assert
    await Assert.That(result.OutboxWork).IsEmpty();
    await Assert.That(coordinator.ProcessWorkBatchCallCount).IsEqualTo(0);

    // Cleanup
    await sut.DisposeAsync();
  }

  // ========================================
  // DISPOSE: pending audit messages NULL path (no audit configured)
  // exercises: pendingAuditMessages = _queues.PendingAuditMessages.Count > 0 ? ... : null;
  // ========================================

  [Test]
  public async Task DisposeAsync_NoAuditEnabled_PendingAuditMessagesNullAsync() {
    // Arrange — no SystemEventOptions
    var coordinator = new ScopedCoverageCoordinator();
    var instanceProvider = new ScopedCoverageInstanceProvider();
    var options = new WorkCoordinatorOptions();

    var sut = new ScopedWorkCoordinatorStrategy(
      coordinator, instanceProvider, null, options
    );

    _queueOutboxMessage(sut);

    // Act
    await sut.DisposeAsync();

    // Assert
    await Assert.That(coordinator.ProcessWorkBatchCallCount).IsEqualTo(1);
  }

  // ========================================
  // FLUSH: all types with logger and metrics (comprehensive)
  // ========================================

  [Test]
  public async Task FlushAsync_AllQueueTypes_WithLoggerAndMetrics_FlushesEverythingAsync() {
    // Arrange
    var logger = new ScopedCoverageLogger();
    var metrics = new WorkCoordinatorMetrics(new WhizbangMetrics());
    var coordinator = new ScopedCoverageCoordinator();
    var instanceProvider = new ScopedCoverageInstanceProvider();
    var options = new WorkCoordinatorOptions();

    var sut = new ScopedWorkCoordinatorStrategy(
      coordinator, instanceProvider, null, options, logger: logger, metrics: metrics
    );

    _queueOutboxMessage(sut);
    _queueInboxMessage(sut);
    sut.QueueOutboxCompletion(Guid.NewGuid(), MessageProcessingStatus.Published);
    sut.QueueInboxCompletion(Guid.NewGuid(), MessageProcessingStatus.Stored);
    sut.QueueOutboxFailure(Guid.NewGuid(), MessageProcessingStatus.Failed, "err1");
    sut.QueueInboxFailure(Guid.NewGuid(), MessageProcessingStatus.Failed, "err2");

    // Act
    var result = await sut.FlushAsync(WorkBatchOptions.None);

    // Assert
    await Assert.That(coordinator.ProcessWorkBatchCallCount).IsEqualTo(1);
    await Assert.That(logger.LogCount).IsGreaterThan(5);

    // Cleanup
    await sut.DisposeAsync();
  }

  // ========================================
  // DISPOSE: error during flush without logger
  // ========================================

  [Test]
  public async Task DisposeAsync_FlushThrows_WithoutLogger_SwallowsExceptionAsync() {
    // Arrange
    var throwingCoordinator = new ScopedCoverageThrowingCoordinator();
    var instanceProvider = new ScopedCoverageInstanceProvider();
    var options = new WorkCoordinatorOptions();

    var sut = new ScopedWorkCoordinatorStrategy(
      throwingCoordinator, instanceProvider, null, options
    );

    _queueOutboxMessage(sut);

    // Act & Assert — should not propagate
    await sut.DisposeAsync();
  }

  // ========================================
  // FLUSH: with inbox completions only (no messages)
  // ========================================

  [Test]
  public async Task FlushAsync_InboxCompletionsOnly_WithLogger_FlushesAsync() {
    // Arrange
    var logger = new ScopedCoverageLogger();
    var coordinator = new ScopedCoverageCoordinator();
    var instanceProvider = new ScopedCoverageInstanceProvider();
    var options = new WorkCoordinatorOptions();

    var sut = new ScopedWorkCoordinatorStrategy(
      coordinator, instanceProvider, null, options, logger: logger
    );

    sut.QueueInboxCompletion(Guid.NewGuid(), MessageProcessingStatus.Stored);

    // Act
    await sut.FlushAsync(WorkBatchOptions.None);

    // Assert
    await Assert.That(coordinator.ProcessWorkBatchCallCount).IsEqualTo(1);

    // Cleanup
    await sut.DisposeAsync();
  }

  // ========================================
  // FLUSH: with outbox failures only (no messages)
  // ========================================

  [Test]
  public async Task FlushAsync_OutboxFailuresOnly_WithLogger_FlushesAsync() {
    // Arrange
    var logger = new ScopedCoverageLogger();
    var coordinator = new ScopedCoverageCoordinator();
    var instanceProvider = new ScopedCoverageInstanceProvider();
    var options = new WorkCoordinatorOptions();

    var sut = new ScopedWorkCoordinatorStrategy(
      coordinator, instanceProvider, null, options, logger: logger
    );

    sut.QueueOutboxFailure(Guid.NewGuid(), MessageProcessingStatus.Failed, "test error");

    // Act
    await sut.FlushAsync(WorkBatchOptions.None);

    // Assert
    await Assert.That(coordinator.ProcessWorkBatchCallCount).IsEqualTo(1);

    // Cleanup
    await sut.DisposeAsync();
  }

  // ========================================
  // FLUSH: with inbox failures only
  // ========================================

  [Test]
  public async Task FlushAsync_InboxFailuresOnly_WithLogger_FlushesAsync() {
    // Arrange
    var logger = new ScopedCoverageLogger();
    var coordinator = new ScopedCoverageCoordinator();
    var instanceProvider = new ScopedCoverageInstanceProvider();
    var options = new WorkCoordinatorOptions();

    var sut = new ScopedWorkCoordinatorStrategy(
      coordinator, instanceProvider, null, options, logger: logger
    );

    sut.QueueInboxFailure(Guid.NewGuid(), MessageProcessingStatus.Failed, "inbox err");

    // Act
    await sut.FlushAsync(WorkBatchOptions.None);

    // Assert
    await Assert.That(coordinator.ProcessWorkBatchCallCount).IsEqualTo(1);

    // Cleanup
    await sut.DisposeAsync();
  }

  // ========================================
  // DISPOSE: with only completions and failures (no messages)
  // ========================================

  [Test]
  public async Task DisposeAsync_CompletionsAndFailuresOnly_WithLogger_FlushesAsync() {
    // Arrange
    var logger = new ScopedCoverageLogger();
    var coordinator = new ScopedCoverageCoordinator();
    var instanceProvider = new ScopedCoverageInstanceProvider();
    var options = new WorkCoordinatorOptions();

    var sut = new ScopedWorkCoordinatorStrategy(
      coordinator, instanceProvider, null, options, logger: logger
    );

    sut.QueueOutboxCompletion(Guid.NewGuid(), MessageProcessingStatus.Published);
    sut.QueueInboxFailure(Guid.NewGuid(), MessageProcessingStatus.Failed, "err");

    // Act
    await sut.DisposeAsync();

    // Assert
    await Assert.That(coordinator.ProcessWorkBatchCallCount).IsEqualTo(1);
    await Assert.That(logger.LogCount).IsGreaterThan(0);
  }

  // ========================================
  // DISPOSE: empty queues with logger (no flush needed)
  // ========================================

  [Test]
  public async Task DisposeAsync_EmptyQueues_WithLogger_DoesNotFlushAsync() {
    // Arrange
    var logger = new ScopedCoverageLogger();
    var coordinator = new ScopedCoverageCoordinator();
    var instanceProvider = new ScopedCoverageInstanceProvider();
    var options = new WorkCoordinatorOptions();

    var sut = new ScopedWorkCoordinatorStrategy(
      coordinator, instanceProvider, null, options, logger: logger
    );

    // Act
    await sut.DisposeAsync();

    // Assert — no flush attempt
    await Assert.That(coordinator.ProcessWorkBatchCallCount).IsEqualTo(0);
  }

  // ========================================
  // FLUSH: with ScopeFactory dependency
  // ========================================

  [Test]
  public async Task FlushAsync_WithScopeFactory_FlushesSuccessfullyAsync() {
    // Arrange
    var services = new ServiceCollection();
    services.AddScoped<IWorkCoordinator, ScopedCoverageCoordinator>();
    var serviceProvider = services.BuildServiceProvider();
    var scopeFactory = serviceProvider.GetRequiredService<IServiceScopeFactory>();

    var coordinator = new ScopedCoverageCoordinator();
    var instanceProvider = new ScopedCoverageInstanceProvider();
    var options = new WorkCoordinatorOptions();

    var sut = new ScopedWorkCoordinatorStrategy(
      coordinator, instanceProvider, null, options,
      dependencies: new ScopedWorkCoordinatorDependencies {
        ScopeFactory = scopeFactory
      }
    );

    _queueOutboxMessage(sut);

    // Act
    await sut.FlushAsync(WorkBatchOptions.None);

    // Assert
    await Assert.That(coordinator.ProcessWorkBatchCallCount).IsEqualTo(1);

    // Cleanup
    await sut.DisposeAsync();
  }

  // ========================================
  // IWorkFlusher with empty queues
  // ========================================

  [Test]
  public async Task IWorkFlusher_FlushAsync_EmptyQueues_DoesNotCallCoordinatorAsync() {
    // Arrange
    var coordinator = new ScopedCoverageCoordinator();
    var instanceProvider = new ScopedCoverageInstanceProvider();
    var options = new WorkCoordinatorOptions();

    var sut = new ScopedWorkCoordinatorStrategy(
      coordinator, instanceProvider, null, options
    );

    // Act
    IWorkFlusher flusher = sut;
    await flusher.FlushAsync(CancellationToken.None);

    // Assert
    await Assert.That(coordinator.ProcessWorkBatchCallCount).IsEqualTo(0);

    // Cleanup
    await sut.DisposeAsync();
  }

  // ========================================
  // StreamId guard: null StreamId is valid
  // ========================================

  [Test]
  public async Task QueueOutboxMessage_NullStreamId_DoesNotThrowAsync() {
    // Arrange
    var coordinator = new ScopedCoverageCoordinator();
    var instanceProvider = new ScopedCoverageInstanceProvider();
    var options = new WorkCoordinatorOptions();

    var sut = new ScopedWorkCoordinatorStrategy(
      coordinator, instanceProvider, null, options
    );

    var messageId = _idProvider.NewGuid();
    var jsonOptions = Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions();
    var envelope = new MessageEnvelope<_coverageEvent1> {
      MessageId = MessageId.From(messageId),
      Payload = new _coverageEvent1(),
      Hops = [new MessageHop { ServiceInstance = ServiceInstanceInfo.Unknown }],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };
    var envelopeJson = JsonSerializer.Serialize((object)envelope, jsonOptions);
    var jsonEnvelope = JsonSerializer.Deserialize<MessageEnvelope<JsonElement>>(envelopeJson, jsonOptions)!;

    // Act & Assert — null StreamId should not throw
    sut.QueueOutboxMessage(new OutboxMessage {
      MessageId = messageId,
      Destination = "test-topic",
      Envelope = jsonEnvelope,
      EnvelopeType = "TestEnvelope, TestAssembly",
      StreamId = null,
      IsEvent = false,
      MessageType = "TestMessage, TestAssembly",
      Metadata = new EnvelopeMetadata { MessageId = MessageId.From(messageId), Hops = [] }
    });

    // Cleanup
    await sut.DisposeAsync();
  }

  [Test]
  public async Task QueueInboxMessage_NullStreamId_DoesNotThrowAsync() {
    // Arrange
    var coordinator = new ScopedCoverageCoordinator();
    var instanceProvider = new ScopedCoverageInstanceProvider();
    var options = new WorkCoordinatorOptions();

    var sut = new ScopedWorkCoordinatorStrategy(
      coordinator, instanceProvider, null, options
    );

    var messageId = _idProvider.NewGuid();
    var jsonOptions = Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions();
    var envelope = new MessageEnvelope<_coverageEvent2> {
      MessageId = MessageId.From(messageId),
      Payload = new _coverageEvent2(),
      Hops = [new MessageHop { ServiceInstance = ServiceInstanceInfo.Unknown }],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };
    var envelopeJson = JsonSerializer.Serialize((object)envelope, jsonOptions);
    var jsonEnvelope = JsonSerializer.Deserialize<MessageEnvelope<JsonElement>>(envelopeJson, jsonOptions)!;

    // Act & Assert — null StreamId should not throw
    sut.QueueInboxMessage(new InboxMessage {
      MessageId = messageId,
      HandlerName = "TestHandler",
      Envelope = jsonEnvelope,
      EnvelopeType = "TestEnvelope, TestAssembly",
      StreamId = null,
      IsEvent = false,
      MessageType = "TestMessage, TestAssembly"
    });

    // Cleanup
    await sut.DisposeAsync();
  }

  // ========================================
  // Test Helpers
  // ========================================

  private void _queueOutboxMessage(ScopedWorkCoordinatorStrategy strategy) {
    var messageId = _idProvider.NewGuid();
    var jsonOptions = Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions();
    var envelope = new MessageEnvelope<_coverageEvent1> {
      MessageId = MessageId.From(messageId),
      Payload = new _coverageEvent1(),
      Hops = [new MessageHop { ServiceInstance = ServiceInstanceInfo.Unknown }],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };
    var envelopeJson = JsonSerializer.Serialize((object)envelope, jsonOptions);
    var jsonEnvelope = JsonSerializer.Deserialize<MessageEnvelope<JsonElement>>(envelopeJson, jsonOptions)!;

    strategy.QueueOutboxMessage(new OutboxMessage {
      MessageId = messageId,
      Destination = "test-topic",
      Envelope = jsonEnvelope,
      EnvelopeType = "TestEnvelope, TestAssembly",
      StreamId = _idProvider.NewGuid(),
      IsEvent = true,
      MessageType = "TestMessage, TestAssembly",
      Metadata = new EnvelopeMetadata {
        MessageId = MessageId.From(messageId),
        Hops = []
      }
    });
  }

  private void _queueNonEventOutboxMessage(ScopedWorkCoordinatorStrategy strategy) {
    var messageId = _idProvider.NewGuid();
    var jsonOptions = Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions();
    var envelope = new MessageEnvelope<_coverageEvent1> {
      MessageId = MessageId.From(messageId),
      Payload = new _coverageEvent1(),
      Hops = [new MessageHop { ServiceInstance = ServiceInstanceInfo.Unknown }],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };
    var envelopeJson = JsonSerializer.Serialize((object)envelope, jsonOptions);
    var jsonEnvelope = JsonSerializer.Deserialize<MessageEnvelope<JsonElement>>(envelopeJson, jsonOptions)!;

    strategy.QueueOutboxMessage(new OutboxMessage {
      MessageId = messageId,
      Destination = "test-topic",
      Envelope = jsonEnvelope,
      EnvelopeType = "TestEnvelope, TestAssembly",
      StreamId = _idProvider.NewGuid(),
      IsEvent = false, // NOT an event
      MessageType = "TestCommand, TestAssembly",
      Metadata = new EnvelopeMetadata {
        MessageId = MessageId.From(messageId),
        Hops = []
      }
    });
  }

  private void _queueInboxMessage(ScopedWorkCoordinatorStrategy strategy) {
    var messageId = _idProvider.NewGuid();
    var jsonOptions = Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions();
    var envelope = new MessageEnvelope<_coverageEvent2> {
      MessageId = MessageId.From(messageId),
      Payload = new _coverageEvent2(),
      Hops = [new MessageHop { ServiceInstance = ServiceInstanceInfo.Unknown }],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };
    var envelopeJson = JsonSerializer.Serialize((object)envelope, jsonOptions);
    var jsonEnvelope = JsonSerializer.Deserialize<MessageEnvelope<JsonElement>>(envelopeJson, jsonOptions)!;

    strategy.QueueInboxMessage(new InboxMessage {
      MessageId = messageId,
      HandlerName = "TestHandler",
      Envelope = jsonEnvelope,
      EnvelopeType = "TestEnvelope, TestAssembly",
      StreamId = _idProvider.NewGuid(),
      IsEvent = true,
      MessageType = "TestMessage, TestAssembly"
    });
  }

  private sealed class ScopedCoverageLogger : ILogger<ScopedWorkCoordinatorStrategy> {
    public int LogCount { get; private set; }

    public void Log<TState>(
      LogLevel logLevel,
      Microsoft.Extensions.Logging.EventId eventId,
      TState state,
      Exception? exception,
      Func<TState, Exception?, string> formatter) {
      LogCount++;
    }

    public bool IsEnabled(LogLevel logLevel) => true;
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
  }

  private sealed class ScopedCoverageCoordinator : IWorkCoordinator {
    public int ProcessWorkBatchCallCount { get; private set; }
    public OutboxMessage[] LastNewOutboxMessages { get; private set; } = [];
    public InboxMessage[] LastNewInboxMessages { get; private set; } = [];

    public Task<WorkBatch> ProcessWorkBatchAsync(
      ProcessWorkBatchRequest request,
      CancellationToken cancellationToken = default) {
      ProcessWorkBatchCallCount++;
      LastNewOutboxMessages = request.NewOutboxMessages;
      LastNewInboxMessages = request.NewInboxMessages;
      return Task.FromResult(new WorkBatch {
        OutboxWork = [],
        InboxWork = [],
        PerspectiveWork = []
      });
    }

    public Task ReportPerspectiveCompletionAsync(
      PerspectiveCursorCompletion completion,
      CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task ReportPerspectiveFailureAsync(
      PerspectiveCursorFailure failure,
      CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount = 2, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new WorkCoordinatorStatistics());

    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(
      Guid streamId,
      string perspectiveName,
      CancellationToken cancellationToken = default) =>
      Task.FromResult<PerspectiveCursorInfo?>(null);
  }

  private sealed class ScopedCoverageThrowingCoordinator : IWorkCoordinator {
    public Task<WorkBatch> ProcessWorkBatchAsync(
      ProcessWorkBatchRequest request,
      CancellationToken cancellationToken = default) {
      throw new InvalidOperationException("Simulated failure");
    }

    public Task ReportPerspectiveCompletionAsync(
      PerspectiveCursorCompletion completion,
      CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task ReportPerspectiveFailureAsync(
      PerspectiveCursorFailure failure,
      CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount = 2, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new WorkCoordinatorStatistics());

    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(
      Guid streamId,
      string perspectiveName,
      CancellationToken cancellationToken = default) =>
      Task.FromResult<PerspectiveCursorInfo?>(null);
  }

  private sealed class ScopedCoverageInstanceProvider : IServiceInstanceProvider {
    public Guid InstanceId { get; } = Guid.NewGuid();
    public string ServiceName { get; } = "CoverageTestService";
    public string HostName { get; } = "test-host";
    public int ProcessId { get; } = 99999;

    public ServiceInstanceInfo ToInfo() => new() {
      ServiceName = ServiceName,
      InstanceId = InstanceId,
      HostName = HostName,
      ProcessId = ProcessId
    };
  }
}
