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
/// Tests for <see cref="OutboxDrainWorker"/> — the per-stream-id outbox drainer.
/// Verifies the per-stream drain flow (read stream_id → fetch batch → publish each →
/// enqueue completion) and that messages within a stream publish in fetch order (FIFO).
/// </summary>
[NotInParallel("WhizbangBackgroundServiceTests")]
public class OutboxDrainWorkerTests {

  // --- fakes ---

  private sealed class FakeOutboxDrainChannel : IOutboxDrainChannel {
    private readonly Channel<Guid> _channel = Channel.CreateUnbounded<Guid>();
    public ChannelReader<Guid> Reader => _channel.Reader;
    public ValueTask WriteAsync(Guid streamId, CancellationToken ct = default) => _channel.Writer.WriteAsync(streamId, ct);
    public bool TryWrite(Guid streamId) => _channel.Writer.TryWrite(streamId);
    public void Complete() => _channel.Writer.Complete();
  }

  private sealed class FakeOutboxCompletionChannel : IOutboxCompletionChannel {
    public ConcurrentBag<Guid> AllIds { get; } = [];
    private readonly object _gate = new();
    private int _target = -1;
    private TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ValueTask EnqueueAsync(Guid id, CancellationToken ct = default) {
      AllIds.Add(id);
      lock (_gate) {
        if (_target > 0 && AllIds.Count >= _target) {
          _reached.TrySetResult();
        }
      }
      return ValueTask.CompletedTask;
    }

    /// <summary>Completion SIGNAL, not a poll — the drain is asynchronous and timing-based waits
    /// make these tests flaky on a loaded machine.</summary>
    public Task WaitForCountAsync(int count, TimeSpan timeout) {
      lock (_gate) {
        _target = count;
        if (AllIds.Count >= count) {
          return Task.CompletedTask;
        }
      }
      return _reached.Task.WaitAsync(timeout);
    }
  }

  private sealed class FakeFailureChannel : IFailureChannel {
    public ConcurrentBag<MessageFailure> All { get; } = [];
    public ValueTask EnqueueAsync(WorkCategory category, MessageFailure failure, CancellationToken ct = default) {
      All.Add(failure);
      return ValueTask.CompletedTask;
    }
  }

  private sealed class FakePublishStrategy : IMessagePublishStrategy {
    public List<OutboxWork> Published { get; } = [];
    public TaskCompletionSource<int> ReachedCount { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public int TargetCount { get; set; } = 1;
    public Task<bool> IsReadyAsync(CancellationToken ct = default) => Task.FromResult(true);
    public Task<MessagePublishResult> PublishAsync(OutboxWork work, CancellationToken ct) {
      Published.Add(work);
      if (Published.Count >= TargetCount) {
        ReachedCount.TrySetResult(Published.Count);
      }
      return Task.FromResult(new MessagePublishResult {
        MessageId = work.MessageId,
        Success = true,
        CompletedStatus = MessageProcessingStatus.Published,
      });
    }
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

  /// <summary>Fake coordinator that returns a fixed batch for a given stream_id.</summary>
  private sealed class FakeWorkCoordinator : IWorkCoordinator {
    public Dictionary<Guid, List<OutboxBatchRow>> RowsByStream { get; } = [];
    public TaskCompletionSource<int> FirstFetchCalled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public int FetchCalls;

    /// <summary>
    /// How many stream_ids each fetch carried. <c>fetch_outbox_batch</c> takes
    /// <c>p_stream_ids UUID[]</c> and partitions by stream, so a drain batch should arrive as
    /// ONE call carrying many ids. A bag of all-1s means the caller fanned the batch out into
    /// one round-trip per stream — each of which re-scans the whole unpublished outbox.
    /// </summary>
    public ConcurrentBag<int> StreamsPerFetch { get; } = [];

    public Task<IReadOnlyList<OutboxBatchRow>> FetchOutboxBatchAsync(
      IReadOnlyList<Guid> streamIds, Guid instanceId, int maxPerStream = 100, CancellationToken cancellationToken = default) {
      var n = Interlocked.Increment(ref FetchCalls);
      StreamsPerFetch.Add(streamIds.Count);
      FirstFetchCalled.TrySetResult(n);
      var result = new List<OutboxBatchRow>();
      foreach (var sid in streamIds) {
        if (RowsByStream.TryGetValue(sid, out var rows)) {
          result.AddRange(rows.Take(maxPerStream));
        }
      }
      return Task.FromResult<IReadOnlyList<OutboxBatchRow>>(result);
    }

    // Required (non-default-implemented) interface members — minimal stubs.
    public Task<WorkBatch> ClaimWorkAsync(ClaimWorkRequest request, CancellationToken ct = default) =>
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

  private static OutboxBatchRow _row(Guid messageId, Guid streamId) {
    var envelope = new MessageEnvelope<JsonElement> {
      MessageId = MessageId.From(messageId),
      Payload = JsonDocument.Parse("{}").RootElement,
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local },
      Hops = [],
    };
    var typeInfo = _jsonOpts.GetTypeInfo(typeof(MessageEnvelope<JsonElement>))
      ?? throw new InvalidOperationException("Test setup: no JsonTypeInfo for MessageEnvelope<JsonElement>");
    var envelopeJson = JsonSerializer.Serialize(envelope, typeInfo);
    return new OutboxBatchRow {
      MessageId = messageId,
      StreamId = streamId,
      Destination = "test-topic",
      MessageType = "TestMessage",
      EnvelopeType = typeof(MessageEnvelope<JsonElement>).AssemblyQualifiedName ?? "MessageEnvelope",
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

  /// <summary>
  /// Publish strategy that mutually-blocks N publishes until all N have arrived.
  /// Use this to prove cross-stream concurrency: with a serial drainer, only one publish
  /// is ever in-flight at a time → <see cref="AllInFlight"/> never resolves → the test
  /// deadlocks and the timeout assertion fails. With a parallel drainer (one task per
  /// stream within a batch), N publishes arrive concurrently and the gate releases.
  /// </summary>
  private sealed class _ConcurrentPublishGateStrategy(int targetInFlight) : IMessagePublishStrategy {
    public TaskCompletionSource<int> AllInFlight { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public ConcurrentBag<OutboxWork> Published { get; } = [];
    private int _inFlight;
    public Task<bool> IsReadyAsync(CancellationToken ct = default) => Task.FromResult(true);
    public async Task<MessagePublishResult> PublishAsync(OutboxWork work, CancellationToken ct) {
      var n = Interlocked.Increment(ref _inFlight);
      if (n >= targetInFlight) {
        AllInFlight.TrySetResult(n);
      }
      await AllInFlight.Task.ConfigureAwait(false);
      Published.Add(work);
      return new MessagePublishResult {
        MessageId = work.MessageId,
        Success = true,
        CompletedStatus = MessageProcessingStatus.Published,
      };
    }
  }

  /// <summary>
  /// The escape hatch: MaxPublishBatchSize = 0 restores the legacy per-stream publish.
  /// </summary>
  /// <remarks>
  /// Kept because cross-stream batching changes the shape of what reaches the broker, and an
  /// operator hitting an unforeseen interaction needs a way back to the previous behavior without
  /// downgrading the package. A hatch that silently does nothing is worse than none, so this locks
  /// it to the observable it promises: one batch per stream.
  /// </remarks>
  [Test]
  public async Task OutboxDrainWorker_ZeroBatchSize_RestoresPerStreamPublishAsync() {
    const int streamCount = 6;
    var streamIds = new Guid[streamCount];
    var coord = new FakeWorkCoordinator();
    for (var i = 0; i < streamCount; i++) {
      streamIds[i] = (Guid)TrackedGuid.NewMedo();
      coord.RowsByStream[streamIds[i]] = [_row((Guid)TrackedGuid.NewMedo(), streamIds[i])];
    }

    var drainChannel = new FakeOutboxDrainChannel();
    var completion = new FakeOutboxCompletionChannel();
    var failure = new FakeFailureChannel();
    var publish = new _BulkCapablePublishStrategy();
    var gate = new SchemaReadyGate();
    gate.MarkReady();

    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coord);
    var sp = services.BuildServiceProvider();

    var worker = new OutboxDrainWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      new FakeServiceInstanceProvider(), drainChannel, completion, failure, gate,
      Options.Create(new OutboxDrainWorkerOptions {
        Enabled = true,
        MaxPerStream = 100,
        MaxConcurrentStreams = streamCount,
        MaxPublishBatchSize = 0,
      }),
      Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions(),
      NullLogger<OutboxDrainWorker>.Instance,
      publish);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    foreach (var sid in streamIds) {
      await drainChannel.WriteAsync(sid);
    }
    await completion.WaitForCountAsync(streamCount, TimeSpan.FromSeconds(30));
    await worker.StopAsync(CancellationToken.None);

    List<IReadOnlyList<OutboxWork>> batches;
    lock (publish.BatchCalls) { batches = [.. publish.BatchCalls]; }

    await Assert.That(batches.Sum(b => b.Count)).IsEqualTo(streamCount)
      .Because("the hatch must not lose rows either");
    await Assert.That(batches.All(b => b.Count == 1)).IsTrue()
      .Because("with the hatch open every batch is one stream's rows — here one row each — which "
             + "is exactly the legacy behavior an operator would be reaching for");
  }

  /// <summary>
  /// Cross-stream publish batching: many streams holding ONE row each must still fill a batch.
  /// </summary>
  /// <remarks>
  /// <para>
  /// The drain assembled its publish batch from a single stream's rows. Measured on a producer
  /// mid-import, 88% of "bulk" publishes carried exactly one message against a cap of 25, because
  /// 98% of streams held exactly one pending row — about 1.4 rows per stream across ~18,000
  /// streams. The drain sustained roughly five broker round trips per second while tens of
  /// thousands of rows waited.
  /// </para>
  /// <para>
  /// Raising stream concurrency cannot fix it: more concurrency yields more simultaneous
  /// SINGLE-message publishes, spending broker connections without changing messages per round
  /// trip, which is the quantity that binds.
  /// </para>
  /// </remarks>
  [Test]
  public async Task OutboxDrainWorker_ManySingleRowStreams_FillsBatchesAcrossStreamsAsync() {
    const int streamCount = 20;
    var streamIds = new Guid[streamCount];
    var coord = new FakeWorkCoordinator();
    for (var i = 0; i < streamCount; i++) {
      streamIds[i] = (Guid)TrackedGuid.NewMedo();
      // The production shape: one pending row per stream.
      coord.RowsByStream[streamIds[i]] = [_row((Guid)TrackedGuid.NewMedo(), streamIds[i])];
    }

    var drainChannel = new FakeOutboxDrainChannel();
    var completion = new FakeOutboxCompletionChannel();
    var failure = new FakeFailureChannel();
    var publish = new _BulkCapablePublishStrategy();
    var gate = new SchemaReadyGate();
    gate.MarkReady();

    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coord);
    var sp = services.BuildServiceProvider();

    var worker = new OutboxDrainWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      new FakeServiceInstanceProvider(), drainChannel, completion, failure, gate,
      Options.Create(new OutboxDrainWorkerOptions {
        Enabled = true,
        MaxPerStream = 100,
        MaxConcurrentStreams = streamCount,
        MaxPublishBatchSize = 25,
      }),
      Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions(),
      NullLogger<OutboxDrainWorker>.Instance,
      publish);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    foreach (var sid in streamIds) {
      await drainChannel.WriteAsync(sid);
    }
    await completion.WaitForCountAsync(streamCount, TimeSpan.FromSeconds(30));
    await worker.StopAsync(CancellationToken.None);

    List<IReadOnlyList<OutboxWork>> batches;
    lock (publish.BatchCalls) { batches = [.. publish.BatchCalls]; }

    var published = batches.Sum(b => b.Count);
    await Assert.That(published).IsEqualTo(streamCount)
      .Because("batching must not lose or duplicate rows — every claimed row publishes exactly once");

    var largest = batches.Count == 0 ? 0 : batches.Max(b => b.Count);
    await Assert.That(largest).IsGreaterThan(1)
      .Because($"{streamCount} streams of one row each produced {batches.Count} batches with a "
             + "largest of " + largest + "; batching PER STREAM makes every batch a singleton, "
             + "which is the measured 88%-singleton defect");

    await Assert.That(batches.Count).IsLessThan(streamCount)
      .Because("the whole point is fewer round trips than rows — one batch per row is the "
             + "behavior being replaced");
  }

