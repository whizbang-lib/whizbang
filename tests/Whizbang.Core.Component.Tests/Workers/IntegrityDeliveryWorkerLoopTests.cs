// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// The integrity delivery drain's loop (#2001): it waits for the schema, runs a pass, survives a failed pass, and
/// stops cleanly. Every wait is on a signal the worker body emits (<see cref="IntegrityDeliveryWorker.OnPassCompleted"/>,
/// or the gate being entered); the clock is fake and advanced explicitly.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Workers/IntegrityDeliveryWorker.cs</code-under-test>
/// <docs>resilience/stream-integrity#integrity-work-runs-on-the-maintainer</docs>
[NotInParallel("WhizbangBackgroundServiceTests")]
public class IntegrityDeliveryWorkerLoopTests {
  private const string TYPE = "loop-test";
  private static readonly TimeSpan _wait = TimeSpan.FromSeconds(30);

  private sealed class QueueCoordinator : IWorkCoordinator {
    public Queue<IntegrityDelivery> Due { get; } = new();
    public Queue<Exception> LeaseFailures { get; } = new();
    public List<Guid> Completed { get; } = [];

    public Task<IReadOnlyList<IntegrityDelivery>> LeaseIntegrityDeliveriesAsync(int maxDeliveries, TimeSpan lease, CancellationToken cancellationToken = default) {
      if (LeaseFailures.TryDequeue(out var failure)) {
        return Task.FromException<IReadOnlyList<IntegrityDelivery>>(failure);
      }
      List<IntegrityDelivery> batch = [];
      while (batch.Count < maxDeliveries && Due.TryDequeue(out var next)) {
        batch.Add(next);
      }
      return Task.FromResult<IReadOnlyList<IntegrityDelivery>>(batch);
    }

    public Task CompleteIntegrityDeliveryAsync(Guid deliveryId, CancellationToken cancellationToken = default) {
      Completed.Add(deliveryId);
      return Task.CompletedTask;
    }

