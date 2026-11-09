// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Lifecycle;
using Whizbang.Core.Messaging;
using Whizbang.Core.Notifications;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Perspectives.Sync;
using Whizbang.Core.Tracing;
using Whizbang.Core.Workers;
using Whizbang.Testing.Options;

namespace Whizbang.Core.Tests.Workers;

#pragma warning disable CA1707, IDE1006

/// <summary>
/// The Blocking startup rewind scan holds only the read-model barrier, and only for the cursors it found flagged (#2214).
/// The consumer loops, which are what clear those cursors, run from the start. The barrier opens when every scan-time
/// cursor is cleared (rewound by this worker, or found no longer flagged by a keyed re-read), or at the cap with a
/// Warning naming what is still rewinding. A flag raised after the scan never extends the wait.
/// </summary>
/// <remarks>
/// The worker runs on a fake clock. The NOTIFY listener is configured with a long safety-net cadence, so the only timer
/// armed at <see cref="POLL"/> is the barrier's re-read poll, and each poll is fired by the test, never by real time.
/// </remarks>
/// <code-under-test>src/Whizbang.Core/Workers/PerspectiveWorker.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Workers/StartupRewindSet.cs</code-under-test>
[NotInParallel("PerspectiveChannelModeTests")]
public class PerspectiveWorkerStartupRewindBarrierTests {
  private const string PERSPECTIVE = "TestPerspective";
  private const string OTHER_PERSPECTIVE = "OtherPerspective";
  private const string SCAN_LOG_CATEGORY = "Whizbang.Core.Workers.PerspectiveStartupScan";
  private static readonly TimeSpan POLL = TimeSpan.FromMilliseconds(1_000);

  // ============================================================
  // Barrier contract
  // ============================================================

  [Test]
  [Timeout(30000)]
  public async Task Blocking_BarrierHeldUntilEveryScanTimeCursorClears_RereadingOnlyThoseStreamsAsync(CancellationToken cancellationToken) {
    var a = Guid.CreateVersion7();
    var b = Guid.CreateVersion7();
    var coordinator = new BarrierCoordinator { ScanResult = [_flagged(a, PERSPECTIVE), _flagged(b, PERSPECTIVE)] };
    coordinator.BatchAnswers.Enqueue([_cursor(a, PERSPECTIVE, PerspectiveProcessingStatus.RewindRequired)]); // b cleared
    coordinator.BatchAnswers.Enqueue([_cursor(a, PERSPECTIVE, PerspectiveProcessingStatus.Completed)]);      // a cleared
    await using var fx = _build(coordinator);

    await fx.Worker.StartAsync(CancellationToken.None);
    await fx.Clock.FireAsync(POLL, cancellationToken);
    await coordinator.WaitForBatchQueriesAsync(1, cancellationToken);

    await Assert.That(fx.Worker.StartupScanComplete.IsCompleted).IsFalse()
      .Because("one scan-time cursor is still flagged, so lens reads must stay held");

    await fx.Clock.FireAsync(POLL, cancellationToken);
    await fx.Worker.StartupScanComplete.WaitAsync(cancellationToken);

    await Assert.That(coordinator.BatchQueries[1]).IsEquivalentTo([a])
      .Because("the re-read asks only for streams still holding a scan-time cursor, by key");
    await Assert.That(coordinator.ScanQueryCount).IsEqualTo(1)
      .Because("the whole cursor table is scanned once, at startup, never again while waiting");
    await Assert.That(fx.Log.Entries).Contains(e => e.Level == LogLevel.Information && e.Message.Contains("2 of 2 flagged cursors"))
      .Because("the completion line says how many of the scan-time set cleared");
  }

  [Test]
  [Timeout(30000)]
  public async Task Blocking_FlagRaisedAfterTheScan_DoesNotExtendTheWaitAsync(CancellationToken cancellationToken) {
    var a = Guid.CreateVersion7();
    var coordinator = new BarrierCoordinator { ScanResult = [_flagged(a, PERSPECTIVE)] };
    // The scan-time cursor cleared; another perspective on the same stream was flagged after the scan.
    coordinator.BatchAnswers.Enqueue([
      _cursor(a, PERSPECTIVE, PerspectiveProcessingStatus.Completed),
      _cursor(a, OTHER_PERSPECTIVE, PerspectiveProcessingStatus.RewindRequired)
    ]);
    await using var fx = _build(coordinator);

    await fx.Worker.StartAsync(CancellationToken.None);
    await fx.Clock.FireAsync(POLL, cancellationToken);
    await fx.Worker.StartupScanComplete.WaitAsync(cancellationToken);

    await Assert.That(coordinator.BatchQueries.Count).IsEqualTo(1)
      .Because("the barrier opened on the first re-read; the later flag is the consumer loops' business, not the barrier's");
  }

