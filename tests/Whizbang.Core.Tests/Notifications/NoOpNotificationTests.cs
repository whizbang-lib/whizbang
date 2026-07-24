using System;
using System.Threading;
using System.Threading.Tasks;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Notifications;
using Whizbang.Core.Notifications.AppSignals;

namespace Whizbang.Core.Tests.Notifications;

#pragma warning disable CA1707
#pragma warning disable IDE1006

/// <summary>
/// Covers the no-op fallback implementations used when no direct postgres
/// connection is configured (<see cref="NoOpAppSignalChannel"/>,
/// <see cref="NoOpWorkNotificationListener"/>) plus the topic validation
/// rules in <see cref="AppSignalTopicValidator"/>. These types are small
/// but were untested — the system silently falls back to them when
/// notifications can't be wired, and operators have observed that
/// "no notifications" mode left subtle bugs (e.g., publish-with-invalid-topic
/// silently succeeded). Locks the validation surface and the no-op contracts.
/// </summary>
/// <docs>fundamentals/work-coordinator/notifications-and-pgbouncer</docs>
public class NoOpNotificationTests {

  // ===================== AppSignalTopicValidator =====================

  [Test]
  public async Task Validate_AcceptsLowercaseAlnumUnderscoreAsync() {
    await Assert.That(AppSignalTopicValidator.Validate("orders")).IsEqualTo("orders");
    await Assert.That(AppSignalTopicValidator.Validate("order_events")).IsEqualTo("order_events");
    await Assert.That(AppSignalTopicValidator.Validate("topic_v2_2026")).IsEqualTo("topic_v2_2026");
  }

  [Test]
  public async Task Validate_RejectsEmptyOrWhitespaceAsync() {
    await Assert.That(() => AppSignalTopicValidator.Validate("")).Throws<ArgumentException>();
    await Assert.That(() => AppSignalTopicValidator.Validate("   ")).Throws<ArgumentException>();
    await Assert.That(() => AppSignalTopicValidator.Validate(null!)).Throws<ArgumentException>();
  }

  [Test]
  public async Task Validate_RejectsReservedWhPrefixAsync() {
    // The internal wh_work channel is off-limits to app code.
    await Assert.That(() => AppSignalTopicValidator.Validate("wh_anything")).Throws<ArgumentException>();
    await Assert.That(() => AppSignalTopicValidator.Validate("wh_")).Throws<ArgumentException>();
  }

  [Test]
  public async Task Validate_RejectsUppercaseAndPunctuationAsync() {
    await Assert.That(() => AppSignalTopicValidator.Validate("Orders")).Throws<ArgumentException>();
    await Assert.That(() => AppSignalTopicValidator.Validate("order-events")).Throws<ArgumentException>();
    await Assert.That(() => AppSignalTopicValidator.Validate("orders!")).Throws<ArgumentException>();
    await Assert.That(() => AppSignalTopicValidator.Validate("123orders")).Throws<ArgumentException>();
  }

  [Test]
  public async Task Validate_EnforcesMaxLength63Async() {
    // ^[a-z][a-z0-9_]{0,62}$ → 63 total chars OK, 64 rejected.
    var ok = "a" + new string('b', 62);  // 63 chars
    var tooLong = "a" + new string('b', 63);  // 64 chars
    await Assert.That(AppSignalTopicValidator.Validate(ok)).IsEqualTo(ok);
    await Assert.That(() => AppSignalTopicValidator.Validate(tooLong)).Throws<ArgumentException>();
  }

  [Test]
  public async Task ToChannelName_PrefixesValidTopicAsync() {
    await Assert.That(AppSignalTopicValidator.ToChannelName("orders")).IsEqualTo("wh_app_orders");
  }

  [Test]
  public async Task ToChannelName_PropagatesValidationErrorAsync() {
    await Assert.That(() => AppSignalTopicValidator.ToChannelName("wh_internal")).Throws<ArgumentException>();
  }

  // ===================== NoOpAppSignalChannel =====================

  [Test]
  public async Task NoOpAppSignalChannel_PublishValidTopic_CompletesSilentlyAsync() {
    var channel = new NoOpAppSignalChannel();
    await channel.PublishAsync("ok_topic", "payload");
    // No assertion needed — successful completion is the contract.
    // surviving the call without throwing IS the contract — no assertion needed
  }

  [Test]
  public async Task NoOpAppSignalChannel_PublishInvalidTopic_ThrowsAsync() {
    var channel = new NoOpAppSignalChannel();
    await Assert.That(async () => await channel.PublishAsync("wh_reserved", "payload"))
      .Throws<ArgumentException>();
  }

  [Test]
  public async Task NoOpAppSignalChannel_SubscribeValidTopic_ReturnsDisposableAsync() {
    var channel = new NoOpAppSignalChannel();
    using var sub = channel.Subscribe("topic", static (_, _) => Task.CompletedTask);
    await Assert.That(sub).IsNotNull();
    // Idempotent disposal:
    sub.Dispose();
    sub.Dispose();
  }

  [Test]
  public async Task NoOpAppSignalChannel_SubscribeInvalidTopic_ThrowsAsync() {
    var channel = new NoOpAppSignalChannel();
    await Assert.That(() =>
        channel.Subscribe("WH_BadCase", static (_, _) => Task.CompletedTask))
      .Throws<ArgumentException>();
  }

  // ===================== NoOpWorkNotificationListener =====================

  [Test]
  public async Task NoOpWorkNotificationListener_ReportsUnhealthyAndNullSignalAsync() {
    var listener = new NoOpWorkNotificationListener();
    await Assert.That(listener.IsHealthy).IsFalse();
    await Assert.That(listener.LastSignalAt).IsNull();
  }

  [Test]
  public async Task NoOpWorkNotificationListener_EventsAreInertAsync() {
    var listener = new NoOpWorkNotificationListener();
    Action<WorkSignalCategory> handler = _ => { };
    Action<bool> healthHandler = _ => { };

    // Add + remove the handlers — the no-op accessors should be inert.
    listener.OnSignal += handler;
    listener.OnSignal -= handler;
    listener.OnHealthChanged += healthHandler;
    listener.OnHealthChanged -= healthHandler;

    // surviving the call without throwing IS the contract — no assertion needed  // surviving the calls is the contract
  }
}
