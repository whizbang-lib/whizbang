// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Execution;
using Whizbang.Core.Lifecycle;
using Whizbang.Core.Messaging;
using Whizbang.Core.Notifications;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Perspectives.Sync;
using Whizbang.Core.Tracing;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;
using Whizbang.Testing.Options;
using Whizbang.Testing.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Issue #1759: the drain parses only the fetched rows outside the cooldown cache, but keeps every fetched row in the
/// event-to-rows lookup. One event can carry a row for each perspective that subscribes to it, and the two rows can be
/// in different states: perspective A's row was applied by an earlier session and sits in the cooldown cache, while
/// perspective B's row is new. The event is parsed for B's row; A must still see its own row as cooled, skip the apply,
/// signal its completion, and write the cooled-row diagnostic for its own row only.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Workers/PerspectiveWorker.cs</code-under-test>
public class PerspectiveWorkerPartlyCooledEventDrainTests {
  private const string PERSPECTIVE_A = "Probe+ProjectionA";
  private const string PERSPECTIVE_B = "Probe+ProjectionB";
  private const string COOLDOWN_DIAGNOSTIC = "[diag.cooldown]";

  private sealed record ProbeEvent(string Data) : IEvent;

  [Test]
  [Timeout(30000)]
  public async Task Drain_EventWithCooledRowForOnePerspectiveAndFreshRowForAnother_AppliesOnlyTheFreshOneAndLogsTheCooledRowAsync(CancellationToken cancellationToken) {
    var streamId = (Guid)TrackedGuid.New();
    var eventId = (Guid)TrackedGuid.New();
    var cooledWorkIdA = (Guid)TrackedGuid.New();
    var freshWorkIdB = (Guid)TrackedGuid.New();

    var f = Fixture.Create(
      drainRows: [_row(streamId, eventId, cooledWorkIdA, PERSPECTIVE_A), _row(streamId, eventId, freshWorkIdB, PERSPECTIVE_B)],
      drainEnvelopes: [_envelope(eventId)],
      cooledWorkIds: [cooledWorkIdA]);

    await f.Harness.EnqueueDrainStreamAsync(streamId, cancellationToken);
    await f.RunOneBatchAsync(cancellationToken);

    await Assert.That(f.Registry.AppliedEventIds(streamId, PERSPECTIVE_B)).IsEquivalentTo([eventId])
      .Because("B's row is not in the cooldown cache, so B applies the event");
    await Assert.That(f.Registry.AppliedEventIds(streamId, PERSPECTIVE_A)).IsEmpty()
      .Because("A's own row is in the cooldown cache, so A must not apply the event again");
    await Assert.That(f.Lifecycle.Signaled).Contains((eventId, PERSPECTIVE_A))
      .Because("a cooled perspective still signals its completion so the WhenAll gate can close");

    var diagnostics = f.Logger.Entries.Where(e => e.Message.StartsWith(COOLDOWN_DIAGNOSTIC, StringComparison.Ordinal)).ToList();
    await Assert.That(diagnostics).Count().IsEqualTo(1)
      .Because("only A has a cooled row; B's row is fresh");
    var diagnostic = diagnostics[0];
    await Assert.That(diagnostic.Level).IsEqualTo(LogLevel.Debug);
    await Assert.That(diagnostic.Values["PerspectiveName"]).IsEqualTo(PERSPECTIVE_A);
    await Assert.That(diagnostic.Values["StreamId"]).IsEqualTo(streamId);
    await Assert.That(diagnostic.Values["CooledCount"]).IsEqualTo(1);
    await Assert.That(diagnostic.Values["EventIds"]).IsEqualTo(eventId.ToString("D"));
    await Assert.That(diagnostic.Values["WorkIds"]).IsEqualTo(cooledWorkIdA.ToString("D"))
      .Because("the diagnostic names A's cooled row, never B's fresh row on the same event");
  }

  // -------------------------------------------------------------------------------------------
  // Fixture
  // -------------------------------------------------------------------------------------------

