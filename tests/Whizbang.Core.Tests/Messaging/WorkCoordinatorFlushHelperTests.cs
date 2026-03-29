using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Tests for WorkCoordinatorFlushHelper.ExecuteFlushAsync — verifies the skipLifecycle
/// parameter correctly controls whether lifecycle stages are invoked while always
/// running the core data path (ProcessWorkBatchAsync + channel write).
/// </summary>
public class WorkCoordinatorFlushHelperTests {
  private readonly Uuid7IdProvider _idProvider = new();

  public record _testEvent([StreamId] string Id = "test-1") : IEvent { }

  // ========================================
  // skipLifecycle: true — data path only
  // ========================================

  /// <summary>
  /// Verifies that skipLifecycle: true still calls ProcessWorkBatchAsync (persists data).
  /// </summary>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/WorkCoordinatorFlushHelperTests.cs:ExecuteFlushAsync_SkipLifecycle_StillCallsProcessWorkBatchAsync</tests>
  [Test]
  public async Task ExecuteFlushAsync_SkipLifecycle_StillCallsProcessWorkBatchAsync() {
    // Arrange
    var coordinator = new FakeWorkCoordinator();
    var instanceProvider = new FakeServiceInstanceProvider();
    var options = new WorkCoordinatorOptions();
    var outboxMessages = new[] { _buildTestOutboxMessage() };

    // Act
    _ = await WorkCoordinatorFlushHelper.ExecuteFlushAsync(
      new FlushContext(
        coordinator, ScopeFactory: null, instanceProvider, options, "test",
        outboxMessages, InboxMessages: [], OutboxCompletions: [],
        InboxCompletions: [], OutboxFailures: [], InboxFailures: [],
        WorkBatchOptions.None, LifecycleMessageDeserializer: null,
        Logger: null, TracingOptions: null, Metrics: null,
        LifecycleMetrics: null, WorkChannelWriter: null,
        PendingAuditMessages: null, SkipLifecycle: true),
      ct: default
    );

    // Assert — data was persisted
    await Assert.That(coordinator.ProcessWorkBatchCallCount).IsEqualTo(1);
    await Assert.That(coordinator.LastNewOutboxMessages).Count().IsEqualTo(1);
  }

  /// <summary>
  /// Verifies that skipLifecycle: true still writes returned work to the channel.
  /// </summary>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/WorkCoordinatorFlushHelperTests.cs:ExecuteFlushAsync_SkipLifecycle_StillWritesToChannelAsync</tests>
  [Test]
  public async Task ExecuteFlushAsync_SkipLifecycle_StillWritesToChannelAsync() {
    // Arrange — coordinator that returns outbox work
    var coordinator = new FakeWorkCoordinatorWithWork();
    var instanceProvider = new FakeServiceInstanceProvider();
    var options = new WorkCoordinatorOptions();
    var channelWriter = new TestWorkChannelWriter();

    // Act
    await WorkCoordinatorFlushHelper.ExecuteFlushAsync(
      new FlushContext(
        coordinator, ScopeFactory: null, instanceProvider, options, "test",
        OutboxMessages: [_buildTestOutboxMessage()], InboxMessages: [],
        OutboxCompletions: [], InboxCompletions: [], OutboxFailures: [],
        InboxFailures: [], WorkBatchOptions.None, LifecycleMessageDeserializer: null,
        Logger: null, TracingOptions: null, Metrics: null,
        LifecycleMetrics: null, WorkChannelWriter: channelWriter,
        PendingAuditMessages: null, SkipLifecycle: true),
      ct: default
    );

    // Assert — work was written to channel
    await Assert.That(channelWriter.WrittenWork).Count().IsGreaterThan(0)
      .Because("Channel write should happen even when lifecycle is skipped");
  }

