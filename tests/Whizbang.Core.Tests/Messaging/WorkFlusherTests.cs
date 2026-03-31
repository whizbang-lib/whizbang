using System.Text.Json;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Tests for IWorkFlusher explicit interface implementation on all strategies.
/// Verifies that IWorkFlusher.FlushAsync delegates to FlushAsync(WorkBatchOptions.None, FlushMode.Required, ct).
/// </summary>
public class WorkFlusherTests {
  private readonly Uuid7IdProvider _idProvider = new();

  public record _testEvent([StreamId] string Data) : IEvent;

  // ========================================
  // Strategy-specific IWorkFlusher Tests
  // ========================================

  [Test]
  public async Task ImmediateStrategy_FlushAsync_DelegatesToStrategyWithRequiredModeAsync() {
    // Arrange
    var fakeCoordinator = new FakeWorkCoordinator();
    var instanceProvider = new FakeServiceInstanceProvider();
    var options = new WorkCoordinatorOptions();

    var strategy = new ImmediateWorkCoordinatorStrategy(
      fakeCoordinator, instanceProvider, options
    );

    strategy.QueueOutboxMessage(_createOutboxMessage());

    // Act
    IWorkFlusher flusher = strategy;
    await flusher.FlushAsync();

    // Assert
    await Assert.That(fakeCoordinator.ProcessWorkBatchCallCount).IsEqualTo(1)
      .Because("IWorkFlusher.FlushAsync should delegate to the strategy's FlushAsync");
    await Assert.That(fakeCoordinator.LastNewOutboxMessages).Count().IsEqualTo(1);
  }

  [Test]
  public async Task ScopedStrategy_FlushAsync_DelegatesToStrategyWithRequiredModeAsync() {
    // Arrange
    var fakeCoordinator = new FakeWorkCoordinator();
    var instanceProvider = new FakeServiceInstanceProvider();
    var options = new WorkCoordinatorOptions();

    var strategy = new ScopedWorkCoordinatorStrategy(
      fakeCoordinator, instanceProvider, workChannelWriter: null, options
    );

    strategy.QueueOutboxMessage(_createOutboxMessage());

    // Act
    IWorkFlusher flusher = strategy;
    await flusher.FlushAsync();

    // Assert
    await Assert.That(fakeCoordinator.ProcessWorkBatchCallCount).IsEqualTo(1)
      .Because("IWorkFlusher.FlushAsync should delegate to the strategy's FlushAsync");
    await Assert.That(fakeCoordinator.LastNewOutboxMessages).Count().IsEqualTo(1);
  }

  [Test]
  public async Task IntervalStrategy_FlushAsync_DelegatesToStrategyWithRequiredModeAsync() {
    // Arrange
    var fakeCoordinator = new FakeWorkCoordinator();
    var instanceProvider = new FakeServiceInstanceProvider();
    var options = new WorkCoordinatorOptions { IntervalMilliseconds = 60_000 };

    var strategy = new IntervalWorkCoordinatorStrategy(
      fakeCoordinator, instanceProvider, options
    );

    strategy.QueueOutboxMessage(_createOutboxMessage());

    // Act
    IWorkFlusher flusher = strategy;
    await flusher.FlushAsync();

    // Assert
    await Assert.That(fakeCoordinator.ProcessWorkBatchCallCount).IsEqualTo(1)
      .Because("IWorkFlusher.FlushAsync should delegate to the strategy's FlushAsync");
    await Assert.That(fakeCoordinator.LastNewOutboxMessages).Count().IsEqualTo(1);

    // Cleanup
    await strategy.DisposeAsync();
  }

  [Test]
  public async Task BatchStrategy_FlushAsync_DelegatesToStrategyWithRequiredModeAsync() {
    // Arrange
    var fakeCoordinator = new FakeWorkCoordinator();
    var instanceProvider = new FakeServiceInstanceProvider();
    var options = new WorkCoordinatorOptions {
      BatchSize = 1000,
      IntervalMilliseconds = 60_000
    };

    var strategy = new BatchWorkCoordinatorStrategy(
      fakeCoordinator, instanceProvider, options
    );

    strategy.QueueOutboxMessage(_createOutboxMessage());

    // Act
    IWorkFlusher flusher = strategy;
    await flusher.FlushAsync();

    // Assert
    await Assert.That(fakeCoordinator.ProcessWorkBatchCallCount).IsEqualTo(1)
      .Because("IWorkFlusher.FlushAsync should delegate to the strategy's FlushAsync");
    await Assert.That(fakeCoordinator.LastNewOutboxMessages).Count().IsEqualTo(1);

    // Cleanup
    await strategy.DisposeAsync();
  }

  // ========================================
  // Additional Coverage Tests
  // ========================================

  [Test]
  public async Task FlushAsync_WithNoQueuedMessages_CallsCoordinatorAsync() {
    // Arrange
    var fakeCoordinator = new FakeWorkCoordinator();
    var instanceProvider = new FakeServiceInstanceProvider();
    var options = new WorkCoordinatorOptions();

    var strategy = new ImmediateWorkCoordinatorStrategy(
      fakeCoordinator, instanceProvider, options
    );

    // Act - flush with nothing queued
    IWorkFlusher flusher = strategy;
    await flusher.FlushAsync();

    // Assert - Immediate strategy always calls coordinator even with empty queues
    await Assert.That(fakeCoordinator.ProcessWorkBatchCallCount).IsEqualTo(1);
  }

  [Test]
  public async Task FlushAsync_WithCancellationToken_PassesThroughAsync() {
    // Arrange
    var fakeCoordinator = new FakeWorkCoordinator();
    var instanceProvider = new FakeServiceInstanceProvider();
    var options = new WorkCoordinatorOptions();

    var strategy = new ImmediateWorkCoordinatorStrategy(
      fakeCoordinator, instanceProvider, options
    );

    strategy.QueueOutboxMessage(_createOutboxMessage());

    using var cts = new CancellationTokenSource();

    // Act - pass a non-default CT
    IWorkFlusher flusher = strategy;
    await flusher.FlushAsync(cts.Token);

    // Assert - flush completed successfully with the token
    await Assert.That(fakeCoordinator.ProcessWorkBatchCallCount).IsEqualTo(1);
    await Assert.That(fakeCoordinator.LastCancellationToken).IsEqualTo(cts.Token);
  }

  // ========================================
  // Helper Methods
  // ========================================

  private OutboxMessage _createOutboxMessage() {
    var messageId = _idProvider.NewGuid();
    var jsonOptions = Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions();
    var envelope = new MessageEnvelope<_testEvent> {
      MessageId = MessageId.From(messageId),
      Payload = new _testEvent("test-data"),
      Hops = [new MessageHop { ServiceInstance = ServiceInstanceInfo.Unknown }],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
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
    public CancellationToken LastCancellationToken { get; private set; }

    public Task<WorkBatch> ProcessWorkBatchAsync(
      ProcessWorkBatchRequest request,
      CancellationToken cancellationToken = default) {
      ProcessWorkBatchCallCount++;
      LastNewOutboxMessages = request.NewOutboxMessages;
      LastCancellationToken = cancellationToken;

      return Task.FromResult(new WorkBatch {
        OutboxWork = [],
        InboxWork = [],
        PerspectiveWork = []
      });
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
