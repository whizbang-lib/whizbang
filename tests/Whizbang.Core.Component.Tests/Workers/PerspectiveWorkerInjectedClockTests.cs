// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

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

/// <summary>
/// Every wait and every timestamp <see cref="PerspectiveWorker"/> takes runs on the clock it was given, not on the
/// machine's (#1861). A test that hands the worker a <see cref="FakeTimeProvider"/> decides when its idle poll, its
/// blocking startup re-poll and its lock keepalive come round, and how old its affinity gates and sent completions
/// are, so none of them can move on its own while the test is looking.
/// </summary>
/// <remarks>
/// Each wait is asserted the same way: the worker arms its wait on the injected clock and then does nothing more,
/// however long real time runs, until the test advances that clock by exactly the interval. Every interval below is
/// short in real time on purpose, so a worker still waiting on the machine's clock comes round on its own within the
/// test and the assertion names it, rather than the test running out its timeout.
/// </remarks>
/// <code-under-test>src/Whizbang.Core/Workers/PerspectiveWorker.cs</code-under-test>
[NotInParallel("PerspectiveChannelModeTests")]
public class PerspectiveWorkerInjectedClockTests {
  private const string PERSPECTIVE = "Clock.Perspective";

  // ============================================================
  // Idle poll
  // ============================================================

  [Test]
  [Timeout(30000)]
  public async Task IdlePoll_WaitsOnTheInjectedClock_PollsAgainOnlyWhenThePollIntervalElapsesAsync(CancellationToken cancellationToken) {
    var pollInterval = TimeSpan.FromMilliseconds(150);
    var clock = new ArmingClock();
    var workChannel = new CountingPerspectiveChannel();
    var fx = _build(clock, new PerspectiveWorkerOptions {
      PollingIntervalMilliseconds = (int)pollInterval.TotalMilliseconds,
      MaxConcurrentDrainConsumers = 1,
    }, workChannel: workChannel);

    using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    await fx.Worker.StartAsync(cts.Token);

    // The first pass is waiting. Either it armed its idle wait on the injected clock, or a wait on the machine's
    // clock elapsed and the loop came round a second time without the test touching the clock.
    var firstArm = clock.Armed(pollInterval, 1);
    await Task.WhenAny(firstArm, workChannel.WaitCalls(2)).WaitAsync(cancellationToken);
    await Assert.That(firstArm.IsCompleted).IsTrue()
      .Because("the idle poll must wait on the worker's injected clock; a wait on the machine's clock lets the loop poll again by itself");
    await Assert.That(workChannel.WaitCallCount).IsEqualTo(1)
      .Because("nothing but the injected clock may end the idle wait");

    clock.Advance(pollInterval - TimeSpan.FromTicks(1));
    await Assert.That(clock.FiredCount(pollInterval)).IsEqualTo(0)
      .Because("the idle wait lasts the whole poll interval on the injected clock");

    clock.Advance(TimeSpan.FromTicks(1));
    await workChannel.WaitCalls(2).WaitAsync(cancellationToken);
    await clock.Armed(pollInterval, 2).WaitAsync(cancellationToken);

    await Assert.That(clock.FiredCount(pollInterval)).IsEqualTo(1)
      .Because("advancing the injected clock by the poll interval ends exactly one idle wait");
    await Assert.That(fx.Worker.ConsecutiveEmptyPolls).IsEqualTo(1)
      .Because("one elapsed interval is one empty poll, and the next one waits on the injected clock again");

    await cts.CancelAsync();
    await fx.Worker.StopAsync(CancellationToken.None);
  }

  // ============================================================
  // Blocking startup rewind scan
  // ============================================================

