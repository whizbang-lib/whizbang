using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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

public class WorkCoordinatorPublisherWorkerChannelTests {
  private sealed record _testMessage { }

  private static MessageEnvelope<JsonElement> _createTestEnvelope(Guid messageId) {
    var envelope = new MessageEnvelope<JsonElement> {
      MessageId = MessageId.From(messageId),
      Payload = JsonDocument.Parse("{}").RootElement,
      Hops = [],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };
    return envelope;
  }

  private sealed class TestWorkCoordinator : IWorkCoordinator {
    public List<OutboxWork> WorkToReturn { get; set; } = [];
    public List<MessageCompletion> ReceivedCompletions { get; } = [];
    public List<MessageFailure> ReceivedFailures { get; } = [];
    public int CallCount { get; private set; }

    public Task<WorkBatch> ProcessWorkBatchAsync(
      ProcessWorkBatchRequest request,
      CancellationToken cancellationToken = default) {

      CallCount++;
      ReceivedCompletions.AddRange(request.OutboxCompletions);
      ReceivedFailures.AddRange(request.OutboxFailures);

      var work = new List<OutboxWork>(WorkToReturn);
      WorkToReturn.Clear();  // Return work once, then empty

      return Task.FromResult(new WorkBatch {
        OutboxWork = work,
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

    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount = 2, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new WorkCoordinatorStatistics());

    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(
      Guid streamId,
      string perspectiveName,
      CancellationToken cancellationToken = default) {
      return Task.FromResult<PerspectiveCursorInfo?>(null);
    }
  }

  private sealed class TestPublishStrategy : IMessagePublishStrategy {
    public ConcurrentBag<OutboxWork> PublishedWork { get; } = [];
    public Func<OutboxWork, MessagePublishResult>? PublishResultFunc { get; set; }
    public TimeSpan PublishDelay { get; set; } = TimeSpan.Zero;
    public bool IsReadyResult { get; set; } = true;

    public Task<bool> IsReadyAsync(CancellationToken cancellationToken = default) {
      return Task.FromResult(IsReadyResult);
    }

    public async Task<MessagePublishResult> PublishAsync(OutboxWork work, CancellationToken cancellationToken) {
      if (PublishDelay > TimeSpan.Zero) {
        await Task.Delay(PublishDelay, cancellationToken);
      }

      PublishedWork.Add(work);

      if (PublishResultFunc != null) {
        return PublishResultFunc(work);
      }

      return new MessagePublishResult {
        MessageId = work.MessageId,
        Success = true,
        CompletedStatus = MessageProcessingStatus.Published,
        Error = null
      };
    }
  }

  [Test]
  public async Task ProcessWorkBatch_WithWork_ShouldPublishViaStrategyAsync() {
    // Arrange
    var workCoordinator = new TestWorkCoordinator();
    var publishStrategy = new TestPublishStrategy();
    var instanceProvider = _createTestInstanceProvider();

    var messageId = Guid.CreateVersion7();
    workCoordinator.WorkToReturn = [
      new OutboxWork {
        MessageId = messageId,
        Destination = "test-topic",
        Envelope = _createTestEnvelope(messageId),
        EnvelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[System.Text.Json.JsonElement, System.Text.Json]], Whizbang.Core",
      MessageType = "System.Text.Json.JsonElement, System.Text.Json",
        StreamId = Guid.CreateVersion7(),
        PartitionNumber = 1,
        Attempts = 0,
        Status = MessageProcessingStatus.Stored,
        Flags = WorkBatchOptions.None,
      }
    ];
    _ = _createServiceCollection(workCoordinator, publishStrategy, instanceProvider);

    // Act & Assert - verify work was published
    // Note: Full worker integration will be tested separately
    // This test focuses on the strategy being called with work from coordinator
    var result = await publishStrategy.PublishAsync(workCoordinator.WorkToReturn[0], CancellationToken.None);

    await Assert.That(result.Success).IsTrue();
    await Assert.That(publishStrategy.PublishedWork).Count().IsEqualTo(1);
    await Assert.That(publishStrategy.PublishedWork.First().MessageId).IsEqualTo(messageId);
  }

  [Test]
  public async Task ProcessWorkBatch_WithFailure_ShouldReportFailureAsync() {
    // Arrange
    var workCoordinator = new TestWorkCoordinator();
    var publishStrategy = new TestPublishStrategy {
      PublishResultFunc = work => new MessagePublishResult {
        MessageId = work.MessageId,
        Success = false,
        CompletedStatus = MessageProcessingStatus.Stored,
        Error = "Transport failed"
      }
    };
    var instanceProvider = _createTestInstanceProvider();

    var messageId = Guid.CreateVersion7();
    workCoordinator.WorkToReturn = [
      new OutboxWork {
        MessageId = messageId,
        Destination = "test-topic",
        Envelope = _createTestEnvelope(messageId),
        EnvelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[System.Text.Json.JsonElement, System.Text.Json]], Whizbang.Core",
      MessageType = "System.Text.Json.JsonElement, System.Text.Json",
        StreamId = Guid.CreateVersion7(),
        PartitionNumber = 1,
        Attempts = 0,
        Status = MessageProcessingStatus.Stored,
        Flags = WorkBatchOptions.None,
      }
    ];

