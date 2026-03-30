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
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Transports;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Tests for bulk publish path in WorkCoordinatorPublisherWorker.
/// Verifies the drain logic, batch publishing, fallback to singular, and partial failure handling.
/// </summary>
public class WorkCoordinatorPublisherWorkerBulkPublishTests {
  private static MessageEnvelope<JsonElement> _createTestEnvelope(Guid messageId) {
    return new MessageEnvelope<JsonElement> {
      MessageId = MessageId.From(messageId),
      Payload = JsonDocument.Parse("{}").RootElement,
      Hops = []
    };
  }

  // Shared StreamId for tests that expect items with the same destination to be in one batch.
  // Stream-aware grouping splits by (Address, StreamId), so tests that rely on batching
  // must use the same StreamId for items that should stay together.
  private static readonly Guid _sharedTestStreamId = Guid.CreateVersion7();

  private static OutboxWork _createOutboxWork(Guid? messageId = null) {
    var id = messageId ?? Guid.CreateVersion7();
    return new OutboxWork {
      MessageId = id,
      Destination = "test-topic",
      Envelope = _createTestEnvelope(id),
      EnvelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[System.Text.Json.JsonElement, System.Text.Json]], Whizbang.Core",
      MessageType = "System.Text.Json.JsonElement, System.Text.Json",
      StreamId = _sharedTestStreamId,
      PartitionNumber = 1,
      Attempts = 0,
      Status = MessageProcessingStatus.Stored,
      Flags = WorkBatchOptions.None,
    };
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
      WorkToReturn.Clear();
      return Task.FromResult(new WorkBatch {
        OutboxWork = work,
        InboxWork = [],
        PerspectiveWork = []
      });
    }

    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion completion, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure failure, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default)
      => Task.FromResult<PerspectiveCursorInfo?>(null);
  }

  private sealed class BulkPublishCapableTestStrategy : IMessagePublishStrategy {
    public ConcurrentBag<OutboxWork> SingularPublished { get; } = [];
    public List<IReadOnlyList<OutboxWork>> BatchPublished { get; } = [];
    public bool IsReadyResult { get; set; } = true;
    public bool SupportsBulkPublish => true;
    public TaskCompletionSource BatchPublishSignal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Func<IReadOnlyList<OutboxWork>, IReadOnlyList<MessagePublishResult>>? BatchResultFunc { get; set; }

    public Task<bool> IsReadyAsync(CancellationToken cancellationToken = default) => Task.FromResult(IsReadyResult);

    public Task<MessagePublishResult> PublishAsync(OutboxWork work, CancellationToken cancellationToken) {
      SingularPublished.Add(work);
      return Task.FromResult(new MessagePublishResult {
        MessageId = work.MessageId,
        Success = true,
        CompletedStatus = MessageProcessingStatus.Published,
        Error = null
      });
    }

    public Task<IReadOnlyList<MessagePublishResult>> PublishBatchAsync(IReadOnlyList<OutboxWork> workItems, CancellationToken cancellationToken) {
      BatchPublished.Add(workItems);

      IReadOnlyList<MessagePublishResult> results;
      if (BatchResultFunc is not null) {
        results = BatchResultFunc(workItems);
      } else {
        results = workItems.Select(w => new MessagePublishResult {
          MessageId = w.MessageId,
          Success = true,
          CompletedStatus = MessageProcessingStatus.Published,
          Error = null
        }).ToList();
      }

      BatchPublishSignal.TrySetResult();
      return Task.FromResult(results);
    }
  }

  private sealed class SingularOnlyTestStrategy : IMessagePublishStrategy {
    public ConcurrentBag<OutboxWork> PublishedWork { get; } = [];
    public bool IsReadyResult { get; set; } = true;
    public TaskCompletionSource PublishSignal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // SupportsBulkPublish uses default interface implementation => false

    public Task<bool> IsReadyAsync(CancellationToken cancellationToken = default) => Task.FromResult(IsReadyResult);

    public Task<MessagePublishResult> PublishAsync(OutboxWork work, CancellationToken cancellationToken) {
      PublishedWork.Add(work);
      PublishSignal.TrySetResult();
      return Task.FromResult(new MessagePublishResult {
        MessageId = work.MessageId,
        Success = true,
        CompletedStatus = MessageProcessingStatus.Published,
        Error = null
      });
    }
  }

  private sealed class TestWorkChannelWriter : IWorkChannelWriter {
    private readonly Channel<OutboxWork> _channel;
    private readonly TaskCompletionSource _requeueSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _writeCount;
    public List<OutboxWork> WrittenWork { get; } = [];
    public Task RequeueSignal => _requeueSignal.Task;

    public TestWorkChannelWriter() {
      _channel = Channel.CreateUnbounded<OutboxWork>();
    }

    public ChannelReader<OutboxWork> Reader => _channel.Reader;

    public ValueTask WriteAsync(OutboxWork work, CancellationToken ct) {
      WrittenWork.Add(work);
      Interlocked.Increment(ref _writeCount);
      return _channel.Writer.WriteAsync(work, ct);
    }

    public bool TryWrite(OutboxWork work) {
      WrittenWork.Add(work);
      var count = Interlocked.Increment(ref _writeCount);
      var result = _channel.Writer.TryWrite(work);
      if (count > 1) {
        _requeueSignal.TrySetResult();
      }
      return result;
    }

    public void Complete() {
      _channel.Writer.Complete();
    }
  }

  private static ServiceInstanceProvider _createTestInstanceProvider() {
    return new ServiceInstanceProvider(
      Guid.NewGuid(),
      "TestService",
      "TestHost",
      Environment.ProcessId
    );
  }

  private static ServiceProvider _createHostedServiceCollection(
    IWorkCoordinator workCoordinator,
    IMessagePublishStrategy publishStrategy,
    IServiceInstanceProvider instanceProvider,
    IWorkChannelWriter workChannelWriter,
    int? maxBulkPublishBatchSize = null) {

    var services = new ServiceCollection();
    services.AddSingleton(workCoordinator);
    services.AddSingleton(publishStrategy);
    services.AddSingleton(instanceProvider);
    services.AddSingleton(workChannelWriter);
    var options = new WorkCoordinatorPublisherOptions {
      PollingIntervalMilliseconds = 100
    };
    if (maxBulkPublishBatchSize.HasValue) {
      options.MaxBulkPublishBatchSize = maxBulkPublishBatchSize.Value;
    }
    services.AddSingleton(Microsoft.Extensions.Options.Options.Create(options));
    services.AddLogging();
    services.AddHostedService<WorkCoordinatorPublisherWorker>();
    return services.BuildServiceProvider();
  }

  // ========================================
  // BULK PUBLISH TESTS
  // ========================================

  [Test]
  public async Task BulkPublish_WithBulkCapableStrategy_UsesBatchPublishAsync() {
    // Arrange
    var workCoordinator = new TestWorkCoordinator();
    var msg1 = _createOutboxWork();
    var msg2 = _createOutboxWork();
    workCoordinator.WorkToReturn = [msg1, msg2];

    var publishStrategy = new BulkPublishCapableTestStrategy();
    var instanceProvider = _createTestInstanceProvider();
    var channelWriter = new TestWorkChannelWriter();
    var services = _createHostedServiceCollection(workCoordinator, publishStrategy, instanceProvider, channelWriter);

    // Act
    var worker = services.GetRequiredService<Microsoft.Extensions.Hosting.IHostedService>();
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    await worker.StartAsync(cts.Token);

    // Wait for batch publish signal
    await publishStrategy.BatchPublishSignal.Task.WaitAsync(cts.Token);

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);

    // Assert — batch publish should have been called, not singular
    await Assert.That(publishStrategy.BatchPublished.Count).IsGreaterThanOrEqualTo(1);
    await Assert.That(publishStrategy.SingularPublished).Count().IsEqualTo(0)
      .Because("Bulk-capable strategy should not use singular publish path");
  }

  [Test]
  public async Task BulkPublish_WithoutBulkCapableStrategy_UsesSingularPublishAsync() {
    // Arrange
    var workCoordinator = new TestWorkCoordinator();
    var msg1 = _createOutboxWork();
    workCoordinator.WorkToReturn = [msg1];

    var publishStrategy = new SingularOnlyTestStrategy();
    var instanceProvider = _createTestInstanceProvider();
    var channelWriter = new TestWorkChannelWriter();
    var services = _createHostedServiceCollection(workCoordinator, publishStrategy, instanceProvider, channelWriter);

    // Act
    var worker = services.GetRequiredService<Microsoft.Extensions.Hosting.IHostedService>();
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    await worker.StartAsync(cts.Token);

    await publishStrategy.PublishSignal.Task.WaitAsync(cts.Token);

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);

    // Assert — singular publish should have been used
    await Assert.That(publishStrategy.PublishedWork.Count).IsGreaterThanOrEqualTo(1);
  }

  [Test]
  public async Task BulkPublish_TransportNotReady_RequeuesAllItemsAsync() {
    // Arrange
    var workCoordinator = new TestWorkCoordinator();
    var msg1 = _createOutboxWork();
    var msg2 = _createOutboxWork();
    workCoordinator.WorkToReturn = [msg1, msg2];

    var publishStrategy = new BulkPublishCapableTestStrategy { IsReadyResult = false };
    var instanceProvider = _createTestInstanceProvider();
    var channelWriter = new TestWorkChannelWriter();
    var services = _createHostedServiceCollection(workCoordinator, publishStrategy, instanceProvider, channelWriter);

    // Act
    var worker = services.GetRequiredService<Microsoft.Extensions.Hosting.IHostedService>();
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    await worker.StartAsync(cts.Token);

    // Wait for requeue signal
    await channelWriter.RequeueSignal.WaitAsync(cts.Token);

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);

    // Assert — all items should be re-queued, no batch publish called
    await Assert.That(publishStrategy.BatchPublished.Count).IsEqualTo(0);
    await Assert.That(channelWriter.WrittenWork.Count).IsGreaterThan(2)
      .Because("Both messages should be re-queued via TryWrite after transport not ready");
  }

  [Test]
  public async Task BulkPublish_PartialFailure_TracksCorrectlyAsync() {
    // Arrange
    var workCoordinator = new TestWorkCoordinator();
    var msg1 = _createOutboxWork();
    var msg2 = _createOutboxWork();
    workCoordinator.WorkToReturn = [msg1, msg2];

    var publishStrategy = new BulkPublishCapableTestStrategy {
      BatchResultFunc = items => items.Select((w, i) => new MessagePublishResult {
        MessageId = w.MessageId,
        Success = i == 0, // First succeeds, second fails
        CompletedStatus = i == 0 ? MessageProcessingStatus.Published : w.Status,
        Error = i == 0 ? null : "Transport error",
        Reason = i == 0 ? MessageFailureReason.None : MessageFailureReason.TransportException
      }).ToList()
    };
    var instanceProvider = _createTestInstanceProvider();
    var channelWriter = new TestWorkChannelWriter();
    var services = _createHostedServiceCollection(workCoordinator, publishStrategy, instanceProvider, channelWriter);

    // Act
    var worker = services.GetRequiredService<Microsoft.Extensions.Hosting.IHostedService>();
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    await worker.StartAsync(cts.Token);

    // Wait for the re-queue signal — fires when TryWrite is called after initial WriteAsync
    // This is deterministic: the failed msg2 is re-queued via TryWrite in the bulk publisher loop
    await channelWriter.RequeueSignal.WaitAsync(cts.Token);

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);

    // Assert — batch publish called, one success, one retryable failure re-queued
    await Assert.That(publishStrategy.BatchPublished.Count).IsGreaterThanOrEqualTo(1);
    // The failed message with TransportException should be re-queued (appears more than once in WrittenWork)
    var msg2WriteCount = channelWriter.WrittenWork.Count(w => w.MessageId == msg2.MessageId);
    await Assert.That(msg2WriteCount).IsGreaterThan(1)
      .Because("Transport exception should cause re-queue of the failed message");
  }

  [Test]
  public async Task BulkPublish_MaxBatchSize_LimitsDrainCountAsync() {
    // Arrange - Set max batch size to 2, provide 5 items
    var workCoordinator = new TestWorkCoordinator();
    var messages = Enumerable.Range(0, 5).Select(_ => _createOutboxWork()).ToList();
    workCoordinator.WorkToReturn = messages;

    var batchCount = 0;
    var publishStrategy = new BulkPublishCapableTestStrategy();
    var originalBatchSignal = publishStrategy.BatchPublishSignal;

    // Track how many batches and their sizes
    var batchSizes = new ConcurrentBag<int>();
    publishStrategy.BatchResultFunc = items => {
      batchSizes.Add(items.Count);
      Interlocked.Increment(ref batchCount);
      return items.Select(w => new MessagePublishResult {
        MessageId = w.MessageId,
        Success = true,
        CompletedStatus = MessageProcessingStatus.Published,
        Error = null
      }).ToList();
    };

    var instanceProvider = _createTestInstanceProvider();
    var channelWriter = new TestWorkChannelWriter();
    var services = _createHostedServiceCollection(workCoordinator, publishStrategy, instanceProvider, channelWriter, maxBulkPublishBatchSize: 2);

    // Act
    var worker = services.GetRequiredService<Microsoft.Extensions.Hosting.IHostedService>();
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    await worker.StartAsync(cts.Token);

    // Wait for all 5 messages to be processed
    await Task.Delay(500, CancellationToken.None);

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);

    // Assert — no single batch should exceed max size of 2
    await Assert.That(batchSizes.All(s => s <= 2)).IsTrue()
      .Because("Each batch should not exceed MaxBulkPublishBatchSize");
    await Assert.That(batchSizes.Sum()).IsEqualTo(5)
      .Because("All 5 messages should eventually be published across batches");
  }
}