  /// <summary>
  /// Verifies that skipLifecycle: true does NOT create lifecycle scopes.
  /// </summary>
  [Test]
  public async Task ExecuteFlushAsync_SkipLifecycle_DoesNotCreateLifecycleScopesAsync() {
    // Arrange
    var coordinator = new FakeWorkCoordinator();
    var instanceProvider = new FakeServiceInstanceProvider();
    var options = new WorkCoordinatorOptions();
    var trackingScopeFactory = new TrackingScopeFactory();

    // Act
    await WorkCoordinatorFlushHelper.ExecuteFlushAsync(
      new FlushContext(
        coordinator, ScopeFactory: trackingScopeFactory, instanceProvider, options, "test",
        OutboxMessages: [_buildTestOutboxMessage()], InboxMessages: [],
        OutboxCompletions: [], InboxCompletions: [], OutboxFailures: [],
        InboxFailures: [], WorkBatchOptions.None, LifecycleMessageDeserializer: null,
        Logger: null, TracingOptions: null, Metrics: null,
        LifecycleMetrics: null, WorkChannelWriter: null,
        PendingAuditMessages: null, SkipLifecycle: true),
      ct: default
    );

    // Assert — no lifecycle scopes created
    await Assert.That(trackingScopeFactory.ScopeCreationCount).IsEqualTo(0)
      .Because("skipLifecycle: true should prevent lifecycle stage invocation");
  }

  // ========================================
  // skipLifecycle: false — full pipeline (default)
  // ========================================

  /// <summary>
  /// Verifies that skipLifecycle: false (default) still calls ProcessWorkBatchAsync.
  /// Full lifecycle invocation is tested extensively in existing strategy tests;
  /// this test confirms the default path does not skip the data path.
  /// </summary>
  [Test]
  public async Task ExecuteFlushAsync_DefaultSkipLifecycle_CallsProcessWorkBatchAsync() {
    // Arrange
    var coordinator = new FakeWorkCoordinator();
    var instanceProvider = new FakeServiceInstanceProvider();
    var options = new WorkCoordinatorOptions();

    // Act — default skipLifecycle (false)
    await WorkCoordinatorFlushHelper.ExecuteFlushAsync(
      new FlushContext(
        coordinator, ScopeFactory: null, instanceProvider, options, "test",
        OutboxMessages: [_buildTestOutboxMessage()], InboxMessages: [],
        OutboxCompletions: [], InboxCompletions: [], OutboxFailures: [],
        InboxFailures: [], WorkBatchOptions.None, LifecycleMessageDeserializer: null,
        Logger: null, TracingOptions: null, Metrics: null,
        LifecycleMetrics: null, WorkChannelWriter: null,
        PendingAuditMessages: null),
      ct: default
    );

    // Assert — data was persisted (same as skipLifecycle: true)
    await Assert.That(coordinator.ProcessWorkBatchCallCount).IsEqualTo(1)
      .Because("Default behavior should call ProcessWorkBatchAsync");
  }

  /// <summary>
  /// Verifies that pending audit messages are merged into the outbox even with skipLifecycle.
  /// </summary>
  [Test]
  public async Task ExecuteFlushAsync_SkipLifecycle_StillMergesAuditMessagesAsync() {
    // Arrange
    var coordinator = new FakeWorkCoordinator();
    var instanceProvider = new FakeServiceInstanceProvider();
    var options = new WorkCoordinatorOptions();
    var auditMessage = _buildTestOutboxMessage();

    // Act
    await WorkCoordinatorFlushHelper.ExecuteFlushAsync(
      new FlushContext(
        coordinator, ScopeFactory: null, instanceProvider, options, "test",
        OutboxMessages: [_buildTestOutboxMessage()], InboxMessages: [],
        OutboxCompletions: [], InboxCompletions: [], OutboxFailures: [],
        InboxFailures: [], WorkBatchOptions.None, LifecycleMessageDeserializer: null,
        Logger: null, TracingOptions: null, Metrics: null,
        LifecycleMetrics: null, WorkChannelWriter: null,
        PendingAuditMessages: [auditMessage], SkipLifecycle: true),
      ct: default
    );

    // Assert — both original + audit message were in the batch
    await Assert.That(coordinator.LastNewOutboxMessages).Count().IsEqualTo(2)
      .Because("Audit messages should be merged even when lifecycle is skipped");
  }