  [Test]
  [Timeout(30000)]
  public async Task Blocking_ConsumerLoopsRunWhileTheBarrierIsHeldAsync(CancellationToken cancellationToken) {
    // The original defect: the scan waited for rewinds before any consumer loop had started, and only a consumer loop
    // performs them. No time passes in this test, so with that ordering the drain cycle below would never run.
    var coordinator = new BarrierCoordinator { ScanResult = [_flagged(Guid.CreateVersion7(), PERSPECTIVE)] };
    await using var fx = _build(coordinator);
    var cycleDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    fx.Worker.OnBatchCycleComplete += () => cycleDone.TrySetResult();

    await fx.Worker.StartAsync(CancellationToken.None);
    await coordinator.WaitForScanAsync(cancellationToken);
    await fx.Harness.EnqueueDrainStreamAsync(Guid.CreateVersion7(), cancellationToken);
    await cycleDone.Task.WaitAsync(cancellationToken);

    await Assert.That(fx.Worker.StartupScanComplete.IsCompleted).IsFalse()
      .Because("work flows while lens reads are still held for the flagged cursor");
  }

  [Test]
  [Timeout(30000)]
  public async Task Blocking_ThisWorkersRewindOfTheScanTimeCursor_OpensTheBarrierWithoutAPollAsync(CancellationToken cancellationToken) {
    var stream = Guid.CreateVersion7();
    var trigger = Guid.CreateVersion7();
    var coordinator = new BarrierCoordinator { ScanResult = [_flagged(stream, PERSPECTIVE)] };
    coordinator.Cursors[(stream, PERSPECTIVE)] = _cursor(stream, PERSPECTIVE, PerspectiveProcessingStatus.RewindRequired) with {
      LastEventId = Guid.CreateVersion7(),
      RewindTriggerEventId = trigger
    };
    await using var fx = _build(coordinator);

    await fx.Worker.StartAsync(CancellationToken.None);
    await coordinator.WaitForScanAsync(cancellationToken);
    await fx.Harness.EnqueueWorkAsync(new PerspectiveWork {
      StreamId = stream,
      PerspectiveName = PERSPECTIVE,
      LastProcessedEventId = Guid.CreateVersion7(),
      PartitionNumber = 1,
      Status = PerspectiveProcessingStatus.RewindRequired,
      WorkId = Guid.CreateVersion7()
    }, cancellationToken);
    await fx.Worker.StartupScanComplete.WaitAsync(cancellationToken);

    await Assert.That(fx.Runner.RewindCount).IsEqualTo(1);
    await Assert.That(coordinator.BatchQueries.Count).IsEqualTo(0)
      .Because("the rewind's own completion is the signal; no poll had to come round");
  }

  [Test]
  [Timeout(60000)]
  public async Task Blocking_AtTheCap_OpensTheBarrierAndWarnsNamingWhatIsStillRewindingAsync(CancellationToken cancellationToken) {
    var stuck = Guid.CreateVersion7();
    var coordinator = new BarrierCoordinator {
      ScanResult = [_flagged(stuck, PERSPECTIVE)],
      DefaultBatchAnswer = [_cursor(stuck, PERSPECTIVE, PerspectiveProcessingStatus.RewindRequired)]
    };
    await using var fx = _build(coordinator);

    await fx.Worker.StartAsync(CancellationToken.None);
    for (var i = 1; i <= 100; i++) {
      await Assert.That(fx.Worker.StartupScanComplete.IsCompleted).IsFalse()
        .Because($"the cap is 100 polls; only {i - 1} have run");
      await fx.Clock.FireAsync(POLL, cancellationToken);
      await coordinator.WaitForBatchQueriesAsync(1, cancellationToken);
    }
    await fx.Worker.StartupScanComplete.WaitAsync(cancellationToken);

    await Assert.That(coordinator.BatchQueries.Count).IsEqualTo(100);
    await Assert.That(fx.Worker.StartupScanComplete.IsCompletedSuccessfully).IsTrue()
      .Because("reaching the cap opens reads; it is not a failure of the barrier");
    await Assert.That(fx.Log.Entries).Contains(e =>
        e.Level == LogLevel.Warning
        && e.Message.Contains($"{stuck}/{PERSPECTIVE}")
        && e.Message.Contains("1 of 1"))
      .Because("an operator must see which streams and perspectives were still rewinding when reads opened");
  }