  [Test]
  [Timeout(30000)]
  public async Task BlockingRewindScan_RepollsOnTheInjectedClock_AndReportsTheWaitItMeasuredAsync(CancellationToken cancellationToken) {
    var pollInterval = TimeSpan.FromMilliseconds(150);
    var clock = new ArmingClock();
    var coordinator = new ClockCoordinator();
    coordinator.RewindResults.Enqueue([
      new RewindCursorInfo(Guid.CreateVersion7(), PERSPECTIVE, null, Guid.CreateVersion7())
    ]);
    var logs = new CapturingLoggerProvider();
    var fx = _build(clock, new PerspectiveWorkerOptions {
      PollingIntervalMilliseconds = (int)pollInterval.TotalMilliseconds,
      MaxConcurrentDrainConsumers = 1,
    }, coordinator: coordinator, rewindOptions: new PerspectiveRewindOptions { StartupRewindMode = RewindStartupMode.Blocking },
      loggerProvider: logs);

    using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    await fx.Worker.StartAsync(cts.Token);

    var firstArm = clock.Armed(pollInterval, 1);
    await Task.WhenAny(firstArm, coordinator.RewindQueries(2)).WaitAsync(cancellationToken);
    await Assert.That(firstArm.IsCompleted).IsTrue()
      .Because("the blocking scan's re-poll must wait on the worker's injected clock");
    await Assert.That(coordinator.RewindQueryCount).IsEqualTo(1)
      .Because("nothing but the injected clock may end the re-poll wait");

    clock.Advance(pollInterval);
    await fx.Worker.StartupScanComplete.WaitAsync(cancellationToken);

    await Assert.That(coordinator.RewindQueryCount).IsEqualTo(2)
      .Because("one elapsed interval is one re-poll, which found nothing left to rewind");
    var completed = logs.Entries.Single(e => e.EventId == 55);
    await Assert.That(completed.State["DurationMs"]).IsEqualTo((long)pollInterval.TotalMilliseconds)
      .Because("the scan reports the time it waited as the injected clock measured it");

    await cts.CancelAsync();
    await fx.Worker.StopAsync(CancellationToken.None);
  }

  // ============================================================
  // Lock keepalive
  // ============================================================

  [Test]
  [Timeout(30000)]
  public async Task LockKeepalive_WaitsOnTheInjectedClock_RenewsOnlyWhenTheIntervalElapsesAsync(CancellationToken cancellationToken) {
    var keepAlive = TimeSpan.FromMilliseconds(150);
    var clock = new ArmingClock();
    var locker = new CountingLocker();
    var fx = _build(clock, streamLocker: locker,
      streamLockOptions: new PerspectiveStreamLockOptions { KeepAliveInterval = keepAlive });

    using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    var keepalive = fx.Worker.StartLockKeepaliveAsync(Guid.CreateVersion7(), PERSPECTIVE, cts.Token);

    var firstArm = clock.Armed(keepAlive, 1);
    await Task.WhenAny(firstArm, locker.Renewals(1)).WaitAsync(cancellationToken);
    await Assert.That(firstArm.IsCompleted).IsTrue()
      .Because("the keepalive must wait on the worker's injected clock");
    await Assert.That(locker.RenewCount).IsEqualTo(0)
      .Because("nothing but the injected clock may end the keepalive wait");

    clock.Advance(keepAlive);
    await locker.Renewals(1).WaitAsync(cancellationToken);
    await clock.Armed(keepAlive, 2).WaitAsync(cancellationToken);

    await Assert.That(locker.RenewCount).IsEqualTo(1)
      .Because("one elapsed interval is one renewal, and the next one waits on the injected clock again");

    await cts.CancelAsync();
    await keepalive.WaitAsync(cancellationToken);
  }

  // ============================================================
  // Affinity gate ages
  // ============================================================

  [Test]
  [Timeout(30000)]
  public async Task AffinityGateSweep_AgesGatesOnTheInjectedClockAsync(CancellationToken cancellationToken) {
    var window = TimeSpan.FromMinutes(1);
    var clock = new ArmingClock();
    var fx = _build(clock, affinityOptions: new PerspectiveStreamAffinityOptions {
      IdleEvictionWindow = window,
      SweepInterval = window,
    });
    var idle = Guid.CreateVersion7();
    var active = Guid.CreateVersion7();

    await fx.Worker.WithStreamAffinityGateAsync(idle, PERSPECTIVE, static _ => Task.CompletedTask, cancellationToken);
    await fx.Worker.WithStreamAffinityGateAsync(active, PERSPECTIVE, static _ => Task.CompletedTask, cancellationToken);
    await Assert.That(fx.Worker.HasStreamAffinityGate(idle, PERSPECTIVE)).IsTrue()
      .Because("no time has passed on the injected clock, so nothing is idle yet");

    clock.Advance(window);
    await fx.Worker.WithStreamAffinityGateAsync(active, PERSPECTIVE, static _ => Task.CompletedTask, cancellationToken);

    await Assert.That(fx.Worker.HasStreamAffinityGate(idle, PERSPECTIVE)).IsFalse()
      .Because("a gate untouched for the whole idle window on the injected clock is swept by the next release");
    await Assert.That(fx.Worker.HasStreamAffinityGate(active, PERSPECTIVE)).IsTrue()
      .Because("the gate just released was touched at the injected clock's current time, so it is not idle");
  }