  /// <summary>
  /// Cross-stream batching must not break per-stream FIFO, which IS a real invariant.
  /// </summary>
  [Test]
  public async Task OutboxDrainWorker_CrossStreamBatching_PreservesPerStreamOrderAsync() {
    const int streamCount = 6;
    const int rowsPerStream = 4;
    var streamIds = new Guid[streamCount];
    var expected = new Dictionary<Guid, List<Guid>>();
    var coord = new FakeWorkCoordinator();
    for (var i = 0; i < streamCount; i++) {
      var sid = (Guid)TrackedGuid.NewMedo();
      streamIds[i] = sid;
      var rows = new List<OutboxBatchRow>();
      var ids = new List<Guid>();
      for (var r = 0; r < rowsPerStream; r++) {
        var mid = (Guid)TrackedGuid.NewMedo();
        ids.Add(mid);
        rows.Add(_row(mid, sid));
      }
      coord.RowsByStream[sid] = rows;
      expected[sid] = ids;
    }

    var drainChannel = new FakeOutboxDrainChannel();
    var completion = new FakeOutboxCompletionChannel();
    var failure = new FakeFailureChannel();
    var publish = new _BulkCapablePublishStrategy();
    var gate = new SchemaReadyGate();
    gate.MarkReady();

    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coord);
    var sp = services.BuildServiceProvider();

    var worker = new OutboxDrainWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      new FakeServiceInstanceProvider(), drainChannel, completion, failure, gate,
      Options.Create(new OutboxDrainWorkerOptions {
        Enabled = true,
        MaxPerStream = 100,
        MaxConcurrentStreams = streamCount,
        MaxPublishBatchSize = 5,
      }),
      Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions(),
      NullLogger<OutboxDrainWorker>.Instance,
      publish);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    foreach (var sid in streamIds) {
      await drainChannel.WriteAsync(sid);
    }
    await completion.WaitForCountAsync(streamCount, TimeSpan.FromSeconds(30));
    await worker.StopAsync(CancellationToken.None);

    List<IReadOnlyList<OutboxWork>> batches;
    lock (publish.BatchCalls) { batches = [.. publish.BatchCalls]; }
    var flat = batches.SelectMany(b => b).ToList();

