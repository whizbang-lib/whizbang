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
using Whizbang.Core.Lifecycle;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Slice 14 of plans/pump-then-process.md — InboxDispatchWorker parallel dispatch with
/// stream-affinity hash partitioning. Same-stream messages must always run serially (FIFO);
/// different-stream messages must parallelize across the N internal partitions.
/// </summary>
[NotInParallel("WhizbangBackgroundServiceTests")]
public class InboxDispatchWorkerParallelismTests {

  // ---------- shared test doubles (mirrors the gating-tests scaffolding) ----------

  private sealed class FakeInstanceProvider : IServiceInstanceProvider {
    public Guid InstanceId { get; } = (Guid)TrackedGuid.NewMedo();
    public string ServiceName => "test-svc";
    public string HostName => "test-host";
    public int ProcessId => 1;
    public ServiceInstanceInfo ToInfo() => new() {
      InstanceId = InstanceId,
      ServiceName = ServiceName,
      HostName = HostName,
      ProcessId = ProcessId
    };
  }

  private sealed class FakeInboxChannelWriter : IInboxChannelWriter {
    private readonly Channel<InboxWork> _channel = Channel.CreateUnbounded<InboxWork>();
    public ChannelReader<InboxWork> Reader => _channel.Reader;
    public ValueTask WriteAsync(InboxWork work, CancellationToken ct = default) => _channel.Writer.WriteAsync(work, ct);
    public bool TryWrite(InboxWork work) => _channel.Writer.TryWrite(work);
    public bool IsInFlight(Guid messageId) => false;
    public void RemoveInFlight(Guid messageId) { }
    public bool ShouldRenewLease(Guid messageId) => false;
    public void Complete() => _channel.Writer.Complete();
    public event Action? OnNewInboxWorkAvailable;
    public void SignalNewInboxWorkAvailable() => OnNewInboxWorkAvailable?.Invoke();
  }

  private sealed class FakeHandlerCommitChannel : IInboxHandlerCommitChannel {
    public ConcurrentBag<HandlerCommitRequest> All { get; } = [];
    public ValueTask EnqueueAsync(HandlerCommitRequest request, CancellationToken ct = default) {
      All.Add(request);
      return ValueTask.CompletedTask;
    }
  }

  private sealed class FakeFailureChannel : IFailureChannel {
    public List<MessageFailure> All { get; } = [];
    public ValueTask EnqueueAsync(WorkCategory category, MessageFailure failure, CancellationToken ct = default) {
      lock (All) { All.Add(failure); }
      return ValueTask.CompletedTask;
    }
  }

  private sealed class FakeReceptorRegistry : IReceptorRegistryQuery {
    public bool HasReceptors(LifecycleStage stage, string messageType) => true;
    public bool HasInboxHandler(string messageType) => true;
    public bool HasAnyConsumer(string messageType) => true;
  }

  /// <summary>
  /// Invoker that records START and END events separately so tests can detect "in flight"
  /// state. Each PreInboxInline invocation can be gated on a per-message TaskCompletionSource
  /// so tests drive interleaving deterministically. One-shot signal: once
  /// <see cref="ReleaseGates"/>[messageId] completes, ALL subsequent stages for that message
  /// proceed without blocking.
  /// </summary>
  private sealed class TrackingReceptorInvoker : IReceptorInvoker {
    public readonly ConcurrentBag<(Guid MessageId, LifecycleStage Stage, DateTime At, bool IsStart)> Events = [];
    public readonly ConcurrentDictionary<Guid, TaskCompletionSource> ReleaseGates = new();

    public bool HasStarted(Guid messageId) => Events.Any(e => e.MessageId == messageId && e.IsStart);
    public bool HasEnded(Guid messageId, LifecycleStage stage) =>
      Events.Any(e => e.MessageId == messageId && e.Stage == stage && !e.IsStart);
    public DateTime? StartedAt(Guid messageId, LifecycleStage stage) =>
      Events.Where(e => e.MessageId == messageId && e.Stage == stage && e.IsStart).Select(e => (DateTime?)e.At).FirstOrDefault();
    public DateTime? EndedAt(Guid messageId, LifecycleStage stage) =>
      Events.Where(e => e.MessageId == messageId && e.Stage == stage && !e.IsStart).Select(e => (DateTime?)e.At).FirstOrDefault();

