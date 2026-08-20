using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using RabbitMQ.Client;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Routing;

namespace Whizbang.Transports.RabbitMQ.Tests;

/// <summary>
/// Topology arc phase 8.5 — RabbitMQ executes the Core poison verdict natively.
/// <para>
/// The decision is the same one Azure Service Bus makes (one policy in Core, no drift); only the
/// mechanism differs: RabbitMQ reads the publisher-set message timestamp plus <c>redelivered</c>
/// and quarantines with <c>BasicNackAsync(requeue: false)</c>, which routes to the queue's
/// dead-letter exchange. Because the timestamp is PUBLISHER-set, not broker-set, the transport
/// must declare whether it can supply a trustworthy age — and when it cannot, degrade to layer 2
/// out loud instead of going quietly inert.
/// </para>
/// </summary>
public class RabbitMQPoisonQuarantineTests {

  private static readonly DateTimeOffset _now = new(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);

  #region Layer 1 — age quarantine to the dead-letter exchange

  [Test]
  public async Task Deliver_AgedMessage_NacksWithoutRequeueToTheDeadLetterExchangeAsync() {
    // The lock: an aged message is quarantined with requeue:false, which is RabbitMQ's route to
    // the per-namespace DLQ the existing dead-letter drainer replays. Requeueing it would put the
    // hostage message straight back in the loop this phase exists to break.
    var (channel, handled) = await _subscribeAsync(_detector(ageThreshold: TimeSpan.FromMinutes(30)));
    var (properties, body) = _wireMessage(publishedAt: _now - TimeSpan.FromHours(4));

    await RabbitTestWire.DeliverAsync(channel, properties, body, deliveryTag: 1, redelivered: true);

    await Assert.That(channel.NackAttempts).Count().IsEqualTo(1);
    await Assert.That(channel.NackAttempts[0].Requeue).IsFalse()
      .Because("requeue:false is the route to the DLX; requeue:true re-arms the storm");
    await Assert.That(channel.AckAttempts).IsEmpty();
    await Assert.That(handled).IsEmpty();
  }

  [Test]
  public async Task Deliver_FreshMessage_ProcessesNormallyAsync() {
    var (channel, handled) = await _subscribeAsync(_detector(ageThreshold: TimeSpan.FromMinutes(30)));
    var (properties, body) = _wireMessage(publishedAt: _now - TimeSpan.FromSeconds(3));

    await RabbitTestWire.DeliverAsync(channel, properties, body, deliveryTag: 1);

    await Assert.That(channel.NackAttempts).IsEmpty();
    await Assert.That(handled).Count().IsEqualTo(1);
  }

  [Test]
  public async Task Deliver_SlowButProgressingMessage_ProcessesNormallyAsync() {
    var (channel, handled) = await _subscribeAsync(
      _detector(lockRenewalDuration: TimeSpan.FromMinutes(5), maxDeliveryAttempts: 10));
    var (properties, body) = _wireMessage(publishedAt: _now - TimeSpan.FromMinutes(45));

    await RabbitTestWire.DeliverAsync(channel, properties, body, deliveryTag: 1, redelivered: true);

    await Assert.That(channel.NackAttempts).IsEmpty();
    await Assert.That(handled).Count().IsEqualTo(1);
  }

  [Test]
  public async Task Deliver_DetectorDisabled_ProcessesTheAgedMessageAsync() {
    var (channel, handled) = await _subscribeAsync(
      _detector(ageThreshold: TimeSpan.FromMinutes(30), enabled: false));
    var (properties, body) = _wireMessage(publishedAt: _now - TimeSpan.FromDays(30));

    await RabbitTestWire.DeliverAsync(channel, properties, body, deliveryTag: 1, redelivered: true);

    await Assert.That(channel.NackAttempts).IsEmpty();
    await Assert.That(handled).Count().IsEqualTo(1);
  }

  [Test]
  public async Task Deliver_NoDetectorWired_ProcessesTheAgedMessageAsync() {
    var (channel, handled) = await _subscribeAsync(poisonDetector: null);
    var (properties, body) = _wireMessage(publishedAt: _now - TimeSpan.FromDays(30));

    await RabbitTestWire.DeliverAsync(channel, properties, body, deliveryTag: 1, redelivered: true);

    await Assert.That(channel.NackAttempts).IsEmpty();
    await Assert.That(handled).Count().IsEqualTo(1);
  }

  #endregion

  #region Capability honesty

  [Test]
  public async Task Deliver_PublisherStampedTimestamp_ReportsTheSurfaceCapableAsync() {
    var capability = new PoisonDetectionCapabilityState();
    var (channel, _) = await _subscribeAsync(
      _detector(ageThreshold: TimeSpan.FromMinutes(30), capabilityState: capability));
    var (properties, body) = _wireMessage(publishedAt: _now - TimeSpan.FromSeconds(3));

    await RabbitTestWire.DeliverAsync(channel, properties, body, deliveryTag: 1);

    await Assert.That(capability.HasDegradedSurface).IsFalse();
  }