  // ========================================
  // Gap 1: Flush path must track outbox messages as in-flight
  // ========================================

  /// <summary>
  /// Verifies that outbox messages written to the channel via ExecuteFlushAsync
  /// are tracked as in-flight by the WorkChannelWriter. This is the root cause
  /// of the ChatService outbox stuck at status=1 — the flush path wrote messages
  /// without tracking, causing duplicate publishing and stuck completions.
  /// </summary>
  [Test]
  public async Task ExecuteFlushAsync_OutboxWork_TrackedAsInFlightOnChannelWriterAsync() {
    // Arrange — use REAL WorkChannelWriter (not test double)
    var coordinator = new FakeWorkCoordinator();
    var instanceProvider = new FakeServiceInstanceProvider();
    var options = new WorkCoordinatorOptions();
    var realChannelWriter = new WorkChannelWriter();
    var outboxMessage = _buildTestOutboxMessage();

    // Act — flush through the real path
    var workBatch = await WorkCoordinatorFlushHelper.ExecuteFlushAsync(
      new FlushContext(
        coordinator, ScopeFactory: null, instanceProvider, options, "test",
        OutboxMessages: [outboxMessage], InboxMessages: [],
        OutboxCompletions: [], InboxCompletions: [], OutboxFailures: [],
        InboxFailures: [], WorkBatchOptions.None, LifecycleMessageDeserializer: null,
        Logger: null, TracingOptions: null, Metrics: null,
        LifecycleMetrics: null, WorkChannelWriter: realChannelWriter,
        PendingAuditMessages: null, SkipLifecycle: true),
      ct: default
    );

    // Assert — every outbox work item returned must be tracked as in-flight
    await Assert.That(workBatch.OutboxWork.Count).IsGreaterThan(0)
      .Because("FakeWorkCoordinator should return outbox work");

    foreach (var work in workBatch.OutboxWork) {
      await Assert.That(realChannelWriter.IsInFlight(work.MessageId)).IsTrue()
        .Because("Flush path must track outbox messages as in-flight to prevent duplicate publishing");
    }
  }

  // ========================================
  // Test Helpers
  // ========================================

