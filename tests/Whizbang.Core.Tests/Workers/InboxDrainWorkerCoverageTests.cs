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
/// Coverage for <see cref="InboxDrainWorker"/> paths the primary suites
/// (<see cref="InboxDrainWorkerTests"/>, <see cref="InboxDrainFetchPlanTests"/>) don't reach: the
/// mid-plan cancellation check in the batched multi-stream fetch, the poison-admission gate's
/// deferral in both the first-pass batched dispatch AND the loop-until-empty inner path, the
/// partial-batch early exit's own cancellation-avoidance, and the inner loop ending via its own
/// while-condition when canceled between fetches.
/// </summary>
/// <docs>fundamentals/work-coordinator/inbox-drain</docs>
[NotInParallel("WhizbangBackgroundServiceTests")]
public class InboxDrainWorkerCoverageTests {

  // --- fakes ---

  private sealed class FakeInboxDrainChannel : IInboxDrainChannel {
    private readonly Channel<Guid> _channel = Channel.CreateUnbounded<Guid>();
    public ChannelReader<Guid> Reader => _channel.Reader;
    public ValueTask WriteAsync(Guid streamId, CancellationToken ct = default) => _channel.Writer.WriteAsync(streamId, ct);
    public bool TryWrite(Guid streamId) => _channel.Writer.TryWrite(streamId);
  }