  private static StreamEventData _row(Guid streamId, Guid eventId, Guid workId, string perspectiveName) => new() {
    StreamId = streamId,
    EventId = eventId,
    EventType = TypeNameFormatter.Format(typeof(ProbeEvent)),
    EventData = """{"Data":"x"}""",
    Metadata = null,
    Scope = null,
    EventWorkId = workId,
    PerspectiveName = perspectiveName,
    CommitSequence = 1
  };

  // The hop's moment plays no part in the drain; a fixed one keeps the test off the real clock.
  private static readonly DateTimeOffset _hopTimestamp = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

  private static MessageEnvelope<IEvent> _envelope(Guid eventId) => new() {
    MessageId = new MessageId(eventId),
    Payload = new ProbeEvent("probe"),
    Hops = [new MessageHop {
      Type = HopType.Current,
      Timestamp = _hopTimestamp,
      CorrelationId = CorrelationId.New(),
      CausationId = MessageId.New(),
      ServiceInstance = new ServiceInstanceInfo { InstanceId = Guid.NewGuid(), ServiceName = "Test", HostName = "test", ProcessId = 1 }
    }],
    DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
  };

  private sealed class Fixture {
    public required PerspectiveWorker Worker { get; init; }
    public required PerspectiveWorkerTestHarness Harness { get; init; }
    public required RecordingRegistry Registry { get; init; }
    public required RecordingLifecycleCoordinator Lifecycle { get; init; }
    public required CapturingLogger Logger { get; init; }

    public static Fixture Create(
        List<StreamEventData> drainRows,
        List<MessageEnvelope<IEvent>> drainEnvelopes,
        List<Guid> cooledWorkIds) {
      var instanceProvider = new InstanceProvider();
      var eventType = TypeNameFormatter.Format(typeof(ProbeEvent));
      var registry = new RecordingRegistry([
        new PerspectiveRegistrationInfo(PERSPECTIVE_A, "global::Test.ProbeProjectionA", "global::Test.ProbeModelA", [eventType]),
        new PerspectiveRegistrationInfo(PERSPECTIVE_B, "global::Test.ProbeProjectionB", "global::Test.ProbeModelB", [eventType])
      ]);
      var lifecycle = new RecordingLifecycleCoordinator(new LifecycleCoordinator(NullLogger<LifecycleCoordinator>.Instance));
      var cache = new RecentlyProcessedEventCache(new SystemTimeProvider());
      foreach (var id in cooledWorkIds) {
        cache.MarkProcessed(id);
      }

      var services = new ServiceCollection();
      services.TryAddWhizbangDefaults();
      services.AddSingleton<IWorkCoordinator>(new Coordinator { DrainRows = drainRows });
      services.AddSingleton<IPerspectiveRunnerRegistry>(registry);
      services.AddSingleton<IServiceInstanceProvider>(instanceProvider);
      services.AddSingleton<IEventStore>(new EventStore { DrainEnvelopes = drainEnvelopes });
      services.AddSingleton<IEventTypeProvider>(new EventTypeProvider());
      services.AddSingleton<ILifecycleCoordinator>(lifecycle);
      services.AddLogging();
      var sp = services.BuildServiceProvider();

      var options = new PerspectiveWorkerOptions {
        PollingIntervalMilliseconds = 50,
        DrainLoopMaxIterations = 1,
        MaxConcurrentDrainConsumers = 1
      };
      var logger = new CapturingLogger();
      var harness = new PerspectiveWorkerTestHarness();
      var worker = new PerspectiveWorker(
        instanceProvider: instanceProvider,
        scopeFactory: sp.GetRequiredService<IServiceScopeFactory>(),
        options: Options.Create(options),
        schemaReadyGate: SchemaReadyGate.AlreadyReady(),
        tracingOptions: new StaticOptionsMonitor<TracingOptions>(new TracingOptions()),
        completionStrategy: new InstantCompletionStrategy(logger: NullLogger<InstantCompletionStrategy>.Instance),
        eventTypeProvider: new EventTypeProvider(),
        syncSignaler: new LocalSyncSignaler(NullLogger<LocalSyncSignaler>.Instance),
        syncEventTracker: new SyncEventTracker(),
        logger: logger,
        snapshotStore: NullPerspectiveSnapshotStore.Instance,
        streamLocker: NullPerspectiveStreamLocker.Instance,
        streamLockOptions: Options.Create(new PerspectiveStreamLockOptions()),
        streamAffinityOptions: Options.Create(new PerspectiveStreamAffinityOptions()),
        processedEventCacheObserver: NullProcessedEventCacheObserver.Instance,
        workChannelWriter: new WorkChannelWriter(),
        rewindOptions: Options.Create(new PerspectiveRewindOptions()),
        perspectiveChannelWriter: harness.ChannelWriter,
        perspectiveCompletionChannel: harness.CompletionCapture,
        failureChannel: harness.FailureCapture,
        leaseRenewalChannel: new CapturingLeaseRenewalChannel(),
        perspectiveDrainChannel: harness.DrainChannel,
        leaseHandleOptions: Options.Create(new LeaseHandleOptions()),
        leaseRenewalOptions: Options.Create(new LeaseRenewalWorkerOptions()),
        deadLetterStore: NullDeadLetterStore.Instance,
        generationProvider: new DefaultGenerationProvider(),
        perspectiveNotificationListener: new NoOpWorkNotificationListener(),
        governor: PerspectiveWorker.CreateDefaultGovernor(options),
        recentlyProcessedEventCache: cache);

      return new Fixture { Worker = worker, Harness = harness, Registry = registry, Lifecycle = lifecycle, Logger = logger };
    }

