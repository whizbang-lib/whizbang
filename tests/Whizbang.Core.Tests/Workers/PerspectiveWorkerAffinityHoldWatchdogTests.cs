using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Tests.Helpers;
using Whizbang.Core.Tracing;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// A drain consumer stuck inside an apply shows from the outside as an idle process whose leased rows keep
/// lapsing and being re-offered; nothing in the logs said which stream, which perspective or which step.
/// The worker now records, per held affinity gate, who holds it, which step it is in and since when, lists
/// the holds on demand, and names at Warning every hold older than
/// <see cref="PerspectiveStreamAffinityOptions.LongHoldWarning"/> (once when it crosses the threshold, then
/// once per threshold while it persists). The ages are computed against a clock the caller may supply, so
/// the tests need no waiting.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Workers/PerspectiveWorker.cs</code-under-test>
public class PerspectiveWorkerAffinityHoldWatchdogTests {
  private const string PERSPECTIVE = "Test.WatchdogPerspective";

  [Test]
  public async Task HeldGate_IsListedWithItsPhase_AndAgesAgainstTheGivenClockAsync() {
    await using var f = await _Fixture.StartAsync(longHoldWarning: TimeSpan.FromSeconds(60));
    await f.Harness.EnqueueDrainStreamAsync(f.StreamId);
    await f.Registry.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

    var holds = f.Worker.SnapshotAffinityHolds(TimeSpan.Zero);
    await Assert.That(holds.Count).IsEqualTo(1)
      .Because("the drain consumer holds exactly one (stream, perspective) gate while the runner is inside the apply");
    await Assert.That(holds[0].StreamId).IsEqualTo(f.StreamId);
    await Assert.That(holds[0].PerspectiveName).IsEqualTo(PERSPECTIVE);
    await Assert.That(holds[0].Phase).IsEqualTo("apply")
      .Because("the step is what tells an operator where the consumer is stuck");
    await Assert.That(holds[0].Path).IsEqualTo("drain");

    var now = TimeProvider.System.GetUtcNow().UtcTicks;
    await Assert.That(f.Worker.SnapshotAffinityHolds(TimeSpan.FromSeconds(60), now + TimeSpan.FromSeconds(30).Ticks)).IsEmpty()
      .Because("a hold younger than the threshold is normal work, not a stall");
    var old = f.Worker.SnapshotAffinityHolds(TimeSpan.FromSeconds(60), now + TimeSpan.FromSeconds(61).Ticks);
    await Assert.That(old.Count).IsEqualTo(1);
    await Assert.That(old[0].Held >= TimeSpan.FromSeconds(61)).IsTrue();

    f.Registry.Release.TrySetResult();
    await f.Coordinator.WaitForCompletionReportedAsync(TimeSpan.FromSeconds(5));
    await f.StopAsync();
    await Assert.That(f.Worker.SnapshotAffinityHolds(TimeSpan.Zero)).IsEmpty()
      .Because("a released gate is not a hold");
  }

  [Test]
  public async Task LongHold_IsNamedAtWarning_OncePerThresholdAsync() {
    await using var f = await _Fixture.StartAsync(longHoldWarning: TimeSpan.FromSeconds(60));
    await f.Harness.EnqueueDrainStreamAsync(f.StreamId);
    await f.Registry.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
    var now = TimeProvider.System.GetUtcNow().UtcTicks;

    await Assert.That(f.Worker.ReportLongAffinityHolds(now + TimeSpan.FromSeconds(30).Ticks)).IsEqualTo(0)
      .Because("under the threshold nothing is reported");
    await Assert.That(f.Worker.ReportLongAffinityHolds(now + TimeSpan.FromSeconds(61).Ticks)).IsEqualTo(1);
    var warning = f.Logger.Snapshot().Single(e => e.Level == LogLevel.Warning && e.Message.Contains("held its affinity gate", StringComparison.Ordinal));
    await Assert.That(warning.Message).Contains(f.StreamId.ToString("D", System.Globalization.CultureInfo.InvariantCulture))
      .Because("the warning names the stream");
    await Assert.That(warning.Message).Contains(PERSPECTIVE)
      .Because("and the perspective");
    await Assert.That(warning.Message).Contains("apply")
      .Because("and the step it is stuck in");
    await Assert.That(warning.Message).Contains("drain")
      .Because("and the processing path");

    await Assert.That(f.Worker.ReportLongAffinityHolds(now + TimeSpan.FromSeconds(62).Ticks)).IsEqualTo(0)
      .Because("named once per threshold, not once per tick");
    await Assert.That(f.Worker.ReportLongAffinityHolds(now + TimeSpan.FromSeconds(122).Ticks)).IsEqualTo(1)
      .Because("and again after another threshold while the hold persists");

    f.Registry.Release.TrySetResult();
    await f.Coordinator.WaitForCompletionReportedAsync(TimeSpan.FromSeconds(5));
  }

