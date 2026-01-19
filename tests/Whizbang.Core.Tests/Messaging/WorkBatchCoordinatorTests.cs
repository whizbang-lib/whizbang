using System.Text.Json;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Tests for IWorkBatchCoordinator and WorkBatchCoordinator.
/// Validates coordination of work batch processing with channel distribution.
/// </summary>
public class WorkBatchCoordinatorTests {

  private static MessageEnvelope<JsonElement> _createTestEnvelope(Guid messageId) {
    return new MessageEnvelope<JsonElement> {
      MessageId = MessageId.From(messageId),
      Payload = JsonDocument.Parse("{}").RootElement,
      Hops = []
    };
  }

  private sealed class TestWorkCoordinator : IWorkCoordinator {
    public WorkBatch? WorkBatchToReturn { get; set; }

    public Task<WorkBatch> ProcessWorkBatchAsync(
      ProcessWorkBatchRequest request,
      CancellationToken cancellationToken = default) {
      return Task.FromResult(WorkBatchToReturn ?? new WorkBatch {
        OutboxWork = [],
        InboxWork = [],
        PerspectiveWork = []
      });
    }

    public Task ReportPerspectiveCompletionAsync(
      PerspectiveCheckpointCompletion completion,
      CancellationToken cancellationToken = default) {
      return Task.CompletedTask;
    }

    public Task ReportPerspectiveFailureAsync(
      PerspectiveCheckpointFailure failure,
      CancellationToken cancellationToken = default) {
      return Task.CompletedTask;
    }

    public Task<PerspectiveCheckpointInfo?> GetPerspectiveCheckpointAsync(
      Guid streamId,
      string perspectiveName,
      CancellationToken cancellationToken = default) {
      return Task.FromResult<PerspectiveCheckpointInfo?>(null);
    }
  }

  private sealed class TestServiceInstanceProvider : IServiceInstanceProvider {
    public Guid InstanceId { get; init; }
    public string ServiceName { get; init; } = "TestService";
    public string HostName { get; init; } = "localhost";
    public int ProcessId { get; init; } = 12345;

    public ServiceInstanceInfo ToInfo() {
      return new ServiceInstanceInfo {
        InstanceId = InstanceId,
        ServiceName = ServiceName,
        HostName = HostName,
        ProcessId = ProcessId
      };
    }
  }

  [Test]
  public async Task ProcessAndDistributeAsync_WithOutboxWork_WritesToOutboxChannelAsync() {
    // Arrange
    var instanceId = Guid.NewGuid();
    var messageId = Guid.NewGuid();
    var outboxWork = new OutboxWork {
      MessageId = messageId,
      Destination = "test-topic",
      EnvelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[System.Text.Json.JsonElement, System.Text.Json]], Whizbang.Core",
      MessageType = "System.Text.Json.JsonElement, System.Text.Json",
      Envelope = _createTestEnvelope(messageId),
      Attempts = 0,
      Status = MessageProcessingStatus.Stored,
      Flags = WorkBatchFlags.None
    };

    var testWorkCoordinator = new TestWorkCoordinator {
      WorkBatchToReturn = new WorkBatch {
        OutboxWork = [outboxWork],
        InboxWork = [],
        PerspectiveWork = []
      }
    };

    var instanceProvider = new TestServiceInstanceProvider { InstanceId = instanceId };
    var outboxChannel = new WorkChannelWriter();
    var perspectiveChannel = new PerspectiveChannelWriter();

    var coordinator = new WorkBatchCoordinator(
      testWorkCoordinator,
      instanceProvider,
      outboxChannel,
      perspectiveChannel
    );

    // Act
    await coordinator.ProcessAndDistributeAsync(instanceId);

    // Assert - outbox work should be in channel
    var canRead = outboxChannel.Reader.TryRead(out var readWork);
    await Assert.That(canRead).IsTrue();
    await Assert.That(readWork?.MessageId).IsEqualTo(outboxWork.MessageId);

