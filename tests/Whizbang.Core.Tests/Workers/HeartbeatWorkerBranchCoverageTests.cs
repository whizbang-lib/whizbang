// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Signals;
using Whizbang.Core.Tests.Observability;
using Whizbang.Core.Workers;

#pragma warning disable IDE0060, RCS1163 // Unused parameters: the fakes implement interface members the tests never exercise

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Branches of <see cref="HeartbeatWorker"/> the other heartbeat suites leave untaken: the options
/// guard, the liveness counters when metrics are registered, the beat-recorded event with and without
/// a subscriber, and graceful stop against a bus that is not configured.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Workers/HeartbeatWorker.cs</code-under-test>
[Category("Workers")]
public class HeartbeatWorkerBranchCoverageTests {
  private static readonly DateTimeOffset _start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

  // ── constructor ────────────────────────────────────────────────────────

  private sealed class NullValueOptions : IOptions<HeartbeatWorkerOptions> {
    public HeartbeatWorkerOptions Value => null!;
  }

  [Test]
  public async Task Constructor_NullOptions_ThrowsArgumentNullExceptionAsync() {
    await Assert.That(() => _construct(null!))
      .Throws<ArgumentNullException>()
      .WithParameterName("options");
  }

  [Test]
  public async Task Constructor_OptionsWithNullValue_ThrowsArgumentNullExceptionAsync() {
    await Assert.That(() => _construct(new NullValueOptions()))
      .Throws<ArgumentNullException>()
      .WithParameterName("options");
  }

  // ── liveness counters ──────────────────────────────────────────────────

  [Test]
  public async Task Tick_WatchdogBeatWithMetrics_CountsOneWatchdogBeatAsync() {
    var clock = new FakeTimeProvider(_start);
    using var factory = new TestMeterFactory();
    var metrics = new InstanceLivenessMetrics(new WhizbangMetrics(factory));
    var options = _options();
    var deadline = HeartbeatLivenessThreshold.StaleThreshold(options) - HeartbeatLivenessThreshold.WatchdogLead(options);
    var worker = _worker(clock, () => Task.FromResult(true), options, metrics: metrics).Worker;

    await worker.TickForTestsAsync(CancellationToken.None);
    clock.Advance(deadline);
    var second = await worker.TickForTestsAsync(CancellationToken.None);

    await Assert.That(second.Reason).IsEqualTo(HeartbeatBeatReason.Watchdog);
    await Assert.That(ProbeMeterReader.ReadTotal(metrics.WatchdogBeats.Meter, "whizbang.liveness.watchdog_beats")).IsEqualTo(1)
      .Because("the initial beat is not a watchdog beat; only the forced one counts");
    await Assert.That(ProbeMeterReader.ReadTotal(metrics.SlowBeats.Meter, "whizbang.liveness.slow_beats")).IsEqualTo(0);
  }

  [Test]
  public async Task Tick_SlowBeatWithMetrics_CountsOneSlowBeatAsync() {
    var clock = new FakeTimeProvider(_start);
    using var factory = new TestMeterFactory();
    var metrics = new InstanceLivenessMetrics(new WhizbangMetrics(factory));
    var options = _options();
    var lead = HeartbeatLivenessThreshold.WatchdogLead(options);
    var worker = _worker(clock, () => {
      clock.Advance(lead);
      return Task.FromResult(true);
    }, options, metrics: metrics).Worker;

    await worker.TickForTestsAsync(CancellationToken.None);

    await Assert.That(ProbeMeterReader.ReadTotal(metrics.SlowBeats.Meter, "whizbang.liveness.slow_beats")).IsEqualTo(1)
      .Because("a beat whose round trip took a full fast interval is a slow beat");
    await Assert.That(ProbeMeterReader.ReadTotal(metrics.WatchdogBeats.Meter, "whizbang.liveness.watchdog_beats")).IsEqualTo(0);
  }

  // ── beat-recorded event ────────────────────────────────────────────────