  [Test]
  public async Task WatchdogOff_ReportsNothingAsync() {
    await using var f = await _Fixture.StartAsync(longHoldWarning: TimeSpan.Zero);
    await f.Harness.EnqueueDrainStreamAsync(f.StreamId);
    await f.Registry.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
    var now = TimeProvider.System.GetUtcNow().UtcTicks;

    await Assert.That(f.Worker.ReportLongAffinityHolds(now + TimeSpan.FromHours(1).Ticks)).IsEqualTo(0)
      .Because("a zero threshold turns the watchdog off");
    await Assert.That(f.Worker.SnapshotAffinityHolds(TimeSpan.Zero).Count).IsEqualTo(1)
      .Because("the hold itself is still visible on demand");

    f.Registry.Release.TrySetResult();
    await f.Coordinator.WaitForCompletionReportedAsync(TimeSpan.FromSeconds(5));
  }

  [Test]
  public async Task DrainWidth_IsClampedToHalfTheGate_AndLoggedOnceAsync() {
    await using var f = await _Fixture.StartAsync(longHoldWarning: TimeSpan.FromSeconds(60), gateMaxConcurrent: 8);
    await f.Harness.EnqueueDrainStreamAsync(f.StreamId);
    await f.Registry.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
    f.Registry.Release.TrySetResult();
    await f.Coordinator.WaitForCompletionReportedAsync(TimeSpan.FromSeconds(5));

    var clamps = f.Logger.Snapshot().Where(e => e.Level == LogLevel.Warning && e.Message.Contains("clamped", StringComparison.Ordinal)).ToList();
    await Assert.That(clamps.Count).IsEqualTo(1)
      .Because("the default consumers times the default width against an 8-slot gate leaves one per consumer, and the clamp is said once");
    await Assert.That(clamps[0].Message).Contains("MaxConcurrent=8")
      .Because("the warning names the gate it clamped against");
  }

  [Test]
  public async Task Watchdog_TicksOnTheWorkerClock_AndNamesAHoldPastTheThresholdAsync() {
    var clock = new FakeTimeProvider();
    await using var f = await _Fixture.StartAsync(longHoldWarning: TimeSpan.FromSeconds(60), timeProvider: clock);
    await f.Harness.EnqueueDrainStreamAsync(f.StreamId);
    await f.Registry.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

    clock.Advance(TimeSpan.FromSeconds(61));
    var warning = await f.Logger
      .WaitForAsync(e => e.Level == LogLevel.Warning && e.Message.Contains("held its affinity gate", StringComparison.Ordinal))
      .WaitAsync(TimeSpan.FromSeconds(10));

    await Assert.That(warning.Message).Contains(PERSPECTIVE)
      .Because("the periodic check runs on the worker's clock and names the hold without anyone asking");

    f.Registry.Release.TrySetResult();
    await f.Coordinator.WaitForCompletionReportedAsync(TimeSpan.FromSeconds(5));
  }

  #region Fixture

  private sealed record WatchdogTestEvent(string Data) : IEvent;

  private sealed class _Fixture : IAsyncDisposable {
    public BlockingRunnerRegistry Registry { get; } = new();
    public CapturingLogger<PerspectiveWorker> Logger { get; } = new();
    public PerspectiveWorkerTestHarness Harness { get; } = new();
    public HoldCoordinator Coordinator { get; } = new();
    public PerspectiveWorker Worker { get; private set; } = null!;
    public Guid StreamId { get; } = (Guid)TrackedGuid.NewMedo();
    private readonly CancellationTokenSource _cts = new();
    private Task _workerTask = Task.CompletedTask;
    private bool _stopped;