  // ============================================================
  // Paths that do not wait
  // ============================================================

  [Test]
  [Timeout(30000)]
  public async Task CleanScan_OpensTheBarrierAtOnce_WithNoRereadAsync(CancellationToken cancellationToken) {
    var coordinator = new BarrierCoordinator { ScanResult = [] };
    await using var fx = _build(coordinator);

    await fx.Worker.StartAsync(CancellationToken.None);
    await fx.Worker.StartupScanComplete.WaitAsync(cancellationToken);

    await Assert.That(coordinator.BatchQueries.Count).IsEqualTo(0);
  }

  [Test]
  [Timeout(30000)]
  public async Task BackgroundMode_OpensTheBarrierAfterTheScan_WithNoRereadAsync(CancellationToken cancellationToken) {
    var coordinator = new BarrierCoordinator { ScanResult = [_flagged(Guid.CreateVersion7(), PERSPECTIVE)] };
    await using var fx = _build(coordinator, RewindStartupMode.Background);

    await fx.Worker.StartAsync(CancellationToken.None);
    await fx.Worker.StartupScanComplete.WaitAsync(cancellationToken);

    await Assert.That(coordinator.BatchQueries.Count).IsEqualTo(0)
      .Because("Background mode does not hold reads for flagged cursors");
  }

  // ============================================================
  // Failure and shutdown while waiting
  // ============================================================

  [Test]
  [Timeout(30000)]
  public async Task Blocking_RereadThrows_LogsAndOpensTheBarrierAsync(CancellationToken cancellationToken) {
    var coordinator = new BarrierCoordinator {
      ScanResult = [_flagged(Guid.CreateVersion7(), PERSPECTIVE)],
      BatchException = new InvalidOperationException("simulated re-read failure")
    };
    await using var fx = _build(coordinator);

    await fx.Worker.StartAsync(CancellationToken.None);
    await fx.Clock.FireAsync(POLL, cancellationToken);
    await fx.Worker.StartupScanComplete.WaitAsync(cancellationToken);

    await Assert.That(fx.Log.Entries).Contains(e => e.Level == LogLevel.Warning && e.Exception is InvalidOperationException)
      .Because("a failed re-read is reported, and reads open as they would after a failed scan");
  }

  [Test]
  [Timeout(30000)]
  public async Task Blocking_RereadOnDisposedServices_SettlesTheBarrierCanceledAsync(CancellationToken cancellationToken) {
    var coordinator = new BarrierCoordinator {
      ScanResult = [_flagged(Guid.CreateVersion7(), PERSPECTIVE)],
      BatchException = new ObjectDisposedException("service provider")
    };
    await using var fx = _build(coordinator);

    await fx.Worker.StartAsync(CancellationToken.None);
    await fx.Clock.FireAsync(POLL, cancellationToken);

    // WhenAny never throws, so a barrier that never settles fails on the test's timeout rather than passing as canceled.
    await Task.WhenAny(fx.Worker.StartupScanComplete).WaitAsync(cancellationToken);

    await Assert.That(fx.Worker.StartupScanComplete.IsCanceled).IsTrue()
      .Because("a disposed container is a shutdown, not a scan that finished");
  }

