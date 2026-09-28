using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;
using Whizbang.Testing.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// The drain's half of #917: a stream's run publishes in order, a failure stops the stream so the rows
/// behind it wait for the retry, and a stream is continued from the lease it already holds instead of
/// waiting a claim cycle per run.
/// </summary>
/// <remarks>
/// Every wait is on a signal the worker body emits: the completion channel reaching a count, or the
/// worker's idle transition at the end of a drain cycle, which comes after the cycle's continuation
/// rounds. No sleeps.
/// </remarks>
/// <docs>fundamentals/work-coordinator/per-stream-drain</docs>
[NotInParallel("WhizbangBackgroundServiceTests")]
public class OutboxDrainWorkerStreamRunTests {

  // --- the tests -----------------------------------------------------------------------------------

  [Test]
  public async Task ALongStream_IsContinuedFromItsCursorWithoutAClaimCycleAsync() {
    var stream = TrackedGuid.New().Value;
    var rows = _rows(stream, 250);
    var coord = new RunCoordinator();
    coord.Leased[stream] = [.. rows.Take(100)];   // the run the claim leased
    coord.Pending[stream] = [.. rows.Skip(100)];   // the rest of the stream, unleased
    var publish = new ScriptedPublishStrategy(bulk: false);
    using var harness = _start(coord, publish, new OutboxDrainWorkerOptions { MaxPerStream = 100 }, stream);
    await harness.Completion.WaitForCountAsync(250, TimeSpan.FromSeconds(30));
    await harness.FirstCycle.WaitAsync(TimeSpan.FromSeconds(30));

    await Assert.That(publish.PublishedIds).IsEquivalentTo(rows.Select(r => r.MessageId), TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("the whole stream publishes in its own order across the claimed run and every continued run");
    await Assert.That(coord.ContinueCursors.Select(c => c.Single().LastPublishedMessageId))
      .IsEquivalentTo([rows[99].MessageId, rows[199].MessageId, rows[249].MessageId], TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("each round continues from the last row published, so a row awaiting its completion is never returned again, "
             + "and the stream moved on three times inside one claim offer");
    await Assert.That(coord.ContinueRunLengths.Distinct()).IsEquivalentTo([100])
      .Because("a continuation round leases one drain page");
  }

  [Test]
  public async Task AFailureMidRun_StopsTheStream_AndItIsNeverContinuedAsync() {
    var stream = TrackedGuid.New().Value;
    var healthy = TrackedGuid.New().Value;
    var rows = _rows(stream, 10);
    var healthyRows = _rows(healthy, 3);
    var coord = new RunCoordinator();
    coord.Leased[stream] = rows;
    coord.Leased[healthy] = healthyRows;
    var publish = new ScriptedPublishStrategy(bulk: false) { FailIds = [rows[3].MessageId] };
    using var harness = _start(coord, publish, new OutboxDrainWorkerOptions { MaxPerStream = 100 }, stream, healthy);
    await harness.FirstCycle.WaitAsync(TimeSpan.FromSeconds(30));

    await Assert.That(publish.AttemptedIds.Where(id => rows.Any(r => r.MessageId == id)))
      .IsEquivalentTo(rows.Take(4).Select(r => r.MessageId), TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("the stream stops at its failed row; the six behind it must not go out ahead of its retry");
    await Assert.That(harness.Failure.All.Select(f => f.MessageId)).IsEquivalentTo([rows[3].MessageId]);
    await Assert.That(coord.ContinueCursors.SelectMany(c => c).Select(c => c.StreamId)).IsEquivalentTo([healthy])
      .Because("a stream with a failure is never continued; its sibling in the same cycle is");
  }

  [Test]
  public async Task APrePublishFailureOnABulkTransport_WithholdsTheRestOfTheStreamAsync() {
    var stream = TrackedGuid.New().Value;
    var other = TrackedGuid.New().Value;
    var rows = _rows(stream, 6);
    rows[2] = rows[2] with { EventData = "{ not an envelope" };   // cannot be deserialized
    var otherRows = _rows(other, 2);
    var coord = new RunCoordinator();
    coord.Leased[stream] = rows;
    coord.Leased[other] = otherRows;
    var publish = new ScriptedPublishStrategy(bulk: true);
    using var harness = _start(coord, publish, new OutboxDrainWorkerOptions { MaxPerStream = 100, MaxPublishBatchSize = 100 }, stream, other);
    await harness.FirstCycle.WaitAsync(TimeSpan.FromSeconds(30));

    await Assert.That(publish.AttemptedIds.Where(id => rows.Any(r => r.MessageId == id)))
      .IsEquivalentTo(rows.Take(2).Select(r => r.MessageId), TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("rows 4-6 sat behind a row that failed before the transport call; they wait for its retry");
    await Assert.That(publish.AttemptedIds.Count(id => otherRows.Any(r => r.MessageId == id))).IsEqualTo(2)
      .Because("one stream's failure never holds back another stream in the same batch");
    await Assert.That(harness.Failure.All.Select(f => f.MessageId)).IsEquivalentTo([rows[2].MessageId]);
  }

  [Test]
  public async Task AFailureOnABulkTransport_StopsTheStreamsNextPageAsync() {
    // Pages of five, shipped as soon as they are queued (batch size 2). Row 2 fails at the transport,
    // so the stream's second page must not be published.
    var stream = TrackedGuid.New().Value;
    var rows = _rows(stream, 10);
    var coord = new RunCoordinator { FetchPagesInOrder = true };
    coord.Leased[stream] = rows;
    var publish = new ScriptedPublishStrategy(bulk: true) { FailIds = [rows[1].MessageId] };
    using var harness = _start(coord, publish, new OutboxDrainWorkerOptions { MaxPerStream = 5, MaxPublishBatchSize = 2 }, stream);
    await harness.FirstCycle.WaitAsync(TimeSpan.FromSeconds(30));

    await Assert.That(publish.AttemptedIds)
      .IsEquivalentTo(rows.Take(5).Select(r => r.MessageId), TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("the first page had already gone to the transport as one call; nothing after it may follow the failure");
    await Assert.That(coord.FetchCalls).IsEqualTo(2)
      .Because("the full first page asked for the stream's tail, and the tail was held back rather than published");
    await Assert.That(coord.ContinueCursors).IsEmpty();
  }

  [Test]
  public async Task ContinuationSwitchedOff_LeavesTheStreamToTheClaimCycleAsync() {
    var stream = TrackedGuid.New().Value;
    var rows = _rows(stream, 3);
    var coord = new RunCoordinator();
    coord.Leased[stream] = rows;
    coord.Pending[stream] = _rows(stream, 3);
    var publish = new ScriptedPublishStrategy(bulk: false);
    using var harness = _start(coord, publish, new OutboxDrainWorkerOptions { ContinueStreamRuns = false }, stream);
    await harness.FirstCycle.WaitAsync(TimeSpan.FromSeconds(30));

    await Assert.That(publish.AttemptedIds.Count).IsEqualTo(3);
    await Assert.That(coord.ContinueCursors).IsEmpty()
      .Because("switched off, a stream moves one claimed run per claim cycle, the previous behavior");
  }

  [Test]
  public async Task ContinuationRounds_StopAtTheirLimitAsync() {
    var stream = TrackedGuid.New().Value;
    var rows = _rows(stream, 30);
    var coord = new RunCoordinator();
    coord.Leased[stream] = [.. rows.Take(10)];
    coord.Pending[stream] = [.. rows.Skip(10)];
    var publish = new ScriptedPublishStrategy(bulk: false);
    using var harness = _start(coord, publish, new OutboxDrainWorkerOptions { MaxPerStream = 10, MaxContinuationRounds = 1 }, stream);
    await harness.FirstCycle.WaitAsync(TimeSpan.FromSeconds(30));

    await Assert.That(coord.ContinueCursors.Count).IsEqualTo(1);
    await Assert.That(publish.AttemptedIds.Count).IsEqualTo(20)
      .Because("past the round limit the claim cycle carries the stream on, so other streams are not held waiting");
  }

  [Test]
  public async Task ASingletonRow_HasNoStreamToContinueAsync() {
    // Both shapes of "no stream": a NULL stream id and the empty one. The drain keys each by its
    // message id.
    var coord = new RunCoordinator();
    var messageId = TrackedGuid.New().Value;
    var emptyStreamMessageId = TrackedGuid.New().Value;
    coord.Leased[messageId] = [_row(messageId, streamId: null)];
    coord.Leased[emptyStreamMessageId] = [_row(emptyStreamMessageId, streamId: Guid.Empty)];
    var publish = new ScriptedPublishStrategy(bulk: false);
    using var harness = _start(coord, publish, new OutboxDrainWorkerOptions(), messageId, emptyStreamMessageId);
    await harness.FirstCycle.WaitAsync(TimeSpan.FromSeconds(30));

    await Assert.That(publish.AttemptedIds).IsEquivalentTo([messageId, emptyStreamMessageId]);
    await Assert.That(coord.ContinueCursors).IsEmpty()
      .Because("a row without a stream is looked up by its own id and has nothing behind it");
  }

  [Test]
  public async Task ABulkTransportReportingResultsOutOfOrder_StillContinuesFromTheStreamsLastRowAsync() {
    var stream = TrackedGuid.New().Value;
    var rows = _rows(stream, 20);
    var coord = new RunCoordinator();
    coord.Leased[stream] = [.. rows.Take(10)];
    coord.Pending[stream] = [.. rows.Skip(10)];
    var publish = new ScriptedPublishStrategy(bulk: true) { ReverseResults = true };
    using var harness = _start(coord, publish, new OutboxDrainWorkerOptions { MaxPerStream = 10, MaxPublishBatchSize = 100 }, stream);
    await harness.FirstCycle.WaitAsync(TimeSpan.FromSeconds(30));

    await Assert.That(coord.ContinueCursors[0].Single().LastPublishedMessageId).IsEqualTo(rows[9].MessageId)
      .Because("the cursor is the stream's LAST row published, whatever order the transport reported the results in");
  }

  // --- fakes ---------------------------------------------------------------------------------------

  private static readonly JsonSerializerOptions _jsonOpts = Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions();

  private static List<OutboxBatchRow> _rows(Guid stream, int count) =>
    [.. Enumerable.Range(0, count).Select(_ => TrackedGuid.New().Value).Order().Select(id => _row(id, stream))];

  private static OutboxBatchRow _row(Guid messageId, Guid? streamId) {
    var envelope = new MessageEnvelope<JsonElement> {
      MessageId = MessageId.From(messageId),
      Payload = JsonDocument.Parse("{}").RootElement,
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local },
      Hops = [],
    };
    var typeInfo = _jsonOpts.GetTypeInfo(typeof(MessageEnvelope<JsonElement>))
      ?? throw new InvalidOperationException("Test setup: no JsonTypeInfo for MessageEnvelope<JsonElement>");
    return new OutboxBatchRow {
      MessageId = messageId,
      StreamId = streamId,
      Destination = "test-topic",
      MessageType = "TestMessage",
      EnvelopeType = typeof(MessageEnvelope<JsonElement>).AssemblyQualifiedName ?? "MessageEnvelope",
      EventData = JsonSerializer.Serialize(envelope, typeInfo),
      Metadata = "{}",
      Status = 1,
      PartitionNumber = 0,
    };
  }

  /// <summary>
  /// A store holding, per stream, the run the claim leased and the rest of the stream. The fetch returns
  /// the leased run; a continuation leases the next page after the drain's cursor and returns it.
  /// </summary>
  private sealed class RunCoordinator : IWorkCoordinator {
    private readonly Lock _lock = new();
    public ConcurrentDictionary<Guid, List<OutboxBatchRow>> Leased { get; } = new();
    public ConcurrentDictionary<Guid, List<OutboxBatchRow>> Pending { get; } = new();
    public List<IReadOnlyList<OutboxStreamCursor>> ContinueCursors { get; } = [];
    public List<int> ContinueRunLengths { get; } = [];
    public int FetchCalls;

    /// <summary>When true, each fetch of a stream returns its next page of the leased run rather than its first.</summary>
    public bool FetchPagesInOrder { get; init; }
    private readonly Dictionary<Guid, int> _pageOffset = [];

    public Task<IReadOnlyList<OutboxBatchRow>> FetchOutboxBatchAsync(
        IReadOnlyList<Guid> streamIds, Guid instanceId, int maxPerStream = 100, CancellationToken cancellationToken = default) {
      Interlocked.Increment(ref FetchCalls);
      var result = new List<OutboxBatchRow>();
      lock (_lock) {
        foreach (var sid in streamIds) {
          if (!Leased.TryGetValue(sid, out var rows)) {
            continue;
          }
          var offset = FetchPagesInOrder ? _pageOffset.GetValueOrDefault(sid) : 0;
          var page = rows.Skip(offset).Take(maxPerStream).ToList();
          if (FetchPagesInOrder) {
            _pageOffset[sid] = offset + page.Count;
          }
          result.AddRange(page);
        }
      }
      return Task.FromResult<IReadOnlyList<OutboxBatchRow>>(result);
    }

    public Task<IReadOnlyList<OutboxBatchRow>> ContinueOutboxStreamsAsync(
        IReadOnlyList<OutboxStreamCursor> streams, Guid instanceId, int runLength, long? maxBytes,
        CancellationToken cancellationToken = default) {
      var result = new List<OutboxBatchRow>();
      lock (_lock) {
        ContinueCursors.Add([.. streams]);
        ContinueRunLengths.Add(runLength);
        foreach (var cursor in streams) {
          if (!Pending.TryGetValue(cursor.StreamId, out var pending)) {
            continue;
          }
          var next = pending.Take(runLength).ToList();
          pending.RemoveRange(0, next.Count);
          result.AddRange(next);
        }
      }
      return Task.FromResult<IReadOnlyList<OutboxBatchRow>>(result);
    }

    public Task<Guid> GetLocalServiceIdAsync(CancellationToken cancellationToken = default) => Task.FromResult(Guid.Empty);
    public Task<WorkBatch> ClaimWorkAsync(ClaimWorkRequest request, CancellationToken cancellationToken = default) =>
      Task.FromResult(new WorkBatch { OutboxWork = [], InboxWork = [], PerspectiveWork = [] });
    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion completion, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure failure, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new WorkCoordinatorStatistics());
    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default) =>
      Task.FromResult<PerspectiveCursorInfo?>(null);
  }

  /// <summary>Publishes everything except <see cref="FailIds"/>, recording what reached it, in order.</summary>
  private sealed class ScriptedPublishStrategy(bool bulk) : IMessagePublishStrategy {
    private readonly Lock _lock = new();
    private readonly List<Guid> _attempted = [];
    public HashSet<Guid> FailIds { get; init; } = [];
    /// <summary>When true, a bulk publish reports its results last-to-first.</summary>
    public bool ReverseResults { get; init; }
    public bool SupportsBulkPublish => bulk;
    public List<Guid> AttemptedIds { get { lock (_lock) { return [.. _attempted]; } } }
    public List<Guid> PublishedIds { get { lock (_lock) { return [.. _attempted.Where(id => !FailIds.Contains(id))]; } } }
    public Task<bool> IsReadyAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);

    public Task<MessagePublishResult> PublishAsync(OutboxWork work, CancellationToken cancellationToken) {
      lock (_lock) { _attempted.Add(work.MessageId); }
      return Task.FromResult(_result(work.MessageId));
    }

    public Task<IReadOnlyList<MessagePublishResult>> PublishBatchAsync(IReadOnlyList<OutboxWork> workItems, CancellationToken cancellationToken) {
      lock (_lock) { _attempted.AddRange(workItems.Select(w => w.MessageId)); }
      var results = workItems.Select(w => _result(w.MessageId));
      return Task.FromResult<IReadOnlyList<MessagePublishResult>>([.. ReverseResults ? results.Reverse() : results]);
    }

    private MessagePublishResult _result(Guid messageId) => new() {
      MessageId = messageId,
      Success = !FailIds.Contains(messageId),
      CompletedStatus = MessageProcessingStatus.Published,
      Error = FailIds.Contains(messageId) ? "broker refused" : null,
    };
  }

  private sealed class CountingCompletionChannel : IOutboxCompletionChannel {
    private readonly Lock _lock = new();
    private int _count;
    private int _target = int.MaxValue;
    private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ValueTask EnqueueAsync(Guid outboxMessageId, CancellationToken cancellationToken = default) {
      lock (_lock) {
        _count++;
        if (_count >= _target) {
          _reached.TrySetResult();
        }
      }
      return ValueTask.CompletedTask;
    }

    public Task WaitForCountAsync(int count, TimeSpan timeout) {
      lock (_lock) {
        _target = count;
        if (_count >= count) {
          return Task.CompletedTask;
        }
      }
      return _reached.Task.WaitAsync(timeout);
    }
  }

  private sealed class RecordingFailureChannel : IFailureChannel {
    public ConcurrentQueue<MessageFailure> All { get; } = new();
    public ValueTask EnqueueAsync(WorkCategory category, MessageFailure failure, CancellationToken cancellationToken = default) {
      All.Enqueue(failure);
      return ValueTask.CompletedTask;
    }
  }

  private sealed class DrainChannel : IOutboxDrainChannel {
    private readonly Channel<Guid> _channel = Channel.CreateUnbounded<Guid>();
    public ChannelReader<Guid> Reader => _channel.Reader;
    public ValueTask WriteAsync(Guid streamId, CancellationToken cancellationToken = default) => _channel.Writer.WriteAsync(streamId, cancellationToken);
    public bool TryWrite(Guid streamId) => _channel.Writer.TryWrite(streamId);
  }

  private sealed class Instance : IServiceInstanceProvider {
    public Guid InstanceId { get; } = TrackedGuid.New().Value;
    public string ServiceName => "test-svc";
    public string HostName => "test-host";
    public int ProcessId => 1;
    public ServiceInstanceInfo ToInfo() => new() { InstanceId = InstanceId, ServiceName = ServiceName, HostName = HostName, ProcessId = ProcessId };
  }

  private sealed class Harness(OutboxDrainWorker worker, CountingCompletionChannel completion,
      RecordingFailureChannel failure, CancellationTokenSource cts, Task firstCycle) : IDisposable {
    /// <summary>The end of the first drain cycle (continuation rounds included): the worker's first active-to-idle edge.</summary>
    public Task FirstCycle => firstCycle;
    public CountingCompletionChannel Completion => completion;
    public RecordingFailureChannel Failure => failure;

    public void Dispose() {
      cts.Cancel();
      try { worker.StopAsync(CancellationToken.None).GetAwaiter().GetResult(); } catch (OperationCanceledException) { /* teardown */ }
      cts.Dispose();
    }
  }

  /// <summary>
  /// Starts a worker with the claim's offers already queued, so they arrive as ONE drain batch, and a
  /// signal on the end of that batch's cycle subscribed before the worker runs.
  /// </summary>
  private static Harness _start(RunCoordinator coord, ScriptedPublishStrategy publish, OutboxDrainWorkerOptions options, params Guid[] offers) {
    options.Enabled = true;
    var services = new ServiceCollection();
    services.TryAddWhizbangDefaults();
    services.AddSingleton<IWorkCoordinator>(coord);
    var sp = services.BuildServiceProvider();
    var gate = new SchemaReadyGate();
    gate.MarkReady();
    var channel = new DrainChannel();
    var completion = new CountingCompletionChannel();
    var failure = new RecordingFailureChannel();
    var worker = new OutboxDrainWorker(
      scopeFactory: sp.GetRequiredService<IServiceScopeFactory>(),
      instanceProvider: new Instance(),
      drainChannel: channel,
      completionChannel: completion,
      failureChannel: failure,
      schemaReadyGate: gate,
      options: Options.Create(options),
      jsonOptions: _jsonOpts,
      logger: NullLogger<OutboxDrainWorker>.Instance,
      publishStrategy: publish,
      lifecycleMessageDeserializer: new JsonLifecycleMessageDeserializer(),
      receptorRegistry: new PermissiveReceptorRegistryQuery(),
      runtimeReceptorRegistry: NullReceptorRegistry.Instance,
      deadLetterStore: NullDeadLetterStore.Instance,
      generationProvider: new DefaultGenerationProvider(),
      governor: OutboxDrainWorker.CreateDefaultGovernor(options));
    foreach (var offer in offers) {
      channel.TryWrite(offer);
    }
    var firstCycle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    worker.OnWorkProcessingIdle += () => firstCycle.TrySetResult();
    var cts = new CancellationTokenSource();
    worker.StartAsync(cts.Token).GetAwaiter().GetResult();
    return new Harness(worker, completion, failure, cts, firstCycle.Task);
  }
}