    public static async Task<_Fixture> StartAsync(TimeSpan longHoldWarning, int gateMaxConcurrent = 0, TimeProvider? timeProvider = null) {
      var f = new _Fixture();
      var eventId = (Guid)TrackedGuid.NewMedo();
      f.Coordinator.StreamEventsToReturn = [
        new StreamEventData {
          StreamId = f.StreamId,
          EventId = eventId,
          EventType = TypeNameFormatter.Format(typeof(WatchdogTestEvent)),
          EventData = JsonSerializer.Serialize(new WatchdogTestEvent("hold")),
          Metadata = null,
          Scope = null,
          EventWorkId = (Guid)TrackedGuid.NewMedo()
        }
      ];
      var eventStore = new HoldEventStore {
        DeserializedEventsToReturn = [
          new MessageEnvelope<IEvent> {
            MessageId = new MessageId(eventId),
            Payload = new WatchdogTestEvent("hold"),
            Hops = [
              new MessageHop {
                Type = HopType.Current,
                Timestamp = DateTimeOffset.UtcNow,
                CorrelationId = CorrelationId.New(),
                CausationId = MessageId.New(),
                ServiceInstance = new ServiceInstanceInfo { InstanceId = (Guid)TrackedGuid.NewMedo(), ServiceName = "TestService", HostName = "test-host", ProcessId = 1 }
              }
            ],
            DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
          }
        ]
      };
      var instanceProvider = new HoldInstanceProvider();
      var services = new ServiceCollection();
      services.AddSingleton<IWorkCoordinator>(f.Coordinator);
      services.AddSingleton<IPerspectiveRunnerRegistry>(f.Registry);
      services.AddSingleton<IServiceInstanceProvider>(instanceProvider);
      services.AddSingleton<IEventStore>(eventStore);
      services.AddSingleton<IEventTypeProvider>(new HoldEventTypeProvider([typeof(WatchdogTestEvent)]));
      services.AddLogging();
      var sp = services.BuildServiceProvider();

      f.Worker = new PerspectiveWorker(
        instanceProvider: instanceProvider,
        scopeFactory: sp.GetRequiredService<IServiceScopeFactory>(),
        options: Options.Create(new PerspectiveWorkerOptions { PollingIntervalMilliseconds = 50 }),
        tracingOptions: null,
        completionStrategy: new InstantCompletionStrategy(),
        eventTypeProvider: null,
        logger: f.Logger,
        streamAffinityOptions: Options.Create(new PerspectiveStreamAffinityOptions { LongHoldWarning = longHoldWarning }),
        perspectiveChannelWriter: f.Harness.ChannelWriter,
        perspectiveCompletionChannel: f.Harness.CompletionCapture,
        failureChannel: f.Harness.FailureCapture,
        perspectiveDrainChannel: f.Harness.DrainChannel,
        schemaReadyGate: SchemaReadyGate.AlreadyReady(),
        gate: gateMaxConcurrent > 0 ? new WorkCoordinatorGate(maxConcurrent: gateMaxConcurrent) : null,
        timeProvider: timeProvider);
      f._workerTask = f.Worker.StartAsync(f._cts.Token);
      return f;
    }

    public async Task StopAsync() {
      if (_stopped) {
        return;
      }
      _stopped = true;
      Registry.Release.TrySetResult();
      await _cts.CancelAsync();
      try {
        await _workerTask.WaitAsync(TimeSpan.FromSeconds(10));
      } catch (OperationCanceledException) {
        // expected on shutdown
      }
    }

    public async ValueTask DisposeAsync() {
      await StopAsync();
      _cts.Dispose();
    }
  }

  /// <summary>A runner that reports when it is inside the apply and stays there until released.</summary>
  private sealed class BlockingRunnerRegistry : IPerspectiveRunnerRegistry {
    public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public IPerspectiveRunner? GetRunner(string perspectiveName, IServiceProvider serviceProvider) => new BlockingRunner(this);

    public IReadOnlyList<PerspectiveRegistrationInfo> GetRegisteredPerspectives() =>
      [new PerspectiveRegistrationInfo(
        PERSPECTIVE,
        "global::Test.WatchdogPerspective",
        "global::Test.WatchdogModel",
        [TypeNameFormatter.Format(typeof(WatchdogTestEvent))])];

    public IReadOnlyList<Type> GetEventTypes() => [typeof(WatchdogTestEvent)];
    public IReadOnlySet<LifecycleStage> LifecycleStagesWithReceptors { get; } = new HashSet<LifecycleStage>();

    private sealed class BlockingRunner(BlockingRunnerRegistry registry) : IPerspectiveRunner {
      public Type PerspectiveType => typeof(object);

      public Task<PerspectiveCursorCompletion> RunAsync(Guid streamId, string perspectiveName, Guid? lastProcessedEventId, CancellationToken cancellationToken) =>
        Task.FromResult(new PerspectiveCursorCompletion { StreamId = streamId, PerspectiveName = perspectiveName, LastEventId = (Guid)TrackedGuid.NewMedo(), Status = PerspectiveProcessingStatus.Completed });

      public async Task<PerspectiveCursorCompletion> RunWithEventsAsync(Guid streamId, string perspectiveName, Guid? lastProcessedEventId, IReadOnlyList<MessageEnvelope<IEvent>> events, CancellationToken cancellationToken = default) {
        registry.Entered.TrySetResult();
        await registry.Release.Task;
        return new PerspectiveCursorCompletion { StreamId = streamId, PerspectiveName = perspectiveName, LastEventId = events.Count > 0 ? events[^1].MessageId.Value : (Guid)TrackedGuid.NewMedo(), Status = PerspectiveProcessingStatus.Completed };
      }

