using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Notifications;
using Whizbang.Core.Observability;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Covers <see cref="IdleActivityTouchHookBinder"/>, the hosted service that wires activity
/// sources to <see cref="IIdleActivityTracker.Touch"/> so a busy service is never mistaken
/// for an idle one.
/// </summary>
/// <remarks>
/// The binder stores each handler in a field so <c>StopAsync</c> can detach the same delegate
/// instance. That detail is the whole test: <c>-=</c> against a freshly-created lambda silently
/// removes nothing, and the binder would keep touching the tracker after shutdown while holding
/// the workers alive. Only the notification listener is an interface, so it is the source whose
/// event this test can raise -- <see cref="ClaimWorker"/> and <see cref="HeartbeatWorker"/>
/// declare their events on concrete classes, which nothing outside them can fire.
/// </remarks>
[Category("Core")]
[Category("Workers")]
public class IdleActivityTouchHookBinderTests {

  private sealed class RecordingTracker : IIdleActivityTracker {
    public List<string> Touches { get; } = [];
    public void Touch(string source) => Touches.Add(source);
    public TimeSpan TimeSinceLastActivity => TimeSpan.Zero;
    public DateTimeOffset LastActivityAt => DateTimeOffset.UtcNow;
    public string LastActivitySource => Touches.Count > 0 ? Touches[^1] : string.Empty;
  }

  /// <summary>Listener whose signal event this test can actually raise.</summary>
  private sealed class RaisableListener : IWorkNotificationListener {
    public bool IsHealthy => true;
    public DateTimeOffset? LastSignalAt { get; private set; }
    public event Action<WorkSignalCategory>? OnSignal;
    public event Action<bool>? OnHealthChanged;

    public void RaiseSignal(WorkSignalCategory category) {
      LastSignalAt = DateTimeOffset.UtcNow;
      OnSignal?.Invoke(category);
    }

    /// <summary>Exercised only to keep the unused-event warning honest.</summary>
    public void RaiseHealthChanged(bool healthy) => OnHealthChanged?.Invoke(healthy);
  }

  private sealed class StubInstanceProvider : IServiceInstanceProvider {
    public Guid InstanceId { get; } = Guid.NewGuid();
    public string ServiceName => "Test";
    public string HostName => "test-host";
    public int ProcessId => 1;
    public ServiceInstanceInfo ToInfo() => new() {
      ServiceName = ServiceName,
      InstanceId = InstanceId,
      HostName = HostName,
      ProcessId = ProcessId,
    };
  }

  private static (IdleActivityTouchHookBinder Binder, RecordingTracker Tracker, RaisableListener Listener) _build() {
    var tracker = new RecordingTracker();
    var listener = new RaisableListener();
    var services = new ServiceCollection();
    services.AddLogging();
    var provider = services.BuildServiceProvider();
    var gate = SchemaReadyGate.AlreadyReady();
    var instance = new StubInstanceProvider();

    var claimWorker = new ClaimWorker(
      provider.GetRequiredService<IServiceScopeFactory>(),
      instance,
      listener,
      gate,
      Options.Create(new ClaimWorkerOptions()),
      NullLogger<ClaimWorker>.Instance);

    var heartbeatWorker = new HeartbeatWorker(
      provider.GetRequiredService<IServiceScopeFactory>(),
      instance,
      gate,
      Options.Create(new HeartbeatWorkerOptions()),
      NullLogger<HeartbeatWorker>.Instance);

    return (new IdleActivityTouchHookBinder(tracker, claimWorker, heartbeatWorker, listener), tracker, listener);
  }

  [Test]
  public async Task BeforeStart_ASignalTouchesNothingAsync() {
    // Establishes that the touch seen after StartAsync is the subscription's doing and not
    // something the listener does on its own.
    var (_, tracker, listener) = _build();

    listener.RaiseSignal(WorkSignalCategory.Outbox);

    await Assert.That(tracker.Touches).IsEmpty();
  }

  [Test]
  public async Task AfterStart_ASignalTouchesTheTrackerAsNotifyAsync() {
    // Zero-idle-polling depends on this: an arriving NOTIFY is real activity, and a tracker
    // that never hears about it lets the service be classified idle while work is flowing.
    var (binder, tracker, listener) = _build();
    await binder.StartAsync(CancellationToken.None);

    listener.RaiseSignal(WorkSignalCategory.Inbox);

    await Assert.That(tracker.Touches).IsEquivalentTo(new List<string> { "notify" })
      .Because("the source string is what makes the activity diagnosable after the fact");
  }

  [Test]
  public async Task AfterStop_TheHandlerIsDetachedAsync() {
    // The reason the binder holds its handlers in fields. Detaching a newly-built lambda would
    // remove nothing, and the tracker would keep being touched by a stopped service -- which
    // both falsifies idle detection and keeps the workers referenced.
    var (binder, tracker, listener) = _build();
    await binder.StartAsync(CancellationToken.None);
    listener.RaiseSignal(WorkSignalCategory.Outbox);
    var touchesWhileRunning = tracker.Touches.Count;

    await binder.StopAsync(CancellationToken.None);
    listener.RaiseSignal(WorkSignalCategory.Outbox);

    await Assert.That(touchesWhileRunning).IsEqualTo(1);
    await Assert.That(tracker.Touches.Count).IsEqualTo(1)
      .Because("a stopped binder must not touch the tracker again");
  }

  [Test]
  public async Task StopWithoutStart_IsHarmlessAsync() {
    // A host that fails during startup still stops every registered hosted service. Unsubscribing
    // handlers that were never attached must not throw, or one failure becomes two.
    var (binder, _, _) = _build();

    await Assert.That(async () => await binder.StopAsync(CancellationToken.None)).ThrowsNothing();
  }

  [Test]
  public async Task RestartingRebindsExactlyOnceAsync() {
    // Start/Stop/Start must leave one subscription, not two. A double-bind would double every
    // touch -- harmless-looking, but it is the shape of a leak that grows with each restart.
    var (binder, tracker, listener) = _build();
    await binder.StartAsync(CancellationToken.None);
    await binder.StopAsync(CancellationToken.None);
    await binder.StartAsync(CancellationToken.None);

    listener.RaiseSignal(WorkSignalCategory.Perspective);

    await Assert.That(tracker.Touches.Count).IsEqualTo(1)
      .Because("rebinding after a stop leaves a single live handler");
  }

  [Test]
  public async Task Constructor_RejectsANullTrackerAsync() {
    var (binder, _, listener) = _build();
    _ = binder;
    await Assert.That(() => new IdleActivityTouchHookBinder(null!, null!, null!, listener))
      .Throws<ArgumentNullException>();
  }
}