  [Test]
  public async Task Deliver_NoPublisherTimestamp_DegradesLoudlyInsteadOfGoingInertAsync() {
    // RabbitMQ's timestamp is publisher-set and optional: a message from a foreign publisher (or
    // an older build) has none. Layer 1 cannot work on that surface — and it must SAY so, on the
    // health surface, rather than quietly enforcing nothing. Silence is how the delivery-count
    // valve went unnoticed for its entire life.
    var capability = new PoisonDetectionCapabilityState();
    var (channel, handled) = await _subscribeAsync(
      _detector(ageThreshold: TimeSpan.FromMinutes(30), capabilityState: capability));
    var (properties, body) = _wireMessage(publishedAt: null);

    await RabbitTestWire.DeliverAsync(channel, properties, body, deliveryTag: 1, redelivered: true);

    await Assert.That(channel.NackAttempts).IsEmpty()
      .Because("an absent publisher timestamp must never be read as an infinitely old message");
    await Assert.That(handled).Count().IsEqualTo(1);
    await Assert.That(capability.HasDegradedSurface).IsTrue();
    await Assert.That(capability.DegradedSurfaces[0].Transport).IsEqualTo("rabbitmq");
  }

  #endregion

  #region Publish-side timestamp

  [Test]
  public async Task PublishAsync_StampsTheMessageTimestampAsync() {
    // Whizbang stamps its own publishes so the age signal EXISTS on this transport at all. The
    // stamp survives requeue and the dead-letter exchange, which is what makes it a first-enqueue
    // time rather than a delivery time.
    var channel = new RecordingChannel();
    var connection = new FakeConnection(() => Task.FromResult<IChannel>(channel));
    var transport = await RabbitTestWire.NewInitializedTransportAsync(
      connection, timeProvider: new FixedTimeProvider(_now));

    await transport.PublishAsync(RabbitTestWire.NewEnvelope(), RabbitTestWire.Destination());

    await Assert.That(channel.Published[0].Properties.Timestamp.UnixTime)
      .IsEqualTo(_now.ToUnixTimeSeconds());
  }

  #endregion

  #region Threshold derivation from the transport's own options

  [Test]
  public async Task PostConfigure_FillsTheDeliveryCapFromTheTransportOptionsAsync() {
    // RabbitMQ has no lock-renewal analogue (no per-delivery lock to renew), so it supplies only
    // the delivery cap and the framework default carries the renewal term. Stating that here
    // keeps the derivation honest rather than silently inheriting an Azure-shaped number.
    var poisonOptions = new PoisonMessageOptions();
    var transportOptions = new RabbitMQOptions { MaxDeliveryAttempts = 4 };

    new RabbitMQPoisonOptionsPostConfigure(Microsoft.Extensions.Options.Options.Create(transportOptions))
      .PostConfigure(null, poisonOptions);

    await Assert.That(poisonOptions.MaxDeliveryAttempts).IsEqualTo(4);
    await Assert.That(poisonOptions.LockRenewalDuration).IsNull();
    await Assert.That(poisonOptions.EffectiveAgeThreshold).IsEqualTo(TimeSpan.FromMinutes(30))
      .Because("5-minute default renewal x 4 attempts is below the documented 30-minute floor");
  }

  [Test]
  public async Task PostConfigure_DoesNotOverrideAnExplicitOperatorValueAsync() {
    var poisonOptions = new PoisonMessageOptions { MaxDeliveryAttempts = 99 };
    var transportOptions = new RabbitMQOptions { MaxDeliveryAttempts = 4 };

    new RabbitMQPoisonOptionsPostConfigure(Microsoft.Extensions.Options.Options.Create(transportOptions))
      .PostConfigure(null, poisonOptions);

    await Assert.That(poisonOptions.MaxDeliveryAttempts).IsEqualTo(99);
  }

  #endregion

  #region Helpers

  private static (BasicProperties Properties, byte[] Body) _wireMessage(DateTimeOffset? publishedAt) {
    var (properties, body) = RabbitTestWire.ValidWireMessage();
    if (publishedAt is { } stamped) {
      properties.Timestamp = new AmqpTimestamp(stamped.ToUnixTimeSeconds());
    }
    return (properties, body);
  }

  private static PoisonMessageDetector _detector(
      TimeSpan? ageThreshold = null,
      TimeSpan? lockRenewalDuration = null,
      int? maxDeliveryAttempts = null,
      bool enabled = true,
      PoisonDetectionCapabilityState? capabilityState = null) =>
    new(
      Microsoft.Extensions.Options.Options.Create(new PoisonMessageOptions {
        Enabled = enabled,
        AgeThreshold = ageThreshold,
        LockRenewalDuration = lockRenewalDuration,
        MaxDeliveryAttempts = maxDeliveryAttempts,
      }),
      NullLogger<PoisonMessageDetector>.Instance,
      new System.Diagnostics.Metrics.Meter("Whizbang.Transports.RabbitMQ.Tests.Poison"),
      capabilityState);

  private static async Task<(RecordingChannel Channel, List<IMessageEnvelope> Handled)> _subscribeAsync(
      IPoisonMessageDetector? poisonDetector) {
    var channel = new RecordingChannel();
    var connection = new FakeConnection(() => Task.FromResult<IChannel>(channel));
    var transport = await RabbitTestWire.NewInitializedTransportAsync(
      connection, poisonDetector: poisonDetector, timeProvider: new FixedTimeProvider(_now));

    var handled = new List<IMessageEnvelope>();
    await transport.SubscribeAsync(
      (envelope, _, _) => { handled.Add(envelope); return Task.CompletedTask; },
      RabbitTestWire.Destination());
    return (channel, handled);
  }

  #endregion
}

/// <summary>TimeProvider pinned to a fixed instant so age assertions are deterministic.</summary>
internal sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider {
  public override DateTimeOffset GetUtcNow() => now;
}