    /// <summary>Starts the worker, waits for the first batch that carried work, and stops it.</summary>
    public async Task RunOneBatchAsync(CancellationToken cancellationToken) {
      var batchComplete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
      var batches = 0;
      Worker.OnBatchCycleComplete += () => {
        if (Interlocked.Increment(ref batches) == 1) {
          batchComplete.TrySetResult();
        }
      };
      using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
      await Worker.StartAsync(cts.Token);
      await batchComplete.Task.WaitAsync(cancellationToken);
      if (Worker.PendingPostLifecycle is { } pending) {
        await pending.WaitAsync(cancellationToken);
      }
      await cts.CancelAsync();
      // Await the worker body, not the task StartAsync returned (flaky-tests.md, Pattern 7). A body leaving through a
      // cancellation catch settles RanToCompletion or Canceled depending on thread-pool timing; either is a clean stop.
      if (Worker.ExecuteTask is { } body) {
        await body.WaitAsync(cancellationToken)
          .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
      }
    }
  }

  /// <summary>One captured log entry: its level, rendered message and structured values.</summary>
  private sealed record LogEntry(LogLevel Level, string Message, IReadOnlyDictionary<string, object?> Values);

  /// <summary>A logger with every level enabled that keeps each entry's structured values.</summary>
  private sealed class CapturingLogger : ILogger<PerspectiveWorker> {
    public ConcurrentQueue<LogEntry> Entries { get; } = new();

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) {
      var values = new Dictionary<string, object?>(StringComparer.Ordinal);
      if (state is IEnumerable<KeyValuePair<string, object?>> pairs) {
        foreach (var pair in pairs) {
          values[pair.Key] = pair.Value;
        }
      }
      Entries.Enqueue(new LogEntry(logLevel, formatter(state, exception), values));
    }
  }

  /// <summary>Delegates to a real coordinator and records every perspective completion signal.</summary>
  private sealed class RecordingLifecycleCoordinator(ILifecycleCoordinator inner) : ILifecycleCoordinator {
    public ConcurrentQueue<(Guid EventId, string Perspective)> Signaled { get; } = new();

    public bool SignalPerspectiveComplete(Guid eventId, string perspectiveName) {
      Signaled.Enqueue((eventId, perspectiveName));
      return inner.SignalPerspectiveComplete(eventId, perspectiveName);
    }

    public ILifecycleTracking BeginTracking(LifecycleTrackingPath path, Guid eventId, IMessageEnvelope envelope, LifecycleStage entryStage, MessageSource source,
      Guid? streamId = null, Type? perspectiveType = null) =>
      inner.BeginTracking(path, eventId, envelope, entryStage, source, streamId, perspectiveType);
    public ILifecycleTracking? GetTracking(LifecycleTrackingPath path, Guid eventId) => inner.GetTracking(path, eventId);
    public void ExpectCompletionsFrom(LifecycleTrackingPath path, Guid eventId, params PostLifecycleCompletionSource[] sources) =>
      inner.ExpectCompletionsFrom(path, eventId, sources);
    public ValueTask SignalSegmentCompleteAsync(LifecycleTrackingPath path, Guid eventId, PostLifecycleCompletionSource source, IServiceProvider scopedProvider,
      CancellationToken ct) => inner.SignalSegmentCompleteAsync(path, eventId, source, scopedProvider, ct);
    public void AbandonTracking(LifecycleTrackingPath path, Guid eventId) => inner.AbandonTracking(path, eventId);
    public void ExpectPerspectiveCompletions(Guid eventId, IReadOnlyList<string> perspectiveNames) =>
      inner.ExpectPerspectiveCompletions(eventId, perspectiveNames);
    public bool AreAllPerspectivesComplete(Guid eventId) => inner.AreAllPerspectivesComplete(eventId);
    public int CleanupStaleTracking(TimeSpan inactivityThreshold) => inner.CleanupStaleTracking(inactivityThreshold);
  }

  /// <summary>Claims nothing on its own; drain fetches return the configured rows for the requested streams.</summary>
  private sealed class Coordinator : IWorkCoordinator {
    public List<StreamEventData> DrainRows { get; init; } = [];

    public Task<WorkBatch> ClaimWorkAsync(ClaimWorkRequest request, CancellationToken cancellationToken = default) =>
      Task.FromResult(new WorkBatch { OutboxWork = [], InboxWork = [], PerspectiveWork = [] });

    public Task<List<StreamEventData>> GetStreamEventsAsync(Guid instanceId, Guid[] streamIds, CancellationToken cancellationToken = default) =>
      Task.FromResult(DrainRows.Where(r => streamIds.Contains(r.StreamId)).ToList());

    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion completion, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure failure, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new WorkCoordinatorStatistics());
    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default)
      => Task.FromResult<PerspectiveCursorInfo?>(null);
  }

  /// <summary>Deserializes a drain fetch to the configured envelopes of the rows it was handed.</summary>
  private sealed class EventStore : IEventStore {
    public List<MessageEnvelope<IEvent>> DrainEnvelopes { get; init; } = [];

    public List<MessageEnvelope<IEvent>> DeserializeStreamEvents(IReadOnlyList<StreamEventData> streamEvents, IReadOnlyList<Type> eventTypes) {
      var wanted = streamEvents.Select(r => r.EventId).ToHashSet();
      return [.. DrainEnvelopes.Where(e => wanted.Contains(e.MessageId.Value))];
    }

    public async IAsyncEnumerable<MessageEnvelope<IEvent>> ReadPolymorphicAsync(Guid streamId, Guid? fromEventId, IReadOnlyList<Type> eventTypes, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default) { await Task.CompletedTask; yield break; }
    public Task AppendAsync<TMessage>(Guid streamId, MessageEnvelope<TMessage> envelope, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task AppendAsync<TMessage>(Guid streamId, TMessage message, CancellationToken cancellationToken = default) where TMessage : notnull => Task.CompletedTask;
    public async IAsyncEnumerable<MessageEnvelope<TMessage>> ReadAsync<TMessage>(Guid streamId, long fromSequence, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default) { await Task.CompletedTask; yield break; }
    public async IAsyncEnumerable<MessageEnvelope<TMessage>> ReadAsync<TMessage>(Guid streamId, Guid? fromEventId, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default) { await Task.CompletedTask; yield break; }
    public Task<List<MessageEnvelope<IEvent>>> GetEventsBetweenPolymorphicAsync(Guid streamId, Guid? afterEventId, Guid upToEventId, IReadOnlyList<Type> eventTypes, CancellationToken cancellationToken = default) => Task.FromResult(new List<MessageEnvelope<IEvent>>());
    public Task<List<MessageEnvelope<TMessage>>> GetEventsBetweenAsync<TMessage>(Guid streamId, Guid? afterEventId, Guid upToEventId, CancellationToken cancellationToken = default) => Task.FromResult(new List<MessageEnvelope<TMessage>>());
    public Task<long> GetLastSequenceAsync(Guid streamId, CancellationToken cancellationToken = default) => Task.FromResult(-1L);
  }

  /// <summary>Records the event ids each (stream, perspective) runner applied, across every run entry point.</summary>
  private sealed class RecordingRegistry(List<PerspectiveRegistrationInfo> registrations) : IPerspectiveRunnerRegistry {
    private readonly ConcurrentQueue<(Guid StreamId, string Perspective, Guid EventId)> _applied = new();

    public List<Guid> AppliedEventIds(Guid streamId, string perspectiveName) =>
      [.. _applied.Where(a => a.StreamId == streamId && string.Equals(a.Perspective, perspectiveName, StringComparison.Ordinal)).Select(a => a.EventId)];

    public IPerspectiveRunner? GetRunner(string perspectiveName, IServiceProvider serviceProvider) => new Runner(this);
    public IReadOnlyList<PerspectiveRegistrationInfo> GetRegisteredPerspectives() => registrations;
    public IReadOnlyList<Type> GetEventTypes() => [typeof(ProbeEvent)];
    public IReadOnlySet<LifecycleStage> LifecycleStagesWithReceptors { get; set; } = new HashSet<LifecycleStage>();

    private sealed class Runner(RecordingRegistry owner) : IPerspectiveRunner {
      public Type PerspectiveType => typeof(object);

      public Task<PerspectiveCursorCompletion> RunAsync(Guid streamId, string perspectiveName, Guid? lastProcessedEventId, CancellationToken cancellationToken = default) {
        // Recorded with an empty event id: any run of a cooled perspective is a re-apply the test rejects.
        owner._applied.Enqueue((streamId, perspectiveName, Guid.Empty));
        return Task.FromResult(_completion(streamId, perspectiveName, Guid.Empty));
      }

      public Task<PerspectiveCursorCompletion> RunWithEventsAsync(Guid streamId, string perspectiveName, Guid? lastProcessedEventId, IReadOnlyList<MessageEnvelope<IEvent>> events, CancellationToken cancellationToken = default) {
        foreach (var e in events) {
          owner._applied.Enqueue((streamId, perspectiveName, e.MessageId.Value));
        }
        return Task.FromResult(_completion(streamId, perspectiveName, events.Count > 0 ? events[^1].MessageId.Value : Guid.Empty));
      }

      public Task<PerspectiveCursorCompletion> RewindAndRunAsync(Guid streamId, string perspectiveName, Guid triggeringEventId, CancellationToken cancellationToken = default) =>
        RunAsync(streamId, perspectiveName, null, cancellationToken);

      public Task BootstrapSnapshotAsync(Guid streamId, string perspectiveName, Guid lastProcessedEventId, CancellationToken cancellationToken = default) => Task.CompletedTask;

      private static PerspectiveCursorCompletion _completion(Guid streamId, string perspectiveName, Guid lastEventId) => new() {
        StreamId = streamId,
        PerspectiveName = perspectiveName,
        LastEventId = lastEventId,
        Status = PerspectiveProcessingStatus.Completed,
        PerspectiveType = typeof(object)
      };
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