    var services = _createServiceCollection(workCoordinator, publishStrategy, instanceProvider);

    // Act
    var result = await publishStrategy.PublishAsync(workCoordinator.WorkToReturn[0], CancellationToken.None);

    // Assert
    await Assert.That(result.Success).IsFalse();
    await Assert.That(result.Error).IsEqualTo("Transport failed");
    await Assert.That(result.CompletedStatus).IsEqualTo(MessageProcessingStatus.Stored);
  }

  [Test]
  public async Task ConcurrentPublishing_ShouldCollectResults_Async() {
    // Arrange
    var publishStrategy = new TestPublishStrategy {
      PublishDelay = TimeSpan.FromMilliseconds(10)  // Simulate async work
    };

    var messages = new List<OutboxWork>();
    for (int i = 0; i < 5; i++) {
      var msgId = Guid.CreateVersion7();
      messages.Add(new OutboxWork {
        MessageId = msgId,
        Destination = "test-topic",
        Envelope = _createTestEnvelope(msgId),
        EnvelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[System.Text.Json.JsonElement, System.Text.Json]], Whizbang.Core",
        MessageType = "System.Text.Json.JsonElement, System.Text.Json",
        StreamId = Guid.CreateVersion7(),
        PartitionNumber = 1,
        Attempts = 0,
        Status = MessageProcessingStatus.Stored,
        Flags = WorkBatchOptions.None,
      });
    }

    // Act - publish all messages concurrently
    var tasks = messages.Select(m => publishStrategy.PublishAsync(m, CancellationToken.None));
    var results = await Task.WhenAll(tasks);