    public Task<WorkBatch> ClaimWorkAsync(ClaimWorkRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new WorkCoordinatorStatistics());
    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<PartitionRecomputeResult> RecomputePartitionNumbersAsync(int partitionCount, CancellationToken cancellationToken = default) => Task.FromResult(new PartitionRecomputeResult());
    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion completion, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure failure, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default) => Task.FromResult<PerspectiveCursorInfo?>(null);
    public Task<IReadOnlyList<MaintenanceResult>> PerformMaintenanceAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<MaintenanceResult>>([]);
  }

  /// <summary>A gate that never opens and says when the worker reached it.</summary>
  private sealed class ClosedGate : ISchemaReadyGate {
    public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public bool IsReady => false;
    public void MarkReady() { }
    public async Task WaitForReadyAsync(CancellationToken cancellationToken) {
      Entered.TrySetResult();
      await Task.Delay(Timeout.Infinite, cancellationToken);
    }
  }

  /// <summary>A fake clock that signals each timer created on it, so a test advances only once the worker is waiting.</summary>
  private sealed class SignalingClock(DateTimeOffset start) : FakeTimeProvider(start) {
    public SemaphoreSlim TimersCreated { get; } = new(0);
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) {
      var timer = base.CreateTimer(callback, state, dueTime, period);
      TimersCreated.Release();
      return timer;
    }
  }

  private sealed class CapturingLogger : ILogger<IntegrityDeliveryWorker> {
    public List<Exception?> Warnings { get; } = [];
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) {
      if (logLevel == LogLevel.Warning) {
        lock (Warnings) { Warnings.Add(exception); }
      }
    }
  }

  private static (IntegrityDeliveryWorker Worker, QueueCoordinator Coordinator, List<string> Handled, CapturingLogger Logger, SignalingClock Clock, ServiceProvider Services)
      _build(ISchemaReadyGate gate) {
    var coordinator = new QueueCoordinator();
    var provider = new ServiceCollection().AddScoped<IWorkCoordinator>(_ => coordinator).BuildServiceProvider();
    var deliveries = new IntegrityDeliveryRegistry();
    var handled = new List<string>();
    deliveries.Register(TYPE, (payload, _, _) => { handled.Add(payload); return Task.CompletedTask; });
    var logger = new CapturingLogger();
    var clock = new SignalingClock(new DateTimeOffset(2026, 11, 2, 12, 0, 0, TimeSpan.Zero));
    var worker = new IntegrityDeliveryWorker(provider.GetRequiredService<IServiceScopeFactory>(), gate,
      Options.Create(new StreamIntegrityOptions { IntegrityDeliveryIntervalSeconds = 15 }), deliveries, logger, clock);
    return (worker, coordinator, handled, logger, clock, provider);
  }

  private static TaskCompletionSource _nextPass(IntegrityDeliveryWorker worker) {
    var passed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    void OnPass() {
      worker.OnPassCompleted -= OnPass;
      passed.TrySetResult();
    }
    worker.OnPassCompleted += OnPass;
    return passed;
  }

  [Test]
  public async Task TheLoop_RunsAPassOnceTheSchemaIsReady_AndStopsCleanlyAsync() {
    var (worker, coordinator, handled, _, _, services) = _build(SchemaReadyGate.AlreadyReady());
    await using var _ = services;
    var id = Guid.NewGuid();
    coordinator.Due.Enqueue(new IntegrityDelivery(id, TYPE, "payload", 0, DateTimeOffset.UnixEpoch));
    var firstPass = _nextPass(worker);

    await worker.StartAsync(CancellationToken.None);
    await firstPass.Task.WaitAsync(_wait);
    await worker.StopAsync(CancellationToken.None);
    await worker.ExecuteTask!.WaitAsync(_wait).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

    await Assert.That(handled).IsEquivalentTo(["payload"]);
    await Assert.That(coordinator.Completed).IsEquivalentTo([id]);
    await Assert.That(worker.ExecuteTask.IsFaulted).IsFalse();
  }

  [Test]
  [Arguments(false)]
  [Arguments(true)]
  public async Task AFailedPass_IsLogged_AndTheNextIntervalRunsAgainAsync(bool cancellationNotFromTheHost) {
    var (worker, coordinator, handled, logger, clock, services) = _build(SchemaReadyGate.AlreadyReady());
    await using var _ = services;
    // A cancellation the host did not ask for (a downstream timeout) is a failed pass too, not a stop.
    coordinator.LeaseFailures.Enqueue(cancellationNotFromTheHost
      ? new OperationCanceledException("a downstream timeout")
      : new InvalidOperationException("database unavailable"));
    coordinator.Due.Enqueue(new IntegrityDelivery(Guid.NewGuid(), TYPE, "after-recovery", 0, DateTimeOffset.UnixEpoch));
    var firstPass = _nextPass(worker);

    await worker.StartAsync(CancellationToken.None);
    await firstPass.Task.WaitAsync(_wait);
    var secondPass = _nextPass(worker);
    await clock.TimersCreated.WaitAsync(_wait);   // the worker is now waiting out its interval
    clock.Advance(TimeSpan.FromSeconds(15));
    await secondPass.Task.WaitAsync(_wait);
    await worker.StopAsync(CancellationToken.None);
    await worker.ExecuteTask!.WaitAsync(_wait).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

    await Assert.That(logger.Warnings.Count).IsEqualTo(1).Because("the failed pass is reported, never swallowed");
    await Assert.That(handled).IsEquivalentTo(["after-recovery"])
      .Because("one failed pass must not stop the drain");
    await Assert.That(worker.ExecuteTask.IsFaulted).IsFalse();
  }

  [Test]
  public async Task StoppedBeforeTheSchemaIsReady_TheLoopEndsWithoutAPassAsync() {
    var gate = new ClosedGate();
    var (worker, coordinator, _, _, _, services) = _build(gate);
    await using var _ = services;
    var passes = 0;
    worker.OnPassCompleted += () => Interlocked.Increment(ref passes);

    await worker.StartAsync(CancellationToken.None);
    await gate.Entered.Task.WaitAsync(_wait);
    await worker.StopAsync(CancellationToken.None);
    await worker.ExecuteTask!.WaitAsync(_wait).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

    await Assert.That(worker.ExecuteTask.IsCompleted).IsTrue();
    await Assert.That(worker.ExecuteTask.IsFaulted).IsFalse();
    await Assert.That(passes).IsEqualTo(0).Because("the worker provably reached the gate and touched nothing before it opened");
    await Assert.That(coordinator.Completed).IsEmpty();
  }
}