    public async ValueTask InvokeAsync(
        IMessageEnvelope envelope,
        LifecycleStage stage,
        ILifecycleContext? context = null,
        CancellationToken cancellationToken = default) {
      // Only track Inline invocations — Detached fire-and-forget tasks race with test teardown.
      if (stage is not (LifecycleStage.PreInboxInline or LifecycleStage.PostInboxInline
                        or LifecycleStage.PostAllPerspectivesInline or LifecycleStage.PostLifecycleInline)) {
        return;
      }
      var messageId = (Guid)envelope.MessageId;
      Events.Add((messageId, stage, DateTime.UtcNow, IsStart: true));
      // Only the FIRST stage (PreInboxInline) waits on the gate — that's enough to drive
      // FIFO/interleaving assertions. Later stages run free so the dispatch can complete.
      if (stage == LifecycleStage.PreInboxInline && ReleaseGates.TryGetValue(messageId, out var gate)) {
        await gate.Task.WaitAsync(cancellationToken);
      }
      Events.Add((messageId, stage, DateTime.UtcNow, IsStart: false));
    }
  }

  private sealed class CountingDeserializer : ILifecycleMessageDeserializer {
    public int CallCount;
    public object DeserializeFromEnvelope(IMessageEnvelope<JsonElement> envelope, string envelopeTypeName) { Interlocked.Increment(ref CallCount); return envelope.Payload; }
    public object DeserializeFromEnvelope(IMessageEnvelope<JsonElement> envelope) { Interlocked.Increment(ref CallCount); return envelope.Payload; }
    public object DeserializeFromBytes(byte[] payload, string messageType) { Interlocked.Increment(ref CallCount); return JsonDocument.Parse(payload).RootElement; }
    public object DeserializeFromJsonElement(JsonElement jsonElement, string messageTypeName) { Interlocked.Increment(ref CallCount); return jsonElement; }
  }

