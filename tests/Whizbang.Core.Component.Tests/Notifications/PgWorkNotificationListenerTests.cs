// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Notifications;
using Whizbang.Core.Signals;
using Whizbang.Data.Postgres.Notifications;

namespace Whizbang.Core.Tests.Notifications;

/// <summary>
/// #2046: <see cref="PgWorkNotificationListener"/> is a subscriber of the signal bus. The work doorbells (outbox, inbox,
/// perspective, orphaned work, a dead letter) arrive as signals; the listener turns each into its
/// <see cref="WorkSignalCategory"/>, mirrors the gate's availability, and subscribes and unsubscribes with the host.
/// </summary>
/// <docs>fundamentals/work-coordinator/notifications-and-pgbouncer</docs>
public class PgWorkNotificationListenerTests {
  private sealed class FakeGate : INotifySignalingGate {
    public bool IsAvailable { get; private set; }
    public DateTimeOffset? LastVerifiedAt => null;
    public DateTimeOffset? LastFailureAt => null;
    public string? LastFailureReason => null;
    public event Action<bool>? OnAvailabilityChanged;
    public Task<bool> ProbeNowAsync(CancellationToken cancellationToken = default) => Task.FromResult(IsAvailable);

    public void Set(bool available) {
      if (IsAvailable == available) {
        return;
      }
      IsAvailable = available;
      OnAvailabilityChanged?.Invoke(available);
    }
  }

  private static (PgWorkNotificationListener Listener, SignalBus Bus, FakeGate Gate, FakeTimeProvider Time) _build() {
    var bus = new SignalBus([new InMemorySignalTransport()], []);
    bus.StartAsync().GetAwaiter().GetResult();
    var gate = new FakeGate();
    var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 4, 12, 0, 0, TimeSpan.Zero));
    var listener = new PgWorkNotificationListener(bus, gate, NullLogger<PgWorkNotificationListener>.Instance, time);
    return (listener, bus, gate, time);
  }

  private static async Task<List<WorkSignalCategory>> _startedAsync(PgWorkNotificationListener listener) {
    var received = new List<WorkSignalCategory>();
    listener.OnSignal += received.Add;
    await ((IHostedService)listener).StartAsync(CancellationToken.None);
    return received;
  }

  [Test]
  public async Task EveryWorkDoorbell_FiresOnSignalWithItsCategoryAsync() {
    var (listener, bus, _, _) = _build();
    var received = await _startedAsync(listener);
    var stream = SignalTarget.Streams([Guid.CreateVersion7()]);

    await bus.PublishAsync(new WorkOutboxAvailableSignal(), stream);
    await bus.PublishAsync(new WorkInboxAvailableSignal(), stream);
    await bus.PublishAsync(new WorkPerspectiveAvailableSignal(), stream);
    // The reap wakes each live instance after it released a dead one's leases (cleanup_stale_instances).
    await bus.PublishAsync(new WorkOrphanedSignal(), stream);
    // The dead-letter table's insert trigger.
    await bus.PublishAsync(new DeadLetterReadySignal(), stream);

    await Assert.That(received).IsEquivalentTo([
      WorkSignalCategory.Outbox, WorkSignalCategory.Inbox, WorkSignalCategory.Perspective,
      WorkSignalCategory.OrphanRedistribute, WorkSignalCategory.DeadLetterReady,
    ], TUnit.Assertions.Enums.CollectionOrdering.Matching);
  }

  [Test]
  public async Task BeforeStart_AndAfterStop_NothingIsHeardAsync() {
    var (listener, bus, _, _) = _build();
    var received = new List<WorkSignalCategory>();
    listener.OnSignal += received.Add;
    var stream = SignalTarget.Streams([Guid.CreateVersion7()]);

    await bus.PublishAsync(new WorkOutboxAvailableSignal(), stream);
    await ((IHostedService)listener).StartAsync(CancellationToken.None);
    await ((IHostedService)listener).StopAsync(CancellationToken.None);
    await ((IHostedService)listener).StopAsync(CancellationToken.None);
    await bus.PublishAsync(new WorkOutboxAvailableSignal(), stream);

    await Assert.That(received).IsEmpty();
  }

  /// <summary>
  /// A host torn down without a clean stop still releases the subscription: disposing a started listener leaves the
  /// bus, so the bus does not keep calling a listener that no longer exists.
  /// </summary>
  [Test]
  public async Task Dispose_WithoutStop_LeavesTheBusAsync() {
    var (listener, bus, _, _) = _build();
    var received = await _startedAsync(listener);

    listener.Dispose();
    await bus.PublishAsync(new WorkInboxAvailableSignal(), SignalTarget.Streams([Guid.CreateVersion7()]));

    await Assert.That(received).IsEmpty();
  }

  [Test]
  public async Task IsHealthy_DelegatesToGateAvailabilityAsync() {
    var (listener, _, gate, _) = _build();

    await Assert.That(listener.IsHealthy).IsFalse();
    gate.Set(true);
    await Assert.That(listener.IsHealthy).IsTrue();
    gate.Set(false);
    await Assert.That(listener.IsHealthy).IsFalse();
  }

  [Test]
  public async Task OnHealthChanged_MirrorsGateAvailabilityTransitionsAsync() {
    var (listener, _, gate, _) = _build();

    var seen = new List<bool>();
    listener.OnHealthChanged += seen.Add;

    gate.Set(true);
    gate.Set(false);
    gate.Set(true);

    await Assert.That(seen).IsEquivalentTo([true, false, true]);
  }

  [Test]
  public async Task ASignal_StampsLastSignalAt_FromTheListenersClockAsync() {
    var (listener, bus, _, time) = _build();
    _ = await _startedAsync(listener);
    await Assert.That(listener.LastSignalAt).IsNull();

    await bus.PublishAsync(new WorkPerspectiveAvailableSignal(), SignalTarget.Streams([Guid.CreateVersion7()]));

    await Assert.That(listener.LastSignalAt).IsEqualTo(time.GetUtcNow());
  }

  [Test]
  public async Task Constructor_RequiresABusAndAGateAsync() {
    var bus = new SignalBus([], []);
    await Assert.That(() => new PgWorkNotificationListener(null!, new FakeGate())).Throws<ArgumentNullException>();
    await Assert.That(() => new PgWorkNotificationListener(bus, null!)).Throws<ArgumentNullException>();
  }
}
