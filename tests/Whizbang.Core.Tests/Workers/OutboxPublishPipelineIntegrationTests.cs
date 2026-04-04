using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
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

/// <summary>
/// Integration tests verifying the complete end-to-end flow that broke in 0.9.7:
/// strategy flushes work → work written to channel → publisher worker reads from channel →
/// transport PublishAsync called → completion reported back to coordinator.
/// </summary>
public class OutboxPublishPipelineIntegrationTests {
  private static MessageEnvelope<JsonElement> _createTestEnvelope(Guid messageId) {
    return new MessageEnvelope<JsonElement> {
      MessageId = MessageId.From(messageId),
      Payload = JsonDocument.Parse("{}").RootElement,
      Hops = [],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };
  }

  private static OutboxMessage _createOutboxMessage(Guid? messageId = null) {
    var id = messageId ?? Guid.CreateVersion7();
    return new OutboxMessage {
      MessageId = id,
      Destination = "test-topic",
      Envelope = _createTestEnvelope(id),
      EnvelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[System.Text.Json.JsonElement, System.Text.Json]], Whizbang.Core",
      StreamId = Guid.CreateVersion7(),
      IsEvent = false,
      MessageType = "TestMessage, TestAssembly",
      Metadata = new EnvelopeMetadata {
        MessageId = MessageId.From(id),
        Hops = []
      }
    };
  }

  [Test]
  public async Task ImmediateStrategy_FlushAsync_PublishesViaWorkerAsync() {
    // Arrange
    var publishedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var coordinator = new TestWorkCoordinator();
    var publishStrategy = new TestPublishStrategy(publishedTcs);
    var channelWriter = new TestWorkChannelWriter();
    var instanceProvider = new TestServiceInstanceProvider();
    var options = new WorkCoordinatorOptions();

    var messageId = Guid.CreateVersion7();
    coordinator.WorkToReturn = [
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

    var strategy = new ImmediateWorkCoordinatorStrategy(
      coordinator, instanceProvider, options, workChannelWriter: channelWriter
    );

    // Start worker consuming from same channel
    var sp = _buildServiceProvider(coordinator, publishStrategy, instanceProvider, channelWriter);
    var worker = sp.GetRequiredService<IHostedService>();
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    await worker.StartAsync(cts.Token);

    try {
      // Act — coordinator loop picks up WorkToReturn via ProcessWorkBatchAsync,
      // writes to channel, publisher loop reads and publishes.
      // No need to manually flush — the coordinator loop handles this.
      await publishedTcs.Task.WaitAsync(TimeSpan.FromSeconds(3));

      // Assert
      await Assert.That(publishStrategy.PublishedWork).Count().IsGreaterThanOrEqualTo(1);
      await Assert.That(publishStrategy.PublishedWork.Any(w => w.MessageId == messageId)).IsTrue();
    } finally {
      await cts.CancelAsync();
      await worker.StopAsync(CancellationToken.None);
    }
  }

  [Test]
  public async Task IntervalStrategy_FlushAsync_PublishesViaWorkerAsync() {
    // Arrange
    var publishedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var coordinator = new TestWorkCoordinator();
    var publishStrategy = new TestPublishStrategy(publishedTcs);
    var channelWriter = new TestWorkChannelWriter();
    var instanceProvider = new TestServiceInstanceProvider();
    var options = new WorkCoordinatorOptions {
      IntervalMilliseconds = 60000,
      PartitionCount = 10000,
      LeaseSeconds = 300,
      StaleThresholdSeconds = 300
    };

    var messageId = Guid.CreateVersion7();
    coordinator.WorkToReturn = [
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

    var strategy = new IntervalWorkCoordinatorStrategy(
      coordinator, instanceProvider, options, workChannelWriter: channelWriter
    );

    var sp = _buildServiceProvider(coordinator, publishStrategy, instanceProvider, channelWriter);
    var worker = sp.GetRequiredService<IHostedService>();
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    await worker.StartAsync(cts.Token);

    try {
      // Act — coordinator loop picks up WorkToReturn, no manual flush needed
      await publishedTcs.Task.WaitAsync(TimeSpan.FromSeconds(3));

      // Assert
      await Assert.That(publishStrategy.PublishedWork).Count().IsGreaterThanOrEqualTo(1);
      await Assert.That(publishStrategy.PublishedWork.Any(w => w.MessageId == messageId)).IsTrue();
    } finally {
      await cts.CancelAsync();
      await worker.StopAsync(CancellationToken.None);
      await strategy.DisposeAsync();
    }
  }

  [Test]
  public async Task BatchStrategy_FlushAsync_PublishesViaWorkerAsync() {
    // Arrange
    var publishedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var coordinator = new TestWorkCoordinator();
    var publishStrategy = new TestPublishStrategy(publishedTcs);
    var channelWriter = new TestWorkChannelWriter();
    var instanceProvider = new TestServiceInstanceProvider();
    var options = new WorkCoordinatorOptions {
      Strategy = WorkCoordinatorStrategy.Batch,
      BatchSize = 100,
      IntervalMilliseconds = 60000,
      PartitionCount = 10000,
      LeaseSeconds = 300,
      StaleThresholdSeconds = 300
    };

    var messageId = Guid.CreateVersion7();
    coordinator.WorkToReturn = [
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

    var strategy = new BatchWorkCoordinatorStrategy(
      coordinator, instanceProvider, options, workChannelWriter: channelWriter
    );

    var sp = _buildServiceProvider(coordinator, publishStrategy, instanceProvider, channelWriter);
    var worker = sp.GetRequiredService<IHostedService>();
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    await worker.StartAsync(cts.Token);

    try {
      // Act — coordinator loop picks up WorkToReturn, no manual flush needed
      await publishedTcs.Task.WaitAsync(TimeSpan.FromSeconds(3));

      // Assert
      await Assert.That(publishStrategy.PublishedWork).Count().IsGreaterThanOrEqualTo(1);
      await Assert.That(publishStrategy.PublishedWork.Any(w => w.MessageId == messageId)).IsTrue();
    } finally {
      await cts.CancelAsync();
      await worker.StopAsync(CancellationToken.None);
      await strategy.DisposeAsync();
    }
  }

  [Test]
  public async Task ScopedStrategy_FlushAsync_PublishesViaWorkerAsync() {
    // Arrange - regression lock: Scoped must still work after refactoring to use helper
    var publishedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var coordinator = new TestWorkCoordinator();
    var publishStrategy = new TestPublishStrategy(publishedTcs);
    var channelWriter = new TestWorkChannelWriter();
    var instanceProvider = new TestServiceInstanceProvider();
    var options = new WorkCoordinatorOptions();

    var messageId = Guid.CreateVersion7();
    coordinator.WorkToReturn = [
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

    var strategy = new ScopedWorkCoordinatorStrategy(
      coordinator, instanceProvider, channelWriter, options
    );

    var sp = _buildServiceProvider(coordinator, publishStrategy, instanceProvider, channelWriter);
    var worker = sp.GetRequiredService<IHostedService>();
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    await worker.StartAsync(cts.Token);

    try {
      // Act — coordinator loop picks up WorkToReturn, no manual flush needed
      await publishedTcs.Task.WaitAsync(TimeSpan.FromSeconds(3));

      // Assert
      await Assert.That(publishStrategy.PublishedWork).Count().IsGreaterThanOrEqualTo(1);
      await Assert.That(publishStrategy.PublishedWork.Any(w => w.MessageId == messageId)).IsTrue();
    } finally {
      await cts.CancelAsync();
      await worker.StopAsync(CancellationToken.None);
    }
  }

  // ========================================
  // PostLifecycle Integration Tests
  // ========================================

  [Test]
  public async Task OutboxWorker_FiresPostLifecycle_WhenEventLeavesServiceAsync() {
    // Arrange — real WorkCoordinatorPublisherWorker with lifecycle tracking.
    // The coordinator loop processes WorkToReturn on startup, writing outbox work
    // to the channel. The publisher loop reads from the channel and publishes.
    // PostLifecycle stages fire after publishing.
    var postLifecycleFired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var publishedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var coordinator = new TestWorkCoordinator();
    var publishStrategy = new TestPublishStrategy(publishedTcs);
    var channelWriter = new TestWorkChannelWriter();
    var instanceProvider = new TestServiceInstanceProvider();

    var messageId = Guid.CreateVersion7();
    coordinator.WorkToReturn = [
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

    var strategy = new ImmediateWorkCoordinatorStrategy(
      coordinator, instanceProvider, new WorkCoordinatorOptions(), workChannelWriter: channelWriter);

    var stagesFired = new ConcurrentBag<LifecycleStage>();
    var sp = _buildServiceProviderWithLifecycle(
      coordinator, publishStrategy, instanceProvider, channelWriter,
      stagesFired, postLifecycleFired);
    var worker = sp.GetRequiredService<Microsoft.Extensions.Hosting.IHostedService>();
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    await worker.StartAsync(cts.Token);

    try {
      // Act — coordinator loop picks up WorkToReturn via ProcessWorkBatchAsync,
      // writes to channel, publisher loop reads and publishes.
      // No need to call strategy.FlushAsync — the coordinator loop handles this.
      await publishedTcs.Task.WaitAsync(TimeSpan.FromSeconds(3));

      // postLifecycleFired set deterministically by PostLifecycleTrackingInvoker
      await postLifecycleFired.Task.WaitAsync(TimeSpan.FromSeconds(3));

      // Drain detached tasks — PostLifecycleDetached runs via Task.Run and may not have
      // added to stagesFired yet even though PostLifecycleInline (which signals the TCS) has completed
      var lifecycleCoordinator = (Whizbang.Core.Lifecycle.LifecycleCoordinator)sp.GetRequiredService<Whizbang.Core.Lifecycle.ILifecycleCoordinator>();
      await lifecycleCoordinator.DrainAllDetachedAsync();

      // Assert — publish happened AND PostLifecycle fired
      await Assert.That(publishStrategy.PublishedWork).Count().IsGreaterThanOrEqualTo(1);
      await Assert.That(stagesFired.Contains(LifecycleStage.PostLifecycleDetached)).IsTrue()
        .Because("OutboxWorker must fire PostLifecycleDetached when event leaves the service");
      await Assert.That(stagesFired.Contains(LifecycleStage.PostLifecycleInline)).IsTrue()
        .Because("OutboxWorker must fire PostLifecycleInline when event leaves the service");
    } finally {
      await cts.CancelAsync();
      await worker.StopAsync(CancellationToken.None);
    }
  }

  /// <summary>
  /// IReceptorInvoker that records lifecycle stages and signals when PostLifecycle fires.
  /// </summary>
  private sealed class PostLifecycleTrackingInvoker(
    ConcurrentBag<LifecycleStage> stagesFired,
    TaskCompletionSource? postLifecycleSignal = null) : IReceptorInvoker {

    public ValueTask InvokeAsync(
        IMessageEnvelope envelope,
        LifecycleStage stage,
        ILifecycleContext? context = null,
        CancellationToken cancellationToken = default) {
      stagesFired.Add(stage);
      if (stage == LifecycleStage.PostLifecycleInline) {
        postLifecycleSignal?.TrySetResult();
      }
      return ValueTask.CompletedTask;
    }
  }

  /// <summary>
  /// Fake deserializer that returns the raw JsonElement payload directly.
  /// Avoids dependency on JsonContextRegistry which requires registered types.
  /// </summary>
  private sealed class PassthroughLifecycleMessageDeserializer : ILifecycleMessageDeserializer {
    public object DeserializeFromJsonElement(JsonElement jsonElement, string messageTypeName) {
      return jsonElement;
    }

    public object DeserializeFromEnvelope(IMessageEnvelope<JsonElement> envelope, string envelopeTypeName) {
      return envelope.Payload;
    }

    public object DeserializeFromEnvelope(IMessageEnvelope<JsonElement> envelope) {
      return envelope.Payload;
    }

    public object DeserializeFromBytes(byte[] jsonBytes, string messageTypeName) {
      using var doc = JsonDocument.Parse(jsonBytes);
      return doc.RootElement.Clone();
    }
  }

  // ========================================
  // Test Infrastructure
  // ========================================

  private static ServiceProvider _buildServiceProvider(
    IWorkCoordinator workCoordinator,
    IMessagePublishStrategy publishStrategy,
    IServiceInstanceProvider instanceProvider,
    IWorkChannelWriter channelWriter) {

    var services = new ServiceCollection();
    services.AddSingleton(workCoordinator);
    services.AddSingleton(publishStrategy);
    services.AddSingleton(instanceProvider);
    services.AddSingleton(channelWriter);
    services.AddSingleton(Microsoft.Extensions.Options.Options.Create(new WorkCoordinatorPublisherOptions {
      PollingIntervalMilliseconds = 50
    }));
    services.AddLogging();
    services.AddHostedService<WorkCoordinatorPublisherWorker>();
    return services.BuildServiceProvider();
  }

  /// <summary>
  /// Same as _buildServiceProvider but with lifecycle coordinator, tracking invoker,
  /// and ILifecycleMessageDeserializer to enable PostLifecycle testing.
  /// </summary>
  private static ServiceProvider _buildServiceProviderWithLifecycle(
    IWorkCoordinator workCoordinator,
    IMessagePublishStrategy publishStrategy,
    IServiceInstanceProvider instanceProvider,
    IWorkChannelWriter channelWriter,
    ConcurrentBag<LifecycleStage> stagesFired,
    TaskCompletionSource postLifecycleSignal) {

    var services = new ServiceCollection();
    services.AddSingleton(workCoordinator);
    services.AddSingleton(publishStrategy);
    services.AddSingleton(instanceProvider);
    services.AddSingleton(channelWriter);
    services.AddSingleton(Microsoft.Extensions.Options.Options.Create(new WorkCoordinatorPublisherOptions {
      PollingIntervalMilliseconds = 50
    }));
    services.AddSingleton<Whizbang.Core.Lifecycle.ILifecycleCoordinator, Whizbang.Core.Lifecycle.LifecycleCoordinator>();
    services.AddScoped<IReceptorInvoker>(sp => new PostLifecycleTrackingInvoker(stagesFired, postLifecycleSignal));
    services.AddSingleton<ILifecycleMessageDeserializer>(new PassthroughLifecycleMessageDeserializer());
    services.AddLogging();
    services.AddHostedService<WorkCoordinatorPublisherWorker>();
    return services.BuildServiceProvider();
  }

  private sealed class TestWorkCoordinator : IWorkCoordinator {
    public List<OutboxWork> WorkToReturn { get; set; } = [];
    public List<MessageCompletion> ReceivedCompletions { get; } = [];
    public List<MessageFailure> ReceivedFailures { get; } = [];

    public Task<WorkBatch> ProcessWorkBatchAsync(
      ProcessWorkBatchRequest request,
      CancellationToken cancellationToken = default) {
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

    public Task ReportPerspectiveCompletionAsync(
      PerspectiveCursorCompletion completion,
      CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task ReportPerspectiveFailureAsync(
      PerspectiveCursorFailure failure,
      CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(
      Guid streamId,
      string perspectiveName,
      CancellationToken cancellationToken = default) =>
      Task.FromResult<PerspectiveCursorInfo?>(null);
  }

  private sealed class TestPublishStrategy(TaskCompletionSource? publishedTcs = null) : IMessagePublishStrategy {
    private readonly TaskCompletionSource? _publishedTcs = publishedTcs;
    public ConcurrentBag<OutboxWork> PublishedWork { get; } = [];

    public Task<bool> IsReadyAsync(CancellationToken cancellationToken = default) =>
      Task.FromResult(true);

    public Task<MessagePublishResult> PublishAsync(OutboxWork work, CancellationToken cancellationToken) {
      PublishedWork.Add(work);
      _publishedTcs?.TrySetResult();

      return Task.FromResult(new MessagePublishResult {
        MessageId = work.MessageId,
        Success = true,
        CompletedStatus = MessageProcessingStatus.Published,
        Error = null
      });
    }
  }

  private sealed class TestWorkChannelWriter : IWorkChannelWriter {
    public void ClearInFlight() { }
    private readonly Channel<OutboxWork> _channel;
    public List<OutboxWork> WrittenWork { get; } = [];

    public TestWorkChannelWriter() {
      _channel = Channel.CreateUnbounded<OutboxWork>();
    }

    public ChannelReader<OutboxWork> Reader => _channel.Reader;

    public ValueTask WriteAsync(OutboxWork work, CancellationToken ct) {
      WrittenWork.Add(work);
      return _channel.Writer.WriteAsync(work, ct);
    }

    public bool TryWrite(OutboxWork work) {
      WrittenWork.Add(work);
      return _channel.Writer.TryWrite(work);
    }

    public void Complete() {
      _channel.Writer.Complete();
    }

    public bool IsInFlight(Guid messageId) => false;
    public void RemoveInFlight(Guid messageId) { }
    public bool ShouldRenewLease(Guid messageId) => false;
  }

  private sealed class TestServiceInstanceProvider : IServiceInstanceProvider {
    public Guid InstanceId { get; } = Guid.CreateVersion7();
    public string ServiceName => "TestService";
    public string HostName => "test-host";
    public int ProcessId => 12345;

    public ServiceInstanceInfo ToInfo() => new() {
      InstanceId = InstanceId,
      ServiceName = ServiceName,
      HostName = HostName,
      ProcessId = ProcessId
    };
  }
}