  [Test]
  public async Task Tick_AcceptedBeatWithSubscriber_RaisesOnHeartbeatRecordedOnceAsync() {
    var clock = new FakeTimeProvider(_start);
    var worker = _worker(clock, () => Task.FromResult(true), _options()).Worker;
    var raised = 0;
    worker.OnHeartbeatRecorded += () => raised++;

    await worker.TickForTestsAsync(CancellationToken.None);

    await Assert.That(raised).IsEqualTo(1);
    await Assert.That(worker.LastAcceptedAt).IsEqualTo(_start);
  }

  [Test]
  public async Task Tick_RefusedBeatWithSubscriber_DoesNotRaiseOnHeartbeatRecordedAsync() {
    var clock = new FakeTimeProvider(_start);
    var worker = _worker(clock, () => Task.FromResult(false), _options()).Worker;
    var raised = 0;
    worker.OnHeartbeatRecorded += () => raised++;

    await worker.TickForTestsAsync(CancellationToken.None);

    await Assert.That(raised).IsEqualTo(0)
      .Because("a refused beat recorded nothing, so nothing may treat it as activity");
    await Assert.That(worker.LastAcceptedAt).IsNull();
  }

  [Test]
  public async Task Tick_AcceptedBeatWithoutSubscriber_StillRecordsTheBeatAsync() {
    var clock = new FakeTimeProvider(_start);
    var (worker, coordinator) = _worker(clock, () => Task.FromResult(true), _options());

    await worker.TickForTestsAsync(CancellationToken.None);

    await Assert.That(coordinator.Beats).IsEqualTo(1);
    await Assert.That(worker.LastAcceptedAt).IsEqualTo(_start)
      .Because("no subscriber to the beat-recorded event must not stop the beat from being accepted");
  }

  // ── graceful stop ──────────────────────────────────────────────────────

  [Test]
  public async Task StopAsync_BusNotConfigured_PublishesNothingAsync() {
    var bus = new RecordingBus(isConfigured: false);
    var worker = _worker(new FakeTimeProvider(_start), () => Task.FromResult(true), _options(), bus: bus).Worker;

    await worker.StopAsync(CancellationToken.None);

    await Assert.That(bus.Published).IsEmpty()
      .Because("an unconfigured bus has no subscribers to tell, so stop must not publish into it");
  }

  [Test]
  public async Task StopAsync_BusConfigured_PublishesInstanceLeavingAsync() {
    var bus = new RecordingBus(isConfigured: true);
    var worker = _worker(new FakeTimeProvider(_start), () => Task.FromResult(true), _options(), bus: bus).Worker;

    await worker.StopAsync(CancellationToken.None);

    await Assert.That(bus.Published).Contains(typeof(InstanceLeavingSignal));
  }

  [Test]
  public async Task StopAsync_BusPublishSuspends_StillPublishesInstanceLeavingAfterResumingAsync() {
    // A real bus publish crosses the network and suspends; the stop path must resume and finish
    // the goodbye rather than only working against a bus that completes synchronously.
    var bus = new YieldingBus();
    var worker = _worker(new FakeTimeProvider(_start), () => Task.FromResult(true), _options(), bus: bus).Worker;

    await worker.StopAsync(CancellationToken.None);

    await Assert.That(bus.Completed).Contains(typeof(InstanceLeavingSignal))
      .Because("the publish resumed after suspending and ran to completion before stop returned");
  }

  // ── helpers ────────────────────────────────────────────────────────────

  private sealed class YieldingBus : ISignalBus {
    public List<Type> Completed { get; } = [];
    public async ValueTask PublishAsync<TSignal>(TSignal signal, SignalTarget target = default, CancellationToken cancellationToken = default)
      where TSignal : ISignal {
      await Task.Yield();
      lock (Completed) { Completed.Add(typeof(TSignal)); }
    }
    public ISignalSubscription Subscribe<TSignal>(Func<TSignal, ValueTask> handler) where TSignal : ISignal
      => new NoopSub();
    private sealed class NoopSub : ISignalSubscription { public void Dispose() { /* nothing to release */ } }
  }

