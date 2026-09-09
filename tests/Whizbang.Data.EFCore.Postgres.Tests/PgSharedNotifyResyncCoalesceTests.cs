using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Notifications;
using Whizbang.Core.Observability;
using Whizbang.Data.Postgres.Notifications;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Regression tests for resync-signal loss in <see cref="PgSharedNotifyConnection"/>.
/// <para>
/// <c>Subscribe</c> only registers intent — the dispatch loop issues the actual <c>LISTEN</c>.
/// A subscription whose resync request is dropped is never listened, so every <c>pg_notify</c>
/// on its channel is silently lost for the lifetime of the connection.
/// </para>
/// </summary>
/// <docs>fundamentals/signal-bus/signal-bus</docs>
[Category("Shard4")]
public class PgSharedNotifyResyncCoalesceTests : EFCoreTestBase {
  private sealed class ChannelProbe(string channel) : INotifySubscription {
    public string ChannelName => channel;
    public void OnNotification(string payload) { }
  }

  private async Task<PgSharedNotifyConnection> _startedConnectionAsync(CancellationToken ct) {
    var opts = new WhizbangNotificationOptions {
      DirectConnectionString = ConnectionString,
      SignalingMode = WorkSignalingMode.ListenNotify,
    };
    var cfg = new ConfigurationBuilder().AddInMemoryCollection([]).Build();
    var shared = new PgSharedNotifyConnection(
      Options.Create(opts), cfg, new ServiceInstanceProvider(cfg),
      NullLogger<PgSharedNotifyConnection>.Instance,
      connectionStringFallback: null,
      timeProvider: null);

    await ((IHostedService)shared).StartAsync(ct);
    var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
    while (!shared.IsAvailable && DateTimeOffset.UtcNow < deadline) {
      await Task.Delay(50, ct);
    }
    await Assert.That(shared.IsAvailable).IsTrue();
    return shared;
  }

  /// <summary>
  /// A second subscription registered while the dispatch loop is mid-sync for the first must
  /// still get its LISTEN. The loop nulls its resync handle before awaiting the LISTEN
  /// round-trip, so a request arriving in that window has nothing to cancel — if the request
  /// is not latched it is dropped outright, and the channel is never listened.
  /// </summary>
  [Test]
  public async Task Subscribe_DuringInFlightSync_StillGetsListenedAsync() {
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    using var shared = await _startedConnectionAsync(cts.Token);

    // Stagger the subscriptions so each lands while the loop is awaiting the previous
    // channel's LISTEN round-trip — after the loop has snapshotted the registry, and while
    // its resync handle is null. A request arriving there has nothing to cancel, so unless it
    // is latched it is dropped and that channel is never listened.
    var channels = new List<string>();
    for (var i = 0; i < 10; i++) {
      var channel = $"wh_resync_probe_{i}_{Guid.NewGuid():N}";
      channels.Add(channel);
      _ = shared.Subscribe(new ChannelProbe(channel));
      await Task.Delay(1, cts.Token);
    }

    using var listenWait = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
    listenWait.CancelAfter(TimeSpan.FromSeconds(10));
    try {
      foreach (var channel in channels) {
        await shared.WaitForChannelListenedAsync(channel, listenWait.Token);
      }
    } catch (OperationCanceledException) {
      // Fall through: the assertion below names the channels that were dropped, which a bare
      // cancellation from the wait would not.
    }

    var listened = shared.ListenedChannelsForTesting;
    await Assert.That(channels.Where(c => !listened.Contains(c)).ToList()).IsEmpty()
      .Because("a resync request that lands while the loop is mid-sync must be latched — a dropped "
             + "one leaves its channel unlistened, and every pg_notify on it is lost for the life "
             + "of the connection");

    await ((IHostedService)shared).StopAsync(CancellationToken.None);
  }
}