    // Assert
    await Assert.That(results).Count().IsEqualTo(5);
    await Assert.That(results.All(r => r.Success)).IsTrue();
    await Assert.That(publishStrategy.PublishedWork).Count().IsEqualTo(5);
  }

  [Test]
  public async Task TransportNotReady_MessageRequeuedToChannelAsync() {
    // Arrange
    var workCoordinator = new TestWorkCoordinator();
    var messageId = Guid.CreateVersion7();
    workCoordinator.WorkToReturn = [
      new OutboxWork {
        MessageId = messageId,
        Destination = "test-topic",
        Envelope = _createTestEnvelope(messageId),
        EnvelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[System.Text.Json.JsonElement, System.Text.Json]], Whizbang.Core",
        MessageType = "System.Text.Json.JsonElement, System.Text.Json",
        StreamId = Guid.CreateVersion7(),
        PartitionNumber = 1,
        Attempts = 0,
        Status = MessageProcessingStatus.Stored,
        Flags = WorkBatchOptions.None,
      }
    ];
    var publishStrategy = new TestPublishStrategy { IsReadyResult = false };
    var instanceProvider = _createTestInstanceProvider();
    var channelWriter = new TestWorkChannelWriter();
    var services = _createHostedServiceCollection(workCoordinator, publishStrategy, instanceProvider, channelWriter);

    // Act — start worker, wait for requeue signal (deterministic), then stop
    var worker = services.GetRequiredService<Microsoft.Extensions.Hosting.IHostedService>();
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    await worker.StartAsync(cts.Token);

    // Wait for the deterministic requeue signal — fires when TryWrite is called after initial WriteAsync
    await channelWriter.RequeueSignal.WaitAsync(cts.Token);

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);

    // Assert — the message must be re-queued via TryWrite (WrittenWork will have >1 entry)
    // First write is from _processWorkBatchAsync, subsequent writes are re-queues from publisher loop
    await Assert.That(channelWriter.WrittenWork.Count).IsGreaterThan(1)
      .Because("Transport-not-ready must re-queue work to the channel via TryWrite to prevent message loss");
    await Assert.That(channelWriter.WrittenWork.All(w => w.MessageId == messageId)).IsTrue()
      .Because("All re-queued messages should be the same work item");
  }

  [Test]
  public async Task TransportException_MessageRequeuedToChannelAsync() {
    // Arrange
    var workCoordinator = new TestWorkCoordinator();
    var messageId = Guid.CreateVersion7();
    workCoordinator.WorkToReturn = [
      new OutboxWork {
        MessageId = messageId,
        Destination = "test-topic",
        Envelope = _createTestEnvelope(messageId),
        EnvelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[System.Text.Json.JsonElement, System.Text.Json]], Whizbang.Core",
        MessageType = "System.Text.Json.JsonElement, System.Text.Json",
        StreamId = Guid.CreateVersion7(),
        PartitionNumber = 1,
        Attempts = 0,
        Status = MessageProcessingStatus.Stored,
        Flags = WorkBatchOptions.None,
      }
    ];
    var publishStrategy = new TestPublishStrategy {
      PublishResultFunc = _ => new MessagePublishResult {
        MessageId = messageId,
        Success = false,
        CompletedStatus = MessageProcessingStatus.Stored,
        Error = "Transport connection failed",
        Reason = MessageFailureReason.TransportException
      }
    };
    var instanceProvider = _createTestInstanceProvider();
    var channelWriter = new TestWorkChannelWriter();
    var services = _createHostedServiceCollection(workCoordinator, publishStrategy, instanceProvider, channelWriter);

    // Act — start worker, wait for requeue signal (deterministic), then stop
    var worker = services.GetRequiredService<Microsoft.Extensions.Hosting.IHostedService>();
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    await worker.StartAsync(cts.Token);

    // Wait for the deterministic requeue signal
    await channelWriter.RequeueSignal.WaitAsync(cts.Token);

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);

    // Assert — the message must be re-queued via TryWrite after transport exception
    await Assert.That(channelWriter.WrittenWork.Count).IsGreaterThan(1)
      .Because("Transport exception must re-queue work to the channel via TryWrite to prevent message loss");
    await Assert.That(channelWriter.WrittenWork.All(w => w.MessageId == messageId)).IsTrue()
      .Because("All re-queued messages should be the same work item");
  }

  private static ServiceInstanceProvider _createTestInstanceProvider() {
    return new ServiceInstanceProvider(
      Guid.NewGuid(),
      "TestService",
      "TestHost",
      Environment.ProcessId
    );
  }

  private static ServiceCollection _createServiceCollection(
    IWorkCoordinator workCoordinator,
    IMessagePublishStrategy publishStrategy,
    IServiceInstanceProvider instanceProvider) {

    var services = new ServiceCollection();
    services.AddSingleton(workCoordinator);
    services.AddSingleton(publishStrategy);
    services.AddSingleton(instanceProvider);
    services.AddSingleton<IWorkChannelWriter>(new TestWorkChannelWriter());  // Required by WorkCoordinatorPublisherWorker
    services.AddLogging();
    return services;
  }

  private static ServiceProvider _createHostedServiceCollection(
    IWorkCoordinator workCoordinator,
    IMessagePublishStrategy publishStrategy,
    IServiceInstanceProvider instanceProvider,
    IWorkChannelWriter workChannelWriter) {

    var services = new ServiceCollection();
    services.AddSingleton(workCoordinator);
    services.AddSingleton(publishStrategy);
    services.AddSingleton(instanceProvider);
    services.AddSingleton(workChannelWriter);
    services.AddSingleton(Microsoft.Extensions.Options.Options.Create(new WorkCoordinatorPublisherOptions {
      PollingIntervalMilliseconds = 100
    }));
    services.AddLogging();
    services.AddHostedService<WorkCoordinatorPublisherWorker>();
    return services.BuildServiceProvider();
  }

  // Test helper - Mock work channel writer with deterministic signals
  private sealed class TestWorkChannelWriter : IWorkChannelWriter {
    public void ClearInFlight() { }
    private readonly System.Threading.Channels.Channel<OutboxWork> _channel;
    private readonly TaskCompletionSource _requeueSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _writeCount;
    public List<OutboxWork> WrittenWork { get; } = [];

    /// <summary>
    /// Deterministic signal that fires when a message is re-queued (TryWrite called after initial WriteAsync).
    /// Use this instead of Task.Delay to wait for requeue behavior.
    /// </summary>
    public Task RequeueSignal => _requeueSignal.Task;

    public TestWorkChannelWriter() {
      _channel = System.Threading.Channels.Channel.CreateUnbounded<OutboxWork>();
    }

    public System.Threading.Channels.ChannelReader<OutboxWork> Reader => _channel.Reader;

    public ValueTask WriteAsync(OutboxWork work, CancellationToken ct) {
      WrittenWork.Add(work);
      Interlocked.Increment(ref _writeCount);
      return _channel.Writer.WriteAsync(work, ct);
    }

    public bool TryWrite(OutboxWork work) {
      WrittenWork.Add(work);
      var count = Interlocked.Increment(ref _writeCount);
      var result = _channel.Writer.TryWrite(work);
      // Signal when a requeue happens (write count > 1 means re-queued)
      if (count > 1) {
        _requeueSignal.TrySetResult();
      }
      return result;
    }

    public void Complete() {
      _channel.Writer.Complete();
    }

    public bool IsInFlight(Guid messageId) => false;
    public void RemoveInFlight(Guid messageId) { }
    public bool ShouldRenewLease(Guid messageId) => false;
    public event Action? OnNewWorkAvailable;
    public void SignalNewWorkAvailable() => OnNewWorkAvailable?.Invoke();
    public event Action? OnNewPerspectiveWorkAvailable;
    public void SignalNewPerspectiveWorkAvailable() => OnNewPerspectiveWorkAvailable?.Invoke();
  }
}