  // ============================================================
  // Completion timestamps
  // ============================================================

  [Test]
  [Timeout(30000)]
  public async Task CompletionBookkeeping_StampsSentAndStaleChecksWithTheInjectedClockAsync(CancellationToken cancellationToken) {
    var clock = new ArmingClock();
    clock.Advance(TimeSpan.FromDays(3));
    var strategy = new RecordingCompletionStrategy();
    var fx = _build(clock, completionStrategy: strategy);
    await strategy.ReportCompletionAsync(new PerspectiveCursorCompletion {
      StreamId = Guid.CreateVersion7(),
      PerspectiveName = PERSPECTIVE,
      LastEventId = Guid.CreateVersion7(),
      Status = PerspectiveProcessingStatus.Completed,
      EventsProcessed = 1,
    }, fx.Coordinator, cancellationToken);

    await fx.Worker.ProcessChannelBatchAsync([], cancellationToken);

    await Assert.That(strategy.SentAt).IsEquivalentTo([clock.GetUtcNow()])
      .Because("a completion handed to the channel is stamped sent at the worker's injected time");
    await Assert.That(strategy.StaleCheckedAt).IsEquivalentTo([clock.GetUtcNow()])
      .Because("sent completions are aged against the same injected clock that stamped them");
  }

  // ============================================================
  // Fixture
  // ============================================================

  private sealed record Fixture(PerspectiveWorker Worker, ClockCoordinator Coordinator);

  private static Fixture _build(
      TimeProvider clock,
      PerspectiveWorkerOptions? options = null,
      ClockCoordinator? coordinator = null,
      PerspectiveRewindOptions? rewindOptions = null,
      ILoggerProvider? loggerProvider = null,
      IPerspectiveChannelWriter? workChannel = null,
      IPerspectiveStreamLocker? streamLocker = null,
      PerspectiveStreamLockOptions? streamLockOptions = null,
      PerspectiveStreamAffinityOptions? affinityOptions = null,
      IPerspectiveCompletionStrategy? completionStrategy = null) {
    var workerOptions = options ?? new PerspectiveWorkerOptions {
      PollingIntervalMilliseconds = 1_000_000,
      MaxConcurrentDrainConsumers = 1,
    };
    var workCoordinator = coordinator ?? new ClockCoordinator();
    var instanceProvider = new InstanceProvider();
    var services = new ServiceCollection();
    services.TryAddWhizbangDefaults();
    services.AddSingleton<IWorkCoordinator>(workCoordinator);
    services.AddSingleton<IPerspectiveRunnerRegistry>(new EmptyRegistry());
    services.AddSingleton<IServiceInstanceProvider>(instanceProvider);
    services.AddLogging(logging => {
      if (loggerProvider is not null) {
        logging.AddProvider(loggerProvider);
      }
    });
    var sp = services.BuildServiceProvider();

    var worker = new PerspectiveWorker(
      instanceProvider: instanceProvider,
      scopeFactory: sp.GetRequiredService<IServiceScopeFactory>(),
      options: Options.Create(workerOptions),
      schemaReadyGate: SchemaReadyGate.AlreadyReady(),
      tracingOptions: new StaticOptionsMonitor<TracingOptions>(new TracingOptions()),
      completionStrategy: completionStrategy ?? new BatchedCompletionStrategy(),
      eventTypeProvider: sp.GetRequiredService<IEventTypeProvider>(),
      syncSignaler: new LocalSyncSignaler(NullLogger<LocalSyncSignaler>.Instance),
      syncEventTracker: new SyncEventTracker(),
      logger: NullLogger<PerspectiveWorker>.Instance,
      snapshotStore: NullPerspectiveSnapshotStore.Instance,
      streamLocker: streamLocker ?? NullPerspectiveStreamLocker.Instance,
      streamLockOptions: Options.Create(streamLockOptions ?? new PerspectiveStreamLockOptions()),
      streamAffinityOptions: Options.Create(affinityOptions ?? new PerspectiveStreamAffinityOptions()),
      processedEventCacheObserver: NullProcessedEventCacheObserver.Instance,
      workChannelWriter: new WorkChannelWriter(),
      rewindOptions: Options.Create(rewindOptions ?? new PerspectiveRewindOptions()),
      perspectiveChannelWriter: workChannel ?? new PerspectiveChannelWriter(),
      perspectiveCompletionChannel: new CapturingPerspectiveCompletionChannel(),
      failureChannel: new CapturingFailureChannel(),
      leaseRenewalChannel: new CapturingLeaseRenewalChannel(),
      perspectiveDrainChannel: new PerspectiveDrainChannel(),
      leaseHandleOptions: Options.Create(new LeaseHandleOptions()),
      leaseRenewalOptions: Options.Create(new LeaseRenewalWorkerOptions()),
      deadLetterStore: NullDeadLetterStore.Instance,
      generationProvider: new DefaultGenerationProvider(),
      perspectiveNotificationListener: new NoOpWorkNotificationListener(),
      governor: PerspectiveWorker.CreateDefaultGovernor(workerOptions),
      timeProvider: clock);
    return new Fixture(worker, workCoordinator);
  }

