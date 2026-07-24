using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Notifications;
using Whizbang.Core.Observability;
using Whizbang.Data.Postgres.Notifications;

namespace Whizbang.Core.Tests.Notifications;

/// <summary>
/// Slice 33.1 — exercises <see cref="PgSharedNotifyConnection"/>'s startup branching +
/// subscription registry interaction WITHOUT a real Postgres. The actual LISTEN/UNLISTEN
/// + reconnect flow needs real Postgres and lives in the EFCore.Postgres integration tests.
/// </summary>
/// <docs>fundamentals/work-coordinator/notifications-and-pgbouncer</docs>
public class PgSharedNotifyConnectionTests {

  private sealed class FakeSubscription(string channel) : INotifySubscription {
    public string ChannelName { get; } = channel;
    public void OnNotification(string payload) { /* slice 33.3 wires dispatch */ }
  }

  private static IConfiguration _emptyConfig() =>
    new ConfigurationBuilder().AddInMemoryCollection([]).Build();

  private static IConfiguration _configWith(string key, string value) =>
    new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [key] = value }).Build();

  private static PgSharedNotifyConnection _build(WhizbangNotificationOptions opts, IConfiguration? cfg = null) {
    cfg ??= _emptyConfig();
    return new PgSharedNotifyConnection(
      Options.Create(opts),
      cfg,
      new ServiceInstanceProvider(cfg),
      NullLogger<PgSharedNotifyConnection>.Instance,
      connectionStringFallback: null);
  }

  [Test]
  public async Task Subscribe_BeforeConnect_RegistersInRegistry_DoesNotThrowAsync() {
    var conn = _build(new WhizbangNotificationOptions { SignalingMode = WorkSignalingMode.Polling });

    var sub = new FakeSubscription("wh_test_channel");
    using var handle = conn.Subscribe(sub);

    await Assert.That(conn.RegistryForTesting.AllChannels()).Contains("wh_test_channel");
    await Assert.That(conn.RegistryForTesting.TotalSubscriberCount()).IsEqualTo(1);
  }

  [Test]
  public async Task Subscribe_ThenDispose_RemovesFromRegistryAsync() {
    var conn = _build(new WhizbangNotificationOptions { SignalingMode = WorkSignalingMode.Polling });
    var sub = new FakeSubscription("wh_test_channel");

    var handle = conn.Subscribe(sub);
    handle.Dispose();

    await Assert.That(conn.RegistryForTesting.AllChannels()).DoesNotContain("wh_test_channel");
    await Assert.That(conn.RegistryForTesting.TotalSubscriberCount()).IsEqualTo(0);
  }

  [Test]
  public async Task Subscribe_DisposeIsIdempotent_DoesNotDoubleRemoveAsync() {
    var conn = _build(new WhizbangNotificationOptions { SignalingMode = WorkSignalingMode.Polling });
    var sub1 = new FakeSubscription("wh_test");
    var sub2 = new FakeSubscription("wh_test");

    var h1 = conn.Subscribe(sub1);
    _ = conn.Subscribe(sub2);
    h1.Dispose();
    h1.Dispose();  // idempotent — must not remove sub2

    await Assert.That(conn.RegistryForTesting.Get("wh_test").Length).IsEqualTo(1);
  }

  [Test]
  public async Task Subscribe_TwoChannels_BothRegisteredAsync() {
    var conn = _build(new WhizbangNotificationOptions { SignalingMode = WorkSignalingMode.Polling });
    using var h1 = conn.Subscribe(new FakeSubscription("ch_a"));
    using var h2 = conn.Subscribe(new FakeSubscription("ch_b"));

    await Assert.That(conn.RegistryForTesting.AllChannels()).Contains("ch_a");
    await Assert.That(conn.RegistryForTesting.AllChannels()).Contains("ch_b");
  }

  [Test]
  public async Task ExecuteAsync_PollingMode_ReturnsImmediatelyWithoutOpeningConnectionAsync() {
    var conn = _build(new WhizbangNotificationOptions { SignalingMode = WorkSignalingMode.Polling });

    using var cts = new CancellationTokenSource();
    await conn.StartAsync(cts.Token);
    await conn.ExecuteTask!;

    await Assert.That(conn.ExecuteTask!.IsCompletedSuccessfully).IsTrue();
    await Assert.That(conn.IsAvailable).IsFalse();
    await Assert.That(conn.IsConnectionOpenForTesting).IsFalse();
    await conn.StopAsync(CancellationToken.None);
  }

  [Test]
  public async Task ExecuteAsync_DisableNotifications_ReturnsImmediatelyAsync() {
    var conn = _build(new WhizbangNotificationOptions {
      DisableNotifications = true,
      ConnectionStringKey = "ignored"
    });

    using var cts = new CancellationTokenSource();
    await conn.StartAsync(cts.Token);
    await conn.ExecuteTask!;

    await Assert.That(conn.ExecuteTask!.IsCompletedSuccessfully).IsTrue();
    await Assert.That(conn.IsAvailable).IsFalse();
    await conn.StopAsync(CancellationToken.None);
  }

  [Test]
  public async Task ExecuteAsync_AutoMode_NoConnectionString_ReturnsWithoutThrowAsync() {
    var conn = _build(new WhizbangNotificationOptions {
      SignalingMode = WorkSignalingMode.Auto,
      // ConnectionStringKey not set, no DirectConnectionString — resolver returns null.
    });

    using var cts = new CancellationTokenSource();
    await conn.StartAsync(cts.Token);
    await conn.ExecuteTask!;

    await Assert.That(conn.ExecuteTask!.IsCompletedSuccessfully).IsTrue();
    await Assert.That(conn.IsAvailable).IsFalse();
    await conn.StopAsync(CancellationToken.None);
  }

  [Test]
  public async Task ExecuteAsync_ListenNotifyMode_NoConnectionString_ThrowsAtStartupAsync() {
    var conn = _build(new WhizbangNotificationOptions {
      SignalingMode = WorkSignalingMode.ListenNotify,
      // No connection string → fail-fast on the ListenNotify contract.
    });

    using var cts = new CancellationTokenSource();
    await conn.StartAsync(cts.Token);

    await Assert.That(async () => await conn.ExecuteTask!).ThrowsExactly<InvalidOperationException>();
    await conn.StopAsync(CancellationToken.None);
  }

  [Test]
  public async Task Subscribe_BeforePollingModeReturns_StillRegistersAsync() {
    // Even when SignalingMode = Polling and ExecuteAsync no-ops, Subscribe still registers the
    // channel. Subscribers don't need to care whether the conn ever opens — they wire up at
    // startup and remain in the registry for slice 33.2's reprobe-recovery path.
    var conn = _build(new WhizbangNotificationOptions { SignalingMode = WorkSignalingMode.Polling });

    using var cts = new CancellationTokenSource();
    await conn.StartAsync(cts.Token);
    await conn.ExecuteTask!;

    using var handle = conn.Subscribe(new FakeSubscription("post_start_channel"));

    await Assert.That(conn.RegistryForTesting.AllChannels()).Contains("post_start_channel");
    await conn.StopAsync(CancellationToken.None);
  }

  [Test]
  public async Task ProbeNowAsync_Slice33_1_ReflectsCurrentIsAvailableAsync() {
    var conn = _build(new WhizbangNotificationOptions { SignalingMode = WorkSignalingMode.Polling });

    // No probe yet (slice 33.2). With Polling mode we never go available, so ProbeNowAsync
    // surfaces the current state (false).
    var result = await conn.ProbeNowAsync();

    await Assert.That(result).IsFalse();
  }
}
