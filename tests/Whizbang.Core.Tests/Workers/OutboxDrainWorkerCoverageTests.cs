using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Security;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Coverage for <see cref="OutboxDrainWorker"/> paths the primary suites (<see
/// cref="OutboxDrainWorkerTests"/>, <see cref="OutboxDrainWorkerGapTests"/>, <see
/// cref="OutboxDrainWorkerLifecycleFailureTests"/>, <see cref="SecurityContextTimeoutTests"/>,
/// <see cref="PublishTimeoutTests"/>) don't reach: cancellation observed during the startup
/// local-service-id lookup, the loop-until-empty inner path's own zero-row confirmation exit and
/// duplicate-refetch skip, cancellation landing exactly at the inner loop's own iteration
/// boundary, the bulk publish path's security-context SUCCESS branch (receptor invoker
/// resolution), the singular publish path's security-context TIMEOUT branch, the abandoned-
/// publish-task observe-only continuations on both publish paths actually running, and the
/// lifecycle-stage helper's event-store-only (null destination) skip.
/// </summary>
/// <docs>fundamentals/work-coordinator/per-stream-drain</docs>
[NotInParallel("WhizbangBackgroundServiceTests")]
public class OutboxDrainWorkerCoverageTests {

  // --- fakes: channels / instance provider ---

  private sealed class _DrainChannel : IOutboxDrainChannel {
    private readonly Channel<Guid> _channel = Channel.CreateUnbounded<Guid>();
    public ChannelReader<Guid> Reader => _channel.Reader;
    public ValueTask WriteAsync(Guid streamId, CancellationToken ct = default) => _channel.Writer.WriteAsync(streamId, ct);
    public bool TryWrite(Guid streamId) => _channel.Writer.TryWrite(streamId);
    public void Complete() => _channel.Writer.Complete();
  }

  private sealed class _CompletionChannel : IOutboxCompletionChannel {
    public ConcurrentBag<Guid> AllIds { get; } = [];
    public ValueTask EnqueueAsync(Guid id, CancellationToken ct = default) {
      AllIds.Add(id);
      return ValueTask.CompletedTask;
    }
  }

  private sealed class _FailureChannel : IFailureChannel {
    public ConcurrentBag<MessageFailure> All { get; } = [];
    public ValueTask EnqueueAsync(WorkCategory category, MessageFailure failure, CancellationToken ct = default) {
      All.Add(failure);
      return ValueTask.CompletedTask;
    }
  }

  private sealed class _ServiceInstanceProvider : IServiceInstanceProvider {
    public Guid InstanceId { get; } = (Guid)TrackedGuid.NewMedo();
    public string ServiceName => "coverage-test-svc";
    public string HostName => "coverage-test-host";
    public int ProcessId => 1;
    public ServiceInstanceInfo ToInfo() => new() {
      InstanceId = InstanceId,
      ServiceName = ServiceName,
      HostName = HostName,
      ProcessId = ProcessId,
    };
  }

  // --- fakes: coordinators ---

  /// <summary>
  /// Base coordinator relying on <see cref="IWorkCoordinator"/>'s own default bodies for every
  /// member <see cref="OutboxDrainWorker"/> doesn't call — it only ever calls
  /// <see cref="IWorkCoordinator.GetLocalServiceIdAsync"/> and the byte-budgeted
  /// <see cref="IWorkCoordinator.FetchOutboxBatchAsync(IReadOnlyList{Guid},Guid,int,long?,CancellationToken)"/>
  /// overload, so only those two need overriding per scenario.
  /// </summary>
  // Extends the shared NoOpWorkCoordinator rather than reimplementing IWorkCoordinator: the
  // interface has many members this test does not care about, and re-declaring them here would
  // break every time one is added.
  private class _CoordinatorBase : NoOpWorkCoordinator, IWorkCoordinator {
    public virtual Task<IReadOnlyList<OutboxBatchRow>> FetchOutboxBatchAsync(
        IReadOnlyList<Guid> streamIds, Guid instanceId, int maxPerStream, long? maxBytes, CancellationToken ct = default) =>
      Task.FromResult<IReadOnlyList<OutboxBatchRow>>([]);

    // Declared virtual here (rather than left to IWorkCoordinator's own default body) so a
    // derived fake can actually override it: a same-named method added only on a subclass does
    // NOT participate in interface dispatch unless the base class's own implementation is
    // virtual — calls through the IWorkCoordinator-typed reference DI hands to the worker would
    // otherwise still resolve to the interface's default and silently skip the override.
    public virtual Task<Guid> GetLocalServiceIdAsync(CancellationToken ct = default) => Task.FromResult(Guid.Empty);
  }

  /// <summary>Consumes returned rows on each fetch — mimics post-completion DELETE, so a
  /// backlog that is an exact multiple of the per-stream cap eventually returns zero rows.</summary>
  private sealed class _ConsumingCoordinator : _CoordinatorBase {
    public Dictionary<Guid, List<OutboxBatchRow>> RowsByStream { get; } = [];
    public int FetchCalls;
    public override Task<IReadOnlyList<OutboxBatchRow>> FetchOutboxBatchAsync(
        IReadOnlyList<Guid> streamIds, Guid instanceId, int maxPerStream, long? maxBytes, CancellationToken ct = default) {
      Interlocked.Increment(ref FetchCalls);
      var result = new List<OutboxBatchRow>();
      lock (RowsByStream) {
        foreach (var sid in streamIds) {
          if (RowsByStream.TryGetValue(sid, out var rows)) {
            var taken = rows.Take(maxPerStream).ToList();
            result.AddRange(taken);
            rows.RemoveRange(0, taken.Count);
          }
        }
      }
      return Task.FromResult<IReadOnlyList<OutboxBatchRow>>(result);
    }
  }

  /// <summary>Returns the SAME fixed row set on every call — never consumes — so a refetch at
  /// the exact per-stream cap always looks identical to the previous fetch.</summary>
  private sealed class _StaticRowsCoordinator : _CoordinatorBase {
    public List<OutboxBatchRow> Rows { get; } = [];
    public int FetchCalls;
    public override Task<IReadOnlyList<OutboxBatchRow>> FetchOutboxBatchAsync(
        IReadOnlyList<Guid> streamIds, Guid instanceId, int maxPerStream, long? maxBytes, CancellationToken ct = default) {
      Interlocked.Increment(ref FetchCalls);
      return Task.FromResult<IReadOnlyList<OutboxBatchRow>>([.. Rows.Take(maxPerStream)]);
    }
  }

  /// <summary>
  /// A coordinator whose successive fetch responses are individually scripted, with an optional
  /// <see cref="AfterCall"/> hook that runs once a call's response is computed but before it's
  /// returned — lets a test cancel the worker's token at the exact point a real host would still
  /// be racing the next fetch.
  /// </summary>
  private sealed class _ScriptedCoordinator : _CoordinatorBase {
    private readonly List<Func<IReadOnlyList<Guid>, IReadOnlyList<OutboxBatchRow>>> _responses = [];
    public int FetchCalls;
    public Action<int>? AfterCall { get; set; }
    public void Enqueue(Func<IReadOnlyList<Guid>, IReadOnlyList<OutboxBatchRow>> respond) => _responses.Add(respond);
    public override Task<IReadOnlyList<OutboxBatchRow>> FetchOutboxBatchAsync(
        IReadOnlyList<Guid> streamIds, Guid instanceId, int maxPerStream, long? maxBytes, CancellationToken ct = default) {
      var n = Interlocked.Increment(ref FetchCalls);
      IReadOnlyList<OutboxBatchRow> response = n <= _responses.Count ? _responses[n - 1](streamIds) : [];
      AfterCall?.Invoke(n);
      return Task.FromResult(response);
    }
  }

  /// <summary>Simulates the local-service-identity lookup itself observing a stopping-token
  /// driven cancellation before the worker ever reaches its main drain loop.</summary>
  private sealed class _CoordinatorCancelsIdentityLookup : _CoordinatorBase {
    public int FetchCalls;
    public override Task<IReadOnlyList<OutboxBatchRow>> FetchOutboxBatchAsync(
        IReadOnlyList<Guid> streamIds, Guid instanceId, int maxPerStream, long? maxBytes, CancellationToken ct = default) {
      Interlocked.Increment(ref FetchCalls);
      return Task.FromResult<IReadOnlyList<OutboxBatchRow>>([]);
    }
    public override Task<Guid> GetLocalServiceIdAsync(CancellationToken ct = default) =>
      Task.FromException<Guid>(new OperationCanceledException("simulated stoppingToken-driven cancellation during identity lookup"));
  }

  // --- fakes: publish strategies ---

  private sealed class _PublishStrategy : IMessagePublishStrategy {
    public ConcurrentQueue<OutboxWork> Published { get; } = new();
    public int TargetCount { get; set; } = 1;
    public TaskCompletionSource ReachedCount { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task<bool> IsReadyAsync(CancellationToken ct = default) => Task.FromResult(true);
    public Task<MessagePublishResult> PublishAsync(OutboxWork work, CancellationToken ct) {
      Published.Enqueue(work);
      if (Published.Count >= TargetCount) {
        ReachedCount.TrySetResult();
      }
      return Task.FromResult(new MessagePublishResult {
        MessageId = work.MessageId,
        Success = true,
        CompletedStatus = MessageProcessingStatus.Published,
      });
    }
  }

  /// <summary>Fails the test loudly (via a propagated exception) if publish is ever reached —
  /// used to prove an early-return branch truly short-circuits before publishing.</summary>
  private sealed class _ThrowIfCalledPublishStrategy : IMessagePublishStrategy {
    public Task<bool> IsReadyAsync(CancellationToken ct = default) => Task.FromResult(true);
    public Task<MessagePublishResult> PublishAsync(OutboxWork work, CancellationToken ct) =>
      throw new InvalidOperationException("PublishAsync must not be called in this scenario");
  }

  private sealed class _BulkSuccessStrategy : IMessagePublishStrategy {
    public List<IReadOnlyList<OutboxWork>> BatchCalls { get; } = [];
    public bool SupportsBulkPublish => true;
    public Task<bool> IsReadyAsync(CancellationToken ct = default) => Task.FromResult(true);
    public Task<MessagePublishResult> PublishAsync(OutboxWork work, CancellationToken ct) =>
      throw new InvalidOperationException("PublishAsync must not be called on a bulk-capable strategy");
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

  /// <summary>Bulk strategy whose <see cref="PublishBatchAsync"/> returns a task the test faults
  /// manually, on its own schedule, after the worker has already abandoned it via timeout.</summary>
  private sealed class _HangingBulkStrategyManualFault(TaskCompletionSource<IReadOnlyList<MessagePublishResult>> tcs) : IMessagePublishStrategy {
    public bool SupportsBulkPublish => true;
    public Task<bool> IsReadyAsync(CancellationToken ct = default) => Task.FromResult(true);
    public Task<MessagePublishResult> PublishAsync(OutboxWork work, CancellationToken ct) =>
      throw new InvalidOperationException("PublishAsync must not be called on a bulk-capable strategy");
    public Task<IReadOnlyList<MessagePublishResult>> PublishBatchAsync(IReadOnlyList<OutboxWork> works, CancellationToken ct) => tcs.Task;
  }

  /// <summary>Singular counterpart of <see cref="_HangingBulkStrategyManualFault"/>.</summary>
  private sealed class _HangingSingleStrategyManualFault(TaskCompletionSource<MessagePublishResult> tcs) : IMessagePublishStrategy {
    public Task<bool> IsReadyAsync(CancellationToken ct = default) => Task.FromResult(true);
    public Task<MessagePublishResult> PublishAsync(OutboxWork work, CancellationToken ct) => tcs.Task;
  }

  // --- fakes: lifecycle / security context ---

  private sealed class _PassthroughDeserializer : ILifecycleMessageDeserializer {
    public object DeserializeFromEnvelope(IMessageEnvelope<JsonElement> envelope, string envelopeTypeName) => envelope.Payload;
    public object DeserializeFromEnvelope(IMessageEnvelope<JsonElement> envelope) => envelope.Payload;
    public object DeserializeFromBytes(byte[] jsonBytes, string messageTypeName) => jsonBytes;
    public object DeserializeFromJsonElement(JsonElement payload, string messageTypeName) => payload;
  }

  /// <summary>Simulates a consumer's hung provider — blocks until its own cancellation token
  /// fires. Slice 5a's per-call timeout must trigger that cancellation; without it, this hangs
  /// forever and the test times out.</summary>
  private sealed class _HangingSecurityContextProvider : IMessageSecurityContextProvider {
    public async ValueTask<IScopeContext?> EstablishContextAsync(
        IMessageEnvelope envelope, IServiceProvider scopedProvider, CancellationToken cancellationToken = default) {
      await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
      return null;
    }
  }

  private sealed class _CapturingReceptorInvoker : IReceptorInvoker {
    private readonly List<LifecycleStage> _stages = [];
    private readonly Lock _lock = new();
    public List<LifecycleStage> Stages {
      get { lock (_lock) { return [.. _stages]; } }
    }
    public ValueTask InvokeAsync(IMessageEnvelope envelope, LifecycleStage stage, ILifecycleContext? context = null, CancellationToken cancellationToken = default) {
      lock (_lock) { _stages.Add(stage); }
      return ValueTask.CompletedTask;
    }
  }

  // --- helpers ---

  private static readonly JsonSerializerOptions _jsonOpts = Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions();

  private static OutboxBatchRow _row(Guid messageId, Guid streamId, int attempts = 0) {
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
      Destination = "coverage-test-topic",
      MessageType = "TestMessage",
      EnvelopeType = typeof(MessageEnvelope<JsonElement>).AssemblyQualifiedName ?? "MessageEnvelope",
      EventData = envelopeJson,
      Metadata = "{}",
      Scope = null,
      Status = 1,
      Attempts = attempts,
      PartitionNumber = 0,
      IsEvent = false,
    };
  }

  private static OutboxWork _work(Guid messageId, string? destination) {
    var envelope = new MessageEnvelope<JsonElement> {
      MessageId = MessageId.From(messageId),
      Payload = JsonDocument.Parse("{}").RootElement,
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local },
      Hops = [],
    };
    return new OutboxWork {
      MessageId = messageId,
      Destination = destination,
      MessageType = "TestMessage",
      EnvelopeType = typeof(MessageEnvelope<JsonElement>).AssemblyQualifiedName ?? "MessageEnvelope",
      Envelope = envelope,
      Attempts = 1,
      Status = MessageProcessingStatus.Stored,
    };
  }

  private static OutboxDrainWorker _buildDirectCallWorker(
      IFailureChannel failure,
      IOutboxCompletionChannel? completion = null,
      IMessagePublishStrategy? publish = null,
      OutboxDrainWorkerOptions? options = null,
      ILifecycleMessageDeserializer? deserializer = null,
      IServiceProvider? sp = null) {
    sp ??= new ServiceCollection().BuildServiceProvider();
    var gate = new SchemaReadyGate();
    gate.MarkReady();
    return new OutboxDrainWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      new _ServiceInstanceProvider(),
      new _DrainChannel(),
      completion ?? new _CompletionChannel(),
      failure,
      gate,
      Options.Create(options ?? new OutboxDrainWorkerOptions { Enabled = true, MaxPerStream = 100 }),
      _jsonOpts,
      NullLogger<OutboxDrainWorker>.Instance,
      publish ?? new _ThrowIfCalledPublishStrategy(),
      lifecycleMessageDeserializer: deserializer);
  }

  // --- tests ---

  /// <summary>
  /// If cancellation observed during the local-service-identity lookup regressed, a shutting-
  /// down worker would fall through into the main drain loop with an uninitialized
  /// _localServiceId instead of exiting cleanly — publishing could start using half-initialized
  /// worker state during shutdown rather than the worker simply stopping.
  /// </summary>
  [Test]
  public async Task ExecuteAsync_LocalServiceIdLookupObservesCancellation_ReturnsBeforeDrainingAsync() {
    var coord = new _CoordinatorCancelsIdentityLookup();
    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coord);
    var sp = services.BuildServiceProvider();
    var gate = new SchemaReadyGate();
    gate.MarkReady();

    var worker = new OutboxDrainWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      new _ServiceInstanceProvider(),
      new _DrainChannel(),
      new _CompletionChannel(),
      new _FailureChannel(),
      gate,
      Options.Create(new OutboxDrainWorkerOptions { Enabled = true }),
      _jsonOpts,
      NullLogger<OutboxDrainWorker>.Instance,
      new _ThrowIfCalledPublishStrategy());

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);

    await worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    await Assert.That(worker.ExecuteTask.IsCompleted).IsTrue()
      .Because("an OperationCanceledException from the identity lookup must end the worker, not hang");
    await Assert.That(worker.ExecuteTask.IsFaulted).IsFalse()
      .Because("the cancellation is caught and returned from cleanly, never surfaced as a fault");
    await Assert.That(coord.FetchCalls).IsEqualTo(0)
      .Because("returning before the main loop means FetchOutboxBatchAsync must never be reached");

    try { await worker.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }
  }

  /// <summary>
  /// If this early exit regressed, a stream whose backlog is an exact multiple of MaxPerStream
  /// would mishandle the confirmation fetch that legitimately returns zero rows — either looping
  /// on wasted fetches or failing to recognize the stream as fully drained.
  /// </summary>
  [Test]
  public async Task DrainStreamInner_ConfirmationFetchReturnsZeroRows_ExitsCleanlyAsync() {
    var streamId = (Guid)TrackedGuid.NewMedo();
    const int maxPerStream = 50;
    var msgs = Enumerable.Range(0, maxPerStream * 2).Select(_ => (Guid)TrackedGuid.NewMedo()).ToArray();

    var coord = new _ConsumingCoordinator();
    coord.RowsByStream[streamId] = [.. msgs.Select(m => _row(m, streamId))];

    var drainChannel = new _DrainChannel();
    var completion = new _CompletionChannel();
    var failure = new _FailureChannel();
    var publish = new _PublishStrategy { TargetCount = msgs.Length };
    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coord);
    var sp = services.BuildServiceProvider();
    var gate = new SchemaReadyGate();
    gate.MarkReady();

    var worker = new OutboxDrainWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      new _ServiceInstanceProvider(), drainChannel, completion, failure, gate,
      Options.Create(new OutboxDrainWorkerOptions { Enabled = true, MaxPerStream = maxPerStream }),
      _jsonOpts,
      NullLogger<OutboxDrainWorker>.Instance,
      publish);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await drainChannel.WriteAsync(streamId);

    await publish.ReachedCount.Task.WaitAsync(TimeSpan.FromSeconds(30));
    cts.Cancel();
    try { await worker.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }

    await Assert.That(publish.Published.Count).IsEqualTo(msgs.Length)
      .Because("every claimed row must publish exactly once even when the backlog is an exact multiple of the per-stream cap");
    await Assert.That(coord.FetchCalls).IsEqualTo(3)
      .Because("2 saturating fetches (100 rows / 50 cap) plus one confirmation fetch that returns zero rows — the exact branch under test");
  }

  /// <summary>
  /// If the seen-set dedup regressed, a stream whose completion-flush lags behind the drain
  /// would re-publish the same messages every time a refetch races ahead of the DELETE, turning
  /// a completion-flush lag into a duplicate-delivery storm.
  /// </summary>
  [Test]
  public async Task DrainStreamInner_RefetchAtExactCapReturnsSameRows_SkipsAlreadySeenAndExitsAsync() {
    var streamId = (Guid)TrackedGuid.NewMedo();
    const int maxPerStream = 5;
    var msgs = Enumerable.Range(0, maxPerStream).Select(_ => (Guid)TrackedGuid.NewMedo()).ToArray();

    var coord = new _StaticRowsCoordinator();
    coord.Rows.AddRange(msgs.Select(m => _row(m, streamId)));

    var drainChannel = new _DrainChannel();
    var completion = new _CompletionChannel();
    var failure = new _FailureChannel();
    var publish = new _PublishStrategy { TargetCount = maxPerStream };
    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coord);
    var sp = services.BuildServiceProvider();
    var gate = new SchemaReadyGate();
    gate.MarkReady();

    var worker = new OutboxDrainWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      new _ServiceInstanceProvider(), drainChannel, completion, failure, gate,
      Options.Create(new OutboxDrainWorkerOptions { Enabled = true, MaxPerStream = maxPerStream }),
      _jsonOpts,
      NullLogger<OutboxDrainWorker>.Instance,
      publish);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await drainChannel.WriteAsync(streamId);

    await publish.ReachedCount.Task.WaitAsync(TimeSpan.FromSeconds(30));
    // Give the inner loop's own re-fetch (which finds only duplicates) a chance to run before
    // asserting no further, spurious publishes happened — mirrors the established "give a
    // chance for a spurious second pass" pattern already used in OutboxDrainWorkerTests.
    await Task.Delay(200);
    cts.Cancel();
    try { await worker.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }

    await Assert.That(publish.Published.Count).IsEqualTo(maxPerStream)
      .Because("the same 5 rows returned again on refetch must never be re-published");
    await Assert.That(coord.FetchCalls).IsEqualTo(2)
      .Because("prefetch (5 rows, saturating) plus one inner re-fetch that finds only already-seen duplicates and exits");
  }

  /// <summary>
  /// If the inner loop only re-checked cancellation somewhere other than its own iteration
  /// boundary, a canceled shutdown mid-drain would keep issuing fetches for a stream with a long
  /// backlog instead of stopping at the next natural boundary — ignoring host shutdown.
  /// </summary>
  [Test]
  public async Task DrainStreamInner_CanceledBetweenInnerFetches_StopsAtNextIterationBoundaryAsync() {
    var streamId = (Guid)TrackedGuid.NewMedo();
    const int maxPerStream = 2;
    var firstPassMsgs = Enumerable.Range(0, maxPerStream).Select(_ => (Guid)TrackedGuid.NewMedo()).ToArray();
    var secondPassMsgs = Enumerable.Range(0, maxPerStream).Select(_ => (Guid)TrackedGuid.NewMedo()).ToArray();

    var coord = new _ScriptedCoordinator();
    using var cts = new CancellationTokenSource();
    // Prefetch (the batch-level fetch): saturates the cap, handing the stream to the inner loop.
    coord.Enqueue(_ => [.. firstPassMsgs.Select(m => _row(m, streamId))]);
    // Inner loop's own first fetch: also saturates the cap — the loop would normally go around
    // again, except cancellation lands right after this call resolves.
    coord.Enqueue(_ => [.. secondPassMsgs.Select(m => _row(m, streamId))]);
    coord.AfterCall = call => {
      if (call == 2) {
        cts.Cancel();
      }
    };

    var drainChannel = new _DrainChannel();
    var completion = new _CompletionChannel();
    var failure = new _FailureChannel();
    var publish = new _PublishStrategy { TargetCount = maxPerStream * 2 };
    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coord);
    var sp = services.BuildServiceProvider();
    var gate = new SchemaReadyGate();
    gate.MarkReady();

    var worker = new OutboxDrainWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      new _ServiceInstanceProvider(), drainChannel, completion, failure, gate,
      Options.Create(new OutboxDrainWorkerOptions { Enabled = true, MaxPerStream = maxPerStream }),
      _jsonOpts,
      NullLogger<OutboxDrainWorker>.Instance,
      publish);

    await worker.StartAsync(cts.Token);
    await drainChannel.WriteAsync(streamId);

    // The worker's own completion is the signal — how much of the second page lands before
    // cancellation is observed is a scheduling detail, not something to assert an exact count on.
    await worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    await Assert.That(worker.ExecuteTask.IsCompleted).IsTrue();
    await Assert.That(worker.ExecuteTask.IsFaulted).IsFalse()
      .Because("the inner loop ending via its own while-condition on cancellation must still shut the worker down cleanly");

    await Assert.That(coord.FetchCalls).IsEqualTo(2)
      .Because("once canceled, the loop must stop at the next iteration boundary rather than issuing a further fetch");
    await Assert.That(publish.Published.Count).IsGreaterThanOrEqualTo(maxPerStream)
      .Because("the first page was already in hand and fully published before cancellation could be observed");

    try { await worker.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }
  }

  /// <summary>
  /// If the bulk path's security-context SUCCESS branch regressed — e.g. failing to fetch the
  /// receptor invoker after establishing context — every row with both a resolvable typed
  /// envelope and a destination would silently skip its Pre/PostOutbox lifecycle stages even
  /// though nothing failed: an invisible feature regression with no error and no log line.
  /// </summary>
  [Test]
  public async Task PublishBulkAsync_SecurityContextSucceeds_ResolvesReceptorInvokerAndFiresLifecycleAsync() {
    var failure = new _FailureChannel();
    var completion = new _CompletionChannel();
    var invoker = new _CapturingReceptorInvoker();
    var services = new ServiceCollection();
    services.AddSingleton<IReceptorInvoker>(invoker);
    var sp = services.BuildServiceProvider();

    var worker = _buildDirectCallWorker(
      failure,
      completion: completion,
      publish: new _BulkSuccessStrategy(),
      deserializer: new _PassthroughDeserializer(),
      sp: sp);

    var messageId = (Guid)TrackedGuid.NewMedo();
    var row = _row(messageId, (Guid)TrackedGuid.NewMedo());

    await worker.PublishBulkAsync([row], CancellationToken.None);

    await Assert.That(failure.All).IsEmpty()
      .Because("a fast-succeeding security context establishment must never be treated as a timeout");
    await Assert.That(invoker.Stages).Contains(LifecycleStage.PreOutboxInline)
      .Because("resolving the receptor invoker after a successful security-context establishment must let PreOutbox lifecycle fire");
    await Assert.That(completion.AllIds).Contains(messageId)
      .Because("the row must still complete normally once lifecycle and publish succeed");
  }

  /// <summary>
  /// If this early return regressed, a hung consumer-side IMessageSecurityContextProvider would
  /// let the singular publish path fall through to PublishAsync anyway — defeating the entire
  /// point of the timeout this branch enforces (the production incident this guarded against).
  /// </summary>
  [Test]
  public async Task PublishOneAsync_SecurityContextTimesOut_ReturnsWithoutPublishingAsync() {
    var failure = new _FailureChannel();
    var completion = new _CompletionChannel();
    var services = new ServiceCollection();
    services.AddSingleton<IMessageSecurityContextProvider>(new _HangingSecurityContextProvider());
    var sp = services.BuildServiceProvider();

    var worker = _buildDirectCallWorker(
      failure,
      completion: completion,
      publish: new _ThrowIfCalledPublishStrategy(),
      options: new OutboxDrainWorkerOptions { Enabled = true, MaxPerStream = 100, SecurityContextTimeoutSeconds = 1 },
      deserializer: new _PassthroughDeserializer(),
      sp: sp);

    var row = _row((Guid)TrackedGuid.NewMedo(), (Guid)TrackedGuid.NewMedo());

    await worker.PublishOneAsync(row, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

    await Assert.That(failure.All.Count).IsEqualTo(1)
      .Because("a timed-out security-context establishment must enqueue exactly one failure for the row");
    await Assert.That(failure.All.Single().Reason).IsEqualTo(MessageFailureReason.SecurityContextEstablishmentFailure);
    await Assert.That(completion.AllIds).IsEmpty()
      .Because("a row that never reaches publish must never be marked complete");
  }

  /// <summary>
  /// If this observe-only continuation regressed, an abandoned PublishBatchAsync call that later
  /// faults after its own timeout already fired would surface as an UnobservedTaskException at
  /// GC time — turning an already-handled timeout into a process-level exception on a completely
  /// unrelated stack, long after anyone was watching for it.
  /// </summary>
  [Test]
  public async Task PublishBulkAsync_AbandonedPublishTaskLaterFaults_NoUnobservedTaskExceptionAsync() {
    var failure = new _FailureChannel();
    var completion = new _CompletionChannel();
    var tcs = new TaskCompletionSource<IReadOnlyList<MessagePublishResult>>();
    var strategy = new _HangingBulkStrategyManualFault(tcs);

    var worker = _buildDirectCallWorker(
      failure,
      completion: completion,
      publish: strategy,
      options: new OutboxDrainWorkerOptions { Enabled = true, MaxPerStream = 100, PublishTimeoutSeconds = 1 });

    var row = _row((Guid)TrackedGuid.NewMedo(), (Guid)TrackedGuid.NewMedo());

    var bulkTask = worker.PublishBulkAsync([row], CancellationToken.None);
    await bulkTask.WaitAsync(TimeSpan.FromSeconds(5));

    await Assert.That(failure.All.Count).IsEqualTo(1)
      .Because("sanity check: the timeout path must already have routed the row to the failure channel before we fault the abandoned task");

    var marker = $"outbox-bulk-abandon-{Guid.NewGuid():N}";
    var unobserved = new List<UnobservedTaskExceptionEventArgs>();
    void OnUnobserved(object? s, UnobservedTaskExceptionEventArgs e) {
      if (e.Exception?.Flatten().InnerExceptions.Any(x => x.Message == marker) == true) {
        unobserved.Add(e);
      }
    }
    TaskScheduler.UnobservedTaskException += OnUnobserved;
    try {
      tcs.SetException(new InvalidOperationException(marker));
      tcs = null!;
      strategy = null!;
      GC.Collect();
      GC.WaitForPendingFinalizers();
      GC.Collect();
    } finally {
      TaskScheduler.UnobservedTaskException -= OnUnobserved;
    }

    await Assert.That(unobserved).IsEmpty()
      .Because("the worker's observe-only continuation on the abandoned bulk publish task must mark its eventual exception observed");
  }

  /// <summary>
  /// Singular-path counterpart of the bulk abandoned-task test above — the two publish paths
  /// wrap their own separate abandoned-task continuations, so one regressing independently of
  /// the other would otherwise go unnoticed.
  /// </summary>
  [Test]
  public async Task PublishOneAsync_AbandonedPublishTaskLaterFaults_NoUnobservedTaskExceptionAsync() {
    var failure = new _FailureChannel();
    var completion = new _CompletionChannel();
    var tcs = new TaskCompletionSource<MessagePublishResult>();
    var strategy = new _HangingSingleStrategyManualFault(tcs);

    var worker = _buildDirectCallWorker(
      failure,
      completion: completion,
      publish: strategy,
      options: new OutboxDrainWorkerOptions { Enabled = true, MaxPerStream = 100, PublishTimeoutSeconds = 1 });

    var row = _row((Guid)TrackedGuid.NewMedo(), (Guid)TrackedGuid.NewMedo());

    var singularTask = worker.PublishOneAsync(row, CancellationToken.None);
    await singularTask.WaitAsync(TimeSpan.FromSeconds(5));

    await Assert.That(failure.All.Count).IsEqualTo(1)
      .Because("sanity check: the singular timeout path must already have routed the row to the failure channel before we fault the abandoned task");

    var marker = $"outbox-single-abandon-{Guid.NewGuid():N}";
    var unobserved = new List<UnobservedTaskExceptionEventArgs>();
    void OnUnobserved(object? s, UnobservedTaskExceptionEventArgs e) {
      if (e.Exception?.Flatten().InnerExceptions.Any(x => x.Message == marker) == true) {
        unobserved.Add(e);
      }
    }
    TaskScheduler.UnobservedTaskException += OnUnobserved;
    try {
      tcs.SetException(new InvalidOperationException(marker));
      tcs = null!;
      strategy = null!;
      GC.Collect();
      GC.WaitForPendingFinalizers();
      GC.Collect();
    } finally {
      TaskScheduler.UnobservedTaskException -= OnUnobserved;
    }

    await Assert.That(unobserved).IsEmpty()
      .Because("the worker's observe-only continuation on the abandoned singular publish task must mark its eventual exception observed");
  }

  /// <summary>
  /// If this early return regressed, an event-store-only row (no transport destination) would
  /// fire Pre/PostOutbox lifecycle stages meant only for messages that actually transit a
  /// transport — receptors would see phantom invocations for rows that never left the event
  /// store.
  /// </summary>
  [Test]
  public async Task InvokeOutboxLifecycleStageAsync_EventStoreOnlyDestination_SkipsStageAsync() {
    var failure = new _FailureChannel();
    var worker = _buildDirectCallWorker(failure);
    var invoker = new _CapturingReceptorInvoker();
    var messageId = (Guid)TrackedGuid.NewMedo();
    var work = _work(messageId, destination: null);

    await worker.InvokeOutboxLifecycleStageAsync(
      work, work.Envelope, invoker,
      LifecycleStage.PreOutboxDetached, LifecycleStage.PreOutboxInline,
      "PreOutbox", CancellationToken.None);

    await Assert.That(invoker.Stages).IsEmpty()
      .Because("an event-store-only message (null destination) must never invoke transport-side lifecycle stages");
    await Assert.That(failure.All).IsEmpty();
  }
}