  private static HeartbeatWorkerOptions _options() => new() { IntervalSeconds = 30, SlowIntervalSeconds = 60 };

  private sealed class RecordingBus(bool isConfigured) : ISignalBus {
    public bool IsConfigured => isConfigured;
    public List<Type> Published { get; } = [];
    public ValueTask PublishAsync<TSignal>(TSignal signal, SignalTarget target = default, CancellationToken cancellationToken = default)
      where TSignal : ISignal {
      lock (Published) { Published.Add(typeof(TSignal)); }
      return ValueTask.CompletedTask;
    }
    public ISignalSubscription Subscribe<TSignal>(Func<TSignal, ValueTask> handler) where TSignal : ISignal
      => new NoopSub();
    private sealed class NoopSub : ISignalSubscription { public void Dispose() { /* nothing to release */ } }
  }

  private sealed class ScriptedHeartbeatCoordinator(Func<Task<bool>> onBeat) : IWorkCoordinator {
    public int Beats { get; private set; }
    public Task<bool> RecordHeartbeatAsync(HeartbeatRequest request, CancellationToken cancellationToken = default) {
      Beats++;
      return onBeat();
    }
    public Task<WorkBatch> ClaimWorkAsync(ClaimWorkRequest request, CancellationToken cancellationToken = default) =>
      Task.FromResult(new WorkBatch { OutboxWork = [], InboxWork = [], PerspectiveWork = [] });
    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken cancellationToken = default) =>
      Task.FromResult(new WorkCoordinatorStatistics());
    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<PartitionRecomputeResult> RecomputePartitionNumbersAsync(int partitionCount, CancellationToken cancellationToken = default) =>
      Task.FromResult(new PartitionRecomputeResult());
    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion completion, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure failure, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default) =>
      Task.FromResult<PerspectiveCursorInfo?>(null);
  }

  private static HeartbeatWorker _construct(IOptions<HeartbeatWorkerOptions> options) {
    var sp = new ServiceCollection().BuildServiceProvider();
    return new HeartbeatWorker(
      scopeFactory: sp.GetRequiredService<IServiceScopeFactory>(),
      instanceProvider: new ServiceInstanceProvider(configuration: new ConfigurationBuilder().Build()),
      schemaReadyGate: SchemaReadyGate.AlreadyReady(),
      options: options,
      logger: NullLogger<HeartbeatWorker>.Instance,
      lifecycleState: HeartbeatTestDependencies.LifecycleState,
      libraryVersion: HeartbeatTestDependencies.Version,
      pinnedPool: NoOpPinnedConnectionPool.Instance,
      aliveLockSource: NullInstanceAliveLockSource.Instance,
      signalBus: NullSignalBus.Instance);
  }

  private static (HeartbeatWorker Worker, ScriptedHeartbeatCoordinator Coordinator) _worker(
      FakeTimeProvider clock,
      Func<Task<bool>> onBeat,
      HeartbeatWorkerOptions options,
      ISignalBus? bus = null,
      InstanceLivenessMetrics? metrics = null) {
    var coordinator = new ScriptedHeartbeatCoordinator(onBeat);
    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coordinator);
    var sp = services.BuildServiceProvider();
    var worker = new HeartbeatWorker(
      scopeFactory: sp.GetRequiredService<IServiceScopeFactory>(),
      instanceProvider: new ServiceInstanceProvider(configuration: new ConfigurationBuilder().Build()),
      schemaReadyGate: SchemaReadyGate.AlreadyReady(),
      options: Options.Create(options),
      logger: NullLogger<HeartbeatWorker>.Instance,
      lifecycleState: HeartbeatTestDependencies.LifecycleState,
      libraryVersion: HeartbeatTestDependencies.Version,
      pinnedPool: NoOpPinnedConnectionPool.Instance,
      aliveLockSource: NullInstanceAliveLockSource.Instance,
      signalBus: bus ?? NullSignalBus.Instance,
      timeProvider: clock,
      metrics: metrics);
    return (worker, coordinator);
  }
}
