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
/// Slice 33.3 — exercises the dispatch handler's per-channel routing + error isolation
/// WITHOUT a real Postgres. The handler is a pure function over the subscription registry
/// (look up subscribers by channel, invoke OnNotification, catch exceptions). Real
/// notification round-trip → handler firing on a live conn is covered by integration
/// tests in EFCore.Postgres.Tests.
/// </summary>
/// <docs>fundamentals/work-coordinator/notifications-and-pgbouncer</docs>
public class PgSharedNotifyConnectionDispatchTests {

  private sealed class RecordingSubscription(string channel) : INotifySubscription {
    public string ChannelName { get; } = channel;
    public List<string> Received { get; } = [];
    public void OnNotification(string payload) => Received.Add(payload);
  }

  private sealed class ThrowingSubscription(string channel) : INotifySubscription {
    public string ChannelName { get; } = channel;
    public int CallCount;
    public void OnNotification(string payload) {
      Interlocked.Increment(ref CallCount);
      throw new InvalidOperationException("intentional test failure");
    }
  }

  private static PgSharedNotifyConnection _build() {
    var cfg = new ConfigurationBuilder().AddInMemoryCollection([]).Build();
    return new PgSharedNotifyConnection(
      Options.Create(new WhizbangNotificationOptions { SignalingMode = WorkSignalingMode.Polling }),
      cfg,
      new ServiceInstanceProvider(cfg),
      NullLogger<PgSharedNotifyConnection>.Instance,
      connectionStringFallback: null,
      timeProvider: null);
  }

  private static void _invokeDispatch(PgSharedNotifyConnection conn, string channel, string payload) {
    // Reach into the private dispatch handler via reflection. The handler IS the unit
    // under test for slice 33.3 — testing it through Npgsql's Notification event would
    // require a real conn. The behavior under test (route by channel, swallow throws,
    // continue dispatch) is pure registry/handler logic.
    //
    // NpgsqlNotificationEventArgs (Npgsql 10) takes an internal NpgsqlReadBuffer — we
    // skip the constructor with GetUninitializedObject + set the backing fields directly.
    // Brittle if Npgsql renames its backing fields, but that's the tradeoff for unit-
    // testing the dispatch handler without a real conn.
    var argsType = typeof(global::Npgsql.NpgsqlNotificationEventArgs);
    var evArgs = (global::Npgsql.NpgsqlNotificationEventArgs)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(argsType);
    var channelField = argsType.GetField("<Channel>k__BackingField",
      System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
    var payloadField = argsType.GetField("<Payload>k__BackingField",
      System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
    channelField.SetValue(evArgs, channel);
    payloadField.SetValue(evArgs, payload);

    var method = typeof(PgSharedNotifyConnection).GetMethod(
      "_dispatchNotification",
      System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
    method.Invoke(conn, [null, evArgs]);
  }

  [Test]
  public async Task Dispatch_NotificationOnUnregisteredChannel_IgnoredAsync() {
    var conn = _build();
    var sub = new RecordingSubscription("channel_a");
    using var handle = conn.Subscribe(sub);

    _invokeDispatch(conn, "channel_b", "should-not-be-delivered");

    await Assert.That(sub.Received).IsEmpty();
  }

  [Test]
  public async Task Dispatch_NotificationOnRegisteredChannel_DeliversPayloadAsync() {
    var conn = _build();
    var sub = new RecordingSubscription("channel_a");
    using var handle = conn.Subscribe(sub);

    _invokeDispatch(conn, "channel_a", "hello");

    await Assert.That(sub.Received).IsEquivalentTo(["hello"]);
  }

  [Test]
  public async Task Dispatch_PayloadDeliveredVerbatim_IncludingSpecialCharsAsync() {
    var conn = _build();
    var sub = new RecordingSubscription("channel_x");
    using var handle = conn.Subscribe(sub);

    var payload = """{"event":"x","stream":"019e5..."}""";
    _invokeDispatch(conn, "channel_x", payload);

    await Assert.That(sub.Received).IsEquivalentTo([payload]);
  }

  [Test]
  public async Task Dispatch_MultipleSubscribersForSameChannel_AllInvokedAsync() {
    var conn = _build();
    var sub1 = new RecordingSubscription("shared");
    var sub2 = new RecordingSubscription("shared");
    var sub3 = new RecordingSubscription("shared");
    using var h1 = conn.Subscribe(sub1);
    using var h2 = conn.Subscribe(sub2);
    using var h3 = conn.Subscribe(sub3);

    _invokeDispatch(conn, "shared", "fanout");

    await Assert.That(sub1.Received).IsEquivalentTo(["fanout"]);
    await Assert.That(sub2.Received).IsEquivalentTo(["fanout"]);
    await Assert.That(sub3.Received).IsEquivalentTo(["fanout"]);
  }

  [Test]
  public async Task Dispatch_SubscriberThrows_OtherSubscribersStillInvokedAsync() {
    var conn = _build();
    var throwing = new ThrowingSubscription("contended");
    var sane = new RecordingSubscription("contended");
    using var h1 = conn.Subscribe(throwing);
    using var h2 = conn.Subscribe(sane);

    _invokeDispatch(conn, "contended", "x");

    await Assert.That(throwing.CallCount).IsEqualTo(1);
    await Assert.That(sane.Received).IsEquivalentTo(["x"]);
  }

  [Test]
  public async Task Dispatch_SubscriberDisposed_NoLongerReceivesNotificationsAsync() {
    var conn = _build();
    var sub = new RecordingSubscription("channel_d");
    var handle = conn.Subscribe(sub);

    _invokeDispatch(conn, "channel_d", "first");
    handle.Dispose();
    _invokeDispatch(conn, "channel_d", "second-after-dispose");

    await Assert.That(sub.Received).IsEquivalentTo(["first"]);
  }

  [Test]
  public async Task Dispatch_NoSubscribers_NoOpAsync() {
    var conn = _build();

    // No subscriptions registered. Dispatch should be a silent no-op (no exception).
    // Lock the silence by also confirming the registry stays empty.
    _invokeDispatch(conn, "unknown_channel", "payload");

    await Assert.That(conn.RegistryForTesting.TotalSubscriberCount()).IsEqualTo(0);
  }
}
