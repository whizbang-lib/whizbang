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
/// Tests for <see cref="InboxDrainWorker"/> — the per-stream-id inbox drainer that adapts
/// stream_ids back into <see cref="InboxWork"/> records and writes to the legacy
/// <see cref="IInboxChannelWriter"/>. <see cref="InboxDispatchWorker"/> reads from there and
/// does the actual handler dispatch + lifecycle hooks (unchanged).
/// </summary>
[NotInParallel("WhizbangBackgroundServiceTests")]
public class InboxDrainWorkerTests {

  // --- fakes ---

  private sealed class FakeInboxDrainChannel : IInboxDrainChannel {
    private readonly Channel<Guid> _channel = Channel.CreateUnbounded<Guid>();
    public ChannelReader<Guid> Reader => _channel.Reader;
    public ValueTask WriteAsync(Guid streamId, CancellationToken ct = default) => _channel.Writer.WriteAsync(streamId, ct);
    public bool TryWrite(Guid streamId) => _channel.Writer.TryWrite(streamId);
    public void Complete() => _channel.Writer.Complete();
  }

  private sealed class CapturingInboxChannel : IInboxChannelWriter {
    private readonly Channel<InboxWork> _channel = Channel.CreateUnbounded<InboxWork>();
    public ConcurrentBag<InboxWork> Written { get; } = [];
    public TaskCompletionSource<int> SecondWritten { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource<int> ReachedCount { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public int TargetCount { get; set; } = 2;
    public ChannelReader<InboxWork> Reader => _channel.Reader;
    public ValueTask WriteAsync(InboxWork work, CancellationToken ct = default) {
      Written.Add(work);
      if (Written.Count >= 2) {
        SecondWritten.TrySetResult(Written.Count);
      }
      if (Written.Count >= TargetCount) {
        ReachedCount.TrySetResult(Written.Count);
      }
      return _channel.Writer.WriteAsync(work, ct);
    }
    public bool TryWrite(InboxWork work) {
      Written.Add(work);
      if (Written.Count >= 2) {
        SecondWritten.TrySetResult(Written.Count);
      }
      if (Written.Count >= TargetCount) {
        ReachedCount.TrySetResult(Written.Count);
      }
      return _channel.Writer.TryWrite(work);
    }
    public bool IsInFlight(Guid messageId) => false;
    public void RemoveInFlight(Guid messageId) { }
    public bool ShouldRenewLease(Guid messageId) => false;
    public void Complete() => _channel.Writer.Complete();
    public event Action? OnNewInboxWorkAvailable;
    public void SignalNewInboxWorkAvailable() => OnNewInboxWorkAvailable?.Invoke();
  }

  private sealed class FakeServiceInstanceProvider : IServiceInstanceProvider {
    public Guid InstanceId { get; } = Guid.NewGuid();
    public string ServiceName => "test-svc";
    public string HostName => "test-host";
    public int ProcessId => 1;
    public ServiceInstanceInfo ToInfo() => new() {
      InstanceId = InstanceId,
      ServiceName = ServiceName,
      HostName = HostName,
      ProcessId = ProcessId,
    };
  }

  private sealed class FakeWorkCoordinator : IWorkCoordinator {
    public Dictionary<Guid, List<InboxBatchRow>> RowsByStream { get; } = [];
    public Task<IReadOnlyList<InboxBatchRow>> FetchInboxBatchAsync(
      IReadOnlyList<Guid> streamIds, Guid instanceId, int maxPerStream = 100, CancellationToken cancellationToken = default) {
      var result = new List<InboxBatchRow>();
      foreach (var sid in streamIds) {
        if (RowsByStream.TryGetValue(sid, out var rows)) {
          result.AddRange(rows.Take(maxPerStream));
        }
      }
      return Task.FromResult<IReadOnlyList<InboxBatchRow>>(result);
    }

    public Task<WorkBatch> ProcessWorkBatchAsync(ProcessWorkBatchRequest request, CancellationToken ct = default) =>
      Task.FromResult(new WorkBatch { OutboxWork = [], InboxWork = [], PerspectiveWork = [] });
    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion c, CancellationToken ct = default) => Task.CompletedTask;
    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure f, CancellationToken ct = default) => Task.CompletedTask;
    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount = 2, CancellationToken ct = default) => Task.CompletedTask;
    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken ct = default) => Task.FromResult(new WorkCoordinatorStatistics());
    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken ct = default) => Task.CompletedTask;
    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string name, CancellationToken ct = default) =>
      Task.FromResult<PerspectiveCursorInfo?>(null);
  }

  // --- helpers ---

  private static readonly JsonSerializerOptions _jsonOpts = Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions();

  private static InboxBatchRow _row(Guid messageId, Guid streamId) {
    var envelope = new MessageEnvelope<JsonElement> {
      MessageId = MessageId.From(messageId),
      Payload = JsonDocument.Parse("{}").RootElement,
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local },
      Hops = [],
    };
    var typeInfo = _jsonOpts.GetTypeInfo(typeof(MessageEnvelope<JsonElement>))
      ?? throw new InvalidOperationException("Test setup: no JsonTypeInfo for MessageEnvelope<JsonElement>");
    var envelopeJson = JsonSerializer.Serialize(envelope, typeInfo);
    return new InboxBatchRow {
      MessageId = messageId,
      StreamId = streamId,
      HandlerName = "TestHandler",
      MessageType = "TestMessage",
      EventData = envelopeJson,
      Metadata = "{}",
      Scope = null,
      Status = 1,
      Attempts = 0,
      PartitionNumber = 0,
      IsEvent = false,
    };
  }

  // --- tests ---

  [Test]
  public async Task InboxDrainWorker_OnStreamId_FetchesBatch_FeedsInboxChannelInOrderAsync() {
    var streamId = (Guid)TrackedGuid.NewMedo();
    var msgA = (Guid)TrackedGuid.NewMedo();
    var msgB = (Guid)TrackedGuid.NewMedo();

    var coord = new FakeWorkCoordinator();
    coord.RowsByStream[streamId] = [_row(msgA, streamId), _row(msgB, streamId)];

    var drain = new FakeInboxDrainChannel();
    var inbox = new CapturingInboxChannel();
    var instance = new FakeServiceInstanceProvider();
    var gate = new SchemaReadyGate();
    gate.MarkReady();

    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coord);
    var sp = services.BuildServiceProvider();

    var worker = new InboxDrainWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      instance, drain, inbox, gate,
      Options.Create(new InboxDrainWorkerOptions { Enabled = true, MaxPerStream = 100 }),
      _jsonOpts,
      NullLogger<InboxDrainWorker>.Instance);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await drain.WriteAsync(streamId);

    _ = await Task.WhenAny(inbox.SecondWritten.Task, Task.Delay(TimeSpan.FromSeconds(15)));
    cts.Cancel();
    try { await worker.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }

    await Assert.That(inbox.Written.Count).IsEqualTo(2);
    var writtenMessageIds = inbox.Written.Select(w => w.MessageId).ToHashSet();
    await Assert.That(writtenMessageIds).Contains(msgA);
    await Assert.That(writtenMessageIds).Contains(msgB);
  }

  [Test]
  public async Task InboxDrainWorker_MoreRowsThanMaxPerStream_LoopsUntilEmptyAsync() {
    // Phase H step 6 slice 3: same loop-until-empty pattern as OutboxDrainWorker.
    // The fake "consumes" returned rows on each fetch — mimics post-completion DELETE.
    var streamId = (Guid)TrackedGuid.NewMedo();
    var msgs = Enumerable.Range(0, 250).Select(_ => (Guid)TrackedGuid.NewMedo()).ToArray();

    var coord = new ConsumingFakeWorkCoordinator();
    coord.RowsByStream[streamId] = [.. msgs.Select(m => _row(m, streamId))];

    var drain = new FakeInboxDrainChannel();
    var inbox = new CapturingInboxChannel { TargetCount = 250 };
    var instance = new FakeServiceInstanceProvider();
    var gate = new SchemaReadyGate();
    gate.MarkReady();

    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coord);
    var sp = services.BuildServiceProvider();

    var worker = new InboxDrainWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      instance, drain, inbox, gate,
      Options.Create(new InboxDrainWorkerOptions { Enabled = true, MaxPerStream = 100 }),
      _jsonOpts,
      NullLogger<InboxDrainWorker>.Instance);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await drain.WriteAsync(streamId);

    _ = await Task.WhenAny(inbox.ReachedCount.Task, Task.Delay(TimeSpan.FromSeconds(30)));
    cts.Cancel();
    try { await worker.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }

    await Assert.That(inbox.Written.Count).IsEqualTo(250)
      .Because("inbox drainer must keep fetching for the same stream until pending=0");
    await Assert.That(coord.FetchCalls).IsGreaterThanOrEqualTo(3)
      .Because("250 rows / MaxPerStream=100 = 3 fetches minimum");
  }

  /// <summary>Fake coordinator that REMOVES returned rows from the dictionary on each fetch — mimics post-completion DELETE.</summary>
  private sealed class ConsumingFakeWorkCoordinator : IWorkCoordinator {
    public Dictionary<Guid, List<InboxBatchRow>> RowsByStream { get; } = [];
    public int FetchCalls;
    public Task<IReadOnlyList<InboxBatchRow>> FetchInboxBatchAsync(
      IReadOnlyList<Guid> streamIds, Guid instanceId, int maxPerStream = 100, CancellationToken cancellationToken = default) {
      Interlocked.Increment(ref FetchCalls);
      var result = new List<InboxBatchRow>();
      foreach (var sid in streamIds) {
        if (RowsByStream.TryGetValue(sid, out var rows)) {
          var taken = rows.Take(maxPerStream).ToList();
          result.AddRange(taken);
          rows.RemoveRange(0, taken.Count);
        }
      }
      return Task.FromResult<IReadOnlyList<InboxBatchRow>>(result);
    }

    public Task<WorkBatch> ProcessWorkBatchAsync(ProcessWorkBatchRequest request, CancellationToken ct = default) =>
      Task.FromResult(new WorkBatch { OutboxWork = [], InboxWork = [], PerspectiveWork = [] });
    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion c, CancellationToken ct = default) => Task.CompletedTask;
    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure f, CancellationToken ct = default) => Task.CompletedTask;
    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount = 2, CancellationToken ct = default) => Task.CompletedTask;
    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken ct = default) => Task.FromResult(new WorkCoordinatorStatistics());
    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken ct = default) => Task.CompletedTask;
    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string name, CancellationToken ct = default) =>
      Task.FromResult<PerspectiveCursorInfo?>(null);
  }

  [Test]
  public async Task InboxDrainWorkerOptions_DefaultEnabled_IsTrueAsync() {
    // After Phase H step 5d-flip, InboxDrainWorker is the only source of InboxWork records
    // for InboxDispatchWorker. If a future refactor flips the default off, inbox dispatch
    // breaks silently. Lock the default.
    var defaults = new InboxDrainWorkerOptions();
    await Assert.That(defaults.Enabled).IsTrue();
  }

  /// <summary>
  /// Row helper that produces a malformed envelope (invalid JSON) so _toInboxWork throws.
  /// Locks the deserialize-failure path: the malformed row is logged and SKIPPED, the
  /// drain continues with subsequent rows.
  /// </summary>
  private static InboxBatchRow _malformedRow(Guid messageId, Guid streamId) => new() {
    MessageId = messageId,
    StreamId = streamId,
    MessageType = "TestMessage",
    EventData = "{not valid json",   // <-- deliberate
    Metadata = "{}",
    Scope = null,
    Status = 1,
    Attempts = 0,
    PartitionNumber = 0,
    HandlerName = "TestHandler",
  };

  [Test]
  public async Task InboxDrainWorker_DeserializeFails_SkipsBadRow_ContinuesDrainAsync() {
    // Locks the _toInboxWork catch path: a row with malformed EventData throws inside
    // _toInboxWork — the drainer logs and CONTINUES (loop continue), enqueues good rows
    // around it. Without this guard, a single corrupted row would block all subsequent
    // inbox processing for the stream.
    var streamId = (Guid)TrackedGuid.NewMedo();
    var badMsg = (Guid)TrackedGuid.NewMedo();
    var goodMsg = (Guid)TrackedGuid.NewMedo();

    var coord = new FakeWorkCoordinator();
    coord.RowsByStream[streamId] = [_malformedRow(badMsg, streamId), _row(goodMsg, streamId)];

    var drainChannel = new FakeInboxDrainChannel();
    var inboxChannelWriter = new CapturingInboxChannel { TargetCount = 1 };
    var instance = new FakeServiceInstanceProvider();
    var gate = new SchemaReadyGate();
    gate.MarkReady();

    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coord);
    var sp = services.BuildServiceProvider();

    var worker = new InboxDrainWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      instance, drainChannel, inboxChannelWriter, gate,
      Options.Create(new InboxDrainWorkerOptions { Enabled = true, MaxPerStream = 100 }),
      _jsonOpts,
      NullLogger<InboxDrainWorker>.Instance);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await drainChannel.WriteAsync(streamId);

    _ = await Task.WhenAny(inboxChannelWriter.ReachedCount.Task, Task.Delay(TimeSpan.FromSeconds(15)));
    cts.Cancel();
    try { await worker.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }

    await Assert.That(inboxChannelWriter.Written.Count).IsEqualTo(1)
      .Because("Only the good row should be enqueued; the malformed one is logged + skipped.");
    await Assert.That(inboxChannelWriter.Written.First().MessageId).IsEqualTo(goodMsg);
  }

  /// <summary>
  /// Captures the per-call <c>streamIds</c> arrays so we can assert how many fetch calls
  /// were issued and what shape they had. Same semantics as <see cref="FakeWorkCoordinator"/>
  /// otherwise.
  /// </summary>
  private sealed class CountingFakeWorkCoordinator : IWorkCoordinator {
    public Dictionary<Guid, List<InboxBatchRow>> RowsByStream { get; } = [];
    public ConcurrentBag<Guid[]> FetchedStreamIdArrays { get; } = [];
    public Task<IReadOnlyList<InboxBatchRow>> FetchInboxBatchAsync(
      IReadOnlyList<Guid> streamIds, Guid instanceId, int maxPerStream = 100, CancellationToken cancellationToken = default) {
      FetchedStreamIdArrays.Add(streamIds.ToArray());
      var result = new List<InboxBatchRow>();
      foreach (var sid in streamIds) {
        if (RowsByStream.TryGetValue(sid, out var rows)) {
          var taken = rows.Take(maxPerStream).ToList();
          result.AddRange(taken);
          rows.RemoveRange(0, taken.Count);
        }
      }
      return Task.FromResult<IReadOnlyList<InboxBatchRow>>(result);
    }

    public Task<WorkBatch> ProcessWorkBatchAsync(ProcessWorkBatchRequest request, CancellationToken ct = default) =>
      Task.FromResult(new WorkBatch { OutboxWork = [], InboxWork = [], PerspectiveWork = [] });
    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion c, CancellationToken ct = default) => Task.CompletedTask;
    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure f, CancellationToken ct = default) => Task.CompletedTask;
    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount = 2, CancellationToken ct = default) => Task.CompletedTask;
    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken ct = default) => Task.FromResult(new WorkCoordinatorStatistics());
    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken ct = default) => Task.CompletedTask;
    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string name, CancellationToken ct = default) =>
      Task.FromResult<PerspectiveCursorInfo?>(null);
  }

  /// <summary>
  /// v0.685 lock-in — when the InboxDrainWorker receives a sliding-window batch
  /// containing multiple distinct stream_ids, it MUST drain them via a SINGLE
  /// multi-stream <c>FetchInboxBatchAsync</c> call rather than looping per-stream.
  /// Slot-3 2026-06-11 measurement showed ~1.9 fetch calls per event because every
  /// stream had only 1-2 rows and the per-call CTE setup cost dominated. Batching
  /// the fetch amortizes that cost across the whole window. Without this lock-in,
  /// a future refactor of <c>InboxDrainWorker.ExecuteAsync</c> could silently
  /// regress back to per-stream loops and the slot-3 win would be lost.
  /// </summary>
  /// <docs>fundamentals/work-coordinator/inbox-drain</docs>
  [Test]
  public async Task InboxDrainWorker_MultipleStreamsInBatch_FetchesOnceWithAllStreamIdsAsync() {
    var streamA = (Guid)TrackedGuid.NewMedo();
    var streamB = (Guid)TrackedGuid.NewMedo();
    var streamC = (Guid)TrackedGuid.NewMedo();

    var coord = new CountingFakeWorkCoordinator();
    coord.RowsByStream[streamA] = [_row((Guid)TrackedGuid.NewMedo(), streamA), _row((Guid)TrackedGuid.NewMedo(), streamA)];
    coord.RowsByStream[streamB] = [_row((Guid)TrackedGuid.NewMedo(), streamB), _row((Guid)TrackedGuid.NewMedo(), streamB)];
    coord.RowsByStream[streamC] = [_row((Guid)TrackedGuid.NewMedo(), streamC), _row((Guid)TrackedGuid.NewMedo(), streamC)];

    var drainChannel = new FakeInboxDrainChannel();
    var inbox = new CapturingInboxChannel { TargetCount = 6 };
    var instance = new FakeServiceInstanceProvider();
    var gate = new SchemaReadyGate();
    gate.MarkReady();

    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coord);
    var sp = services.BuildServiceProvider();

    // SlidingWindowBatcher accumulates writes within its window. Pre-queue all three
    // stream_ids before StartAsync so the batcher sees them in one window batch.
    _ = drainChannel.TryWrite(streamA);
    _ = drainChannel.TryWrite(streamB);
    _ = drainChannel.TryWrite(streamC);

    var worker = new InboxDrainWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      instance, drainChannel, inbox, gate,
      Options.Create(new InboxDrainWorkerOptions { Enabled = true, MaxPerStream = 100 }),
      _jsonOpts,
      NullLogger<InboxDrainWorker>.Instance);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);

    _ = await Task.WhenAny(inbox.ReachedCount.Task, Task.Delay(TimeSpan.FromSeconds(15)));
    cts.Cancel();
    try { await worker.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }

    await Assert.That(inbox.Written.Count).IsEqualTo(6)
      .Because("All 6 inbox rows across 3 streams must be enqueued for the inbox dispatch path; the batching change is behaviour-preserving.");

    await Assert.That(coord.FetchedStreamIdArrays.Count).IsEqualTo(1)
      .Because("v0.685 — the deduped sliding-window batch MUST be drained with a single multi-stream FetchInboxBatchAsync call. The pre-v0.685 per-stream loop produced one fetch per stream (slot-3 2026-06-11: 31k calls for 16k events ≈ 1.9 per event, 59% of slot-3 DB time).");

    var firstArray = coord.FetchedStreamIdArrays.First();
    await Assert.That(firstArray).Contains(streamA);
    await Assert.That(firstArray).Contains(streamB);
    await Assert.That(firstArray).Contains(streamC);
  }
}