  private sealed class CapturingInboxChannel : IInboxChannelWriter {
    private readonly Channel<InboxWork> _channel = Channel.CreateUnbounded<InboxWork>();
    public List<InboxWork> Written { get; } = [];
    public TaskCompletionSource<int> ReachedCount { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public int TargetCount { get; set; } = 1;
    public ChannelReader<InboxWork> Reader => _channel.Reader;
    public ValueTask WriteAsync(InboxWork work, CancellationToken ct = default) {
      lock (Written) {
        Written.Add(work);
        if (Written.Count >= TargetCount) {
          ReachedCount.TrySetResult(Written.Count);
        }
      }
      return _channel.Writer.WriteAsync(work, ct);
    }
    public bool TryWrite(InboxWork work) {
      lock (Written) {
        Written.Add(work);
        if (Written.Count >= TargetCount) {
          ReachedCount.TrySetResult(Written.Count);
        }
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

  /// <summary>
  /// A coordinator whose successive <see cref="FetchInboxBatchAsync"/> calls are individually
  /// scripted via <see cref="Enqueue"/>, with an optional <see cref="AfterCall"/> hook that runs
  /// once a call's response is computed but before it's returned -- lets a test cancel the
  /// worker's token at the exact point a real host would still be racing the next fetch.
  /// </summary>
  private sealed class ScriptedWorkCoordinator : IWorkCoordinator {
    private readonly List<Func<IReadOnlyList<Guid>, IReadOnlyList<InboxBatchRow>>> _responses = [];
    public List<Guid[]> FetchedGroups { get; } = [];
    public int CallCount { get; private set; }
    public Action<int>? AfterCall { get; set; }

    public void Enqueue(Func<IReadOnlyList<Guid>, IReadOnlyList<InboxBatchRow>> respond) => _responses.Add(respond);

    public Task<IReadOnlyList<InboxBatchRow>> FetchInboxBatchAsync(
        IReadOnlyList<Guid> streamIds, Guid instanceId, int maxPerStream = 100, CancellationToken cancellationToken = default) {
      CallCount++;
      FetchedGroups.Add([.. streamIds]);
      IReadOnlyList<InboxBatchRow> response = CallCount <= _responses.Count ? _responses[CallCount - 1](streamIds) : [];
      AfterCall?.Invoke(CallCount);
      return Task.FromResult(response);
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

  // --- helpers ---

  private static readonly JsonSerializerOptions _jsonOpts = Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions();

  private static InboxBatchRow _row(Guid messageId, Guid streamId, int attempts = 0) {
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
      Attempts = attempts,
      PartitionNumber = 0,
      IsEvent = false,
    };
  }

  // --- tests ---

  [Test]
  public async Task DrainStreamBatch_CanceledBetweenFetchGroups_StopsIssuingFurtherFetchesAsync() {
    // If this mid-plan cancellation check regressed, a shutting-down worker would still issue
    // every remaining quantized-cap group's fetch against a coordinator/DB connection the host is
    // already tearing down -- extra queries that show up as spurious errors on every clean stop,
    // multiplying by however many groups the plan happened to produce that cycle.
    var deep = (Guid)TrackedGuid.NewMedo();
    var shallow = (Guid)TrackedGuid.NewMedo();
    var deepMsg = (Guid)TrackedGuid.NewMedo();
    var shallowMsg = (Guid)TrackedGuid.NewMedo();

    var coord = new ScriptedWorkCoordinator();
    using var cts = new CancellationTokenSource();
    coord.Enqueue(streamIds => [.. streamIds.Select(sid => _row(sid == deep ? deepMsg : shallowMsg, sid))]);
    coord.AfterCall = call => {
      if (call == 1) {
        cts.Cancel();
      }
    };

    var drain = new FakeInboxDrainChannel();
    var inbox = new CapturingInboxChannel { TargetCount = 1 };
    var instance = new FakeServiceInstanceProvider();
    var gate = new SchemaReadyGate();
    gate.MarkReady();
    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coord);
    var sp = services.BuildServiceProvider();

    var worker = new InboxDrainWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      instance, drain, inbox, gate,
      Options.Create(new InboxDrainWorkerOptions { Enabled = true, MaxPerStream = 100, MaxPerStreamCeiling = 1000 }),
      _jsonOpts,
      NullLogger<InboxDrainWorker>.Instance);

    // Two distinct observed depths force _planFetches to produce two separate quantized-cap
    // groups, so the mid-plan cancellation check actually has a second group to skip.
    worker.RecordObservedDepthForTest(deep, 5_000);
    worker.RecordObservedDepthForTest(shallow, 3);
    _ = drain.TryWrite(deep);
    _ = drain.TryWrite(shallow);

    await worker.StartAsync(cts.Token);

    await worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(10))
      .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    await Assert.That(worker.ExecuteTask.IsCompleted).IsTrue();
    await Assert.That(worker.ExecuteTask.IsFaulted).IsFalse()
      .Because("a mid-batch shutdown must end the worker cleanly, never as an escaped fault");

    await Assert.That(coord.CallCount).IsEqualTo(1)
      .Because("cancellation must stop the plan loop before the second group's fetch is even issued");

    try { await worker.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }
  }

  [Test]
  public async Task DrainStreamBatch_OnePoisonRowInTheFirstPassFetch_IsDeferredButItsSiblingStillDispatchesAsync() {
    // The first-pass batched dispatch is a second place the poison-admission gate must be
    // checked (the loop-until-empty inner path is the other). If this check were ever skipped
    // here, a row already past its attempt ceiling would re-enter the working set through the
    // one path that forgot to gate it, undoing the retirement the ceiling exists to enforce.
    var streamId = (Guid)TrackedGuid.NewMedo();
    var poisonMsg = (Guid)TrackedGuid.NewMedo();
    var goodMsg = (Guid)TrackedGuid.NewMedo();

    var coord = new ScriptedWorkCoordinator();
    coord.Enqueue(_ => [_row(poisonMsg, streamId, attempts: 11), _row(goodMsg, streamId, attempts: 0)]);

    var drain = new FakeInboxDrainChannel();
    var inbox = new CapturingInboxChannel { TargetCount = 1 };
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

    _ = await Task.WhenAny(inbox.ReachedCount.Task, Task.Delay(TimeSpan.FromSeconds(15)));
    cts.Cancel();
    try { await worker.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }

    await Assert.That(inbox.Written.Count).IsEqualTo(1)
      .Because("only the fresh row may enter the working set on the first pass; the row past its attempt ceiling must be deferred, not dropped or double-admitted");
    await Assert.That(inbox.Written.Single().MessageId).IsEqualTo(goodMsg);
  }

  [Test]
  public async Task DrainStreamInner_SecondPassReturnsFewerRowsThanTheCapWithOnePoisonRow_ExitsWithoutAnUnnecessaryConfirmationFetchAsync() {
    // Two invariants, one scenario. First: the loop-until-empty inner path has its OWN
    // poison-admission check; if it regressed, a row the first pass already deferred once would
    // slip through the second time it's fetched. Second: a page thinner than the cap means the
    // stream is drained; if the early exit regressed, every drain would pay for one extra
    // confirmation fetch that (per the slice-32 measurement this guards) almost always returns
    // nothing -- doubling round-trips for no gain.
    var streamId = (Guid)TrackedGuid.NewMedo();
    var firstPassMsgs = Enumerable.Range(0, 3).Select(_ => (Guid)TrackedGuid.NewMedo()).ToArray();
    var poisonMsg = (Guid)TrackedGuid.NewMedo();
    var secondPassGoodMsg = (Guid)TrackedGuid.NewMedo();

    var coord = new ScriptedWorkCoordinator();
    // First pass (the batched, multi-stream fetch): exactly saturates the floor cap, so the
    // batch dispatcher hands this stream to the loop-until-empty inner path.
    coord.Enqueue(_ => [.. firstPassMsgs.Select(m => _row(m, streamId))]);
    // Second pass (the inner loop's own fetch): fewer rows than the cap, one of them poisoned.
    coord.Enqueue(_ => [_row(poisonMsg, streamId, attempts: 11), _row(secondPassGoodMsg, streamId)]);

    var drain = new FakeInboxDrainChannel();
    var inbox = new CapturingInboxChannel { TargetCount = 4 };
    var signalCount = 0;
    inbox.OnNewInboxWorkAvailable += () => Interlocked.Increment(ref signalCount);
    var instance = new FakeServiceInstanceProvider();
    var gate = new SchemaReadyGate();
    gate.MarkReady();
    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coord);
    var sp = services.BuildServiceProvider();

    var worker = new InboxDrainWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      instance, drain, inbox, gate,
      Options.Create(new InboxDrainWorkerOptions {
        Enabled = true,
        MaxPerStream = 3,
        AdaptivePerStreamEnabled = false,
      }),
      _jsonOpts,
      NullLogger<InboxDrainWorker>.Instance);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await drain.WriteAsync(streamId);