    // Assert - perspective channel should be empty
    await Assert.That(perspectiveChannel.Reader.TryRead(out _)).IsFalse();
  }

  [Test]
  public async Task ProcessAndDistributeAsync_WithPerspectiveWork_WritesToPerspectiveChannelAsync() {
    // Arrange
    var instanceId = Guid.NewGuid();
    var perspectiveWork = new PerspectiveWork {
      StreamId = Guid.NewGuid(),
      PerspectiveName = "TestPerspective",
      LastProcessedEventId = null,
      Status = PerspectiveProcessingStatus.None,
      PartitionNumber = null,
      Flags = WorkBatchFlags.None
    };

    var testWorkCoordinator = new TestWorkCoordinator {
      WorkBatchToReturn = new WorkBatch {
        OutboxWork = [],
        InboxWork = [],
        PerspectiveWork = [perspectiveWork]
      }
    };

    var instanceProvider = new TestServiceInstanceProvider { InstanceId = instanceId };
    var outboxChannel = new WorkChannelWriter();
    var perspectiveChannel = new PerspectiveChannelWriter();

    var coordinator = new WorkBatchCoordinator(
      testWorkCoordinator,
      instanceProvider,
      outboxChannel,
      perspectiveChannel
    );

    // Act
    await coordinator.ProcessAndDistributeAsync(instanceId);

    // Assert - perspective work should be in channel
    var canRead = perspectiveChannel.Reader.TryRead(out var readWork);
    await Assert.That(canRead).IsTrue();
    await Assert.That(readWork?.StreamId).IsEqualTo(perspectiveWork.StreamId);

    // Assert - outbox channel should be empty
    await Assert.That(outboxChannel.Reader.TryRead(out _)).IsFalse();
  }

  [Test]
  public async Task ProcessAndDistributeAsync_WithBothWorkTypes_DistributesToBothChannelsAsync() {
    // Arrange
    var instanceId = Guid.NewGuid();
    var messageId = Guid.NewGuid();
    var outboxWork = new OutboxWork {
      MessageId = messageId,
      Destination = "test-topic",
      EnvelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[System.Text.Json.JsonElement, System.Text.Json]], Whizbang.Core",
      MessageType = "System.Text.Json.JsonElement, System.Text.Json",
      Envelope = _createTestEnvelope(messageId),
      Attempts = 0,
      Status = MessageProcessingStatus.Stored,
      Flags = WorkBatchFlags.None
    };
    var perspectiveWork = new PerspectiveWork {
      StreamId = Guid.NewGuid(),
      PerspectiveName = "TestPerspective",
      LastProcessedEventId = null,
      Status = PerspectiveProcessingStatus.None,
      PartitionNumber = null,
      Flags = WorkBatchFlags.None
    };

    var testWorkCoordinator = new TestWorkCoordinator {
      WorkBatchToReturn = new WorkBatch {
        OutboxWork = [outboxWork],
        InboxWork = [],
        PerspectiveWork = [perspectiveWork]
      }
    };

    var instanceProvider = new TestServiceInstanceProvider { InstanceId = instanceId };
    var outboxChannel = new WorkChannelWriter();
    var perspectiveChannel = new PerspectiveChannelWriter();

    var coordinator = new WorkBatchCoordinator(
      testWorkCoordinator,
      instanceProvider,
      outboxChannel,
      perspectiveChannel
    );

    // Act
    await coordinator.ProcessAndDistributeAsync(instanceId);

    // Assert - both channels should have work
    await Assert.That(outboxChannel.Reader.TryRead(out var readOutboxWork)).IsTrue();
    await Assert.That(readOutboxWork?.MessageId).IsEqualTo(outboxWork.MessageId);

    await Assert.That(perspectiveChannel.Reader.TryRead(out var readPerspectiveWork)).IsTrue();
    await Assert.That(readPerspectiveWork?.StreamId).IsEqualTo(perspectiveWork.StreamId);
  }
}
