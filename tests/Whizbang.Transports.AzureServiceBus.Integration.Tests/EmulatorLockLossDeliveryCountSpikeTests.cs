using Azure.Messaging.ServiceBus;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Transports.AzureServiceBus.Integration.Tests.Containers;

#pragma warning disable CA1707 // Identifiers should not contain underscores (test method names use underscores by convention)

namespace Whizbang.Transports.AzureServiceBus.Integration.Tests;

/// <summary>
/// SPIKE ARTIFACT (topology arc, before the phase-6 DLQ locks) — the traffic-classes
/// proposal's open question: does CONNECTION-DEATH lock loss increment DeliveryCount the way
/// an explicit abandon does? If it does NOT, the per-subscription MaxDeliveryCount DLQ safety
/// valve never fires under exactly the storm conditions that need it (mass lock loss from
/// dying/overloaded consumers), and DLQ posture must not assume storm-driven dead-lettering.
/// </summary>
/// <remarks>
/// <para>These tests RECORD REALITY on the Service Bus EMULATOR — their assertions document
/// what the emulator does, so the phase-6 DLQ locks can be shaped against observed behavior
/// rather than assumptions. Each doc comment states the production implication either way.
/// The emulator's settlement fidelity is not guaranteed to match the Azure service; where
/// behavior diverges from the documented service behavior the comment says so, and the
/// production DLQ posture must be validated against a real namespace before relying on it.</para>
/// <para><b>RECORDED FINDINGS (2026-08, servicebus-emulator:latest):</b></para>
/// <list type="bullet">
///   <item>SESSION lock loss via connection death: DeliveryCount does NOT increment (stays
///   1 on redelivery) — the open question CONFIRMED for session entities; the DLQ safety
///   valve cannot fire from lock-loss storms on session-enabled command inboxes.</item>
///   <item>Explicit AbandonAsync (session): DeliveryCount increments (2) — the valve works
///   for handler-failure paths.</item>
///   <item>NON-session message-lock loss via connection death: DeliveryCount increments
///   (2) — the valve works for plain subscriptions even under connection death.</item>
///   <item>Repeated non-session lock loss: the message DOES dead-letter at
///   MaxDeliveryCount — the valve end-to-end on plain entities.</item>
/// </list>
/// <para>Entities (Config.json): <c>topic-spike-session/sub-spike</c> (sessions, LockDuration
/// PT5S, MaxDeliveryCount 3) and <c>topic-spike-plain/sub-spike</c> (non-session, same
/// lock/delivery settings). The short lock duration bounds the lock-loss wait.</para>
/// </remarks>
[Category("Integration")]
[NotInParallel("ServiceBus")]
[Timeout(240_000)]
[ClassDataSource<ServiceBusEmulatorFixtureSource>(Shared = SharedType.PerAssembly)]
public sealed class EmulatorLockLossDeliveryCountSpikeTests(ServiceBusEmulatorFixtureSource fixtureSource) {
  private readonly ServiceBusEmulatorFixture _fixture = fixtureSource.Fixture;

  private const string SESSION_TOPIC = "topic-spike-session";
  private const string PLAIN_TOPIC = "topic-spike-plain";
  private const string SUBSCRIPTION = "sub-spike";

  /// <summary>Lock duration configured on both spike subscriptions (Config.json).</summary>
  private static readonly TimeSpan _lockDuration = TimeSpan.FromSeconds(5);

  private async Task _sendAsync(string topic, string marker, string? sessionId = null) {
    await using var sender = _fixture.Client.CreateSender(topic);
    var message = new ServiceBusMessage($"{{\"spike\":\"{marker}\"}}") {
      MessageId = marker,
      ContentType = "application/json"
    };
    if (sessionId is not null) {
      message.SessionId = sessionId;
    }
    await sender.SendMessageAsync(message);
  }

  /// <summary>Accepts the spike session, retrying until the previous holder's lock is
  /// released (bounded; accept-failure is the signal the lock is still held).</summary>
  private async Task<ServiceBusSessionReceiver> _acceptSessionWhenReleasedAsync(
      ServiceBusClient client, string sessionId, CancellationToken ct) {
    var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(60);
    while (true) {
      try {
        return await client.AcceptSessionAsync(SESSION_TOPIC, SUBSCRIPTION, sessionId, cancellationToken: ct);
      } catch (ServiceBusException ex) when (
          ex.Reason is ServiceBusFailureReason.SessionCannotBeLocked or ServiceBusFailureReason.ServiceTimeout
          && DateTimeOffset.UtcNow < deadline) {
        // Session lock still held by the dead client — the broker releases it at lock
        // expiry; keep asking (the accept succeeding IS the release signal).
      }
    }
  }