      public Task<PerspectiveCursorCompletion> RewindAndRunAsync(Guid streamId, string perspectiveName, Guid triggeringEventId, CancellationToken cancellationToken = default) =>
        RunAsync(streamId, perspectiveName, null, cancellationToken);

      public Task BootstrapSnapshotAsync(Guid streamId, string perspectiveName, Guid lastProcessedEventId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
  }

  private sealed class HoldCoordinator : IWorkCoordinator {
    private readonly TaskCompletionSource _completionReported = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public List<StreamEventData> StreamEventsToReturn { get; set; } = [];

    public async Task WaitForCompletionReportedAsync(TimeSpan timeout) {
      using var cts = new CancellationTokenSource(timeout);
      try {
        await _completionReported.Task.WaitAsync(cts.Token);
      } catch (OperationCanceledException) {
        throw new TimeoutException($"Completion was not reported within {timeout}");
      }
    }

    public Task<WorkBatch> ClaimWorkAsync(ClaimWorkRequest request, CancellationToken cancellationToken = default)
      => Task.FromResult(new WorkBatch { OutboxWork = [], InboxWork = [], PerspectiveWork = [], PerspectiveStreamIds = [] });
    public Task<List<StreamEventData>> GetStreamEventsAsync(Guid instanceId, Guid[] streamIds, CancellationToken cancellationToken = default)
      => Task.FromResult(new List<StreamEventData>(StreamEventsToReturn));
    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion completion, CancellationToken cancellationToken = default) {
      _completionReported.TrySetResult();
      return Task.CompletedTask;
    }
    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure failure, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount = 2, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new WorkCoordinatorStatistics());
    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default)
      => Task.FromResult<PerspectiveCursorInfo?>(null);
  }

  private sealed class HoldEventStore : IEventStore {
    public List<MessageEnvelope<IEvent>> DeserializedEventsToReturn { get; set; } = [];
    public List<MessageEnvelope<IEvent>> DeserializeStreamEvents(IReadOnlyList<StreamEventData> streamEvents, IReadOnlyList<Type> eventTypes) => [.. DeserializedEventsToReturn];
    public Task AppendAsync<TMessage>(Guid streamId, MessageEnvelope<TMessage> envelope, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task AppendAsync<TMessage>(Guid streamId, TMessage message, CancellationToken cancellationToken = default) where TMessage : notnull => Task.CompletedTask;
    public async IAsyncEnumerable<MessageEnvelope<TMessage>> ReadAsync<TMessage>(Guid streamId, long fromSequence, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default) { await Task.CompletedTask; yield break; }
    public async IAsyncEnumerable<MessageEnvelope<TMessage>> ReadAsync<TMessage>(Guid streamId, Guid? fromEventId, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default) { await Task.CompletedTask; yield break; }
    public async IAsyncEnumerable<MessageEnvelope<IEvent>> ReadPolymorphicAsync(Guid streamId, Guid? fromEventId, IReadOnlyList<Type> eventTypes, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default) { await Task.CompletedTask; yield break; }
    public Task<List<MessageEnvelope<IEvent>>> GetEventsBetweenPolymorphicAsync(Guid streamId, Guid? afterEventId, Guid upToEventId, IReadOnlyList<Type> eventTypes, CancellationToken cancellationToken = default) => Task.FromResult(new List<MessageEnvelope<IEvent>>());
    public Task<List<MessageEnvelope<TMessage>>> GetEventsBetweenAsync<TMessage>(Guid streamId, Guid? afterEventId, Guid upToEventId, CancellationToken cancellationToken = default) => Task.FromResult(new List<MessageEnvelope<TMessage>>());
    public Task<long> GetLastSequenceAsync(Guid streamId, CancellationToken cancellationToken = default) => Task.FromResult(-1L);
  }

  private sealed class HoldEventTypeProvider(IReadOnlyList<Type> eventTypes) : IEventTypeProvider {
    public IReadOnlyList<Type> GetEventTypes() => eventTypes;
  }

  private sealed class HoldInstanceProvider : IServiceInstanceProvider {
    public Guid InstanceId { get; } = (Guid)TrackedGuid.NewMedo();
    public string ServiceName { get; } = "TestService";
    public string HostName { get; } = "test-host";
    public int ProcessId { get; } = 4321;
    public ServiceInstanceInfo ToInfo() => new() { ServiceName = ServiceName, InstanceId = InstanceId, HostName = HostName, ProcessId = ProcessId };
  }

  #endregion
}
