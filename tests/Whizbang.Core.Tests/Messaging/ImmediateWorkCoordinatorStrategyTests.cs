using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.SystemEvents;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Tests for ImmediateWorkCoordinatorStrategy - verifies immediate flush behavior.
/// </summary>
public class ImmediateWorkCoordinatorStrategyTests {
  private readonly Uuid7IdProvider _idProvider = new();

  // Simple test message for envelope creation
  public record _testEvent([StreamId] string Data) : IEvent;

  // ========================================
  // Priority 3 Tests: Immediate Strategy
  // ========================================

  [Test]
  public async Task FlushAsync_ImmediatelyCallsWorkCoordinatorAsync() {
    // Arrange
    var fakeCoordinator = new FakeWorkCoordinator();
    var instanceProvider = new FakeServiceInstanceProvider();
    var options = new WorkCoordinatorOptions {
      IntervalMilliseconds = 1000,
      PartitionCount = 10000,
      LeaseSeconds = 300,
      AbandonStaleInstanceThresholdSeconds = 300,
      DebugMode = false
    };

    var sut = new ImmediateWorkCoordinatorStrategy(
      fakeCoordinator,
      instanceProvider,
      options
    );

    var messageId = _idProvider.NewGuid();
    var envelope = new MessageEnvelope<_testEvent> {
      MessageId = MessageId.From(messageId),
      Payload = new _testEvent("test-data"),
      Hops = [],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };

    // Serialize typed envelope to JsonElement envelope for OutboxMessage
    var jsonOptions = Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions();
    var envelopeJson = JsonSerializer.Serialize((object)envelope, jsonOptions);
    var jsonEnvelope = JsonSerializer.Deserialize<MessageEnvelope<JsonElement>>(envelopeJson, jsonOptions)
      ?? throw new InvalidOperationException("Failed to deserialize envelope");

    sut.QueueOutboxMessage(new OutboxMessage {
      MessageId = messageId,
      Destination = "test-topic",
      Envelope = jsonEnvelope,
      EnvelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[System.Object, System.Private.CoreLib]], Whizbang.Core",
      StreamId = _idProvider.NewGuid(),
      IsEvent = true,
      MessageType = "TestMessage, TestAssembly",
      Metadata = new EnvelopeMetadata {
        MessageId = MessageId.From(messageId),
        Hops = []
      }
    });

    // Act
    await sut.FlushAsync(WorkBatchOptions.None);

