using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Configuration;
using Whizbang.Core.Notifications;
using Whizbang.Core.Observability;
using Whizbang.Data.Postgres.Notifications;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// The other half of the shared LISTEN connection's lifecycle: what happens when the last
/// subscriber to a channel goes away.
/// <para>
/// One connection multiplexes every channel this pod cares about, and channels come and go with
/// subscribers — a stream closes, a perspective is retired, a lens stops being queried. A
/// connection that only ever adds is a connection that keeps waking for notifications nobody is
/// listening to, for the life of the process. The LISTEN half is covered by the end-to-end
/// latency tests; the UNLISTEN half never ran.
/// </para>
/// </summary>
/// <remarks>
/// Live PostgreSQL, because the whole behavior is <c>LISTEN</c>/<c>UNLISTEN</c> against a real
/// session. The assertion reads the connection's own view of its listened set: PostgreSQL exposes
/// no catalog for another session's channels, and "the notification stopped arriving" is an
/// absence that cannot be waited for.
/// </remarks>
/// <code-under-test>src/Whizbang.Data.Postgres/Notifications/PgSharedNotifyConnection.cs</code-under-test>
[Category("Integration")]
[Category("Shard2")]
public class PgSharedNotifyConnectionUnlistenTests : EFCoreTestBase {

  [Test]
  [Timeout(60000)]
  public async Task TheLastSubscriberLeaving_DropsTheChannelFromTheListenSetAsync(
      CancellationToken cancellationToken) {
    using var shared = _sharedConnection();
    await shared.StartAsync(cancellationToken);
    try {
      var channel = $"wh_test_unlisten_{Guid.CreateVersion7():N}";
      var handle = shared.Subscribe(new NoopSubscription(channel));

      // Deterministic: the LISTEN is issued by the dispatch loop, not by Subscribe.
      await shared.WaitForChannelListenedAsync(channel, cancellationToken);
      await Assert.That(shared.ListenedChannelsForTesting).Contains(channel);

      handle.Dispose();
      await _awaitRemovalPassAsync(shared, cancellationToken);

      await Assert.That(shared.ListenedChannelsForTesting).DoesNotContain(channel)
        .Because("a shared connection that only ever adds channels keeps waking for notifications "
               + "nobody is listening to, for the life of the process");
    } finally {
      await shared.StopAsync(CancellationToken.None);
    }
  }

  [Test]
  [Timeout(60000)]
  public async Task OneOfTwoSubscribersLeaving_KeepsTheChannelListenedAsync(
      CancellationToken cancellationToken) {
    // The guard on the other side: UNLISTEN is per CHANNEL, not per subscription. Dropping the
    // channel while a second subscriber still wants it would silently stop delivering to a live
    // consumer — the failure would look like a lost notification, arbitrarily far from here.
    using var shared = _sharedConnection();
    await shared.StartAsync(cancellationToken);
    try {
      var channel = $"wh_test_shared_{Guid.CreateVersion7():N}";
      var first = shared.Subscribe(new NoopSubscription(channel));
      var second = shared.Subscribe(new NoopSubscription(channel));
      await shared.WaitForChannelListenedAsync(channel, cancellationToken);

      first.Dispose();
      await _awaitRemovalPassAsync(shared, cancellationToken);

      await Assert.That(shared.ListenedChannelsForTesting).Contains(channel)
        .Because("the second subscriber is still waiting on this channel — dropping it here loses "
               + "notifications for a live consumer");

      second.Dispose();
      await _awaitRemovalPassAsync(shared, cancellationToken);

      await Assert.That(shared.ListenedChannelsForTesting).DoesNotContain(channel)
        .Because("once nobody wants it, the channel goes");
    } finally {
      await shared.StopAsync(CancellationToken.None);
    }
  }

  private PgSharedNotifyConnection _sharedConnection() =>
    new(Options.Create(new WhizbangNotificationOptions {
      DirectConnectionString = ConnectionString,
      SignalingMode = WorkSignalingMode.ListenNotify,
    }),
        new ConfigurationBuilder().AddInMemoryCollection([]).Build(),
        new ServiceInstanceProvider(new ConfigurationBuilder().AddInMemoryCollection([]).Build()),
        NullLogger<PgSharedNotifyConnection>.Instance,
        connectionStringFallback: null,
        timeProvider: null);

  /// <summary>
  /// Blocks until a resync pass that could have removed channels has fully completed.
  /// </summary>
  /// <remarks>
  /// Removal has no signal of its own — nothing fires when a channel is UNLISTENed — and a single
  /// probe is not enough, because a pass issues its LISTENs before its UNLISTENs: the probe's
  /// signal arrives while the removals of that same pass are still ahead of it. Two probes in
  /// sequence settle it. The second is subscribed only after the first has been listened, so its
  /// own LISTEN cannot belong to that pass — the pass had already computed its channel set — and
  /// for a later pass to reach its add phase, the earlier one must have run to the end, removals
  /// included.
  /// </remarks>
  private static async Task _awaitRemovalPassAsync(
      PgSharedNotifyConnection shared, CancellationToken cancellationToken) {
    for (var probeIndex = 0; probeIndex < 2; probeIndex++) {
      var probe = $"wh_test_resync_probe_{Guid.CreateVersion7():N}";
      using var handle = shared.Subscribe(new NoopSubscription(probe));
      await shared.WaitForChannelListenedAsync(probe, cancellationToken);
    }
  }

  private sealed class NoopSubscription(string channel) : INotifySubscription {
    public string ChannelName => channel;
    public void OnNotification(string payload) { }
  }
}
