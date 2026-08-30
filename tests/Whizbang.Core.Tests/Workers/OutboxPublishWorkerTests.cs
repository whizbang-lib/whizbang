using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

[NotInParallel("WhizbangBackgroundServiceTests")]
public class OutboxPublishWorkerTests {

  // ============================================================
  // Test fakes
  // ============================================================

  private sealed class FakeWorkChannelWriter : IWorkChannelWriter {
    private readonly Channel<OutboxWork> _channel = Channel.CreateUnbounded<OutboxWork>();
    public ConcurrentBag<Guid> RemovedInFlight { get; } = [];
    public ChannelReader<OutboxWork> Reader => _channel.Reader;
    public ValueTask WriteAsync(OutboxWork work, CancellationToken ct = default) => _channel.Writer.WriteAsync(work, ct);
    public bool TryWrite(OutboxWork work) => _channel.Writer.TryWrite(work);
    public void Complete() => _channel.Writer.Complete();
    public bool IsInFlight(Guid messageId) => false;
    public void RemoveInFlight(Guid messageId) { RemovedInFlight.Add(messageId); }
    public void ClearInFlight() { }
    public bool ShouldRenewLease(Guid messageId) => false;
    public event Action? OnNewWorkAvailable;
    public void SignalNewWorkAvailable() => OnNewWorkAvailable?.Invoke();
    public event Action? OnNewPerspectiveWorkAvailable;
    public void SignalNewPerspectiveWorkAvailable() => OnNewPerspectiveWorkAvailable?.Invoke();
  }

  private sealed class FakeOutboxCompletionChannel : IOutboxCompletionChannel {
    public TaskCompletionSource<Guid> FirstId { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public ConcurrentBag<Guid> AllIds { get; } = [];
    public ValueTask EnqueueAsync(Guid id, CancellationToken ct = default) {
      AllIds.Add(id);
      FirstId.TrySetResult(id);
      return ValueTask.CompletedTask;
    }
  }

  private sealed class FakeFailureChannel : IFailureChannel {
    public TaskCompletionSource<MessageFailure> FirstFailure { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public ConcurrentBag<(WorkCategory cat, MessageFailure f)> All { get; } = [];
    public ValueTask EnqueueAsync(WorkCategory category, MessageFailure failure, CancellationToken ct = default) {
      All.Add((category, failure));
      FirstFailure.TrySetResult(failure);
      return ValueTask.CompletedTask;
    }
  }

  private sealed class FakeLeaseRenewalChannel : ILeaseRenewalChannel {
    public TaskCompletionSource<Guid> FirstRenewal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public ConcurrentBag<(WorkCategory cat, Guid id)> All { get; } = [];
    public ValueTask EnqueueAsync(WorkCategory category, Guid id, CancellationToken ct = default) {
      All.Add((category, id));
      FirstRenewal.TrySetResult(id);
      return ValueTask.CompletedTask;
    }
  }

  private sealed class FakeSingularStrategy : IMessagePublishStrategy {
    public bool ReadyValue { get; set; } = true;
    public bool ShouldFailPublish { get; set; }
    public TaskCompletionSource<OutboxWork> FirstPublished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public ConcurrentBag<OutboxWork> Published { get; } = [];

    public Task<bool> IsReadyAsync(CancellationToken ct = default) => Task.FromResult(ReadyValue);
    public Task<MessagePublishResult> PublishAsync(OutboxWork work, CancellationToken ct) {
      Published.Add(work);
      FirstPublished.TrySetResult(work);
      return Task.FromResult(new MessagePublishResult {
        MessageId = work.MessageId,
        Success = !ShouldFailPublish,
        CompletedStatus = ShouldFailPublish ? work.Status : MessageProcessingStatus.Published,
        Error = ShouldFailPublish ? "simulated failure" : null,
        Reason = ShouldFailPublish ? MessageFailureReason.Unknown : MessageFailureReason.Unknown
      });
    }
  }

  private sealed class FakeBulkStrategy : IMessagePublishStrategy {
    public bool ReadyValue { get; set; } = true;
    public TaskCompletionSource<IReadOnlyList<OutboxWork>> FirstBatch { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public ConcurrentBag<OutboxWork> AllPublished { get; } = [];
    public bool SupportsBulkPublish => true;
    public Task<bool> IsReadyAsync(CancellationToken ct = default) => Task.FromResult(ReadyValue);
    public Task<MessagePublishResult> PublishAsync(OutboxWork work, CancellationToken ct)
      => throw new InvalidOperationException("FakeBulkStrategy: only PublishBatchAsync should be called");
    public Task<IReadOnlyList<MessagePublishResult>> PublishBatchAsync(IReadOnlyList<OutboxWork> works, CancellationToken ct) {
      foreach (var w in works) { AllPublished.Add(w); }
      FirstBatch.TrySetResult(works);
      var results = works.Select(w => new MessagePublishResult {
        MessageId = w.MessageId,
        Success = true,
        CompletedStatus = MessageProcessingStatus.Published,
        Error = null
      }).ToList();
      return Task.FromResult<IReadOnlyList<MessagePublishResult>>(results);
    }
  }