  // ============================================================
  // Test doubles
  // ============================================================

  /// <summary>
  /// A fake clock that reports every wait armed on it, by due time, and counts the ones that elapsed. A wait the
  /// worker takes on any other clock never shows up here.
  /// </summary>
  private sealed class ArmingClock : FakeTimeProvider {
    private readonly Lock _lock = new();
    private readonly List<TimeSpan> _armed = [];
    private readonly Dictionary<TimeSpan, int> _fired = [];
    private readonly List<(TimeSpan Due, int Count, TaskCompletionSource Signal)> _waiters = [];

    /// <summary>Completes once <paramref name="count"/> waits of <paramref name="due"/> have been armed.</summary>
    public Task Armed(TimeSpan due, int count) {
      lock (_lock) {
        if (_armed.Count(d => d == due) >= count) {
          return Task.CompletedTask;
        }
        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _waiters.Add((due, count, signal));
        return signal.Task;
      }
    }

    /// <summary>How many waits of <paramref name="due"/> have elapsed.</summary>
    public int FiredCount(TimeSpan due) {
      lock (_lock) {
        return _fired.GetValueOrDefault(due);
      }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) {
      var due = new DueBox<TimeSpan>(dueTime);
      if (dueTime != Timeout.InfiniteTimeSpan) {
        _onArmed(dueTime);
      }
      var inner = base.CreateTimer(s => {
        _onFired(due.Value);
        callback(s);
      }, state, dueTime, period);
      return new ArmingTimer(inner, due, this);
    }

    private void _onArmed(TimeSpan dueTime) {
      lock (_lock) {
        _armed.Add(dueTime);
        var count = _armed.Count(d => d == dueTime);
        foreach (var waiter in _waiters.Where(w => w.Due == dueTime && count >= w.Count).ToList()) {
          _waiters.Remove(waiter);
          waiter.Signal.TrySetResult();
        }
      }
    }

    private void _onFired(TimeSpan dueTime) {
      lock (_lock) {
        _fired[dueTime] = _fired.GetValueOrDefault(dueTime) + 1;
      }
    }

    private sealed class ArmingTimer(ITimer inner, DueBox<TimeSpan> due, ArmingClock clock) : ITimer {
      public bool Change(TimeSpan dueTime, TimeSpan period) {
        due.Value = dueTime;
        if (dueTime != Timeout.InfiniteTimeSpan) {
          clock._onArmed(dueTime);
        }
        return inner.Change(dueTime, period);
      }

      public void Dispose() => inner.Dispose();
      public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
  }

  private sealed class DueBox<T>(T value) {
    public T Value { get; set; } = value;
  }

  /// <summary>A perspective work channel that counts how often the consumer loop starts a wait on it.</summary>
  private sealed class CountingPerspectiveChannel : IPerspectiveChannelWriter {
    private readonly Channel<PerspectiveWork> _channel = Channel.CreateUnbounded<PerspectiveWork>();
    private readonly CountingReader _reader;