  private OutboxMessage _buildTestOutboxMessage() {
    var messageId = _idProvider.NewGuid();
    var jsonOptions = Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions();
    var envelope = new MessageEnvelope<_testEvent> {
      MessageId = MessageId.From(messageId),
      Payload = new _testEvent(),
      Hops = [new MessageHop { ServiceInstance = ServiceInstanceInfo.Unknown }]
    };
    var envelopeJson = JsonSerializer.Serialize((object)envelope, jsonOptions);
    var jsonEnvelope = JsonSerializer.Deserialize<MessageEnvelope<JsonElement>>(envelopeJson, jsonOptions)!;

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

  // ========================================
  // Test Fakes
  // ========================================

  private sealed class FakeWorkCoordinator : IWorkCoordinator {
    public int ProcessWorkBatchCallCount { get; private set; }
    public OutboxMessage[] LastNewOutboxMessages { get; private set; } = [];

    public Task<WorkBatch> ProcessWorkBatchAsync(
      ProcessWorkBatchRequest request,
      CancellationToken cancellationToken = default) {
      ProcessWorkBatchCallCount++;
      LastNewOutboxMessages = request.NewOutboxMessages;
      // Simulate process_work_batch: stored messages are returned as outbox work
      var outboxWork = request.NewOutboxMessages.Select(m => new OutboxWork {
        MessageId = m.MessageId,
        Destination = m.Destination,
        Envelope = m.Envelope,
        EnvelopeType = m.EnvelopeType,
        MessageType = m.MessageType,
        Status = MessageProcessingStatus.Stored,
        Attempts = 0
      }).ToList();

      return Task.FromResult(new WorkBatch {
        OutboxWork = outboxWork,
        InboxWork = [],
        PerspectiveWork = []
      });
    }

    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion completion, CancellationToken cancellationToken = default)
      => Task.CompletedTask;
    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure failure, CancellationToken cancellationToken = default)
      => Task.CompletedTask;
    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default)
      => Task.FromResult<PerspectiveCursorInfo?>(null);
  }

  private sealed class FakeWorkCoordinatorWithWork : IWorkCoordinator {
    public Task<WorkBatch> ProcessWorkBatchAsync(
      ProcessWorkBatchRequest request,
      CancellationToken cancellationToken = default) {
      var messageId = Guid.CreateVersion7();
      var envelope = new MessageEnvelope<JsonElement> {
        MessageId = MessageId.From(messageId),
        Payload = JsonDocument.Parse("{}").RootElement,
        Hops = []
      };

      return Task.FromResult(new WorkBatch {
        OutboxWork = [
          new OutboxWork {
            MessageId = messageId,
            Destination = "test-topic",
            Flags = WorkBatchOptions.NewlyStored,
            Envelope = envelope,
            EnvelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[System.Text.Json.JsonElement, System.Text.Json]], Whizbang.Core",
            MessageType = "System.Text.Json.JsonElement, System.Text.Json",
            Attempts = 0
          }
        ],
        InboxWork = [],
        PerspectiveWork = []
      });
    }

    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion completion, CancellationToken cancellationToken = default)
      => Task.CompletedTask;
    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure failure, CancellationToken cancellationToken = default)
      => Task.CompletedTask;
    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default)
      => Task.FromResult<PerspectiveCursorInfo?>(null);
  }

  private sealed class FakeServiceInstanceProvider : IServiceInstanceProvider {
    public Guid InstanceId { get; } = Guid.NewGuid();
    public string ServiceName { get; } = "TestService";
    public string HostName { get; } = "test-host";
    public int ProcessId { get; } = 12345;
    public ServiceInstanceInfo ToInfo() => new() {
      ServiceName = ServiceName,
      InstanceId = InstanceId,
      HostName = HostName,
      ProcessId = ProcessId
    };
  }

  private sealed class TrackingScopeFactory : IServiceScopeFactory {
    public int ScopeCreationCount { get; private set; }

    public IServiceScope CreateScope() {
      ScopeCreationCount++;
      return new FakeServiceScope();
    }

    private sealed class FakeServiceScope : IServiceScope {
      public IServiceProvider ServiceProvider { get; } = new FakeServiceProvider();
      public void Dispose() { }
    }

    private sealed class FakeServiceProvider : IServiceProvider {
      public object? GetService(Type serviceType) => null;
    }
  }

  private sealed class TestWorkChannelWriter : IWorkChannelWriter {
    private readonly Channel<OutboxWork> _channel = Channel.CreateUnbounded<OutboxWork>();
    public List<OutboxWork> WrittenWork { get; } = [];

    public ChannelReader<OutboxWork> Reader => _channel.Reader;

    public ValueTask WriteAsync(OutboxWork work, CancellationToken ct = default) {
      WrittenWork.Add(work);
      return _channel.Writer.WriteAsync(work, ct);
    }

    public bool TryWrite(OutboxWork work) {
      WrittenWork.Add(work);
      return _channel.Writer.TryWrite(work);
    }

    public void Complete() => _channel.Writer.Complete();

    public bool IsInFlight(Guid messageId) => false;
    public void RemoveInFlight(Guid messageId) { }
    public bool ShouldRenewLease(Guid messageId) => false;
  }
}