  [Test]
  [Timeout(30000)]
  public async Task Blocking_StopWhileWaiting_SettlesTheBarrierCanceledAndStopsCleanlyAsync(CancellationToken cancellationToken) {
    var coordinator = new BarrierCoordinator { ScanResult = [_flagged(Guid.CreateVersion7(), PERSPECTIVE)] };
    var fx = _build(coordinator);

    using var cts = new CancellationTokenSource();
    await fx.Worker.StartAsync(cts.Token);
    await coordinator.WaitForScanAsync(cancellationToken);
    await fx.Clock.WaitForArmedAsync(POLL, cancellationToken);
    await fx.Worker.StopAsync(CancellationToken.None);

    await Assert.That(fx.Worker.StartupScanComplete.IsCanceled).IsTrue()
      .Because("a barrier that waited on a stopping worker observes the stop rather than hanging");
    await Assert.That(fx.Worker.ExecuteTask!.IsFaulted).IsFalse();
    await fx.DisposeAsync();
  }

  // ============================================================
  // Fixture
  // ============================================================

  private static RewindCursorInfo _flagged(Guid streamId, string perspective) =>
    new(streamId, perspective, LastEventId: null, RewindTriggerEventId: Guid.CreateVersion7());

  private static PerspectiveCursorInfo _cursor(Guid streamId, string perspective, PerspectiveProcessingStatus status) =>
    new() { StreamId = streamId, PerspectiveName = perspective, Status = status };

  private sealed record Fixture(
    PerspectiveWorker Worker,
    PerspectiveWorkerTestHarness Harness,
    ArmedTimerClock Clock,
    ListLoggerProvider Log,
    RewindingRunner Runner,
    ServiceProvider Services) : IAsyncDisposable {
    private bool _disposed;

    public async ValueTask DisposeAsync() {
      if (_disposed) {
        return;
      }
      _disposed = true;
      try {
        await Worker.StopAsync(CancellationToken.None);
      } catch (OperationCanceledException) {
        // stopping is teardown; its outcome is not what these tests assert
      }
      Worker.Dispose();
      await Services.DisposeAsync();
    }
  }

  private static Fixture _build(BarrierCoordinator coordinator, RewindStartupMode mode = RewindStartupMode.Blocking) {
    var harness = new PerspectiveWorkerTestHarness();
    var clock = new ArmedTimerClock();
    var log = new ListLoggerProvider(SCAN_LOG_CATEGORY);
    var runner = new RewindingRunner();
    var instanceProvider = new InstanceProvider();
    var options = new PerspectiveWorkerOptions {
      PollingIntervalMilliseconds = (int)POLL.TotalMilliseconds,
      // The consumer loop's idle timer uses this cadence when NOTIFY is configured, so POLL is the barrier's alone.
      NotifyHealthyPollingIntervalMilliseconds = 1_000_000,
      MaxConcurrentDrainConsumers = 1,
    };

    var services = new ServiceCollection();
    services.TryAddWhizbangDefaults();
    services.AddSingleton<IWorkCoordinator>(coordinator);
    services.AddSingleton<IPerspectiveRunnerRegistry>(new SingleRunnerRegistry(PERSPECTIVE, runner));
    services.AddSingleton<IServiceInstanceProvider>(instanceProvider);
    services.AddLogging(b => b.AddProvider(log));
    var sp = services.BuildServiceProvider();

    var worker = new PerspectiveWorker(
      instanceProvider: instanceProvider,
      scopeFactory: sp.GetRequiredService<IServiceScopeFactory>(),
      options: Options.Create(options),
      tracingOptions: new StaticOptionsMonitor<TracingOptions>(new TracingOptions()),
      completionStrategy: new InstantCompletionStrategy(logger: NullLogger<InstantCompletionStrategy>.Instance),
      eventTypeProvider: sp.GetRequiredService<IEventTypeProvider>(),
      syncSignaler: new LocalSyncSignaler(NullLogger<LocalSyncSignaler>.Instance),
      syncEventTracker: new SyncEventTracker(),
      logger: NullLogger<PerspectiveWorker>.Instance,
      snapshotStore: NullPerspectiveSnapshotStore.Instance,
      streamLocker: NullPerspectiveStreamLocker.Instance,
      streamLockOptions: Options.Create(new PerspectiveStreamLockOptions()),
      streamAffinityOptions: Options.Create(new PerspectiveStreamAffinityOptions()),
      processedEventCacheObserver: NullProcessedEventCacheObserver.Instance,
      workChannelWriter: new WorkChannelWriter(),
      rewindOptions: Options.Create(new PerspectiveRewindOptions { StartupRewindMode = mode }),
      perspectiveChannelWriter: harness.ChannelWriter,
      perspectiveCompletionChannel: harness.CompletionCapture,
      failureChannel: harness.FailureCapture,
      leaseRenewalChannel: harness.LeaseRenewalCapture,
      perspectiveDrainChannel: harness.DrainChannel,
      leaseHandleOptions: Options.Create(new LeaseHandleOptions()),
      leaseRenewalOptions: Options.Create(new LeaseRenewalWorkerOptions()),
      deadLetterStore: NullDeadLetterStore.Instance,
      generationProvider: new DefaultGenerationProvider(),
      perspectiveNotificationListener: new ConfiguredNotificationListener(),
      governor: PerspectiveWorker.CreateDefaultGovernor(options),
      timeProvider: clock);
    return new Fixture(worker, harness, clock, log, runner, sp);
  }