    public CountingPerspectiveChannel() => _reader = new CountingReader(_channel.Reader);

    public ChannelReader<PerspectiveWork> Reader => _reader;
    public int WaitCallCount => _reader.Calls.Count;
    public Task WaitCalls(int count) => _reader.Calls.Reached(count);
    public ValueTask WriteAsync(PerspectiveWork work, CancellationToken ct = default) => _channel.Writer.WriteAsync(work, ct);
    public bool TryWrite(PerspectiveWork work) => _channel.Writer.TryWrite(work);
    public void Complete() => _channel.Writer.TryComplete();

    private sealed class CountingReader(ChannelReader<PerspectiveWork> inner) : ChannelReader<PerspectiveWork> {
      public Counter Calls { get; } = new();
      public override bool TryRead(out PerspectiveWork item) => inner.TryRead(out item!);
      public override ValueTask<bool> WaitToReadAsync(CancellationToken cancellationToken = default) {
        Calls.Increment();
        return inner.WaitToReadAsync(cancellationToken);
      }
    }
  }

  /// <summary>A count that tests can wait on reaching a value.</summary>
  private sealed class Counter {
    private readonly Lock _lock = new();
    private readonly List<(int Count, TaskCompletionSource Signal)> _waiters = [];
    private int _count;

    public int Count {
      get {
        lock (_lock) {
          return _count;
        }
      }
    }

    public void Increment() {
      lock (_lock) {
        _count++;
        foreach (var waiter in _waiters.Where(w => _count >= w.Count).ToList()) {
          _waiters.Remove(waiter);
          waiter.Signal.TrySetResult();
        }
      }
    }

    public Task Reached(int count) {
      lock (_lock) {
        if (_count >= count) {
          return Task.CompletedTask;
        }
        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _waiters.Add((count, signal));
        return signal.Task;
      }
    }
  }

  private sealed class CountingLocker : IPerspectiveStreamLocker {
    private readonly Counter _renewals = new();
    public int RenewCount => _renewals.Count;
    public Task Renewals(int count) => _renewals.Reached(count);

    public Task<bool> TryAcquireLockAsync(Guid streamId, string perspectiveName, Guid instanceId, string reason, CancellationToken ct = default) =>
      Task.FromResult(true);

    public Task RenewLockAsync(Guid streamId, string perspectiveName, Guid instanceId, CancellationToken ct = default) {
      _renewals.Increment();
      return Task.CompletedTask;
    }

    public Task ReleaseLockAsync(Guid streamId, string perspectiveName, Guid instanceId, CancellationToken ct = default) => Task.CompletedTask;
  }

  /// <summary>Records the time the worker passes every time it stamps or ages its completions.</summary>
  private sealed class RecordingCompletionStrategy : IPerspectiveCompletionStrategy {
    private readonly BatchedCompletionStrategy _inner = new();
    public List<DateTimeOffset> SentAt { get; } = [];
    public List<DateTimeOffset> StaleCheckedAt { get; } = [];

    public Task ReportCompletionAsync(PerspectiveCursorCompletion completion, IWorkCoordinator coordinator, CancellationToken cancellationToken) =>
      _inner.ReportCompletionAsync(completion, coordinator, cancellationToken);
    public Task ReportFailureAsync(PerspectiveCursorFailure failure, IWorkCoordinator coordinator, CancellationToken cancellationToken) =>
      _inner.ReportFailureAsync(failure, coordinator, cancellationToken);
    public TrackedCompletion<PerspectiveCursorCompletion>[] GetPendingCompletions() => _inner.GetPendingCompletions();
    public TrackedCompletion<PerspectiveCursorFailure>[] GetPendingFailures() => _inner.GetPendingFailures();

    public void MarkAsSent(
        TrackedCompletion<PerspectiveCursorCompletion>[] completions,
        TrackedCompletion<PerspectiveCursorFailure>[] failures,
        DateTimeOffset sentAt) {
      SentAt.Add(sentAt);
      _inner.MarkAsSent(completions, failures, sentAt);
    }

    public void MarkAsAcknowledged(int completionCount, int failureCount) => _inner.MarkAsAcknowledged(completionCount, failureCount);
    public void ClearAcknowledged() => _inner.ClearAcknowledged();