    // Assert - FlushAsync should immediately call the work coordinator's store path
    await Assert.That(fakeCoordinator.ProcessWorkBatchCallCount).IsEqualTo(1)
      .Because("Immediate strategy should call the work coordinator on FlushAsync");
    await Assert.That(fakeCoordinator.LastNewOutboxMessages).Count().IsEqualTo(1);
    await Assert.That(fakeCoordinator.LastNewOutboxMessages[0].MessageId).IsEqualTo(messageId);
  }

  [Test]
  public async Task QueueOutboxMessage_FlushesOnCallAsync() {
    // Arrange
    var fakeCoordinator = new FakeWorkCoordinator();
    var instanceProvider = new FakeServiceInstanceProvider();
    var options = new WorkCoordinatorOptions {
      IntervalMilliseconds = 1000,
      PartitionCount = 10000,
      LeaseSeconds = 300,
      AbandonStaleInstanceThresholdSeconds = 300,
      DebugMode = false
    };

    var sut = new ImmediateWorkCoordinatorStrategy(
      fakeCoordinator,
      instanceProvider,
      options
    );

    var messageId = _idProvider.NewGuid();
    var envelope = new MessageEnvelope<_testEvent> {
      MessageId = MessageId.From(messageId),
      Payload = new _testEvent("test-data"),
      Hops = [],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };

    // Serialize typed envelope to JsonElement envelope for OutboxMessage
    var jsonOptions = Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions();
    var envelopeJson = JsonSerializer.Serialize((object)envelope, jsonOptions);
    var jsonEnvelope = JsonSerializer.Deserialize<MessageEnvelope<JsonElement>>(envelopeJson, jsonOptions)
      ?? throw new InvalidOperationException("Failed to deserialize envelope");

    var outboxMessage = new OutboxMessage {
      MessageId = messageId,
      Destination = "test-topic",
      Envelope = jsonEnvelope,
      EnvelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[System.Object, System.Private.CoreLib]], Whizbang.Core",
      StreamId = _idProvider.NewGuid(),
      IsEvent = true,
      MessageType = "TestMessage, TestAssembly",
      Metadata = new EnvelopeMetadata {
        MessageId = MessageId.From(messageId),
        Hops = []
      }
    };

    // Act
    sut.QueueOutboxMessage(outboxMessage);
    await sut.FlushAsync(WorkBatchOptions.None);

    // Assert - Message should be passed to coordinator
    await Assert.That(fakeCoordinator.LastNewOutboxMessages).Count().IsEqualTo(1);
    await Assert.That(fakeCoordinator.LastNewOutboxMessages[0].MessageId).IsEqualTo(messageId);
    await Assert.That(fakeCoordinator.LastNewOutboxMessages[0].Destination).IsEqualTo("test-topic");
  }

  [Test]
  public async Task QueueInboxMessage_FlushesOnCallAsync() {
    // Arrange
    var fakeCoordinator = new FakeWorkCoordinator();
    var instanceProvider = new FakeServiceInstanceProvider();
    var options = new WorkCoordinatorOptions {
      IntervalMilliseconds = 1000,
      PartitionCount = 10000,
      LeaseSeconds = 300,
      AbandonStaleInstanceThresholdSeconds = 300,
      DebugMode = false
    };

    var sut = new ImmediateWorkCoordinatorStrategy(
      fakeCoordinator,
      instanceProvider,
      options
    );

    var messageId = _idProvider.NewGuid();
    var envelope = new MessageEnvelope<_testEvent> {
      MessageId = MessageId.From(messageId),
      Payload = new _testEvent("test-data"),
      Hops = [],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };

    // Serialize typed envelope to JsonElement envelope for InboxMessage
    var jsonOptions = Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions();
    var envelopeJson = JsonSerializer.Serialize((object)envelope, jsonOptions);
    var jsonEnvelope = JsonSerializer.Deserialize<MessageEnvelope<JsonElement>>(envelopeJson, jsonOptions)
      ?? throw new InvalidOperationException("Failed to deserialize envelope");

    var inboxMessage = new InboxMessage {
      MessageId = messageId,
      HandlerName = "TestHandler",
      Envelope = jsonEnvelope,
      EnvelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[System.Object, System.Private.CoreLib]], Whizbang.Core",
      StreamId = _idProvider.NewGuid(),
      IsEvent = true,
      MessageType = "TestMessage, TestAssembly"
    };

    // Act
    sut.QueueInboxMessage(inboxMessage);
    await sut.FlushAsync(WorkBatchOptions.None);

    // Assert - Message should be passed to coordinator
    await Assert.That(fakeCoordinator.LastNewInboxMessages).Count().IsEqualTo(1);
    await Assert.That(fakeCoordinator.LastNewInboxMessages[0].MessageId).IsEqualTo(messageId);
    await Assert.That(fakeCoordinator.LastNewInboxMessages[0].HandlerName).IsEqualTo("TestHandler");
  }

  // ========================================
  // Constructor Tests
  // ========================================

  [Test]
  public async Task Constructor_WithNullCoordinator_ThrowsArgumentNullExceptionAsync() {
    // Arrange
    var instanceProvider = new FakeServiceInstanceProvider();
    var options = new WorkCoordinatorOptions();

    // Act & Assert
    await Assert.That(() => new ImmediateWorkCoordinatorStrategy(
      null!,
      instanceProvider,
      options
    )).Throws<ArgumentNullException>();
  }

  [Test]
  public async Task Constructor_WithNullInstanceProvider_ThrowsArgumentNullExceptionAsync() {
    // Arrange
    var coordinator = new FakeWorkCoordinator();
    var options = new WorkCoordinatorOptions();

    // Act & Assert
    await Assert.That(() => new ImmediateWorkCoordinatorStrategy(
      coordinator,
      null!,
      options
    )).Throws<ArgumentNullException>();
  }

  [Test]
  public async Task Constructor_WithNullOptions_ThrowsArgumentNullExceptionAsync() {
    // Arrange
    var coordinator = new FakeWorkCoordinator();
    var instanceProvider = new FakeServiceInstanceProvider();

    // Act & Assert
    await Assert.That(() => new ImmediateWorkCoordinatorStrategy(
      coordinator,
      instanceProvider,
      null!
    )).Throws<ArgumentNullException>();
  }

  // ========================================
  // Flush Clears Queue Tests
  // ========================================
  // Deleted as obsolete after Phase H (coverage moved to WorkCoordinatorFlushHelperTests):
  //   Queue{Outbox,Inbox}{Completion,Failure}_FlushesOnCallAsync — completion/failure
  //   routing now flows through IOutboxCompletionChannel / IFailureChannel; direct-
  //   coordinator path drops them by design.

  // ========================================
  // Logger Coverage Tests
  // ========================================
  // Deleted as obsolete (asserted on coordinator-captured fields that no longer
  // flow through the coordinator post-Phase-H):
  //   FlushAsync_ClearsQueuesAfterFlushAsync — checked Last{Outbox,Inbox}Completions
  //   FlushAsync_WithDebugMode_SetsDebugFlagAsync — flag no longer routed via coord
  //   FlushAsync_WithoutDebugMode_DoesNotSetDebugFlagAsync — same

  [Test]
  public async Task QueueOutboxMessage_WithLogger_LogsMessageQueuedAsync() {
    // Arrange - logger != null exercises line 83
    var fakeCoordinator = new FakeWorkCoordinator();
    var instanceProvider = new FakeServiceInstanceProvider();
    var options = new WorkCoordinatorOptions {
      IntervalMilliseconds = 1000,
      PartitionCount = 10000,
      LeaseSeconds = 300,
      AbandonStaleInstanceThresholdSeconds = 300
    };
    var logger = new FakeLogger<ImmediateWorkCoordinatorStrategy>();

    var sut = new ImmediateWorkCoordinatorStrategy(
      fakeCoordinator, instanceProvider, options, logger: logger
    );

    // Act
    sut.QueueOutboxMessage(_createOutboxMessage());

    // Assert - logger was called
    await Assert.That(logger.LogCount).IsGreaterThan(0);
  }

  [Test]
  public async Task QueueInboxMessage_WithLogger_LogsMessageQueuedAsync() {
    // Arrange - logger != null exercises line 105
    var fakeCoordinator = new FakeWorkCoordinator();
    var instanceProvider = new FakeServiceInstanceProvider();
    var options = new WorkCoordinatorOptions {
      IntervalMilliseconds = 1000,
      PartitionCount = 10000,
      LeaseSeconds = 300,
      AbandonStaleInstanceThresholdSeconds = 300
    };
    var logger = new FakeLogger<ImmediateWorkCoordinatorStrategy>();

    var sut = new ImmediateWorkCoordinatorStrategy(
      fakeCoordinator, instanceProvider, options, logger: logger
    );

    // Act
    sut.QueueInboxMessage(new InboxMessage {
      MessageId = Guid.NewGuid(),
      HandlerName = "TestHandler",
      Envelope = _createJsonEnvelope(),
      EnvelopeType = "Test",
      StreamId = Guid.NewGuid(),
      MessageType = "TestMessage"
    });

    // Assert
    await Assert.That(logger.LogCount).IsGreaterThan(0);
  }

  [Test]
  public async Task QueueOutboxCompletion_WithLogger_LogsCompletionQueuedAsync() {
    // Arrange - logger != null exercises line 118
    var fakeCoordinator = new FakeWorkCoordinator();
    var instanceProvider = new FakeServiceInstanceProvider();
    var options = new WorkCoordinatorOptions {
      IntervalMilliseconds = 1000,
      PartitionCount = 10000,
      LeaseSeconds = 300,
      AbandonStaleInstanceThresholdSeconds = 300
    };
    var logger = new FakeLogger<ImmediateWorkCoordinatorStrategy>();

    var sut = new ImmediateWorkCoordinatorStrategy(
      fakeCoordinator, instanceProvider, options, logger: logger
    );

    // Act
    sut.QueueOutboxCompletion(Guid.NewGuid(), MessageProcessingStatus.Published);

    // Assert
    await Assert.That(logger.LogCount).IsGreaterThan(0);
  }

  [Test]
  public async Task QueueInboxCompletion_WithLogger_LogsCompletionQueuedAsync() {
    // Arrange - logger != null exercises line 131
    var fakeCoordinator = new FakeWorkCoordinator();
    var instanceProvider = new FakeServiceInstanceProvider();
    var options = new WorkCoordinatorOptions {
      IntervalMilliseconds = 1000,
      PartitionCount = 10000,
      LeaseSeconds = 300,
      AbandonStaleInstanceThresholdSeconds = 300
    };
    var logger = new FakeLogger<ImmediateWorkCoordinatorStrategy>();

    var sut = new ImmediateWorkCoordinatorStrategy(
      fakeCoordinator, instanceProvider, options, logger: logger
    );

    // Act
    sut.QueueInboxCompletion(Guid.NewGuid(), MessageProcessingStatus.Published);

    // Assert
    await Assert.That(logger.LogCount).IsGreaterThan(0);
  }

  [Test]
  public async Task QueueOutboxFailure_WithLogger_LogsFailureQueuedAsync() {
    // Arrange - logger != null exercises line 145
    var fakeCoordinator = new FakeWorkCoordinator();
    var instanceProvider = new FakeServiceInstanceProvider();
    var options = new WorkCoordinatorOptions {
      IntervalMilliseconds = 1000,
      PartitionCount = 10000,
      LeaseSeconds = 300,
      AbandonStaleInstanceThresholdSeconds = 300
    };
    var logger = new FakeLogger<ImmediateWorkCoordinatorStrategy>();

    var sut = new ImmediateWorkCoordinatorStrategy(
      fakeCoordinator, instanceProvider, options, logger: logger
    );

    // Act
    sut.QueueOutboxFailure(Guid.NewGuid(), MessageProcessingStatus.Failed, "Test error");

    // Assert
    await Assert.That(logger.LogCount).IsGreaterThan(0);
  }

  [Test]
  public async Task QueueInboxFailure_WithLogger_LogsFailureQueuedAsync() {
    // Arrange - logger != null exercises line 159
    var fakeCoordinator = new FakeWorkCoordinator();
    var instanceProvider = new FakeServiceInstanceProvider();
    var options = new WorkCoordinatorOptions {
      IntervalMilliseconds = 1000,
      PartitionCount = 10000,
      LeaseSeconds = 300,
      AbandonStaleInstanceThresholdSeconds = 300
    };
    var logger = new FakeLogger<ImmediateWorkCoordinatorStrategy>();

    var sut = new ImmediateWorkCoordinatorStrategy(
      fakeCoordinator, instanceProvider, options, logger: logger
    );

    // Act
    sut.QueueInboxFailure(Guid.NewGuid(), MessageProcessingStatus.Failed, "Test error");

    // Assert
    await Assert.That(logger.LogCount).IsGreaterThan(0);
  }

  [Test]
  public async Task FlushAsync_WithLogger_LogsFlushStartingAsync() {
    // Arrange - logger != null exercises lines 185-191
    var fakeCoordinator = new FakeWorkCoordinator();
    var instanceProvider = new FakeServiceInstanceProvider();
    var options = new WorkCoordinatorOptions {
      IntervalMilliseconds = 1000,
      PartitionCount = 10000,
      LeaseSeconds = 300,
      AbandonStaleInstanceThresholdSeconds = 300
    };
    var logger = new FakeLogger<ImmediateWorkCoordinatorStrategy>();

    var sut = new ImmediateWorkCoordinatorStrategy(
      fakeCoordinator, instanceProvider, options, logger: logger
    );

    // Queue a message so FlushAsync has work (exercises flush logging)
    sut.QueueOutboxMessage(_createOutboxMessage());

    // Act
    await sut.FlushAsync(WorkBatchOptions.None);

    // Assert - Multiple log entries: one for queue, one for flush
    await Assert.That(logger.LogCount).IsGreaterThanOrEqualTo(2);
  }

  // ========================================
  // Audit Message Building Coverage (Lines 90-92, 226-227)
  // ========================================

  [Test]
  public async Task QueueOutboxMessage_WithAuditEnabled_BuildsAuditMessageAsync() {
    // Arrange - EventAuditEnabled + IsEvent exercises lines 90-92
    var fakeCoordinator = new FakeWorkCoordinator();
    var instanceProvider = new FakeServiceInstanceProvider();
    var options = new WorkCoordinatorOptions {
      IntervalMilliseconds = 1000,
      PartitionCount = 10000,
      LeaseSeconds = 300,
      AbandonStaleInstanceThresholdSeconds = 300
    };
    var systemEventOptions = new Whizbang.Core.SystemEvents.SystemEventOptions();
    systemEventOptions.EnableEventAudit();

    var sut = new ImmediateWorkCoordinatorStrategy(
      fakeCoordinator, instanceProvider, options,
      systemEventOptions: Microsoft.Extensions.Options.Options.Create(systemEventOptions)
    );

    // Queue an event message with IsEvent=true
    sut.QueueOutboxMessage(_createOutboxMessage());

    // Flush to merge audit messages (line 226-227)
    await sut.FlushAsync(WorkBatchOptions.None);

    // Assert - Should have original + audit message in the batch
    await Assert.That(fakeCoordinator.LastNewOutboxMessages.Length).IsGreaterThanOrEqualTo(1);
  }

  // ========================================
  // Channel Write Tests
  // ========================================

  [Test]
  public async Task FlushAsync_WithReturnedWork_WritesToChannelAsync() {
    // Arrange
    var channelWriter = new TestWorkChannelWriter();
    var messageId1 = Guid.CreateVersion7();
    var messageId2 = Guid.CreateVersion7();
    var fakeCoordinator = new FakeWorkCoordinator {
      WorkToReturn = [
        new OutboxWork {
          MessageId = messageId1,
          Destination = "test-topic",
          EnvelopeType = "Test",
          MessageType = "Test",
          Envelope = _createJsonEnvelope(),
          Attempts = 0,
          Status = MessageProcessingStatus.None
        },
        new OutboxWork {
          MessageId = messageId2,
          Destination = "test-topic",
          EnvelopeType = "Test",
          MessageType = "Test",
          Envelope = _createJsonEnvelope(),
          Attempts = 0,
          Status = MessageProcessingStatus.None
        }
      ]
    };
    var instanceProvider = new FakeServiceInstanceProvider();
    var options = new WorkCoordinatorOptions();

    var sut = new ImmediateWorkCoordinatorStrategy(
      fakeCoordinator, instanceProvider, options, workChannelWriter: channelWriter
    );

    sut.QueueOutboxMessage(_createOutboxMessage());

    // Act
    await sut.FlushAsync(WorkBatchOptions.None);

    // Assert — ExecuteFlushAsync signals publisher but does not write to channel
    await Assert.That(channelWriter.WrittenWork).Count().IsEqualTo(0)
      .Because("ExecuteFlushAsync signals publisher but does not write to channel");
    // Work was still persisted via StoreOutboxMessagesAsync
    await Assert.That(fakeCoordinator.ProcessWorkBatchCallCount).IsEqualTo(1);
  }

  // ========================================
  // Helper Methods
  // ========================================
  // Deleted: FlushAsync_NullChannelWriter_DoesNotThrowAsync,
  //          FlushAsync_ChannelClosed_HandlesGracefullyAsync.
  // Both asserted result.OutboxWork.Count == 1 against the legacy claim-during-flush
  // behavior. ExecuteFlushAsync returns an empty WorkBatch post-Phase-H.

  private OutboxMessage _createOutboxMessage() {
    var messageId = _idProvider.NewGuid();
    var jsonOptions = Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions();
    var envelope = new MessageEnvelope<_testEvent> {
      MessageId = MessageId.From(messageId),
      Payload = new _testEvent("test-data"),
      Hops = [new MessageHop { ServiceInstance = ServiceInstanceInfo.Unknown }],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };
    var envelopeJson = System.Text.Json.JsonSerializer.Serialize((object)envelope, jsonOptions);
    var jsonEnvelope = System.Text.Json.JsonSerializer.Deserialize<MessageEnvelope<System.Text.Json.JsonElement>>(envelopeJson, jsonOptions)!;

    return new OutboxMessage {
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
    };
  }

  private MessageEnvelope<System.Text.Json.JsonElement> _createJsonEnvelope() {
    var jsonOptions = Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions();
    var envelope = new MessageEnvelope<_testEvent> {
      MessageId = MessageId.New(),
      Payload = new _testEvent("test"),
      Hops = [new MessageHop { ServiceInstance = ServiceInstanceInfo.Unknown }],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };
    var json = System.Text.Json.JsonSerializer.Serialize((object)envelope, jsonOptions);
    return System.Text.Json.JsonSerializer.Deserialize<MessageEnvelope<System.Text.Json.JsonElement>>(json, jsonOptions)!;
  }

  // ========================================
  // Test Fakes
  // ========================================

  private sealed class FakeLogger<T> : Microsoft.Extensions.Logging.ILogger<T> {
    public int LogCount { get; private set; }

    public void Log<TState>(
      Microsoft.Extensions.Logging.LogLevel logLevel,
      Microsoft.Extensions.Logging.EventId eventId,
      TState state,
      Exception? exception,
      Func<TState, Exception?, string> formatter) {
      LogCount++;
    }

    public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
  }

  // ========================================
  // Test Fakes
  // ========================================

  private sealed class TestWorkChannelWriter : IWorkChannelWriter {
    public void ClearInFlight() { }
    public List<OutboxWork> WrittenWork { get; } = [];

    public System.Threading.Channels.ChannelReader<OutboxWork> Reader =>
      throw new NotImplementedException("Reader not needed for tests");

    public ValueTask WriteAsync(OutboxWork work, CancellationToken ct) {
      WrittenWork.Add(work);
      return ValueTask.CompletedTask;
    }

    public bool TryWrite(OutboxWork work) {
      WrittenWork.Add(work);
      return true;
    }

    public void Complete() { }

    public bool IsInFlight(Guid messageId) => false;
    public void RemoveInFlight(Guid messageId) { }
    public bool ShouldRenewLease(Guid messageId) => false;
    public event Action? OnNewWorkAvailable;
    public void SignalNewWorkAvailable() => OnNewWorkAvailable?.Invoke();
    public event Action? OnNewPerspectiveWorkAvailable;
    public void SignalNewPerspectiveWorkAvailable() => OnNewPerspectiveWorkAvailable?.Invoke();
  }

  private sealed class FakeWorkCoordinator : IWorkCoordinator {
    public int ProcessWorkBatchCallCount { get; private set; }
    public OutboxMessage[] LastNewOutboxMessages { get; private set; } = [];
    public InboxMessage[] LastNewInboxMessages { get; private set; } = [];
    public MessageCompletion[] LastOutboxCompletions { get; private set; } = [];
    public MessageCompletion[] LastInboxCompletions { get; private set; } = [];
    public MessageFailure[] LastOutboxFailures { get; private set; } = [];
    public MessageFailure[] LastInboxFailures { get; private set; } = [];
    public WorkBatchOptions LastFlags { get; private set; }
    public List<OutboxWork> WorkToReturn { get; set; } = [];

    public Task StoreOutboxMessagesAsync(
      OutboxMessage[] messages,
      int partitionCount = 2,
      CancellationToken cancellationToken = default) {
      ProcessWorkBatchCallCount++;
      LastNewOutboxMessages = messages;
      return Task.CompletedTask;
    }

    public Task ReportPerspectiveCompletionAsync(
      PerspectiveCursorCompletion completion,
      CancellationToken cancellationToken = default) {
      return Task.CompletedTask;
    }

    public Task ReportPerspectiveFailureAsync(
      PerspectiveCursorFailure failure,
      CancellationToken cancellationToken = default) {
      return Task.CompletedTask;
    }

    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount = 2, CancellationToken cancellationToken = default) {
      ProcessWorkBatchCallCount++;
      LastNewInboxMessages = messages;
      return Task.CompletedTask;
    }

    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new WorkCoordinatorStatistics());

    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(
      Guid streamId,
      string perspectiveName,
      CancellationToken cancellationToken = default) {
      return Task.FromResult<PerspectiveCursorInfo?>(null);
    }
  }

  private sealed class FakeServiceInstanceProvider : IServiceInstanceProvider {
    public Guid InstanceId { get; } = Guid.NewGuid();
    public string ServiceName { get; } = "TestService";
    public string HostName { get; } = "test-host";
    public int ProcessId { get; } = 12345;

    public ServiceInstanceInfo ToInfo() {
      return new ServiceInstanceInfo {
        ServiceName = ServiceName,
        InstanceId = InstanceId,
        HostName = HostName,
        ProcessId = ProcessId
      };
    }
  }
}