  private static OutboxWork _makeWork(Guid? id = null) {
    var msgId = id ?? (Guid)TrackedGuid.NewMedo();
    var streamId = (Guid)TrackedGuid.NewMedo();
    return new OutboxWork {
      MessageId = msgId,
      Destination = "test-topic",
      Envelope = new MessageEnvelope<JsonElement> {
        MessageId = MessageId.From(msgId),
        Payload = JsonDocument.Parse("{}").RootElement,
        Hops = [],
        DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
      },
      EnvelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[System.Text.Json.JsonElement, System.Text.Json]], Whizbang.Core",
      MessageType = "System.Text.Json.JsonElement, System.Text.Json",
      StreamId = streamId,
      PartitionNumber = 1,
      Attempts = 0,
      Status = MessageProcessingStatus.Stored,
      Flags = WorkBatchOptions.None,
    };
  }

  // ============================================================
  // Tests
  // ============================================================

  [Test]
  public async Task SingularPublish_HappyPath_RoutesToOutboxCompletionChannelAsync() {
    var channel = new FakeWorkChannelWriter();
    var completion = new FakeOutboxCompletionChannel();
    var failure = new FakeFailureChannel();
    var renewal = new FakeLeaseRenewalChannel();
    var strategy = new FakeSingularStrategy();
    var gate = new SchemaReadyGate();
    gate.MarkReady();

    var sp = new ServiceCollection().BuildServiceProvider();
    var worker = new OutboxPublishWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      channel, completion, failure, renewal, gate,
      Options.Create(new OutboxPublishWorkerOptions { Enabled = true }),
      NullLogger<OutboxPublishWorker>.Instance,
      instanceProvider: new Whizbang.Core.Observability.ServiceInstanceProvider(),
      publishStrategy: strategy);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);

    var work = _makeWork();
    await channel.WriteAsync(work, cts.Token);

