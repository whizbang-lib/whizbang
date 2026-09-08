using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Lifecycle;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Locks the claimed-work path against the drain-mode pass (issue #700). A batch carries two work
/// sources: claimed items (the claim loop leased the rows and handed them over) and drain signals
/// (a stream's doorbell rang). The drain pass may find that every event it fetched is cooled: its
/// work id is in the recently-processed cache, so the apply is skipped and the event is only signaled
/// into the batch's lifecycle bookkeeping. That bookkeeping is not progress. Treating it as progress
/// discarded every claimed item in the batch, and the rows behind them stayed leased until the lease
/// lapsed, were re-claimed, and were discarded again: a livelock in which the lease counter climbs
/// to the dead-letter threshold without one apply ever running.
/// </summary>
/// <remarks>
/// The fake coordinator claims nothing on its own; every item enters through the channel surfaces so
/// one batch can be composed exactly. The recently-processed cache is pre-marked to make the drain
/// pass "cooled only" without any apply having happened in this process.
/// </remarks>
/// <code-under-test>src/Whizbang.Core/Workers/PerspectiveWorker.cs</code-under-test>
public class PerspectiveWorkerClaimedWorkSurvivesCooledDrainTests {
  private const string PERSPECTIVE = "Probe+Projection";

  private sealed record ProbeEvent(string Data) : IEvent;

  [Test]
  public async Task Batch_WithClaimedWork_AndDrainPassThatOnlyCooledEvents_StillProcessesTheClaimedWorkAsync() {
    // Two streams: the claimed one and the signaled one. The signaled stream's fetched rows are all
    // cooled, so the drain pass applies nothing for it.
    var claimedStream = Guid.NewGuid();
    var signaledStream = Guid.NewGuid();
    var cooledEventId = Guid.NewGuid();
    var cooledWorkId = Guid.NewGuid();

    var f = _Fixture.Create(
      drainRows: [_row(signaledStream, cooledEventId, cooledWorkId)],
      drainEnvelopes: [_envelope(cooledEventId, new ProbeEvent("cooled"))],
      claimedStreamEvents: [_envelope(Guid.NewGuid(), new ProbeEvent("claimed"))],
      cooledWorkIds: [cooledWorkId]);

    await f.Harness.EnqueueWorkAsync(new PerspectiveWork { WorkId = Guid.NewGuid(), StreamId = claimedStream, PerspectiveName = PERSPECTIVE });
    await f.Harness.EnqueueDrainStreamAsync(signaledStream);

    await f.RunOneBatchAsync();

    // The claimed stream must reach its runner in this batch, cooled drain events or not.
    await Assert.That(f.Registry.InvocationsFor(claimedStream)).IsEqualTo(1)
      .Because("claimed work is not progress-gated on a drain pass that applied nothing");
    // And the cooled stream must NOT have been applied: the cache said it already was.
    await Assert.That(f.Registry.InvocationsFor(signaledStream)).IsEqualTo(0);
  }

  [Test]
  public async Task Batch_ClaimedWork_ForTheStreamWhoseDrainPassOnlyCooled_IsProcessedAsync() {
    // Same stream on both sources. The drain pass cools everything for it, so the claimed path is the
    // only route to its pending rows.
    var stream = Guid.NewGuid();
    var cooledEventId = Guid.NewGuid();
    var cooledWorkId = Guid.NewGuid();

    var f = _Fixture.Create(
      drainRows: [_row(stream, cooledEventId, cooledWorkId)],
      drainEnvelopes: [_envelope(cooledEventId, new ProbeEvent("cooled"))],
      claimedStreamEvents: [_envelope(Guid.NewGuid(), new ProbeEvent("claimed"))],
      cooledWorkIds: [cooledWorkId]);

    await f.Harness.EnqueueWorkAsync(new PerspectiveWork { WorkId = Guid.NewGuid(), StreamId = stream, PerspectiveName = PERSPECTIVE });
    await f.Harness.EnqueueDrainStreamAsync(stream);

    await f.RunOneBatchAsync();

    await Assert.That(f.Registry.InvocationsFor(stream)).IsEqualTo(1)
      .Because("a drain pass that only cooled the stream did no work for it; the claimed item must");
  }

  [Test]
  public async Task Batch_ClaimedWork_ForAStreamTheDrainPassApplied_IsNotAppliedTwiceAsync() {
    // The reason the old discard existed: a stream both claimed and signaled, with fresh events, must be
    // applied once (the drain pass owns it), not once by each path.
    var stream = Guid.NewGuid();
    var freshEventId = Guid.NewGuid();

    var f = _Fixture.Create(
      drainRows: [_row(stream, freshEventId, Guid.NewGuid())],
      drainEnvelopes: [_envelope(freshEventId, new ProbeEvent("fresh"))],
      claimedStreamEvents: [_envelope(Guid.NewGuid(), new ProbeEvent("claimed"))],
      cooledWorkIds: []);

    await f.Harness.EnqueueWorkAsync(new PerspectiveWork { WorkId = Guid.NewGuid(), StreamId = stream, PerspectiveName = PERSPECTIVE });
    await f.Harness.EnqueueDrainStreamAsync(stream);

    await f.RunOneBatchAsync();

    await Assert.That(f.Registry.InvocationsFor(stream)).IsEqualTo(1)
      .Because("the drain pass applied the stream; the claimed copy of the same stream is redundant");
  }

  // -------------------------------------------------------------------------------------------
  // Fixture
  // -------------------------------------------------------------------------------------------

  private static StreamEventData _row(Guid streamId, Guid eventId, Guid workId) => new() {
    StreamId = streamId,
    EventId = eventId,
    EventType = TypeNameFormatter.Format(typeof(ProbeEvent)),
    EventData = """{"Data":"x"}""",
    Metadata = null,
    Scope = null,
    EventWorkId = workId,
    PerspectiveName = PERSPECTIVE,
    CommitSequence = 1
  };

  private static MessageEnvelope<IEvent> _envelope(Guid eventId, IEvent payload) => new() {
    MessageId = new MessageId(eventId),
    Payload = payload,
    Hops = [new MessageHop {
      Type = HopType.Current,
      Timestamp = DateTimeOffset.UtcNow,
      CorrelationId = CorrelationId.New(),
      CausationId = MessageId.New(),
      ServiceInstance = new ServiceInstanceInfo { InstanceId = Guid.NewGuid(), ServiceName = "Test", HostName = "test", ProcessId = 1 }
    }],
    DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
  };

  private sealed class _Fixture {
    public required PerspectiveWorker Worker { get; init; }
    public required PerspectiveWorkerTestHarness Harness { get; init; }
    public required CountingRegistry Registry { get; init; }

    public static _Fixture Create(
        List<StreamEventData> drainRows,
        List<MessageEnvelope<IEvent>> drainEnvelopes,
        List<MessageEnvelope<IEvent>> claimedStreamEvents,
        List<Guid> cooledWorkIds) {
      var instanceProvider = new InstanceProvider();
      var registry = new CountingRegistry([
        new PerspectiveRegistrationInfo(PERSPECTIVE, "global::Test.ProbeProjection", "global::Test.ProbeModel",
          [TypeNameFormatter.Format(typeof(ProbeEvent))])
      ]);
      var coordinator = new Coordinator { DrainRows = drainRows };
      var eventStore = new EventStore { DrainEnvelopes = drainEnvelopes, ClaimedStreamEvents = claimedStreamEvents };
      var cache = new RecentlyProcessedEventCache(new SystemTimeProvider());
      foreach (var id in cooledWorkIds) {
        cache.MarkProcessed(id);
      }

      var services = new ServiceCollection();
      services.AddSingleton<IWorkCoordinator>(coordinator);
      services.AddSingleton<IPerspectiveRunnerRegistry>(registry);
      services.AddSingleton<IServiceInstanceProvider>(instanceProvider);
      services.AddSingleton<IEventStore>(eventStore);
      services.AddSingleton<IEventTypeProvider>(new EventTypeProvider());
      services.AddSingleton<ILifecycleCoordinator, LifecycleCoordinator>();
      services.AddLogging();
      var sp = services.BuildServiceProvider();

      var harness = new PerspectiveWorkerTestHarness();
      var worker = new PerspectiveWorker(
        instanceProvider: instanceProvider,
        scopeFactory: sp.GetRequiredService<IServiceScopeFactory>(),
        // One consumer loop so the claimed item and the drain signal are read into the SAME batch;
        // the defect lives in how one batch reconciles its two sources.
        options: Options.Create(new PerspectiveWorkerOptions {
          PollingIntervalMilliseconds = 50,
          DrainLoopMaxIterations = 1,
          MaxConcurrentDrainConsumers = 1
        }),
        tracingOptions: null,
        completionStrategy: new InstantCompletionStrategy(),
        eventTypeProvider: null,
        perspectiveChannelWriter: harness.ChannelWriter,
        perspectiveCompletionChannel: harness.CompletionCapture,
        failureChannel: harness.FailureCapture,
        perspectiveDrainChannel: harness.DrainChannel,
        schemaReadyGate: SchemaReadyGate.AlreadyReady(),
        recentlyProcessedEventCache: cache);

      return new _Fixture { Worker = worker, Harness = harness, Registry = registry };
    }

    /// <summary>Starts the worker, waits for the first batch that carried work, and stops it.</summary>
    public async Task RunOneBatchAsync() {
      var batchComplete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
      var batches = 0;
      Worker.OnBatchCycleComplete += () => {
        if (Interlocked.Increment(ref batches) == 1) {
          batchComplete.TrySetResult();
        }
      };
      using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
      await Worker.StartAsync(cts.Token);
      await batchComplete.Task.WaitAsync(TimeSpan.FromSeconds(10));
      if (Worker.PendingPostLifecycle is { } pending) {
        await pending.WaitAsync(TimeSpan.FromSeconds(5));
      }
      await cts.CancelAsync();
      // Await the worker BODY, not the task StartAsync returned: .NET hands that back as
      // Task.CompletedTask the moment ExecuteAsync is queued to the thread pool, so this method used
      // to return while the worker was still running and the invocation counts the tests assert on
      // were still moving. SuppressThrowing because a body leaving through a cancellation catch
      // settles RanToCompletion or Canceled depending on thread-pool timing — either is a clean stop.
      if (Worker.ExecuteTask is { } body) {
        await body.WaitAsync(TimeSpan.FromSeconds(30))
          .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
      }
    }
  }

  /// <summary>Claims nothing on its own; drain fetches return the configured rows for every call.</summary>
  private sealed class Coordinator : IWorkCoordinator {
    public List<StreamEventData> DrainRows { get; init; } = [];

    public Task<WorkBatch> ClaimWorkAsync(ClaimWorkRequest request, CancellationToken cancellationToken = default) =>
      Task.FromResult(new WorkBatch { OutboxWork = [], InboxWork = [], PerspectiveWork = [] });

    public Task<List<StreamEventData>> GetStreamEventsAsync(Guid instanceId, Guid[] streamIds, CancellationToken cancellationToken = default) =>
      Task.FromResult(DrainRows.Where(r => streamIds.Contains(r.StreamId)).ToList());

    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion completion, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure failure, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount = 2, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new WorkCoordinatorStatistics());
    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default)
      => Task.FromResult<PerspectiveCursorInfo?>(null);
  }

  /// <summary>Drain fetches deserialize to the configured envelopes; the claimed path reads its stream's events polymorphically.</summary>
  private sealed class EventStore : IEventStore {
    public List<MessageEnvelope<IEvent>> DrainEnvelopes { get; init; } = [];
    public List<MessageEnvelope<IEvent>> ClaimedStreamEvents { get; init; } = [];

    public List<MessageEnvelope<IEvent>> DeserializeStreamEvents(IReadOnlyList<StreamEventData> streamEvents, IReadOnlyList<Type> eventTypes) {
      var wanted = streamEvents.Select(r => r.EventId).ToHashSet();
      return DrainEnvelopes.Where(e => wanted.Contains(e.MessageId.Value)).ToList();
    }

    public async IAsyncEnumerable<MessageEnvelope<IEvent>> ReadPolymorphicAsync(Guid streamId, Guid? fromEventId, IReadOnlyList<Type> eventTypes, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default) {
      await Task.CompletedTask;
      foreach (var e in ClaimedStreamEvents) {
        yield return e;
      }
    }

    public Task AppendAsync<TMessage>(Guid streamId, MessageEnvelope<TMessage> envelope, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task AppendAsync<TMessage>(Guid streamId, TMessage message, CancellationToken cancellationToken = default) where TMessage : notnull => Task.CompletedTask;
    public async IAsyncEnumerable<MessageEnvelope<TMessage>> ReadAsync<TMessage>(Guid streamId, long fromSequence, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default) { await Task.CompletedTask; yield break; }
    public async IAsyncEnumerable<MessageEnvelope<TMessage>> ReadAsync<TMessage>(Guid streamId, Guid? fromEventId, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default) { await Task.CompletedTask; yield break; }
    public Task<List<MessageEnvelope<IEvent>>> GetEventsBetweenPolymorphicAsync(Guid streamId, Guid? afterEventId, Guid upToEventId, IReadOnlyList<Type> eventTypes, CancellationToken cancellationToken = default) => Task.FromResult(new List<MessageEnvelope<IEvent>>());
    public Task<List<MessageEnvelope<TMessage>>> GetEventsBetweenAsync<TMessage>(Guid streamId, Guid? afterEventId, Guid upToEventId, CancellationToken cancellationToken = default) => Task.FromResult(new List<MessageEnvelope<TMessage>>());
    public Task<long> GetLastSequenceAsync(Guid streamId, CancellationToken cancellationToken = default) => Task.FromResult(-1L);
  }

  /// <summary>Counts runner invocations per stream across both run entry points.</summary>
  private sealed class CountingRegistry(List<PerspectiveRegistrationInfo> registrations) : IPerspectiveRunnerRegistry {
    private readonly ConcurrentDictionary<Guid, int> _invocations = new();

    public int InvocationsFor(Guid streamId) => _invocations.GetValueOrDefault(streamId);

    public IPerspectiveRunner? GetRunner(string perspectiveName, IServiceProvider serviceProvider) => new Runner(this);
    public IReadOnlyList<PerspectiveRegistrationInfo> GetRegisteredPerspectives() => registrations;
    public IReadOnlyList<Type> GetEventTypes() => [typeof(ProbeEvent)];
    public IReadOnlySet<LifecycleStage> LifecycleStagesWithReceptors { get; set; } = new HashSet<LifecycleStage>();

    private void _count(Guid streamId) => _invocations.AddOrUpdate(streamId, 1, static (_, n) => n + 1);

    private sealed class Runner(CountingRegistry owner) : IPerspectiveRunner {
      public Type PerspectiveType => typeof(object);

      public Task<PerspectiveCursorCompletion> RunAsync(Guid streamId, string name, Guid? lastProcessedEventId, CancellationToken cancellationToken) {
        owner._count(streamId);
        return Task.FromResult(new PerspectiveCursorCompletion { StreamId = streamId, PerspectiveName = name, LastEventId = Guid.NewGuid(), Status = PerspectiveProcessingStatus.Completed, PerspectiveType = typeof(object) });
      }

      public Task<PerspectiveCursorCompletion> RunWithEventsAsync(Guid streamId, string name, Guid? lastProcessedEventId, IReadOnlyList<MessageEnvelope<IEvent>> events, CancellationToken cancellationToken = default) {
        owner._count(streamId);
        return Task.FromResult(new PerspectiveCursorCompletion {
          StreamId = streamId,
          PerspectiveName = name,
          LastEventId = events.Count > 0 ? events[^1].MessageId.Value : Guid.NewGuid(),
          Status = PerspectiveProcessingStatus.Completed,
          PerspectiveType = typeof(object)
        });
      }

      public Task<PerspectiveCursorCompletion> RewindAndRunAsync(Guid streamId, string name, Guid triggeringEventId, CancellationToken cancellationToken = default) =>
        RunAsync(streamId, name, null, cancellationToken);

      public Task BootstrapSnapshotAsync(Guid streamId, string name, Guid lastProcessedEventId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
  }

  private sealed class InstanceProvider : IServiceInstanceProvider {
    public Guid InstanceId { get; } = Guid.NewGuid();
    public string ServiceName { get; } = "TestService";
    public string HostName { get; } = "test-host";
    public int ProcessId { get; } = 1;
    public ServiceInstanceInfo ToInfo() => new() { ServiceName = ServiceName, InstanceId = InstanceId, HostName = HostName, ProcessId = ProcessId };
  }

  private sealed class EventTypeProvider : IEventTypeProvider {
    public IReadOnlyList<Type> GetEventTypes() => [typeof(ProbeEvent)];
  }
}