    foreach (var (sid, want) in expected) {
      var got = flat.Where(w => w.StreamId == sid).Select(w => w.MessageId).ToList();
      await Assert.That(got).IsEquivalentTo(want)
        .Because("mixing streams into one publish is safe ONLY while each stream keeps its own "
               + "order — batches publish in emission order, so a stream split across two batches "
               + "must still arrive in sequence");
    }
  }

  /// <summary>
  /// Production follow-up — a consumer's bulk import ran at a fraction of expected
  /// throughput with many streams pending in the outbox, root-caused to a serial cross-stream foreach in
  /// <c>OutboxDrainWorker.ExecuteAsync</c>. Per-stream FIFO is required; cross-stream
  /// FIFO is NOT — different streams can and must drain in parallel.
  ///
  /// This test locks the invariant: with N streams in a single drain batch and a publish
  /// strategy that mutually-blocks until all N publishes are concurrently in flight, the
  /// drainer MUST run them in parallel. A serial drainer deadlocks (only 1 ever in flight)
  /// and the test times out. A parallel drainer (capped at <c>MaxConcurrentStreams</c>)
  /// reaches the gate and all complete promptly.
  /// </summary>
  [Test]
  public async Task OutboxDrainWorker_MultipleStreamsInOneBatch_DrainsConcurrentlyAcrossStreamsAsync() {
    const int streamCount = 4;
    var streamIds = new Guid[streamCount];
    var messageIds = new Guid[streamCount];
    var coord = new FakeWorkCoordinator();
    for (var i = 0; i < streamCount; i++) {
      streamIds[i] = (Guid)TrackedGuid.NewMedo();
      messageIds[i] = (Guid)TrackedGuid.NewMedo();
      coord.RowsByStream[streamIds[i]] = [_row(messageIds[i], streamIds[i])];
    }

    var drainChannel = new FakeOutboxDrainChannel();
    var completion = new FakeOutboxCompletionChannel();
    var failure = new FakeFailureChannel();
    var publish = new _ConcurrentPublishGateStrategy(targetInFlight: streamCount);
    var instance = new FakeServiceInstanceProvider();
    var gate = new SchemaReadyGate();
    gate.MarkReady();

    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coord);
    var sp = services.BuildServiceProvider();

    var worker = new OutboxDrainWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      instance, drainChannel, completion, failure, gate,
      Options.Create(new OutboxDrainWorkerOptions {
        Enabled = true,
        MaxPerStream = 100,
        MaxConcurrentStreams = streamCount,
      }),
      Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions(),
      NullLogger<OutboxDrainWorker>.Instance,
      publish);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    foreach (var sid in streamIds) {
      await drainChannel.WriteAsync(sid);
    }

    // With cross-stream parallelism: all 4 PublishAsync invocations arrive and release the
    // gate within a few hundred ms. Without it: only 1 is ever in-flight → AllInFlight never
    // resolves → the timeout below wins and the assertion fails.
    var winner = await Task.WhenAny(publish.AllInFlight.Task, Task.Delay(TimeSpan.FromSeconds(5)));

    await Assert.That(publish.AllInFlight.Task.IsCompletedSuccessfully)
      .IsTrue()
      .Because("OutboxDrainWorker must drain different streams within one batch in parallel; serial cross-stream foreach blocks all but one publish");

    cts.Cancel();
    try { await worker.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }
  }

  /// <summary>Publish strategy that completes a signal once N rows have been published.</summary>
  private sealed class _CountingPublishStrategy(int expected) : IMessagePublishStrategy {
    public TaskCompletionSource<int> Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _count;
    public Task<bool> IsReadyAsync(CancellationToken ct = default) => Task.FromResult(true);
    public Task<MessagePublishResult> PublishAsync(OutboxWork work, CancellationToken ct) {
      if (Interlocked.Increment(ref _count) >= expected) {
        Reached.TrySetResult(_count);
      }
      return Task.FromResult(new MessagePublishResult {
        MessageId = work.MessageId,
        Success = true,
        CompletedStatus = MessageProcessingStatus.Published,
      });
    }
  }

  /// <summary>
  /// The drainer must fetch a whole drain batch with ONE multi-stream call, not one call per
  /// stream. <c>fetch_outbox_batch</c> is built for this — it takes <c>p_stream_ids UUID[]</c>,
  /// ranks with <c>PARTITION BY o.stream_id</c>, and caps <c>p_max_per_stream</c> per stream.
  /// <c>InboxDrainWorker</c> already batches its mirror call for exactly this reason.
  ///
  /// Fanning out costs more than N round-trips: there is no index on <c>wh_outbox(stream_id)</c>,
  /// so every per-stream call scans all unpublished rows and discards the ~99% belonging to
  /// other streams. Draining N streams then costs N full scans of the same working set to
  /// return N rows, plus N query plans.
  ///
  /// Deterministic by construction: all stream_ids are written BEFORE the worker starts, so the
  /// batcher's first read sees the whole set — no reliance on a sliding-window race. The
  /// assertion is on batch SHAPE (some call carried more than one id) rather than an exact call
  /// count, so it stays honest if the batcher legitimately splits a window.
  /// </summary>
  [Test]
  public async Task OutboxDrainWorker_MultipleStreamsInOneBatch_IssuesOneMultiStreamFetchAsync() {
    const int streamCount = 4;
    var streamIds = new Guid[streamCount];
    var coord = new FakeWorkCoordinator();
    for (var i = 0; i < streamCount; i++) {
      streamIds[i] = (Guid)TrackedGuid.NewMedo();
      coord.RowsByStream[streamIds[i]] = [_row((Guid)TrackedGuid.NewMedo(), streamIds[i])];
    }

    var drainChannel = new FakeOutboxDrainChannel();
    var publish = new _CountingPublishStrategy(expected: streamCount);
    var gate = new SchemaReadyGate();
    gate.MarkReady();

    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coord);
    var sp = services.BuildServiceProvider();

    var worker = new OutboxDrainWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      new FakeServiceInstanceProvider(), drainChannel,
      new FakeOutboxCompletionChannel(), new FakeFailureChannel(), gate,
      Options.Create(new OutboxDrainWorkerOptions {
        Enabled = true,
        MaxPerStream = 100,
        MaxConcurrentStreams = streamCount,
      }),
      Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions(),
      NullLogger<OutboxDrainWorker>.Instance,
      publish);

    // Queue the whole batch before the worker reads — the first batcher window sees all four.
    foreach (var sid in streamIds) {
      await drainChannel.WriteAsync(sid);
    }

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);

    _ = await Task.WhenAny(publish.Reached.Task, Task.Delay(TimeSpan.FromSeconds(5)));

    await Assert.That(publish.Reached.Task.IsCompletedSuccessfully).IsTrue()
      .Because("every queued row must still be published — batching the fetch must not lose rows");

    var perFetch = coord.StreamsPerFetch.ToArray();
    await Assert.That(perFetch.Length).IsGreaterThan(0)
      .Because("the drainer must have fetched at least once");
    await Assert.That(perFetch.Max()).IsGreaterThanOrEqualTo(2)
      .Because(
        "a drain batch of 4 streams must reach the coordinator as a multi-stream fetch; " +
        "all-1s means the batch was fanned out into one round-trip per stream, each re-scanning "
        + "the entire unpublished outbox");
    await Assert.That(coord.FetchCalls).IsLessThan(streamCount)
      .Because("batching must reduce the round-trip count below one-per-stream");

    cts.Cancel();
    try { await worker.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }
  }

  /// <summary>
  /// Publish strategy that reports bulk capability and records whether it was called via the
  /// batch API vs the single API. Used to prove the drainer prefers <c>PublishBatchAsync</c>
  /// when <c>SupportsBulkPublish == true</c>, instead of looping <c>PublishAsync</c> per row.
  /// </summary>
  private sealed class _BulkCapablePublishStrategy : IMessagePublishStrategy {
    public int SingleCallCount;
    public List<IReadOnlyList<OutboxWork>> BatchCalls { get; } = [];
    public bool SupportsBulkPublish => true;
    public Task<bool> IsReadyAsync(CancellationToken ct = default) => Task.FromResult(true);
    public Task<MessagePublishResult> PublishAsync(OutboxWork work, CancellationToken ct) {
      Interlocked.Increment(ref SingleCallCount);
      return Task.FromResult(new MessagePublishResult {
        MessageId = work.MessageId,
        Success = true,
        CompletedStatus = MessageProcessingStatus.Published,
      });
    }
    public Task<IReadOnlyList<MessagePublishResult>> PublishBatchAsync(IReadOnlyList<OutboxWork> works, CancellationToken ct) {
      lock (BatchCalls) { BatchCalls.Add(works); }
      var results = works.Select(w => new MessagePublishResult {
        MessageId = w.MessageId,
        Success = true,
        CompletedStatus = MessageProcessingStatus.Published,
      }).ToList();
      return Task.FromResult<IReadOnlyList<MessagePublishResult>>(results);
    }
  }

  /// <summary>
  /// Production follow-up — within-stream bulk publish: when the publish strategy reports
  /// <see cref="IMessagePublishStrategy.SupportsBulkPublish"/>, the drainer MUST send the
  /// stream's fetched rows via <see cref="IMessagePublishStrategy.PublishBatchAsync"/> in
  /// ONE call, not loop <see cref="IMessagePublishStrategy.PublishAsync"/> per row. Per-row
  /// publish forces one Service Bus round-trip per message; one bulk call ships the whole
  /// stream's batch in one round-trip (up to the transport's batch size limit).
  /// </summary>
  [Test]
  public async Task OutboxDrainWorker_StreamWithManyRows_PublishesAsOneBulkCall_WhenStrategySupportsItAsync() {
    var streamId = (Guid)TrackedGuid.NewMedo();
    var msgIds = Enumerable.Range(0, 10).Select(_ => (Guid)TrackedGuid.NewMedo()).ToArray();

    var coord = new FakeWorkCoordinator();
    coord.RowsByStream[streamId] = [.. msgIds.Select(id => _row(id, streamId))];

    var drainChannel = new FakeOutboxDrainChannel();
    var completion = new FakeOutboxCompletionChannel();
    var failure = new FakeFailureChannel();
    var publish = new _BulkCapablePublishStrategy();
    var instance = new FakeServiceInstanceProvider();
    var gate = new SchemaReadyGate();
    gate.MarkReady();

    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coord);
    var sp = services.BuildServiceProvider();

    var allPublished = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
    var worker = new OutboxDrainWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      instance, drainChannel, completion, failure, gate,
      Options.Create(new OutboxDrainWorkerOptions { Enabled = true, MaxPerStream = 100 }),
      Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions(),
      NullLogger<OutboxDrainWorker>.Instance,
      publish);
    worker.OnOutboxMessagePublished += _ => {
      if (completion.AllIds.Count >= msgIds.Length) {
        allPublished.TrySetResult(completion.AllIds.Count);
      }
    };

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await drainChannel.WriteAsync(streamId);

    _ = await Task.WhenAny(allPublished.Task, Task.Delay(TimeSpan.FromSeconds(5)));
    cts.Cancel();
    try { await worker.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }

    await Assert.That(publish.BatchCalls.Count)
      .IsEqualTo(1)
      .Because("a stream with 10 rows on a bulk-capable transport must publish as ONE PublishBatchAsync call, not 10 PublishAsync calls");
    await Assert.That(publish.BatchCalls[0].Count).IsEqualTo(10);
    await Assert.That(publish.SingleCallCount)
      .IsEqualTo(0)
      .Because("PublishAsync MUST NOT be called when SupportsBulkPublish is true and a batch is available");
  }

  /// <summary>Bulk strategy that returns a mix of success and failure results — covers
  /// the per-result routing branches in <c>_publishBulkAsync</c>.</summary>
  private sealed class _BulkMixedResultStrategy(HashSet<Guid> failIds) : IMessagePublishStrategy {
    public List<IReadOnlyList<OutboxWork>> BatchCalls { get; } = [];
    public bool SupportsBulkPublish => true;
    public Task<bool> IsReadyAsync(CancellationToken ct = default) => Task.FromResult(true);
    public Task<MessagePublishResult> PublishAsync(OutboxWork work, CancellationToken ct) =>
      Task.FromResult(new MessagePublishResult { MessageId = work.MessageId, Success = true, CompletedStatus = MessageProcessingStatus.Published });
    public Task<IReadOnlyList<MessagePublishResult>> PublishBatchAsync(IReadOnlyList<OutboxWork> works, CancellationToken ct) {
      lock (BatchCalls) { BatchCalls.Add(works); }
      var results = works.Select(w => new MessagePublishResult {
        MessageId = w.MessageId,
        Success = !failIds.Contains(w.MessageId),
        CompletedStatus = failIds.Contains(w.MessageId) ? w.Status : MessageProcessingStatus.Published,
        Error = failIds.Contains(w.MessageId) ? "broker said no" : null,
      }).ToList();
      return Task.FromResult<IReadOnlyList<MessagePublishResult>>(results);
    }
  }

  /// <summary>Bulk strategy whose batch call throws — covers the "whole batch fails" branch
  /// that fans every row out to the failure channel.</summary>
  private sealed class _BulkThrowingPublishStrategy : IMessagePublishStrategy {
    public bool SupportsBulkPublish => true;
    public Task<bool> IsReadyAsync(CancellationToken ct = default) => Task.FromResult(true);
    public Task<MessagePublishResult> PublishAsync(OutboxWork work, CancellationToken ct) =>
      throw new InvalidOperationException("PublishAsync should not be called on bulk-capable strategy");
    public Task<IReadOnlyList<MessagePublishResult>> PublishBatchAsync(IReadOnlyList<OutboxWork> works, CancellationToken ct) =>
      throw new InvalidOperationException("transport down");
  }

  /// <summary>Production throughput fix coverage: per-result routing when the batch call
  /// returns a mix of success and failure. Locks that successful rows enqueue completion
  /// and failed rows enqueue a <see cref="MessageFailure"/> with the broker error.</summary>
  [Test]
  public async Task OutboxDrainWorker_BulkResultsMixed_RoutesSuccessToCompletion_FailureToFailureChannelAsync() {
    var streamId = (Guid)TrackedGuid.NewMedo();
    var ok1 = (Guid)TrackedGuid.NewMedo();
    var bad = (Guid)TrackedGuid.NewMedo();
    var ok2 = (Guid)TrackedGuid.NewMedo();

    var coord = new FakeWorkCoordinator();
    coord.RowsByStream[streamId] = [_row(ok1, streamId), _row(bad, streamId), _row(ok2, streamId)];

    var drainChannel = new FakeOutboxDrainChannel();
    var completion = new FakeOutboxCompletionChannel();
    var failure = new FakeFailureChannel();
    var publish = new _BulkMixedResultStrategy([bad]);
    var instance = new FakeServiceInstanceProvider();
    var gate = new SchemaReadyGate();
    gate.MarkReady();

    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coord);
    var sp = services.BuildServiceProvider();

    var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    var worker = new OutboxDrainWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      instance, drainChannel, completion, failure, gate,
      Options.Create(new OutboxDrainWorkerOptions { Enabled = true, MaxPerStream = 100 }),
      Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions(),
      NullLogger<OutboxDrainWorker>.Instance,
      publish);
    worker.OnOutboxMessagePublished += _ => {
      if (completion.AllIds.Count + failure.All.Count >= 3) {
        done.TrySetResult(true);
      }
    };

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await drainChannel.WriteAsync(streamId);
    _ = await Task.WhenAny(done.Task, Task.Delay(TimeSpan.FromSeconds(5)));
    cts.Cancel();
    try { await worker.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }

    await Assert.That(completion.AllIds).Contains(ok1);
    await Assert.That(completion.AllIds).Contains(ok2);
    await Assert.That(completion.AllIds).DoesNotContain(bad);
    await Assert.That(failure.All.Count).IsEqualTo(1);
    await Assert.That(failure.All.Single().MessageId).IsEqualTo(bad);
    await Assert.That(failure.All.Single().Error).IsEqualTo("broker said no");
  }

  /// <summary>Production throughput fix coverage: whole-batch publish exception fans every row
  /// out to the failure channel so claim_orphaned_outbox can re-lease them next cycle.</summary>
  [Test]
  public async Task OutboxDrainWorker_BulkPublishThrows_RoutesAllRowsToFailureChannelAsync() {
    var streamId = (Guid)TrackedGuid.NewMedo();
    var msgIds = Enumerable.Range(0, 5).Select(_ => (Guid)TrackedGuid.NewMedo()).ToArray();

    var coord = new FakeWorkCoordinator();
    coord.RowsByStream[streamId] = [.. msgIds.Select(id => _row(id, streamId))];

    var drainChannel = new FakeOutboxDrainChannel();
    var completion = new FakeOutboxCompletionChannel();
    var failure = new FakeFailureChannel();
    var publish = new _BulkThrowingPublishStrategy();
    var instance = new FakeServiceInstanceProvider();
    var gate = new SchemaReadyGate();
    gate.MarkReady();

    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coord);
    var sp = services.BuildServiceProvider();

    var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    var worker = new OutboxDrainWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      instance, drainChannel, completion, failure, gate,
      Options.Create(new OutboxDrainWorkerOptions { Enabled = true, MaxPerStream = 100 }),
      Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions(),
      NullLogger<OutboxDrainWorker>.Instance,
      publish);
    // OnWorkProcessingIdle fires once the batch finishes — even when every row failed.
    // Use it as the completion signal so the test stays deterministic without Task.Delay.
    var idle = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    worker.OnWorkProcessingIdle += () => idle.TrySetResult(true);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await drainChannel.WriteAsync(streamId);

    _ = await Task.WhenAny(idle.Task, Task.Delay(TimeSpan.FromSeconds(5)));
    cts.Cancel();
    try { await worker.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }

    await Assert.That(failure.All.Count)
      .IsEqualTo(msgIds.Length)
      .Because("PublishBatchAsync throwing must route every row in the batch to the failure channel");
    foreach (var id in msgIds) {
      await Assert.That(failure.All.Any(f => f.MessageId == id)).IsTrue();
    }
    await Assert.That(completion.AllIds).IsEmpty();
  }

  /// <summary>Production throughput fix: backward-compat — setting MaxConcurrentStreams=1
  /// restores the pre-fix serial cross-stream behavior while preserving correctness.</summary>
  [Test]
  public async Task OutboxDrainWorker_MaxConcurrentStreams_OneRestoresSerialCrossStreamDrainAsync() {
    var streamA = (Guid)TrackedGuid.NewMedo();
    var streamB = (Guid)TrackedGuid.NewMedo();
    var msgA = (Guid)TrackedGuid.NewMedo();
    var msgB = (Guid)TrackedGuid.NewMedo();

    var coord = new FakeWorkCoordinator();
    coord.RowsByStream[streamA] = [_row(msgA, streamA)];
    coord.RowsByStream[streamB] = [_row(msgB, streamB)];

    var drainChannel = new FakeOutboxDrainChannel();
    var completion = new FakeOutboxCompletionChannel();
    var failure = new FakeFailureChannel();
    var publish = new FakePublishStrategy { TargetCount = 2 };
    var instance = new FakeServiceInstanceProvider();
    var gate = new SchemaReadyGate();
    gate.MarkReady();

    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coord);
    var sp = services.BuildServiceProvider();

    var worker = new OutboxDrainWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      instance, drainChannel, completion, failure, gate,
      Options.Create(new OutboxDrainWorkerOptions {
        Enabled = true,
        MaxPerStream = 100,
        MaxConcurrentStreams = 1,
      }),
      Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions(),
      NullLogger<OutboxDrainWorker>.Instance,
      publish);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await drainChannel.WriteAsync(streamA);
    await drainChannel.WriteAsync(streamB);

    _ = await Task.WhenAny(publish.ReachedCount.Task, Task.Delay(TimeSpan.FromSeconds(5)));
    cts.Cancel();
    try { await worker.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }

    await Assert.That(publish.Published.Count).IsEqualTo(2);
  }

  [Test]
  public async Task OutboxDrainWorker_OnStreamId_FetchesBatch_PublishesEach_EnqueuesCompletionAsync() {
    var streamId = (Guid)TrackedGuid.NewMedo();
    var msgA = (Guid)TrackedGuid.NewMedo();
    var msgB = (Guid)TrackedGuid.NewMedo();

    var coord = new FakeWorkCoordinator();
    coord.RowsByStream[streamId] = [_row(msgA, streamId), _row(msgB, streamId)];

    var drainChannel = new FakeOutboxDrainChannel();
    var completion = new FakeOutboxCompletionChannel();
    var failure = new FakeFailureChannel();
    var publish = new FakePublishStrategy { TargetCount = 2 };
    var instance = new FakeServiceInstanceProvider();
    var gate = new SchemaReadyGate();
    gate.MarkReady();

    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coord);
    var sp = services.BuildServiceProvider();

    var worker = new OutboxDrainWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      instance, drainChannel, completion, failure, gate,
      Options.Create(new OutboxDrainWorkerOptions { Enabled = true, MaxPerStream = 100 }),
      Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions(),
      NullLogger<OutboxDrainWorker>.Instance,
      publish);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await drainChannel.WriteAsync(streamId);

    // Diagnose: was FetchOutboxBatchAsync even called?
    var fetchCalled = await Task.WhenAny(coord.FirstFetchCalled.Task, Task.Delay(TimeSpan.FromSeconds(15)));
    await Assert.That(coord.FirstFetchCalled.Task.IsCompleted).IsTrue()
      .Because("worker should call FetchOutboxBatchAsync after a stream_id arrives on the drain channel");

    var reached = await Task.WhenAny(publish.ReachedCount.Task, Task.Delay(TimeSpan.FromSeconds(15)));
    cts.Cancel();
    try { await worker.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }

    await Assert.That(publish.ReachedCount.Task.IsCompleted).IsTrue();
    await Assert.That(publish.Published.Count).IsEqualTo(2);
    // FIFO within stream — fetch order is preserved.
    await Assert.That(publish.Published[0].MessageId).IsEqualTo(msgA);
    await Assert.That(publish.Published[1].MessageId).IsEqualTo(msgB);
    // Both completions enqueued.
    await Assert.That(completion.AllIds).Contains(msgA);
    await Assert.That(completion.AllIds).Contains(msgB);
    await Assert.That(failure.All).IsEmpty();
  }

  [Test]
  public async Task OutboxDrainWorker_MoreRowsThanMaxPerStream_LoopsUntilEmptyAsync() {
    // Phase H step 6 slice 3: drainer continues fetching for the same stream while there
    // are NEW rows. Simulates 250 pending rows / MaxPerStream=100 → 3 publish iterations
    // (100 + 100 + 50). The fake "consumes" returned rows on each fetch (mimicking what
    // happens once completion-flush lands and complete_outbox_published deletes them).
    var streamId = (Guid)TrackedGuid.NewMedo();
    var msgs = Enumerable.Range(0, 250).Select(_ => (Guid)TrackedGuid.NewMedo()).ToArray();

    var coord = new ConsumingFakeWorkCoordinator();
    coord.RowsByStream[streamId] = [.. msgs.Select(m => _row(m, streamId))];

    var drainChannel = new FakeOutboxDrainChannel();
    var completion = new FakeOutboxCompletionChannel();
    var failure = new FakeFailureChannel();
    var publish = new FakePublishStrategy { TargetCount = 250 };
    var instance = new FakeServiceInstanceProvider();
    var gate = new SchemaReadyGate();
    gate.MarkReady();

    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coord);
    var sp = services.BuildServiceProvider();

    var worker = new OutboxDrainWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      instance, drainChannel, completion, failure, gate,
      Options.Create(new OutboxDrainWorkerOptions { Enabled = true, MaxPerStream = 100 }),
      _jsonOpts,
      NullLogger<OutboxDrainWorker>.Instance,
      publish);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await drainChannel.WriteAsync(streamId);

    _ = await Task.WhenAny(publish.ReachedCount.Task, Task.Delay(TimeSpan.FromSeconds(30)));
    cts.Cancel();
    try { await worker.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }

    await Assert.That(publish.Published.Count).IsEqualTo(250)
      .Because("drainer must keep fetching for the same stream until pending=0; 250 rows / MaxPerStream=100 = 3 iterations");
    await Assert.That(coord.FetchCalls).IsGreaterThanOrEqualTo(3)
      .Because("drainer should have fetched at least 3 times to drain 250 rows at MaxPerStream=100");
  }

  [Test]
  public async Task OutboxDrainWorker_RefetchReturnsSameRows_ExitsWithoutDoublePublishAsync() {
    // Race: completion-flush lags. After publishing batch 1, the next fetch returns the
    // SAME rows (because complete_outbox_published hasn't deleted them yet). Drainer must
    // detect via session-set and exit without re-publishing — the next claim_work tick
    // will re-issue once the rows clear.
    var streamId = (Guid)TrackedGuid.NewMedo();
    var msgs = Enumerable.Range(0, 50).Select(_ => (Guid)TrackedGuid.NewMedo()).ToArray();

    var coord = new FakeWorkCoordinator();  // does NOT consume rows on fetch
    coord.RowsByStream[streamId] = [.. msgs.Select(m => _row(m, streamId))];

    var drainChannel = new FakeOutboxDrainChannel();
    var completion = new FakeOutboxCompletionChannel();
    var failure = new FakeFailureChannel();
    var publish = new FakePublishStrategy { TargetCount = 50 };
    var instance = new FakeServiceInstanceProvider();
    var gate = new SchemaReadyGate();
    gate.MarkReady();

    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coord);
    var sp = services.BuildServiceProvider();

    var worker = new OutboxDrainWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      instance, drainChannel, completion, failure, gate,
      Options.Create(new OutboxDrainWorkerOptions { Enabled = true, MaxPerStream = 100 }),
      _jsonOpts,
      NullLogger<OutboxDrainWorker>.Instance,
      publish);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await drainChannel.WriteAsync(streamId);

    _ = await Task.WhenAny(publish.ReachedCount.Task, Task.Delay(TimeSpan.FromSeconds(30)));
    // Give a chance for any spurious second-pass publishes to happen.
    await Task.Delay(200);
    cts.Cancel();
    try { await worker.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }

    await Assert.That(publish.Published.Count).IsEqualTo(50)
      .Because("session-set dedup must skip rows already published this drain session — no re-publish even if fetch returns the same rows");
  }

  /// <summary>Fake coordinator that REMOVES returned rows from the dictionary on each fetch — mimics post-completion DELETE.</summary>
  private sealed class ConsumingFakeWorkCoordinator : IWorkCoordinator {
    public Dictionary<Guid, List<OutboxBatchRow>> RowsByStream { get; } = [];
    public int FetchCalls;
    public Task<IReadOnlyList<OutboxBatchRow>> FetchOutboxBatchAsync(
      IReadOnlyList<Guid> streamIds, Guid instanceId, int maxPerStream = 100, CancellationToken cancellationToken = default) {
      Interlocked.Increment(ref FetchCalls);
      var result = new List<OutboxBatchRow>();
      foreach (var sid in streamIds) {
        if (RowsByStream.TryGetValue(sid, out var rows)) {
          var taken = rows.Take(maxPerStream).ToList();
          result.AddRange(taken);
          rows.RemoveRange(0, taken.Count);
        }
      }
      return Task.FromResult<IReadOnlyList<OutboxBatchRow>>(result);
    }

    public Task<WorkBatch> ClaimWorkAsync(ClaimWorkRequest request, CancellationToken ct = default) =>
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
  public async Task OutboxDrainWorker_RepeatedStreamId_DrainerIsIdempotent_OnceCompletedAsync() {
    // The Part C invariant: claim_work emitting the same stream_id repeatedly must NOT cause
    // re-publish. The drainer fetches eligible rows; once a row is completed (production: deleted)
    // the next fetch returns 0 rows. Models the "rerun-claim doesn't re-issue" guarantee.

    var streamId = (Guid)TrackedGuid.NewMedo();
    var msgA = (Guid)TrackedGuid.NewMedo();

    var coord = new FakeWorkCoordinator();
    coord.RowsByStream[streamId] = [_row(msgA, streamId)];

    var drainChannel = new FakeOutboxDrainChannel();
    var completion = new FakeOutboxCompletionChannel();
    var failure = new FakeFailureChannel();
    var publish = new FakePublishStrategy { TargetCount = 1 };
    var instance = new FakeServiceInstanceProvider();
    var gate = new SchemaReadyGate();
    gate.MarkReady();

    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coord);
    var sp = services.BuildServiceProvider();

    var worker = new OutboxDrainWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      instance, drainChannel, completion, failure, gate,
      Options.Create(new OutboxDrainWorkerOptions { Enabled = true, MaxPerStream = 100 }),
      Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions(),
      NullLogger<OutboxDrainWorker>.Instance,
      publish);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);

    // First drain: publishes msgA.
    await drainChannel.WriteAsync(streamId);
    _ = await Task.WhenAny(publish.ReachedCount.Task, Task.Delay(TimeSpan.FromSeconds(30)));

    // Simulate completion-flush deleting the row by clearing the fake's stream contents.
    coord.RowsByStream[streamId] = [];

    // Second drain on the same stream_id: fetch returns nothing → publish NOT called again.
    await drainChannel.WriteAsync(streamId);
    // Wait briefly to let the worker process the second message via TaskCompletionSource on completion channel signal.
    var secondCompletionSeen = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    _ = Task.Run(async () => {
      // Allow worker to actually iterate. Use a fast loop checking the publish count.
      for (var i = 0; i < 100; i++) {
        await Task.Delay(20);
        if (publish.Published.Count > 1) {
          secondCompletionSeen.TrySetResult(false);
          return;
        }
      }
      secondCompletionSeen.TrySetResult(true);
    });

    var ok = await Task.WhenAny(secondCompletionSeen.Task, Task.Delay(TimeSpan.FromSeconds(30)));
    cts.Cancel();
    try { await worker.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }

    await Assert.That(publish.Published.Count).IsEqualTo(1);
  }

  // ==========================================================================
  // Pre/Post Outbox lifecycle hooks
  // ==========================================================================

  private sealed class CapturingLifecycleDeserializer : ILifecycleMessageDeserializer {
    public object DeserializeFromEnvelope(IMessageEnvelope<JsonElement> envelope, string envelopeTypeName) => envelope.Payload;
    public object DeserializeFromEnvelope(IMessageEnvelope<JsonElement> envelope) => envelope.Payload;
    public object DeserializeFromBytes(byte[] jsonBytes, string messageTypeName) => jsonBytes;
    public object DeserializeFromJsonElement(JsonElement payload, string messageTypeName) => payload;
  }

  private sealed class CapturingReceptorInvoker : IReceptorInvoker {
    private readonly List<(LifecycleStage Stage, IMessageEnvelope Envelope)> _invocations = [];
    private readonly Lock _lock = new();
    public Func<LifecycleStage, IMessageEnvelope, Task>? OnInvoke { get; set; }

    public List<(LifecycleStage Stage, IMessageEnvelope Envelope)> Invocations {
      get { lock (_lock) { return [.. _invocations]; } }
    }

    public async ValueTask InvokeAsync(IMessageEnvelope envelope, LifecycleStage stage, ILifecycleContext? context = null, CancellationToken cancellationToken = default) {
      lock (_lock) {
        _invocations.Add((stage, envelope));
      }
      if (OnInvoke is not null) {
        await OnInvoke(stage, envelope);
      }
    }
  }

  private sealed class AlwaysHasReceptorsRegistry : IReceptorRegistryQuery {
    public bool HasReceptors(LifecycleStage stage, string messageType) => true;
    public bool HasInboxHandler(string messageType) => true;
    public bool HasAnyConsumer(string messageType) => true;
  }

  [Test]
  public async Task OutboxDrainWorker_WithLifecycleDeps_FiresPreAndPostOutboxInline_AroundPublishAsync() {
    var streamId = (Guid)TrackedGuid.NewMedo();
    var msgId = (Guid)TrackedGuid.NewMedo();

    var coord = new FakeWorkCoordinator();
    coord.RowsByStream[streamId] = [_row(msgId, streamId)];

    var drainChannel = new FakeOutboxDrainChannel();
    var completion = new FakeOutboxCompletionChannel();
    var failure = new FakeFailureChannel();
    var publish = new FakePublishStrategy { TargetCount = 1 };
    var instance = new FakeServiceInstanceProvider();
    var gate = new SchemaReadyGate();
    gate.MarkReady();

    var invoker = new CapturingReceptorInvoker();
    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coord);
    services.AddSingleton<IReceptorInvoker>(invoker);
    var sp = services.BuildServiceProvider();

    var worker = new OutboxDrainWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      instance, drainChannel, completion, failure, gate,
      Options.Create(new OutboxDrainWorkerOptions { Enabled = true, MaxPerStream = 100 }),
      _jsonOpts,
      NullLogger<OutboxDrainWorker>.Instance,
      publish,
      lifecycleMessageDeserializer: new CapturingLifecycleDeserializer(),
      receptorRegistry: new AlwaysHasReceptorsRegistry(),
      runtimeReceptorRegistry: null);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await drainChannel.WriteAsync(streamId);

    _ = await Task.WhenAny(publish.ReachedCount.Task, Task.Delay(TimeSpan.FromSeconds(15)));
    // Allow Post-Outbox lifecycle to fire after publish (it runs synchronously after PublishAsync returns).
    for (var i = 0; i < 50 && invoker.Invocations.All(x => x.Stage != LifecycleStage.PostOutboxInline); i++) {
      await Task.Delay(20);
    }
    cts.Cancel();
    try { await worker.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }

    var stages = invoker.Invocations.Select(x => x.Stage).ToList();
    await Assert.That(stages).Contains(LifecycleStage.PreOutboxInline)
      .Because("PreOutboxInline must fire before publish when a deserializer + receptor registry are wired.");
    await Assert.That(stages).Contains(LifecycleStage.PostOutboxInline)
      .Because("PostOutboxInline must fire after a successful publish.");
    await Assert.That(publish.Published.Count).IsEqualTo(1);
  }

  [Test]
  public async Task OutboxDrainWorker_NoLifecycleDeps_PublishesWithoutLifecycle_NoOpAsync() {
    // When lifecycleMessageDeserializer + receptorRegistry are absent (legacy / minimal
    // hosts), the worker must degrade gracefully: publish + complete still happen,
    // lifecycle invocation simply no-ops.
    var streamId = (Guid)TrackedGuid.NewMedo();
    var msgId = (Guid)TrackedGuid.NewMedo();

    var coord = new FakeWorkCoordinator();
    coord.RowsByStream[streamId] = [_row(msgId, streamId)];

    var drainChannel = new FakeOutboxDrainChannel();
    var completion = new FakeOutboxCompletionChannel();
    var failure = new FakeFailureChannel();
    var publish = new FakePublishStrategy { TargetCount = 1 };
    var instance = new FakeServiceInstanceProvider();
    var gate = new SchemaReadyGate();
    gate.MarkReady();

    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coord);
    var sp = services.BuildServiceProvider();

    var worker = new OutboxDrainWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      instance, drainChannel, completion, failure, gate,
      Options.Create(new OutboxDrainWorkerOptions { Enabled = true, MaxPerStream = 100 }),
      _jsonOpts,
      NullLogger<OutboxDrainWorker>.Instance,
      publish);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await drainChannel.WriteAsync(streamId);

    _ = await Task.WhenAny(publish.ReachedCount.Task, Task.Delay(TimeSpan.FromSeconds(15)));
    cts.Cancel();
    try { await worker.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }

    await Assert.That(publish.Published.Count).IsEqualTo(1)
      .Because("publish must still happen even when lifecycle dependencies are unwired (legacy host).");
    await Assert.That(completion.AllIds).Contains(msgId);
  }

  [Test]
  public async Task OutboxDrainWorker_LifecycleReceptorThrows_PublishAndCompletionStillHappenAsync() {
    // A misbehaving lifecycle receptor must NOT block the publish or completion path —
    // the OutboxDrainWorker wraps lifecycle invocation in try/catch and proceeds.
    // Production invariant: an Outbox row that's about to be published or has just
    // been published MUST always reach completion-flush regardless of receptor faults.
    //
    // Slice 1 of release/v0.645.0-alpha.1 (outbox-DLQ + dual-hash analysis) updates
    // the failure-channel assertion: the lifecycle exception now also routes through
    // IFailureChannel so process_outbox_failures populates wh_outbox.error with the
    // full ex.ToString(). The pre-slice "no failure record on lifecycle fault" was
    // the actual production BUG (a stuck RemoveUserCommand ran hundreds of retries over
    // 24 h with empty wh_outbox.error). Locking the new "lifecycle exception →
    // failure record" invariant per feedback_lock_invariants_in_tests.
    var streamId = (Guid)TrackedGuid.NewMedo();
    var msgId = (Guid)TrackedGuid.NewMedo();

    var coord = new FakeWorkCoordinator();
    coord.RowsByStream[streamId] = [_row(msgId, streamId)];

    var drainChannel = new FakeOutboxDrainChannel();
    var completion = new FakeOutboxCompletionChannel();
    var failure = new FakeFailureChannel();
    var publish = new FakePublishStrategy { TargetCount = 1 };
    var instance = new FakeServiceInstanceProvider();
    var gate = new SchemaReadyGate();
    gate.MarkReady();

    var invoker = new CapturingReceptorInvoker {
      OnInvoke = (stage, _) => {
        if (stage == LifecycleStage.PreOutboxInline) {
          throw new InvalidOperationException("test-induced receptor failure");
        }
        return Task.CompletedTask;
      }
    };
    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coord);
    services.AddSingleton<IReceptorInvoker>(invoker);
    var sp = services.BuildServiceProvider();

    var worker = new OutboxDrainWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      instance, drainChannel, completion, failure, gate,
      Options.Create(new OutboxDrainWorkerOptions { Enabled = true, MaxPerStream = 100 }),
      _jsonOpts,
      NullLogger<OutboxDrainWorker>.Instance,
      publish,
      lifecycleMessageDeserializer: new CapturingLifecycleDeserializer(),
      receptorRegistry: new AlwaysHasReceptorsRegistry(),
      runtimeReceptorRegistry: null);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await drainChannel.WriteAsync(streamId);

    _ = await Task.WhenAny(publish.ReachedCount.Task, Task.Delay(TimeSpan.FromSeconds(15)));
    cts.Cancel();
    try { await worker.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }

    await Assert.That(publish.Published.Count).IsEqualTo(1)
      .Because("Receptor throwing at PreOutboxInline must not stop the transport publish.");
    await Assert.That(completion.AllIds).Contains(msgId)
      .Because("Completion must still be enqueued — the row is safely durable in the outbox table and reaching the transport.");
    // Slice 1: lifecycle exception must surface through IFailureChannel so
    // process_outbox_failures populates wh_outbox.error. The test-induced fault
    // throws at PreOutboxInline and surfaces as a WorkCategory.Outbox failure
    // record whose Error contains the full ex.ToString().
    await Assert.That(failure.All.Count).IsEqualTo(1)
      .Because("Production fix: lifecycle exceptions MUST enqueue a failure so wh_outbox.error captures the cause — production ran hundreds of silent retries before this routing existed.");
    var captured = failure.All.Single();
    await Assert.That(captured.MessageId).IsEqualTo(msgId)
      .Because("Failure record must target the offending row.");
    await Assert.That(captured.Error).Contains("test-induced receptor failure")
      .Because("The exception message must reach the failure record so it lands in wh_outbox.error.");
    await Assert.That(captured.Error).Contains("InvalidOperationException")
      .Because("Slice 2's SQL fingerprint algorithm reads exception type from the first line of error_text; preserving it here keeps live fingerprinting working.");
  }

  private sealed class NeverHasReceptorsRegistry : IReceptorRegistryQuery {
    public bool HasReceptors(LifecycleStage stage, string messageType) => false;
    public bool HasInboxHandler(string messageType) => false;
    public bool HasAnyConsumer(string messageType) => false;
  }

  /// <summary>
  /// When the registry reports no receptors for the gated Outbox stages, lifecycle
  /// invocation short-circuits BEFORE creating a scope. Locks the fast-path: publish
  /// still happens, receptor invoker is never called.
  /// </summary>
  [Test]
  public async Task OutboxDrainWorker_NoReceptorsRegistered_LifecycleShortCircuits_PublishStillFiresAsync() {
    var streamId = (Guid)TrackedGuid.NewMedo();
    var msgId = (Guid)TrackedGuid.NewMedo();

    var coord = new FakeWorkCoordinator();
    coord.RowsByStream[streamId] = [_row(msgId, streamId)];

    var drainChannel = new FakeOutboxDrainChannel();
    var completion = new FakeOutboxCompletionChannel();
    var failure = new FakeFailureChannel();
    var publish = new FakePublishStrategy { TargetCount = 1 };
    var instance = new FakeServiceInstanceProvider();
    var gate = new SchemaReadyGate();
    gate.MarkReady();

    var invoker = new CapturingReceptorInvoker();
    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coord);
    services.AddSingleton<IReceptorInvoker>(invoker);
    var sp = services.BuildServiceProvider();

    var worker = new OutboxDrainWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      instance, drainChannel, completion, failure, gate,
      Options.Create(new OutboxDrainWorkerOptions { Enabled = true, MaxPerStream = 100 }),
      _jsonOpts,
      NullLogger<OutboxDrainWorker>.Instance,
      publish,
      lifecycleMessageDeserializer: new CapturingLifecycleDeserializer(),
      receptorRegistry: new NeverHasReceptorsRegistry(),
      runtimeReceptorRegistry: null);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await drainChannel.WriteAsync(streamId);

    _ = await Task.WhenAny(publish.ReachedCount.Task, Task.Delay(TimeSpan.FromSeconds(15)));
    cts.Cancel();
    try { await worker.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }

    await Assert.That(publish.Published.Count).IsEqualTo(1)
      .Because("Publish must still happen when no receptors are registered for the gated Outbox stages.");
    await Assert.That(invoker.Invocations.Count).IsEqualTo(0)
      .Because("With registry reporting no receptors for the stage, the worker must short-circuit before invoking the receptor invoker.");
  }

  /// <summary>
  /// An OutboxBatchRow with an empty/null destination represents an event-store-only
  /// message — those rows exist for event store persistence only and MUST NOT fire
  /// transport-side lifecycle stages. Locks that destination-empty rows bypass the
  /// lifecycle invocation entirely.
  /// </summary>
  [Test]
  public async Task OutboxDrainWorker_EmptyDestination_SkipsLifecycle_PublishStillFiresAsync() {
    var streamId = (Guid)TrackedGuid.NewMedo();
    var msgId = (Guid)TrackedGuid.NewMedo();

    var coord = new FakeWorkCoordinator();
    var row = _row(msgId, streamId);
    coord.RowsByStream[streamId] = [row with { Destination = string.Empty }];

    var drainChannel = new FakeOutboxDrainChannel();
    var completion = new FakeOutboxCompletionChannel();
    var failure = new FakeFailureChannel();
    var publish = new FakePublishStrategy { TargetCount = 1 };
    var instance = new FakeServiceInstanceProvider();
    var gate = new SchemaReadyGate();
    gate.MarkReady();

    var invoker = new CapturingReceptorInvoker();
    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coord);
    services.AddSingleton<IReceptorInvoker>(invoker);
    var sp = services.BuildServiceProvider();

    var worker = new OutboxDrainWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      instance, drainChannel, completion, failure, gate,
      Options.Create(new OutboxDrainWorkerOptions { Enabled = true, MaxPerStream = 100 }),
      _jsonOpts,
      NullLogger<OutboxDrainWorker>.Instance,
      publish,
      lifecycleMessageDeserializer: new CapturingLifecycleDeserializer(),
      receptorRegistry: new AlwaysHasReceptorsRegistry(),
      runtimeReceptorRegistry: null);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await drainChannel.WriteAsync(streamId);

    _ = await Task.WhenAny(publish.ReachedCount.Task, Task.Delay(TimeSpan.FromSeconds(15)));
    cts.Cancel();
    try { await worker.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }

    await Assert.That(invoker.Invocations.Count).IsEqualTo(0)
      .Because("Empty-destination (event-store-only) messages must not fire transport-side lifecycle stages.");
  }

  /// <summary>Publish strategy that returns Success=false to exercise the failure path.</summary>
  private sealed class FailingPublishStrategy : IMessagePublishStrategy {
    public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task<bool> IsReadyAsync(CancellationToken ct = default) => Task.FromResult(true);
    public Task<MessagePublishResult> PublishAsync(OutboxWork work, CancellationToken ct) {
      Reached.TrySetResult();
      return Task.FromResult(new MessagePublishResult {
        MessageId = work.MessageId,
        Success = false,
        CompletedStatus = MessageProcessingStatus.Failed,
        Error = "transport reported failure",
        Reason = MessageFailureReason.Unknown,
      });
    }
  }

  [Test]
  public async Task OutboxDrainWorker_PublishReturnsFailure_RoutesToFailureChannelAsync() {
    // Locks the publish-failure path: when IMessagePublishStrategy returns Success=false,
    // the row must NOT be enqueued for completion — it routes to the failure channel so the
    // lease can release, attempts counter can bump on re-claim, and the row eventually
    // dead-letters per MaxAttempts.
    var streamId = (Guid)TrackedGuid.NewMedo();
    var msgId = (Guid)TrackedGuid.NewMedo();

    var coord = new FakeWorkCoordinator();
    coord.RowsByStream[streamId] = [_row(msgId, streamId)];

    var drainChannel = new FakeOutboxDrainChannel();
    var completion = new FakeOutboxCompletionChannel();
    var failure = new FakeFailureChannel();
    var publish = new FailingPublishStrategy();
    var instance = new FakeServiceInstanceProvider();
    var gate = new SchemaReadyGate();
    gate.MarkReady();

    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coord);
    var sp = services.BuildServiceProvider();

    var worker = new OutboxDrainWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      instance, drainChannel, completion, failure, gate,
      Options.Create(new OutboxDrainWorkerOptions { Enabled = true, MaxPerStream = 100 }),
      _jsonOpts,
      NullLogger<OutboxDrainWorker>.Instance,
      publish);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await drainChannel.WriteAsync(streamId);

    _ = await Task.WhenAny(publish.Reached.Task, Task.Delay(TimeSpan.FromSeconds(15)));
    // Give a moment for the failure-channel enqueue to land.
    for (var i = 0; i < 50 && failure.All.IsEmpty; i++) {
      await Task.Delay(20);
    }
    cts.Cancel();
    try { await worker.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }

    await Assert.That(failure.All.Count).IsEqualTo(1)
      .Because("Success=false from transport must route the row to the failure channel for orphan re-claim.");
    await Assert.That(completion.AllIds).IsEmpty()
      .Because("Failed publishes MUST NOT enqueue a completion — that would mark the row Published and bypass retry.");
  }

  /// <summary>Publish strategy that throws a non-cancellation exception to exercise the catch path.</summary>
  private sealed class ThrowingPublishStrategy : IMessagePublishStrategy {
    public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task<bool> IsReadyAsync(CancellationToken ct = default) => Task.FromResult(true);
    public Task<MessagePublishResult> PublishAsync(OutboxWork work, CancellationToken ct) {
      Reached.TrySetResult();
      throw new InvalidOperationException("simulated transport blow-up");
    }
  }

  [Test]
  public async Task OutboxDrainWorker_PublishThrows_RoutesToFailureChannelAsync() {
    // Locks the publish-throws path: when IMessagePublishStrategy.PublishAsync throws an
    // unexpected exception (NOT OperationCanceledException tied to the worker shutdown),
    // the row routes to the failure channel and the drain loop continues with the next stream.
    var streamId = (Guid)TrackedGuid.NewMedo();
    var msgId = (Guid)TrackedGuid.NewMedo();

    var coord = new FakeWorkCoordinator();
    coord.RowsByStream[streamId] = [_row(msgId, streamId)];

    var drainChannel = new FakeOutboxDrainChannel();
    var completion = new FakeOutboxCompletionChannel();
    var failure = new FakeFailureChannel();
    var publish = new ThrowingPublishStrategy();
    var instance = new FakeServiceInstanceProvider();
    var gate = new SchemaReadyGate();
    gate.MarkReady();

    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coord);
    var sp = services.BuildServiceProvider();

    var worker = new OutboxDrainWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      instance, drainChannel, completion, failure, gate,
      Options.Create(new OutboxDrainWorkerOptions { Enabled = true, MaxPerStream = 100 }),
      _jsonOpts,
      NullLogger<OutboxDrainWorker>.Instance,
      publish);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await drainChannel.WriteAsync(streamId);

    _ = await Task.WhenAny(publish.Reached.Task, Task.Delay(TimeSpan.FromSeconds(15)));
    for (var i = 0; i < 50 && failure.All.IsEmpty; i++) {
      await Task.Delay(20);
    }
    cts.Cancel();
    try { await worker.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }

    await Assert.That(failure.All.Count).IsEqualTo(1)
      .Because("A throwing publish must route the row to the failure channel.");
    await Assert.That(completion.AllIds).IsEmpty();
  }

  /// <summary>Deserializer that throws to exercise the lifecycle-deserialize catch path.</summary>
  private sealed class ThrowingLifecycleDeserializer : ILifecycleMessageDeserializer {
    public object DeserializeFromEnvelope(IMessageEnvelope<JsonElement> envelope, string envelopeTypeName)
      => throw new InvalidOperationException("simulated deserialize failure");
    public object DeserializeFromEnvelope(IMessageEnvelope<JsonElement> envelope)
      => throw new InvalidOperationException("simulated deserialize failure");
    public object DeserializeFromBytes(byte[] jsonBytes, string messageTypeName)
      => throw new InvalidOperationException("simulated deserialize failure");
    public object DeserializeFromJsonElement(JsonElement jsonElement, string messageTypeName)
      => throw new InvalidOperationException("simulated deserialize failure");
  }

  [Test]
  public async Task OutboxDrainWorker_LifecycleDeserializerThrows_PublishStillFiresAsync() {
    // Locks the _tryResolveTypedEnvelope catch path: a deserializer that throws (e.g., type
    // not in JSON context, malformed payload) must NOT block publish. The row is still
    // safely durable in the outbox and reaches transport; only lifecycle invocation is
    // skipped for this message.
    var streamId = (Guid)TrackedGuid.NewMedo();
    var msgId = (Guid)TrackedGuid.NewMedo();

    var coord = new FakeWorkCoordinator();
    coord.RowsByStream[streamId] = [_row(msgId, streamId)];

    var drainChannel = new FakeOutboxDrainChannel();
    var completion = new FakeOutboxCompletionChannel();
    var failure = new FakeFailureChannel();
    var publish = new FakePublishStrategy { TargetCount = 1 };
    var instance = new FakeServiceInstanceProvider();
    var gate = new SchemaReadyGate();
    gate.MarkReady();

    var invoker = new CapturingReceptorInvoker();
    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coord);
    services.AddSingleton<IReceptorInvoker>(invoker);
    var sp = services.BuildServiceProvider();

    var worker = new OutboxDrainWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      instance, drainChannel, completion, failure, gate,
      Options.Create(new OutboxDrainWorkerOptions { Enabled = true, MaxPerStream = 100 }),
      _jsonOpts,
      NullLogger<OutboxDrainWorker>.Instance,
      publish,
      lifecycleMessageDeserializer: new ThrowingLifecycleDeserializer(),
      receptorRegistry: new AlwaysHasReceptorsRegistry(),
      runtimeReceptorRegistry: null);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await drainChannel.WriteAsync(streamId);

    _ = await Task.WhenAny(publish.ReachedCount.Task, Task.Delay(TimeSpan.FromSeconds(15)));
    cts.Cancel();
    try { await worker.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }

    await Assert.That(publish.Published.Count).IsEqualTo(1)
      .Because("Lifecycle deserialize failure must not block publish.");
    await Assert.That(completion.AllIds).Contains(msgId);
    await Assert.That(invoker.Invocations.Count).IsEqualTo(0)
      .Because("With deserialize failing, lifecycle invocation must be skipped (typedEnvelope is null).");
  }
}
