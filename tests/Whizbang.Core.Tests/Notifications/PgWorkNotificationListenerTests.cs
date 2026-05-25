using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Notifications;
using Whizbang.Data.Postgres.Notifications;

namespace Whizbang.Core.Tests.Notifications;

/// <summary>
/// Slice 33.4 — verifies <see cref="PgWorkNotificationListener"/>'s subscriber behavior
/// (parses payloads → fires <see cref="IWorkNotificationListener.OnSignal"/>, mirrors the
/// gate's availability, registers/unregisters via <see cref="IHostedService.StartAsync"/>
/// /<see cref="IHostedService.StopAsync"/>). Replaces the pre-slice-33 startup-branching
/// tests (PgWorkNotificationListenerSignalingModeTests) that asserted the listener's own
/// <c>BackgroundService</c> lifecycle — that lifecycle moved to <c>PgSharedNotifyConnection</c>
/// (slice 33.2 tests cover the SignalingMode branching against the shared connection).
/// </summary>
/// <docs>fundamentals/work-coordinator/notifications-and-pgbouncer</docs>
public class PgWorkNotificationListenerTests {

  private sealed class FakeSharedConnection : ISharedNotifyConnection {
    public readonly List<INotifySubscription> Active = [];
    public readonly List<string> SubscribeChannels = [];
    public readonly List<string> DisposeChannels = [];
    public IDisposable Subscribe(INotifySubscription subscription) {
      Active.Add(subscription);
      SubscribeChannels.Add(subscription.ChannelName);
      return new Handle(this, subscription);
    }
    private sealed class Handle(FakeSharedConnection owner, INotifySubscription sub) : IDisposable {
      public void Dispose() {
        owner.Active.Remove(sub);
        owner.DisposeChannels.Add(sub.ChannelName);
      }
    }
  }

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

  private static (PgWorkNotificationListener Listener, FakeSharedConnection Conn, FakeGate Gate, Guid InstanceId) _build() {
    var cfg = new ConfigurationBuilder().AddInMemoryCollection([]).Build();
    var instanceProvider = new Whizbang.Core.Observability.ServiceInstanceProvider(cfg);
    var conn = new FakeSharedConnection();
    var gate = new FakeGate();
    var listener = new PgWorkNotificationListener(
      conn, gate, instanceProvider, NullLogger<PgWorkNotificationListener>.Instance);
    return (listener, conn, gate, instanceProvider.InstanceId);
  }

  [Test]
  public async Task ChannelName_IsInstanceRoutedAsync() {
    var (listener, _, _, instanceId) = _build();

    await Assert.That(((INotifySubscription)listener).ChannelName).IsEqualTo($"wh_work_i_{instanceId:D}");
  }

  [Test]
  public async Task StartAsync_SubscribesToSharedConnection_OnInstanceChannelAsync() {
    var (listener, conn, _, instanceId) = _build();

    await ((IHostedService)listener).StartAsync(CancellationToken.None);

    await Assert.That(conn.SubscribeChannels).IsEquivalentTo([$"wh_work_i_{instanceId:D}"]);
    await Assert.That(conn.Active).Count().IsEqualTo(1);
  }

  [Test]
  public async Task StopAsync_DisposesSubscriptionAsync() {
    var (listener, conn, _, instanceId) = _build();

    await ((IHostedService)listener).StartAsync(CancellationToken.None);
    await ((IHostedService)listener).StopAsync(CancellationToken.None);

    await Assert.That(conn.DisposeChannels).IsEquivalentTo([$"wh_work_i_{instanceId:D}"]);
    await Assert.That(conn.Active).IsEmpty();
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
  public async Task OnNotification_OutboxPayload_FiresOnSignalWithOutboxAsync() {
    var (listener, _, _, _) = _build();
    var received = new List<WorkSignalCategory>();
    listener.OnSignal += received.Add;

    ((INotifySubscription)listener).OnNotification("outbox");

    await Assert.That(received).IsEquivalentTo([WorkSignalCategory.Outbox]);
  }

  [Test]
  public async Task OnNotification_InboxPayload_FiresOnSignalWithInboxAsync() {
    var (listener, _, _, _) = _build();
    var received = new List<WorkSignalCategory>();
    listener.OnSignal += received.Add;

    ((INotifySubscription)listener).OnNotification("inbox");

    await Assert.That(received).IsEquivalentTo([WorkSignalCategory.Inbox]);
  }

  [Test]
  public async Task OnNotification_PerspectivePayload_FiresOnSignalWithPerspectiveAsync() {
    var (listener, _, _, _) = _build();
    var received = new List<WorkSignalCategory>();
    listener.OnSignal += received.Add;

    ((INotifySubscription)listener).OnNotification("perspective");

    await Assert.That(received).IsEquivalentTo([WorkSignalCategory.Perspective]);
  }

  [Test]
  public async Task OnNotification_UnknownPayload_IsIgnoredAsync() {
    var (listener, _, _, _) = _build();
    var received = new List<WorkSignalCategory>();
    listener.OnSignal += received.Add;

    ((INotifySubscription)listener).OnNotification("bogus");

    await Assert.That(received).IsEmpty();
  }

  [Test]
  public async Task OnNotification_UpdatesLastSignalAtAsync() {
    var (listener, _, _, _) = _build();
    var before = DateTimeOffset.UtcNow;

    ((INotifySubscription)listener).OnNotification("outbox");

    await Assert.That(listener.LastSignalAt).IsNotNull();
    await Assert.That(listener.LastSignalAt!.Value >= before).IsTrue();
  }
}