    public void ResetStale(DateTimeOffset now) {
      StaleCheckedAt.Add(now);
      _inner.ResetStale(now);
    }
  }

  private sealed record LogEntry(int EventId, IReadOnlyDictionary<string, object?> State);

  private sealed class CapturingLoggerProvider : ILoggerProvider {
    private readonly Lock _lock = new();
    private readonly List<LogEntry> _entries = [];

    public IReadOnlyList<LogEntry> Entries {
      get {
        lock (_lock) {
          return [.. _entries];
        }
      }
    }

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(this);
    public void Dispose() { /* nothing to release */ }

    private sealed class CapturingLogger(CapturingLoggerProvider provider) : ILogger {
      public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
      public bool IsEnabled(LogLevel logLevel) => true;

      public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) {
        var values = state is IReadOnlyList<KeyValuePair<string, object?>> pairs
          ? pairs.ToDictionary(p => p.Key, p => p.Value)
          : [];
        lock (provider._lock) {
          provider._entries.Add(new LogEntry(eventId.Id, values));
        }
      }
    }
  }

  private sealed class InstanceProvider : IServiceInstanceProvider {
    public Guid InstanceId { get; } = Guid.CreateVersion7();
    public string ServiceName => "clock-test-svc";
    public string HostName => "clock-test-host";
    public int ProcessId => 1;
    public ServiceInstanceInfo ToInfo() => new() {
      InstanceId = InstanceId,
      ServiceName = ServiceName,
      HostName = HostName,
      ProcessId = ProcessId,
    };
  }

  private sealed class EmptyRegistry : IPerspectiveRunnerRegistry {
    public IPerspectiveRunner? GetRunner(string perspectiveName, IServiceProvider serviceProvider) => null;
    public IReadOnlyList<PerspectiveRegistrationInfo> GetRegisteredPerspectives() => [];
    public IReadOnlyList<Type> GetEventTypes() => [];
    public IReadOnlySet<LifecycleStage> LifecycleStagesWithReceptors { get; } = new HashSet<LifecycleStage>();
  }

  /// <summary>The coordinator surface the worker reaches at startup and per batch, with a scripted rewind scan.</summary>
  private sealed class ClockCoordinator : IWorkCoordinator {
    private readonly Counter _rewindQueries = new();
    public Queue<IReadOnlyList<RewindCursorInfo>> RewindResults { get; } = new();
    public int RewindQueryCount => _rewindQueries.Count;
    public Task RewindQueries(int count) => _rewindQueries.Reached(count);

    public Task<IReadOnlyList<RewindCursorInfo>> GetCursorsRequiringRewindAsync(CancellationToken cancellationToken = default) {
      IReadOnlyList<RewindCursorInfo> result;
      lock (RewindResults) {
        result = RewindResults.Count > 0 ? RewindResults.Dequeue() : [];
      }
      _rewindQueries.Increment();
      return Task.FromResult(result);
    }

    public Task<WorkBatch> ClaimWorkAsync(ClaimWorkRequest request, CancellationToken cancellationToken = default) =>
      Task.FromResult(new WorkBatch { OutboxWork = [], InboxWork = [], PerspectiveWork = [] });
    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task StoreOutboxMessagesAsync(OutboxMessage[] messages, int partitionCount, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion completion, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure failure, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default) =>
      Task.FromResult<PerspectiveCursorInfo?>(null);
    public Task<List<StreamEventData>> GetStreamEventsAsync(Guid instanceId, Guid[] streamIds, CancellationToken cancellationToken = default) =>
      Task.FromResult(new List<StreamEventData>());
    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken cancellationToken = default) =>
      Task.FromResult(new WorkCoordinatorStatistics());
    public Task<IReadOnlyList<OrphanedLifecycleEvent>> GetOrphanedLifecycleEventsAsync(
        Dictionary<string, IReadOnlyList<string>> perspectivesPerEventType,
        TimeSpan lookbackWindow,
        int maxOrphans = 100,
        CancellationToken cancellationToken = default) =>
      Task.FromResult<IReadOnlyList<OrphanedLifecycleEvent>>([]);
    public Task RecordLifecycleCompletionAsync(Guid eventId, CancellationToken cancellationToken = default) => Task.CompletedTask;
  }
}