  private static InboxWork _makeWork(Guid streamId, Guid messageId) {
    return new InboxWork {
      MessageId = messageId,
      Envelope = new MessageEnvelope<JsonElement> {
        MessageId = MessageId.From(messageId),
        Payload = JsonDocument.Parse("{}").RootElement,
        Hops = [],
        DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Inbox }
      },
      MessageType = "Slice14.Test.Event, Test",
      StreamId = streamId,
      PartitionNumber = 1,
      Attempts = 0,
      Status = MessageProcessingStatus.Stored,
      Flags = WorkBatchOptions.None,
    };
  }

  private sealed class WorkerHarness(
      InboxDispatchWorker worker,
      ServiceProvider sp,
      FakeInboxChannelWriter inbox,
      FakeHandlerCommitChannel handlerCommit,
      TrackingReceptorInvoker invoker,
      FakeFailureChannel failure) : IAsyncDisposable {
    public InboxDispatchWorker Worker { get; } = worker;
    public FakeInboxChannelWriter Inbox { get; } = inbox;
    public FakeHandlerCommitChannel HandlerCommit { get; } = handlerCommit;
    public TrackingReceptorInvoker Invoker { get; } = invoker;
    public FakeFailureChannel Failure { get; } = failure;
    public async ValueTask DisposeAsync() {
      try { await Worker.StopAsync(CancellationToken.None); } catch { }
      await sp.DisposeAsync();
    }
  }

  private static WorkerHarness _buildWorker(int maxConcurrent) {
    var instance = new FakeInstanceProvider();
    var inbox = new FakeInboxChannelWriter();
    var handlerCommit = new FakeHandlerCommitChannel();
    var failure = new FakeFailureChannel();
    var gate = new SchemaReadyGate();
    gate.MarkReady();
    var invoker = new TrackingReceptorInvoker();
    var deserializer = new CountingDeserializer();

    var services = new ServiceCollection();
    services.AddScoped<IReceptorInvoker>(_ => invoker);
    var sp = services.BuildServiceProvider();

    var worker = new InboxDispatchWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      instance, inbox, handlerCommit, failure, gate,
      Options.Create(new InboxDispatchWorkerOptions { MaxConcurrentDispatch = maxConcurrent }),
      Options.Create(new WorkCoordinatorOptions()),
      NullLogger<InboxDispatchWorker>.Instance,
      lifecycleMessageDeserializer: deserializer,
      receptorRegistry: new FakeReceptorRegistry());

    return new WorkerHarness(worker, sp, inbox, handlerCommit, invoker, failure);
  }

  // ---------- TESTS ----------

  [Test]
  public async Task SameStream_TwoMessages_ProcessSeriallyAsync() {
    // Lock the stream-FIFO invariant under parallel dispatch: two messages on the same stream
    // must process in arrival order, never overlap. Uses the release-gate pattern: msg1's gate
    // is held; msg2 cannot start until msg1's gate releases. If parallel dispatch ignored
    // stream affinity, msg2 would proceed on a different consumer task while msg1 was gated —
    // and the assertion below (msg1 ends BEFORE msg2 starts) would fail.
    await using var harness = _buildWorker(maxConcurrent: 8);
    var streamId = (Guid)TrackedGuid.NewMedo();
    var m1 = (Guid)TrackedGuid.NewMedo();
    var m2 = (Guid)TrackedGuid.NewMedo();
    var m1Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    harness.Invoker.ReleaseGates[m1] = m1Gate;

    using var cts = new CancellationTokenSource();
    await harness.Worker.StartAsync(cts.Token);

    await harness.Inbox.WriteAsync(_makeWork(streamId, m1), cts.Token);
    await harness.Inbox.WriteAsync(_makeWork(streamId, m2), cts.Token);

    // Wait until msg1 is recorded as STARTED (its gate is held — it's in flight).
    var deadline = DateTimeOffset.UtcNow.AddSeconds(2);
    while (!harness.Invoker.HasStarted(m1) && DateTimeOffset.UtcNow < deadline) {
      await Task.Yield();
    }
    await Assert.That(harness.Invoker.HasStarted(m1)).IsTrue().Because("Msg1 must reach the receptor invoker.");

    // While msg1 is still gated, msg2 must NOT have started yet.
    await Task.Delay(200); // generous window for msg2 to start if FIFO is broken
    await Assert.That(harness.Invoker.HasStarted(m2)).IsFalse()
      .Because("Slice 14 invariant: same-stream messages must serialize. Msg2 must not start while Msg1 is still in flight.");

    // Release msg1; msg2 now flows.
    m1Gate.SetResult();
    deadline = DateTimeOffset.UtcNow.AddSeconds(2);
    while (harness.HandlerCommit.All.Count < 2 && DateTimeOffset.UtcNow < deadline) {
      await Task.Yield();
    }
    await Assert.That(harness.HandlerCommit.All.Count).IsEqualTo(2);

    // Assert msg1's PreInboxInline ended before msg2's started.
    var m1End = harness.Invoker.EndedAt(m1, LifecycleStage.PreInboxInline);
    var m2Start = harness.Invoker.StartedAt(m2, LifecycleStage.PreInboxInline);
    await Assert.That(m1End).IsNotNull();
    await Assert.That(m2Start).IsNotNull();
    await Assert.That(m1End!.Value <= m2Start!.Value).IsTrue()
      .Because("Same-stream FIFO: msg1's PreInboxInline must complete before msg2's PreInboxInline begins.");
    await cts.CancelAsync();
  }

  [Test]
  public async Task DifferentStreams_ProcessConcurrentlyAsync() {
    // Lock the parallel-across-streams invariant: two messages on DIFFERENT streams must run
    // concurrently (overlapping start/end windows). Pre-slice-14 the dispatcher was a single
    // await-foreach so msg2 always waited for msg1, regardless of stream.
    //
    // We use two messages whose stream IDs hash to DIFFERENT internal partitions. With
    // MaxConcurrentDispatch=8, ~7/8 of random pairs hit different partitions — to make the
    // test deterministic we pick stream IDs with known partition placement: hash(0)=0,
    // hash(1)=1 (across the 8 partitions).
    await using var harness = _buildWorker(maxConcurrent: 8);
    var s1 = _streamIdInPartition(0, 8);
    var s2 = _streamIdInPartition(1, 8);
    var m1 = (Guid)TrackedGuid.NewMedo();
    var m2 = (Guid)TrackedGuid.NewMedo();
    var m1Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var m2Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    harness.Invoker.ReleaseGates[m1] = m1Gate;
    harness.Invoker.ReleaseGates[m2] = m2Gate;

    using var cts = new CancellationTokenSource();
    await harness.Worker.StartAsync(cts.Token);
    await harness.Inbox.WriteAsync(_makeWork(s1, m1), cts.Token);
    await harness.Inbox.WriteAsync(_makeWork(s2, m2), cts.Token);

    // Wait for BOTH messages to register as STARTED. If parallelism is broken, m2 never starts
    // because m1's gate is held — the assertion below catches that.
    var deadline = DateTimeOffset.UtcNow.AddSeconds(3);
    while ((!harness.Invoker.HasStarted(m1) || !harness.Invoker.HasStarted(m2))
           && DateTimeOffset.UtcNow < deadline) {
      await Task.Yield();
    }

    await Assert.That(harness.Invoker.HasStarted(m1)).IsTrue().Because("Msg1 should start.");
    await Assert.That(harness.Invoker.HasStarted(m2)).IsTrue()
      .Because("Slice 14 invariant: different-stream messages must dispatch in parallel. If a single consumer serialized them, Msg2 would never start while Msg1 is gated.");

    m1Gate.SetResult();
    m2Gate.SetResult();
    deadline = DateTimeOffset.UtcNow.AddSeconds(2);
    while (harness.HandlerCommit.All.Count < 2 && DateTimeOffset.UtcNow < deadline) {
      await Task.Yield();
    }
    await Assert.That(harness.HandlerCommit.All.Count).IsEqualTo(2);
    await cts.CancelAsync();
  }

  [Test]
  public async Task MaxConcurrentDispatch_DefaultIsGreaterThanOneAsync() {
    // Lock the default config: out-of-the-box, MaxConcurrentDispatch must be > 1 so the
    // production path benefits from parallelism without explicit opt-in. Pre-slice-14 the
    // worker was a single await-foreach (effectively MaxConcurrentDispatch=1).
    var defaultOptions = new InboxDispatchWorkerOptions();
    await Assert.That(defaultOptions.MaxConcurrentDispatch).IsGreaterThan(1)
      .Because("Slice 14: parallel inbox dispatch must be on by default — saga fan-out throughput depends on it.");
  }

  /// <summary>
  /// Generates a stream id whose hash maps to <paramref name="partition"/> under the partition
  /// scheme the worker uses. Uses brute-force search — rejection-sampling avoids depending on
  /// the worker's exact hash formula and stays correct if it changes.
  /// </summary>
  private static Guid _streamIdInPartition(int partition, int partitionCount) {
    while (true) {
      var candidate = (Guid)TrackedGuid.NewMedo();
      if ((uint)candidate.GetHashCode() % (uint)partitionCount == partition) {
        return candidate;
      }
    }
  }

  [Test]
  [Timeout(30000)]
  public async Task ADispatchCancelledByShutdown_IsNotRecordedAsAFailureAsync(
      CancellationToken cancellationToken) {
    // A dispatch that throws is routed to the failure channel and dropped from in-flight, so the
    // message can be retried or dead-lettered on its own terms. A dispatch cancelled by shutdown
    // is not a message that failed — it never finished being tried. Recording it burns an attempt
    // against the poison bound on a message no receptor rejected, and enough restarts would
    // quarantine perfectly good traffic.
    //
    // The gate is held and never released, so the invoker is parked inside PreInboxInline when
    // the worker's token is cancelled — which is exactly the window the partition consumer's
    // filtered catch exists for.
    await using var harness = _buildWorker(maxConcurrent: 4);
    var streamId = (Guid)TrackedGuid.NewMedo();
    var messageId = (Guid)TrackedGuid.NewMedo();
    harness.Invoker.ReleaseGates[messageId] =
      new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

    using var cts = new CancellationTokenSource();
    await harness.Worker.StartAsync(cts.Token);
    await harness.Inbox.WriteAsync(_makeWork(streamId, messageId), cts.Token);

    // Wait until the dispatch is genuinely parked in the gate before cancelling.
    var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
    while (!harness.Invoker.HasStarted(messageId) && DateTimeOffset.UtcNow < deadline) {
      await Task.Yield();
    }
    await Assert.That(harness.Invoker.HasStarted(messageId)).IsTrue()
      .Because("the dispatch has to be inside the gate for the cancellation to land where the "
             + "filtered catch lives");
    await cts.CancelAsync();
    try { await harness.Worker.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }

    List<MessageFailure> recorded;
    lock (harness.Failure.All) { recorded = [.. harness.Failure.All]; }
    await Assert.That(recorded).IsEmpty()
      .Because("an interrupted dispatch is not a rejected message — recording it spends an "
             + "attempt against the poison bound that no receptor ever asked for");
  }
}