  [Test]
  public async Task SessionLockLoss_ClientDeathWithoutSettling_DeliveryCountOnRedeliveryAsync(CancellationToken ct) {
    var sessionId = $"spike-{Guid.NewGuid():N}";
    var marker = $"lockloss-{Guid.NewGuid():N}";
    await _sendAsync(SESSION_TOPIC, marker, sessionId);

    // Receive under a DOOMED client and kill it WITHOUT settling — the ungraceful
    // connection death a crashing/overloaded consumer produces.
    int firstDeliveryCount;
    var doomedClient = new ServiceBusClient(_fixture.ConnectionString);
    try {
      var doomedReceiver = await doomedClient.AcceptSessionAsync(SESSION_TOPIC, SUBSCRIPTION, sessionId, cancellationToken: ct);
      var first = await doomedReceiver.ReceiveMessageAsync(TimeSpan.FromSeconds(30), ct)
        ?? throw new InvalidOperationException("Spike message was not delivered on first receive.");
      firstDeliveryCount = first.DeliveryCount;
    } finally {
      await doomedClient.DisposeAsync(); // ungraceful: no settle, no session close semantics
    }
    await Assert.That(firstDeliveryCount).IsEqualTo(1);

    // Re-accept once the broker releases the session lock, and observe DeliveryCount.
    var receiver = await _acceptSessionWhenReleasedAsync(_fixture.Client, sessionId, ct);
    await using (receiver) {
      var redelivered = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(30), ct)
        ?? throw new InvalidOperationException("Spike message was not redelivered after session lock loss.");

      // RECORDED REALITY (2026-08, servicebus-emulator:latest): connection-death SESSION
      // lock loss does NOT increment DeliveryCount — the redelivery still reports 1, unlike
      // the explicit-abandon contrast (2) and unlike NON-session message-lock loss (2).
      // THE PROPOSAL'S OPEN QUESTION IS CONFIRMED for session entities: under session-lock-
      // loss storms (dying/overloaded consumers — exactly the storm conditions that need a
      // safety valve) the per-subscription MaxDeliveryCount DLQ valve NEVER fires, because
      // neither the broker's count-based dead-lettering nor the transport's own
      // MaxDeliveryAttempts branch (which reads the same DeliveryCount) ever sees the count
      // rise. Messages are hostage, not poison. PRODUCTION IMPLICATION: per-namespace DLQ
      // locks must rely on EXPLICIT dead-letter paths only (handler failure → abandon →
      // count rises → dead-letter), never on storm-driven count exhaustion, for
      // session-enabled command inboxes. EMULATOR FIDELITY CAVEAT: this is the emulator's
      // settlement engine; validate against a real namespace before relying on the inverse.
      await Assert.That(redelivered.DeliveryCount).IsEqualTo(1)
        .Because("session lock loss is delivery-count-blind on the emulator — the DLQ safety valve cannot fire from lock-loss storms on session entities");
      await receiver.CompleteMessageAsync(redelivered, ct);
    }
  }

  [Test]
  public async Task SessionAbandon_Explicit_DeliveryCountIncrementsAsync(CancellationToken ct) {
    // CONTRAST CASE: the documented, unambiguous path — AbandonAsync increments
    // DeliveryCount immediately and releases the message.
    var sessionId = $"spike-{Guid.NewGuid():N}";
    var marker = $"abandon-{Guid.NewGuid():N}";
    await _sendAsync(SESSION_TOPIC, marker, sessionId);

    var receiver = await _fixture.Client.AcceptSessionAsync(SESSION_TOPIC, SUBSCRIPTION, sessionId, cancellationToken: ct);
    await using (receiver) {
      var first = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(30), ct)
        ?? throw new InvalidOperationException("Spike message was not delivered on first receive.");
      await Assert.That(first.DeliveryCount).IsEqualTo(1);
      await receiver.AbandonMessageAsync(first, cancellationToken: ct);

      var redelivered = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(30), ct)
        ?? throw new InvalidOperationException("Spike message was not redelivered after abandon.");
      await Assert.That(redelivered.DeliveryCount).IsEqualTo(2);
      await receiver.CompleteMessageAsync(redelivered, ct);
    }
  }

  [Test]
  public async Task PlainLockLoss_ClientDeathWithoutSettling_DeliveryCountOnRedeliveryAsync(CancellationToken ct) {
    // Non-session variant: the message lock (not a session lock) is what dies with the
    // client; redelivery happens at lock expiry.
    var marker = $"plainloss-{Guid.NewGuid():N}";
    await _sendAsync(PLAIN_TOPIC, marker);

    int firstDeliveryCount;
    var doomedClient = new ServiceBusClient(_fixture.ConnectionString);
    try {
      var doomedReceiver = doomedClient.CreateReceiver(PLAIN_TOPIC, SUBSCRIPTION);
      var first = await doomedReceiver.ReceiveMessageAsync(TimeSpan.FromSeconds(30), ct)
        ?? throw new InvalidOperationException("Spike message was not delivered on first receive.");
      firstDeliveryCount = first.DeliveryCount;
    } finally {
      await doomedClient.DisposeAsync(); // ungraceful: lock dies with the connection
    }
    await Assert.That(firstDeliveryCount).IsEqualTo(1);

    await using var receiver = _fixture.Client.CreateReceiver(PLAIN_TOPIC, SUBSCRIPTION);
    // The broker redelivers after lock expiry (PT5S); the long max-wait absorbs it — the
    // receive returning IS the signal, no fixed sleep.
    var redelivered = await receiver.ReceiveMessageAsync(_lockDuration + TimeSpan.FromSeconds(55), ct)
      ?? throw new InvalidOperationException("Spike message was not redelivered after message lock loss.");

    // RECORDED REALITY: the emulator increments DeliveryCount on message-lock loss too.
    await Assert.That(redelivered.DeliveryCount).IsEqualTo(2);
    await receiver.CompleteMessageAsync(redelivered, ct);
  }

  [Test]
  public async Task RepeatedLockLoss_DlqSafetyValve_RecordedRealityAsync(CancellationToken ct) {
    // THE DECIDING SPIKE for phase-6 DLQ posture: kill the receiver (connection death, no
    // settle) MaxDeliveryCount times — does the message land in the DLQ? MaxDeliveryCount
    // is 3 on sub-spike (Config.json).
    var marker = $"dlqvalve-{Guid.NewGuid():N}";
    await _sendAsync(PLAIN_TOPIC, marker);

    const int maxDeliveryCount = 3;
    for (var kill = 0; kill < maxDeliveryCount; kill++) {
      var doomedClient = new ServiceBusClient(_fixture.ConnectionString);
      try {
        var doomedReceiver = doomedClient.CreateReceiver(PLAIN_TOPIC, SUBSCRIPTION);
        // Redelivery after the previous kill's lock expiry — the receive IS the wait.
        var delivery = await doomedReceiver.ReceiveMessageAsync(_lockDuration + TimeSpan.FromSeconds(55), ct);
        if (delivery is null) {
          break; // no further delivery — reality recorded below via the DLQ probe
        }
      } finally {
        await doomedClient.DisposeAsync();
      }
    }

    // Probe the DLQ (bounded receive; null = valve did not fire).
    await using var dlqReceiver = _fixture.Client.CreateReceiver(PLAIN_TOPIC, SUBSCRIPTION, new ServiceBusReceiverOptions {
      SubQueue = SubQueue.DeadLetter
    });
    var deadLettered = await dlqReceiver.ReceiveMessageAsync(_lockDuration + TimeSpan.FromSeconds(55), ct);

    // RECORDED REALITY: after MaxDeliveryCount connection-death lock losses the message IS
    // dead-lettered — the DLQ safety valve DOES fire under storm conditions on the emulator.
    // Production implication: per-namespace DLQ locks may assert dead-lettering for both
    // handler-failure AND lock-loss paths. If this assertion ever fails (message never
    // dead-letters), the valve is delivery-count-blind under lock loss and phase-6 DLQ
    // locks must only rely on explicit dead-letter paths (handler failure / rejection).
    await Assert.That(deadLettered).IsNotNull();
    await Assert.That(deadLettered!.MessageId).IsEqualTo(marker);
    await dlqReceiver.CompleteMessageAsync(deadLettered, ct);
  }
}