  // ============================================================
  // Test doubles
  // ============================================================

  /// <summary>A fake clock that reports each timer the code under test arms, with its due time.</summary>
  private sealed class ArmedTimerClock : FakeTimeProvider {
    private readonly Channel<TimeSpan> _armed = Channel.CreateUnbounded<TimeSpan>();

    public ArmedTimerClock() : base(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)) { }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) {
      var timer = base.CreateTimer(callback, state, dueTime, period);
      _armed.Writer.TryWrite(dueTime);
      return timer;
    }

    /// <summary>Completes once a timer due in <paramref name="due"/> has been armed, skipping others.</summary>
    public async Task WaitForArmedAsync(TimeSpan due, CancellationToken cancellationToken) {
      TimeSpan armed;
      do {
        armed = await _armed.Reader.ReadAsync(cancellationToken);
      } while (armed != due);
    }

    /// <summary>Waits for the barrier to arm its poll, then advances past it.</summary>
    public async Task FireAsync(TimeSpan due, CancellationToken cancellationToken) {
      await WaitForArmedAsync(due, cancellationToken);
      Advance(due);
    }
  }

  private sealed class BarrierCoordinator : IWorkCoordinator {
    public IReadOnlyList<RewindCursorInfo> ScanResult { get; init; } = [];
    public Queue<List<PerspectiveCursorInfo>> BatchAnswers { get; } = new();
    public List<PerspectiveCursorInfo> DefaultBatchAnswer { get; init; } = [];
    public Exception? BatchException { get; init; }
    public ConcurrentDictionary<(Guid StreamId, string PerspectiveName), PerspectiveCursorInfo> Cursors { get; } = new();
    public List<Guid[]> BatchQueries { get; } = [];

    private int _scanQueryCount;
    public int ScanQueryCount => _scanQueryCount;
    private readonly TaskCompletionSource _scanned = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly SemaphoreSlim _batchSignal = new(0, int.MaxValue);

    public Task WaitForScanAsync(CancellationToken cancellationToken) => _scanned.Task.WaitAsync(cancellationToken);

    public async Task WaitForBatchQueriesAsync(int count, CancellationToken cancellationToken) {
      for (var i = 0; i < count; i++) {
        await _batchSignal.WaitAsync(cancellationToken);
      }
    }

    public Task<IReadOnlyList<RewindCursorInfo>> GetCursorsRequiringRewindAsync(CancellationToken cancellationToken = default) {
      Interlocked.Increment(ref _scanQueryCount);
      _scanned.TrySetResult();
      return Task.FromResult(ScanResult);
    }

    public Task<List<PerspectiveCursorInfo>> GetPerspectiveCursorsBatchAsync(Guid[] streamIds, CancellationToken cancellationToken = default) {
      List<PerspectiveCursorInfo> answer;
      lock (BatchQueries) {
        BatchQueries.Add(streamIds);
        answer = BatchAnswers.Count > 0 ? BatchAnswers.Dequeue() : DefaultBatchAnswer;
      }
      try {
        if (BatchException is not null) {
          throw BatchException;
        }
        return Task.FromResult(answer);
      } finally {
        _batchSignal.Release();
      }
    }

    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default) =>
      Task.FromResult(Cursors.TryGetValue((streamId, perspectiveName), out var cursor) ? cursor : null);

    public Task<WorkBatch> ClaimWorkAsync(ClaimWorkRequest request, CancellationToken cancellationToken = default) =>
      Task.FromResult(new WorkBatch { OutboxWork = [], InboxWork = [], PerspectiveWork = [] });
    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion completion, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure failure, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken cancellationToken = default) =>
      Task.FromResult(new WorkCoordinatorStatistics());
  }

  /// <summary>A runner whose rewind completes the cursor, counting the rewinds it ran.</summary>
  private sealed class RewindingRunner : IPerspectiveRunner {
    private int _rewindCount;
    public int RewindCount => _rewindCount;
    public Type PerspectiveType => typeof(object);

    public Task<PerspectiveCursorCompletion> RunAsync(Guid streamId, string perspectiveName, Guid? lastProcessedEventId, CancellationToken cancellationToken = default) =>
      Task.FromResult(_completed(streamId, perspectiveName));

    public Task<PerspectiveCursorCompletion> RewindAndRunAsync(Guid streamId, string perspectiveName, Guid triggeringEventId, CancellationToken cancellationToken = default) {
      Interlocked.Increment(ref _rewindCount);
      return Task.FromResult(_completed(streamId, perspectiveName));
    }

    public Task BootstrapSnapshotAsync(Guid streamId, string perspectiveName, Guid lastProcessedEventId, CancellationToken cancellationToken = default) =>
      Task.CompletedTask;

    private static PerspectiveCursorCompletion _completed(Guid streamId, string perspectiveName) => new() {
      StreamId = streamId,
      PerspectiveName = perspectiveName,
      LastEventId = Guid.CreateVersion7(),
      Status = PerspectiveProcessingStatus.Completed
    };
  }

  private sealed class SingleRunnerRegistry(string registeredPerspectiveName, IPerspectiveRunner runner) : IPerspectiveRunnerRegistry {
    public IPerspectiveRunner? GetRunner(string perspectiveName, IServiceProvider serviceProvider) =>
      perspectiveName == registeredPerspectiveName ? runner : null;

    public IReadOnlyList<PerspectiveRegistrationInfo> GetRegisteredPerspectives() =>
      [new PerspectiveRegistrationInfo(registeredPerspectiveName, $"global::Test.{registeredPerspectiveName}", "global::Test.TestModel", ["global::Test.TestEvent"])];

    public IReadOnlyList<Type> GetEventTypes() => [];
    public IReadOnlySet<LifecycleStage> LifecycleStagesWithReceptors { get; } = new HashSet<LifecycleStage>();
  }

  private sealed class InstanceProvider : IServiceInstanceProvider {
    public Guid InstanceId { get; } = Guid.CreateVersion7();
    public string ServiceName => "TestService";
    public string HostName => "test-host";
    public int ProcessId => 1;
    public ServiceInstanceInfo ToInfo() => new() { ServiceName = ServiceName, InstanceId = InstanceId, HostName = HostName, ProcessId = ProcessId };
  }

  /// <summary>A NOTIFY listener that is configured and healthy, so the consumer loop idles on the safety-net cadence.</summary>
  private sealed class ConfiguredNotificationListener : IWorkNotificationListener {
    public bool IsHealthy => true;
    public DateTimeOffset? LastSignalAt => null;
    public event Action<WorkSignalCategory>? OnSignal { add { /* never raised */ } remove { /* never raised */ } }
    public event Action<bool>? OnHealthChanged { add { /* never raised */ } remove { /* never raised */ } }
  }

  /// <summary>Records every entry logged under one category, with its level.</summary>
  private sealed class ListLoggerProvider(string category) : ILoggerProvider {
    private readonly ConcurrentQueue<(LogLevel Level, string Message, Exception? Exception)> _entries = new();

    public (LogLevel Level, string Message, Exception? Exception)[] Entries => [.. _entries];

    public ILogger CreateLogger(string categoryName) =>
      categoryName == category ? new Logger(this) : NullLogger.Instance;

    public void Dispose() { }

    private sealed class Logger(ListLoggerProvider owner) : ILogger {
      public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
      public bool IsEnabled(LogLevel logLevel) => true;
      public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        owner._entries.Enqueue((logLevel, formatter(state, exception), exception));
    }
  }
}