    _ = await Task.WhenAny(inbox.ReachedCount.Task, Task.Delay(TimeSpan.FromSeconds(15)));
    cts.Cancel();
    try { await worker.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }

    await Assert.That(inbox.Written.Count).IsEqualTo(4)
      .Because("3 rows from the saturating first pass plus the one admitted row from the second pass; the poisoned row must never appear");
    await Assert.That(inbox.Written.Any(w => w.MessageId == poisonMsg)).IsFalse();
    await Assert.That(coord.CallCount).IsEqualTo(2)
      .Because("a thinner-than-cap second pass must exit immediately rather than pay for a third, unnecessary confirmation fetch");
    await Assert.That(signalCount).IsGreaterThanOrEqualTo(1)
      .Because("newly admitted work reaching the working set through the early-exit branch must still wake the dispatch side, or the enqueued rows sit unnoticed until some unrelated signal arrives");
  }

  [Test]
  public async Task DrainStreamInner_CanceledBetweenFetches_StopsAtTheNextIterationBoundaryAsync() {
    // The loop-until-empty inner path only re-checks cancellation at the top of its while loop,
    // between fetches -- it does not abandon a page mid-write. If that check were ever removed,
    // a canceled drain would keep fetching and writing indefinitely instead of stopping at the
    // next natural boundary, ignoring host shutdown entirely.
    var streamId = (Guid)TrackedGuid.NewMedo();
    var firstPassMsgs = Enumerable.Range(0, 2).Select(_ => (Guid)TrackedGuid.NewMedo()).ToArray();
    var secondPassMsgs = Enumerable.Range(0, 2).Select(_ => (Guid)TrackedGuid.NewMedo()).ToArray();

    var coord = new ScriptedWorkCoordinator();
    using var cts = new CancellationTokenSource();
    // First pass: exactly saturates the cap (2), handing the stream to the inner loop.
    coord.Enqueue(_ => [.. firstPassMsgs.Select(m => _row(m, streamId))]);
    // Second pass (inner loop iteration 1): also fully saturates the cap, so the loop would
    // normally go around again -- except cancellation lands right after this call resolves.
    coord.Enqueue(_ => [.. secondPassMsgs.Select(m => _row(m, streamId))]);
    coord.AfterCall = call => {
      if (call == 2) {
        cts.Cancel();
      }
    };

    var drain = new FakeInboxDrainChannel();
    var inbox = new CapturingInboxChannel { TargetCount = 4 };
    var instance = new FakeServiceInstanceProvider();
    var gate = new SchemaReadyGate();
    gate.MarkReady();
    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coord);
    var sp = services.BuildServiceProvider();

    var worker = new InboxDrainWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      instance, drain, inbox, gate,
      Options.Create(new InboxDrainWorkerOptions {
        Enabled = true,
        MaxPerStream = 2,
        AdaptivePerStreamEnabled = false,
      }),
      _jsonOpts,
      NullLogger<InboxDrainWorker>.Instance);

    await worker.StartAsync(cts.Token);
    await drain.WriteAsync(streamId);

    // The worker's own completion is the signal, not a row count: how much of the second page is
    // written before the cancellation is observed is a scheduling detail, and waiting on a fixed
    // count would make the test hang to its ceiling whenever that detail changes.
    await worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(10))
      .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    await Assert.That(worker.ExecuteTask.IsCompleted).IsTrue();
    await Assert.That(worker.ExecuteTask.IsFaulted).IsFalse()
      .Because("the inner loop ending via its own while-condition on cancellation must still shut the worker down cleanly, not as an escaped fault");

    await Assert.That(coord.CallCount).IsEqualTo(2)
      .Because("this is the whole invariant: once canceled, the loop stops at the next iteration "
             + "boundary rather than issuing a further fetch, so a shutting-down host stops pulling work");
    await Assert.That(inbox.Written.Count).IsGreaterThanOrEqualTo(2)
      .Because("the first page was already in hand and fully written before cancellation could be "
             + "observed -- a canceled drain must not discard rows it already fetched");

    try { await worker.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }
  }
}
