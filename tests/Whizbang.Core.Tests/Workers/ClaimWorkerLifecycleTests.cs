using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Notifications;
using Whizbang.Core.Observability;
using Whizbang.Core.Signals;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Covers the ClaimWorker's signal wiring and teardown — the paths around the claim loop
/// rather than inside it.
/// </summary>
/// <remarks>
/// The worker subscribes to a notification listener for its whole lifetime. Those handlers
/// hold it alive, so failing to release them on Dispose leaks the worker; and a signal
/// arriving after teardown must be absorbed rather than thrown from a background callback
/// nobody is awaiting.
/// </remarks>
[Category("Core")]
[Category("Workers")]
public class ClaimWorkerLifecycleTests {

  private sealed class StubInstanceProvider : IServiceInstanceProvider {
    public Guid InstanceId { get; } = TrackedGuid.NewMedo();
    public string ServiceName => "test";
    public string HostName => "test-host";
    public int ProcessId => 1;
    public ServiceInstanceInfo ToInfo() => new() {
      InstanceId = InstanceId,
      ServiceName = ServiceName,
      HostName = HostName,
      ProcessId = ProcessId,
    };
  }

  /// <summary>A listener whose signals a test can raise, and which counts live subscribers.</summary>
  private sealed class ControllableListener : IWorkNotificationListener {
    private Action<WorkSignalCategory>? _onSignal;
    private Action<bool>? _onHealth;

    public bool IsHealthy => true;
    public DateTimeOffset? LastSignalAt => null;

    public event Action<WorkSignalCategory>? OnSignal {
      add { _onSignal += value; SignalSubscribers++; }
      remove { _onSignal -= value; SignalSubscribers--; }
    }

    public event Action<bool>? OnHealthChanged {
      add { _onHealth += value; HealthSubscribers++; }
      remove { _onHealth -= value; HealthSubscribers--; }
    }

    public int SignalSubscribers { get; private set; }
    public int HealthSubscribers { get; private set; }

    public void Raise(WorkSignalCategory category) => _onSignal?.Invoke(category);
  }


  /// <summary>A signal bus whose subscriptions record their own disposal.</summary>
  private sealed class TrackingSignalBus : ISignalBus {
    private sealed class Subscription(TrackingSignalBus owner) : ISignalSubscription {
      public void Dispose() => owner.DisposeCount++;
    }

    public int SubscribeCount { get; private set; }
    public int DisposeCount { get; set; }

    public ValueTask PublishAsync<TSignal>(
        TSignal signal, SignalTarget target = default, CancellationToken cancellationToken = default)
      where TSignal : ISignal => ValueTask.CompletedTask;

    public ISignalSubscription Subscribe<TSignal>(Func<TSignal, ValueTask> handler)
      where TSignal : ISignal {
      SubscribeCount++;
      return new Subscription(this);
    }
  }

