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

/// <summary>
/// Coverage-gap tests for <see cref="OutboxPublishWorker"/> targeting lines left uncovered by the
/// existing test files in this directory: the <c>IsIdle</c> proxy property, the schema-ready-gate
/// cancellation catch, both loops' graceful-channel-completion exit paths, the singular loop's
/// publish-exception failure-channel routing, and the post-outbox lifecycle's second
/// <c>IReceptorInvoker</c> resolution coming back unavailable.
/// </summary>
[NotInParallel("WhizbangBackgroundServiceTests")]
public class OutboxPublishWorkerCoverageTests {

  // ============================================================
  // Test fakes (mirrors the shape used by OutboxPublishWorkerTests.cs)
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
    public ConcurrentBag<(WorkCategory Cat, MessageFailure Failure)> All { get; } = [];
    public ValueTask EnqueueAsync(WorkCategory category, MessageFailure failure, CancellationToken ct = default) {
      All.Add((category, failure));
      FirstFailure.TrySetResult(failure);
      return ValueTask.CompletedTask;
    }
  }

  private sealed class FakeLeaseRenewalChannel : ILeaseRenewalChannel {
    public ConcurrentBag<(WorkCategory Cat, Guid Id)> All { get; } = [];
    public ValueTask EnqueueAsync(WorkCategory category, Guid id, CancellationToken ct = default) {
      All.Add((category, id));
      return ValueTask.CompletedTask;
    }
  }

  private sealed class FakeSingularStrategy : IMessagePublishStrategy {
    public bool ReadyValue { get; set; } = true;
    public ConcurrentBag<OutboxWork> Published { get; } = [];

    public Task<bool> IsReadyAsync(CancellationToken ct = default) => Task.FromResult(ReadyValue);
    public Task<MessagePublishResult> PublishAsync(OutboxWork work, CancellationToken ct) {
      Published.Add(work);
      return Task.FromResult(new MessagePublishResult {
        MessageId = work.MessageId,
        Success = true,
        CompletedStatus = MessageProcessingStatus.Published,
        Error = null,
        Reason = MessageFailureReason.Unknown
      });
    }
  }

  private sealed class FakeBulkStrategy : IMessagePublishStrategy {
    public bool SupportsBulkPublish => true;
    public Task<bool> IsReadyAsync(CancellationToken ct = default) => Task.FromResult(true);
    public Task<MessagePublishResult> PublishAsync(OutboxWork work, CancellationToken ct)
      => throw new InvalidOperationException("FakeBulkStrategy: only PublishBatchAsync should be called");
    public Task<IReadOnlyList<MessagePublishResult>> PublishBatchAsync(IReadOnlyList<OutboxWork> works, CancellationToken ct) {
      var results = works.Select(w => new MessagePublishResult {
        MessageId = w.MessageId,
        Success = true,
        CompletedStatus = MessageProcessingStatus.Published,
        Error = null
      }).ToList();
      return Task.FromResult<IReadOnlyList<MessagePublishResult>>(results);
    }
  }

  /// <summary>Always throws from PublishAsync — exercises the singular loop's exception catch,
  /// as opposed to a strategy that merely returns a failed <see cref="MessagePublishResult"/>.</summary>
  private sealed class FakeThrowingStrategy : IMessagePublishStrategy {
    public Task<bool> IsReadyAsync(CancellationToken ct = default) => Task.FromResult(true);
    public Task<MessagePublishResult> PublishAsync(OutboxWork work, CancellationToken ct) =>
      throw new InvalidOperationException("publish exploded");
  }

  /// <summary>Minimal deserializer that hands back a non-null placeholder — only used to satisfy the
  /// pre-outbox lifecycle's null-check gate so it proceeds to build a typed envelope.</summary>
  private sealed class FakeLifecycleMessageDeserializer : ILifecycleMessageDeserializer {
    public object DeserializeFromEnvelope(IMessageEnvelope<JsonElement> envelope, string envelopeTypeName) => new object();
    public object DeserializeFromEnvelope(IMessageEnvelope<JsonElement> envelope) => new object();
    public object DeserializeFromBytes(byte[] jsonBytes, string messageTypeName) => new object();
    public object DeserializeFromJsonElement(JsonElement jsonElement, string messageTypeName) => new object();
  }

  /// <summary>Records every stage it is invoked with, so a test can assert exactly which lifecycle
  /// stages fired rather than just "nothing threw".</summary>
  private sealed class FakeReceptorInvoker : IReceptorInvoker {
    public ConcurrentQueue<LifecycleStage> InvokedStages { get; } = new();
    public ValueTask InvokeAsync(
        IMessageEnvelope envelope,
        LifecycleStage stage,
        ILifecycleContext? context = null,
        CancellationToken cancellationToken = default) {
      InvokedStages.Enqueue(stage);
      return ValueTask.CompletedTask;
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
  // OutboxPublishWorker.cs:114 — IsIdle proxy property
  // ============================================================

  [Test]
  public async Task IsIdle_ReflectsWorkChannelBacklogAsync() {
    // If IsIdle stopped reading the live channel count, the idle signal fixtures rely on to know
    // "no pending work" would report stale data — downstream coordination that waits on idle would
    // either race ahead of real work or stall waiting for an idle signal that never distinguishes
    // an empty channel from a backlogged one.
    var channel = new FakeWorkChannelWriter();
    var completion = new FakeOutboxCompletionChannel();
    var failure = new FakeFailureChannel();
    var renewal = new FakeLeaseRenewalChannel();
    var gate = new SchemaReadyGate();

    var sp = new ServiceCollection().BuildServiceProvider();
    var worker = new OutboxPublishWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      channel, completion, failure, renewal, gate,
      Options.Create(new OutboxPublishWorkerOptions()),
      NullLogger<OutboxPublishWorker>.Instance,
      instanceProvider: new ServiceInstanceProvider());

    await Assert.That(worker.IsIdle).IsTrue()
      .Because("a freshly constructed worker with an empty channel has no pending work");

    await channel.WriteAsync(_makeWork(), CancellationToken.None);

    await Assert.That(worker.IsIdle).IsFalse()
      .Because("a queued, undrained item must flip IsIdle to false");
  }

  // ============================================================
  // OutboxPublishWorker.cs:137 — schema-ready-gate cancellation catch
  // ============================================================

  [Test]
  public async Task ExecuteAsync_StoppedWhileWaitingOnSchemaGate_ReturnsWithoutEnteringPublishLoopAsync() {
    // If this catch stopped swallowing the cancellation (or the early return were removed), a
    // shutdown that arrives before the schema is ready would either hang StopAsync waiting on a
    // task that never observes cancellation, or surface as a faulted ExecuteTask instead of a
    // clean stop — and the publish loop must never have been entered at all.
    var channel = new FakeWorkChannelWriter();
    var completion = new FakeOutboxCompletionChannel();
    var failure = new FakeFailureChannel();
    var renewal = new FakeLeaseRenewalChannel();
    var strategy = new FakeSingularStrategy();
    var gate = new SchemaReadyGate(); // never marked ready

    var sp = new ServiceCollection().BuildServiceProvider();
    var worker = new OutboxPublishWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      channel, completion, failure, renewal, gate,
      Options.Create(new OutboxPublishWorkerOptions { Enabled = true }),
      NullLogger<OutboxPublishWorker>.Instance,
      instanceProvider: new ServiceInstanceProvider(),
      publishStrategy: strategy);

    await worker.StartAsync(CancellationToken.None);

    // ExecuteAsync runs synchronously up to the first incomplete await (the gate wait, since the
    // gate is never marked ready), so by the time StartAsync returns the worker is parked there.
    // Stopping now races the exact "shutdown before schema ready" window this catch exists for.
    // Bounded explicitly: if the premise is wrong (cancellation never reaches the gate wait),
    // this must fail with a timeout, not hang the suite.
    await worker.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

    await Assert.That(worker.ExecuteTask!.IsCompleted).IsTrue()
      .Because("the gate-wait catch must let ExecuteAsync return promptly on stop, not hang");
    await Assert.That(worker.ExecuteTask.IsFaulted).IsFalse()
      .Because("the cancellation must be caught internally, not surfaced as a fault");
    await Assert.That(strategy.Published).IsEmpty()
      .Because("shutdown before the schema gate opened must never let the publish loop start");
  }

  // ============================================================
  // OutboxPublishWorker.cs:145,242 — singular loop graceful completion
  // OutboxPublishWorker.cs:145,343 — bulk loop graceful completion
  // ============================================================

  [Test]
  public async Task ExecuteAsync_SingularPath_ChannelCompletesWithNoWork_StopsCleanlyAsync() {
    // If the singular loop's normal (non-exception) exit path regressed, a host whose work channel
    // completes (e.g. on graceful shutdown before any row ever arrived) would either hang forever
    // inside the await-foreach or blow past LogStopped into an unhandled state instead of returning.
    var channel = new FakeWorkChannelWriter();
    channel.Complete();
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
      instanceProvider: new ServiceInstanceProvider(),
      publishStrategy: strategy);

    await worker.StartAsync(CancellationToken.None);
    await worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

    await Assert.That(worker.ExecuteTask.IsCompletedSuccessfully).IsTrue()
      .Because("an already-completed, empty channel must let the singular loop exit normally, not fault or hang");
    await Assert.That(strategy.Published).IsEmpty();

    await worker.StopAsync(CancellationToken.None);
  }

  [Test]
  public async Task ExecuteAsync_BulkPath_ChannelCompletesWithNoWork_StopsCleanlyAsync() {
    // Same invariant as the singular case, for the bulk loop: a transport that supports bulk
    // publish must also be able to shut down cleanly when the work channel simply completes with
    // nothing ever queued, instead of the bulk loop's own await-foreach hanging or faulting.
    var channel = new FakeWorkChannelWriter();
    channel.Complete();
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
      instanceProvider: new ServiceInstanceProvider(),
      publishStrategy: strategy);

    await worker.StartAsync(CancellationToken.None);
    await worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

    await Assert.That(worker.ExecuteTask.IsCompletedSuccessfully).IsTrue()
      .Because("an already-completed, empty channel must let the bulk loop exit normally, not fault or hang");
    await Assert.That(completion.AllIds).IsEmpty();

    await worker.StopAsync(CancellationToken.None);
  }

  // ============================================================
  // OutboxPublishWorker.cs:225-231 — singular loop publish-exception routes to failure channel
  // ============================================================

  [Test]
  public async Task SingularPublish_PublishThrows_RoutesToFailureChannelAndProcessesNextItemAsync() {
    // If a publish-time exception stopped being routed to the failure channel (or wedged the
    // loop), a transient publish crash would silently drop the outbox row with no record and no
    // retry path, and every row queued behind it would never get a chance to publish either.
    var channel = new FakeWorkChannelWriter();
    var completion = new FakeOutboxCompletionChannel();
    var failure = new FakeFailureChannel();
    var renewal = new FakeLeaseRenewalChannel();
    var strategy = new FakeThrowingStrategy();
    var gate = new SchemaReadyGate();
    gate.MarkReady();

    var sp = new ServiceCollection().BuildServiceProvider();
    var worker = new OutboxPublishWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      channel, completion, failure, renewal, gate,
      Options.Create(new OutboxPublishWorkerOptions { Enabled = true }),
      NullLogger<OutboxPublishWorker>.Instance,
      instanceProvider: new ServiceInstanceProvider(),
      publishStrategy: strategy);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);

    var firstWork = _makeWork();
    await channel.WriteAsync(firstWork, cts.Token);

    var routedFirst = await failure.FirstFailure.Task.WaitAsync(TimeSpan.FromSeconds(5));
    await Assert.That(routedFirst.MessageId).IsEqualTo(firstWork.MessageId);
    await Assert.That(routedFirst.Error).IsEqualTo("publish exploded")
      .Because("the caught exception's Message must reach the failure channel, not a generic string");
    await Assert.That(channel.RemovedInFlight).Contains(firstWork.MessageId);
    await Assert.That(completion.AllIds).IsEmpty();

    // A second item behind the failing one must still get a turn — one item's exception must not
    // wedge the loop or leave the rest of the queue stuck forever.
    var secondWork = _makeWork();
    await channel.WriteAsync(secondWork, cts.Token);

    var sw = System.Diagnostics.Stopwatch.StartNew();
    while (failure.All.Count < 2 && sw.Elapsed < TimeSpan.FromSeconds(5)) {
      await Task.Yield();
    }
    await Assert.That(failure.All.Select(entry => entry.Failure.MessageId)).Contains(secondWork.MessageId)
      .Because("the loop must keep draining after a publish-time exception on the prior item");

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);
  }

  // ============================================================
  // OutboxPublishWorker.cs:534 — post-outbox lifecycle, second IReceptorInvoker resolve is null
  // ============================================================

  [Test]
  public async Task SingularPublish_PostOutboxLifecycle_ReceptorInvokerUnavailableOnSecondResolve_SkipsPostStagesAsync() {
    // If this early return regressed, a scope where the receptor invoker becomes unavailable
    // between the pre- and post-outbox resolution would NullReferenceException the publish loop
    // instead of degrading to "no post-outbox lifecycle stages fired" — crashing every row behind
    // it in the channel rather than just skipping this row's post-outbox notifications.
    var receptorInvoker = new FakeReceptorInvoker();
    var resolveCount = 0;
    var receptorServices = new ServiceCollection();
    receptorServices.AddTransient<IReceptorInvoker>(_ => {
      resolveCount++;
      return resolveCount == 1 ? receptorInvoker : null!;
    });
    var receptorProvider = receptorServices.BuildServiceProvider();

    var channel = new FakeWorkChannelWriter();
    var completion = new FakeOutboxCompletionChannel();
    var failure = new FakeFailureChannel();
    var renewal = new FakeLeaseRenewalChannel();
    var strategy = new FakeSingularStrategy();
    var gate = new SchemaReadyGate();
    gate.MarkReady();

    var worker = new OutboxPublishWorker(
      receptorProvider.GetRequiredService<IServiceScopeFactory>(),
      channel, completion, failure, renewal, gate,
      Options.Create(new OutboxPublishWorkerOptions { Enabled = true }),
      NullLogger<OutboxPublishWorker>.Instance,
      instanceProvider: new ServiceInstanceProvider(),
      publishStrategy: strategy,
      lifecycleMessageDeserializer: new FakeLifecycleMessageDeserializer());

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);

    var work = _makeWork();
    await channel.WriteAsync(work, cts.Token);

    // Publish still completes normally — the early return only skips post-outbox lifecycle
    // notifications, it must not affect result routing.
    await completion.FirstId.Task.WaitAsync(TimeSpan.FromSeconds(5));

    await Assert.That(resolveCount).IsEqualTo(2)
      .Because("pre-outbox resolves the invoker once and post-outbox resolves again from the same scope");

    var invokedStages = receptorInvoker.InvokedStages.ToList();
    await Assert.That(invokedStages.Count).IsEqualTo(4)
      .Because("only the pre-outbox direct-invoke stages (2 stages x itself + ImmediateDetached) should have fired");
    await Assert.That(invokedStages).Contains(LifecycleStage.PreOutboxDetached);
    await Assert.That(invokedStages).Contains(LifecycleStage.PreOutboxInline);
    await Assert.That(invokedStages).DoesNotContain(LifecycleStage.PostOutboxDetached)
      .Because("post-outbox must return before invoking any receptor once the second resolve comes back null");
    await Assert.That(invokedStages).DoesNotContain(LifecycleStage.PostOutboxInline)
      .Because("post-outbox must return before invoking any receptor once the second resolve comes back null");

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);
  }
}