    var publishedId = await completion.FirstId.Task.WaitAsync(TimeSpan.FromSeconds(5));
    await Assert.That(publishedId).IsEqualTo(work.MessageId);
    await Assert.That(strategy.Published).Contains(work);
    await Assert.That(failure.All).IsEmpty();
    await Assert.That(renewal.All).IsEmpty();

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);
  }

  [Test]
  public async Task BulkPublish_HappyPath_RoutesAllSuccessesToOutboxCompletionChannelAsync() {
    var channel = new FakeWorkChannelWriter();
    var completion = new FakeOutboxCompletionChannel();
    var failure = new FakeFailureChannel();
    var renewal = new FakeLeaseRenewalChannel();
    var strategy = new FakeBulkStrategy();
    var gate = new SchemaReadyGate();
    gate.MarkReady();

    var sp = new ServiceCollection().BuildServiceProvider();
    var worker = new OutboxPublishWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      channel, completion, failure, renewal, gate,
      Options.Create(new OutboxPublishWorkerOptions { Enabled = true, MaxBulkPublishBatchSize = 10 }),
      NullLogger<OutboxPublishWorker>.Instance,
      instanceProvider: new Whizbang.Core.Observability.ServiceInstanceProvider(),
      publishStrategy: strategy);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);

    var works = new[] { _makeWork(), _makeWork(), _makeWork() };
    foreach (var w in works) { await channel.WriteAsync(w, cts.Token); }

    var batch = await strategy.FirstBatch.Task.WaitAsync(TimeSpan.FromSeconds(5));
    await Assert.That(batch.Count).IsGreaterThanOrEqualTo(1);

    // Wait for at least one completion to be enqueued.
    await completion.FirstId.Task.WaitAsync(TimeSpan.FromSeconds(5));
    // Drain a moment for the rest of the batch's completions to finish enqueuing.
    var sw = System.Diagnostics.Stopwatch.StartNew();
    while (completion.AllIds.Count < works.Length && sw.Elapsed < TimeSpan.FromSeconds(2)) {
      await Task.Yield();
    }
    foreach (var w in works) {
      await Assert.That(completion.AllIds).Contains(w.MessageId);
    }
    await Assert.That(failure.All).IsEmpty();

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);
  }

  [Test]
  public async Task TransportNotReady_RebuffersAndEnqueuesRenewalAsync() {
    var channel = new FakeWorkChannelWriter();
    var completion = new FakeOutboxCompletionChannel();
    var failure = new FakeFailureChannel();
    var renewal = new FakeLeaseRenewalChannel();
    var strategy = new FakeSingularStrategy { ReadyValue = false };
    var gate = new SchemaReadyGate();
    gate.MarkReady();

    var sp = new ServiceCollection().BuildServiceProvider();
    var worker = new OutboxPublishWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      channel, completion, failure, renewal, gate,
      // Short retry delay so we don't sit in the busy-loop guard for the full 100ms default
      // on test shutdown — also lets us shut down quickly after the renewal assertion.
      Options.Create(new OutboxPublishWorkerOptions { Enabled = true, TransportNotReadyRetryDelayMilliseconds = 25 }),
      NullLogger<OutboxPublishWorker>.Instance,
      instanceProvider: new Whizbang.Core.Observability.ServiceInstanceProvider(),
      publishStrategy: strategy);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);

    var work = _makeWork();
    await channel.WriteAsync(work, cts.Token);

    var renewedId = await renewal.FirstRenewal.Task.WaitAsync(TimeSpan.FromSeconds(5));
    await Assert.That(renewedId).IsEqualTo(work.MessageId);

    // Re-buffered at least once → the message is back in the channel; the worker keeps trying
    // while transport stays not-ready, so we just assert no completion fired and no failure routed.
    await Assert.That(completion.AllIds).IsEmpty();
    await Assert.That(failure.All).IsEmpty();

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);
  }

  [Test]
  public async Task PublishFailure_RoutesToFailureChannelAsync() {
    var channel = new FakeWorkChannelWriter();
    var completion = new FakeOutboxCompletionChannel();
    var failure = new FakeFailureChannel();
    var renewal = new FakeLeaseRenewalChannel();
    var strategy = new FakeSingularStrategy { ShouldFailPublish = true };
    var gate = new SchemaReadyGate();
    gate.MarkReady();

    var sp = new ServiceCollection().BuildServiceProvider();
    var worker = new OutboxPublishWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      channel, completion, failure, renewal, gate,
      Options.Create(new OutboxPublishWorkerOptions { Enabled = true }),
      NullLogger<OutboxPublishWorker>.Instance,
      instanceProvider: new Whizbang.Core.Observability.ServiceInstanceProvider(),
      publishStrategy: strategy);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);

    var work = _makeWork();
    await channel.WriteAsync(work, cts.Token);

    var routed = await failure.FirstFailure.Task.WaitAsync(TimeSpan.FromSeconds(5));
    await Assert.That(routed.MessageId).IsEqualTo(work.MessageId);
    await Assert.That(routed.Error).IsEqualTo("simulated failure");
    await Assert.That(channel.RemovedInFlight).Contains(work.MessageId);
    await Assert.That(completion.AllIds).IsEmpty();
    await Assert.That(renewal.All).IsEmpty();

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);
  }

  [Test]
  public async Task ExecuteAsync_DisabledOptions_NoMessagesConsumedAsync() {
    var channel = new FakeWorkChannelWriter();
    var completion = new FakeOutboxCompletionChannel();
    var failure = new FakeFailureChannel();
    var renewal = new FakeLeaseRenewalChannel();
    var strategy = new FakeSingularStrategy();
    var gate = new SchemaReadyGate();
    gate.MarkReady();  // gate ready but Enabled=false should still skip

    var sp = new ServiceCollection().BuildServiceProvider();
    var worker = new OutboxPublishWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      channel, completion, failure, renewal, gate,
      Options.Create(new OutboxPublishWorkerOptions { Enabled = false }),
      NullLogger<OutboxPublishWorker>.Instance,
      instanceProvider: new Whizbang.Core.Observability.ServiceInstanceProvider(),
      publishStrategy: strategy);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);

    var work = _makeWork();
    await channel.WriteAsync(work, cts.Token);

    // Give it a beat — if killswitch leaks, the publish would happen quickly.
    await Task.WhenAny(strategy.FirstPublished.Task, Task.Delay(500, CancellationToken.None));
    await Assert.That(strategy.FirstPublished.Task.IsCompleted).IsFalse();
    await Assert.That(strategy.Published).IsEmpty();
    await Assert.That(completion.AllIds).IsEmpty();

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);
  }

  [Test]
  public async Task ExecuteAsync_BlocksOnSchemaGate_UntilMarkedReadyAsync() {
    var channel = new FakeWorkChannelWriter();
    var completion = new FakeOutboxCompletionChannel();
    var failure = new FakeFailureChannel();
    var renewal = new FakeLeaseRenewalChannel();
    var strategy = new FakeSingularStrategy();
    var gate = new SchemaReadyGate();  // not marked ready

    var sp = new ServiceCollection().BuildServiceProvider();
    var worker = new OutboxPublishWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      channel, completion, failure, renewal, gate,
      Options.Create(new OutboxPublishWorkerOptions { Enabled = true }),
      NullLogger<OutboxPublishWorker>.Instance,
      instanceProvider: new Whizbang.Core.Observability.ServiceInstanceProvider(),
      publishStrategy: strategy);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);

    var work = _makeWork();
    await channel.WriteAsync(work, cts.Token);

    // No publish while gate is closed.
    await Task.WhenAny(strategy.FirstPublished.Task, Task.Delay(300, CancellationToken.None));
    await Assert.That(strategy.FirstPublished.Task.IsCompleted).IsFalse();

    // Open gate — publish proceeds.
    gate.MarkReady();
    var published = await strategy.FirstPublished.Task.WaitAsync(TimeSpan.FromSeconds(5));
    await Assert.That(published.MessageId).IsEqualTo(work.MessageId);

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);
  }
}