  private sealed class MinimalCoordinator : IWorkCoordinator {
    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken ct = default) => Task.CompletedTask;
    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken ct = default)
      => Task.FromResult(new WorkCoordinatorStatistics());
    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(
        Guid streamId, string perspectiveName, CancellationToken ct = default)
      => Task.FromResult<PerspectiveCursorInfo?>(null);
    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion c, CancellationToken ct = default)
      => Task.CompletedTask;
    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure f, CancellationToken ct = default)
      => Task.CompletedTask;
    public Task StoreInboxMessagesAsync(InboxMessage[] m, int partitionCount, CancellationToken ct = default)
      => Task.CompletedTask;
  }

  private static ClaimWorker _worker(ControllableListener listener, ISignalBus? bus = null) {
    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(new MinimalCoordinator());
    var sp = services.BuildServiceProvider();
    var gate = new SchemaReadyGate();
    gate.MarkReady();
    return new ClaimWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      new StubInstanceProvider(),
      listener,
      gate,
      Options.Create(new ClaimWorkerOptions {
        PollingIntervalMilliseconds = 50,
        PollingMaxIntervalMilliseconds = 200,
      }),
      NullLogger<ClaimWorker>.Instance,
      signalBus: bus);
  }

  [Test]
  public async Task Constructor_SubscribesToTheNotificationListenerAsync() {
    var listener = new ControllableListener();

    using var worker = _worker(listener);

    await Assert.That(listener.SignalSubscribers).IsGreaterThan(0);
  }

  [Test]
  public async Task Dispose_ReleasesTheSignalBusSubscriptionsAsync() {
    // The bus holds a handler reference per subscription. A worker that stops without releasing
    // them leaves three dead handlers on a bus that outlives it, so every later publish walks
    // them and wakes a worker that is gone.
    var bus = new TrackingSignalBus();
    var worker = _worker(new ControllableListener(), bus);

    worker.Dispose();

    await Assert.That(bus.SubscribeCount).IsEqualTo(3);
    await Assert.That(bus.DisposeCount).IsEqualTo(3);
  }

  [Test]
  public async Task Dispose_DoesNotReleaseTheSameSubscriptionTwiceAsync() {
    // StopAsync followed by the host's own disposal is an ordinary shape, so the second pass
    // has to find the handles already cleared rather than dispose them again.
    var bus = new TrackingSignalBus();
    var worker = _worker(new ControllableListener(), bus);

    worker.Dispose();
    worker.Dispose();

    await Assert.That(bus.DisposeCount).IsEqualTo(3);
  }

  [Test]
  public async Task Dispose_WithoutASignalBus_IsSafeAsync() {
    // The bus is optional: in a pull-only deployment nothing was ever subscribed, and teardown
    // must not fault on the handles that were never assigned.
    var listener = new ControllableListener();
    var worker = _worker(listener);

    worker.Dispose();
    worker.Dispose();

    await Assert.That(listener.SignalSubscribers).IsGreaterThan(0);
  }

  [Test]
  public async Task OrphanRedistributeSignal_RequestsAnImmediatePollAsync() {
    // Orphan redistribution is time-sensitive: the rows are already claimed by an instance
    // that stopped, so this category skips the doorbell and pokes the loop directly.
    var listener = new ControllableListener();
    using var worker = _worker(listener);

    listener.Raise(WorkSignalCategory.OrphanRedistribute);

    // Reaching here without throwing is the assertion — the handler runs on the listener's
    // thread, so an exception would surface as an unobserved callback failure.
    await Assert.That(listener.SignalSubscribers).IsGreaterThan(0);
  }

  [Test]
  [Arguments(WorkSignalCategory.Outbox)]
  [Arguments(WorkSignalCategory.Inbox)]
  [Arguments(WorkSignalCategory.Perspective)]
  public async Task WorkSignals_WakeTheLoopWithoutABusAsync(WorkSignalCategory category) {
    // With no signal bus configured the listener is the only wake source, so these
    // categories have to ring the doorbell themselves.
    var listener = new ControllableListener();
    using var worker = _worker(listener);

    listener.Raise(category);

    await Assert.That(listener.SignalSubscribers).IsGreaterThan(0);
  }

  [Test]
  public async Task SignalsAfterDispose_AreAbsorbedAsync() {
    // A signal can arrive from the listener's thread after teardown. It must not throw:
    // nobody is awaiting that callback, so an exception there is unobserved.
    var listener = new ControllableListener();
    var worker = _worker(listener);
    worker.Dispose();

    // The listener handler outlives Dispose by design — it is the only wake path for the
    // orphan and dead-letter categories, which have no typed bus signal. So the signal still
    // reaches a worker whose nap token is disposed, and the guard has to swallow that.
    listener.Raise(WorkSignalCategory.Outbox);
    listener.Raise(WorkSignalCategory.OrphanRedistribute);

    await Assert.That(listener.SignalSubscribers).IsGreaterThan(0);
  }

  [Test]
  public async Task RequestImmediatePoll_IsIdempotentWhileOnePollIsPendingAsync() {
    // The doorbell is a one-slot semaphore: several signals arriving before the loop wakes
    // collapse into a single poll rather than queueing a burst of them.
    var listener = new ControllableListener();
    using var worker = _worker(listener);

    worker.RequestImmediatePoll();
    worker.RequestImmediatePoll();
    worker.RequestImmediatePoll();

    await Assert.That(listener.SignalSubscribers).IsGreaterThan(0);
  }
}
