// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Whizbang.Core.Transports;
using Whizbang.Testing.Tests.TestSupport;
using Whizbang.Testing.Transport;

namespace Whizbang.Testing.Tests.Transport;

/// <summary>
/// Tests for <see cref="SubscriptionWarmup"/> and <see cref="SignalAwaiter"/>.
/// Warmup runs use signal-driven fake transports; no wall-clock waits.
/// </summary>
public class SubscriptionWarmupTests {
  private static readonly TransportDestination _destination = new("warmup-topic");
  private static readonly TimeSpan _longTimeout = TimeSpan.FromSeconds(30);

  [Test]
  public async Task GenerateWarmupId_HasPrefixAndIsUniqueAsync() {
    var first = SubscriptionWarmup.GenerateWarmupId();
    var second = SubscriptionWarmup.GenerateWarmupId();

    await Assert.That(first.StartsWith("warmup-", StringComparison.Ordinal)).IsTrue();
    await Assert.That(first).IsNotEqualTo(second);
  }

  [Test]
  public async Task IsWarmupMessage_DetectsPrefixAsync() {
    await Assert.That(SubscriptionWarmup.IsWarmupMessage(SubscriptionWarmup.GenerateWarmupId())).IsTrue();
    await Assert.That(SubscriptionWarmup.IsWarmupMessage("regular-content")).IsFalse();
    await Assert.That(SubscriptionWarmup.IsWarmupMessage(null)).IsFalse();
  }

  [Test]
  public async Task CreateDiscriminatingAwaiters_WarmupMessage_SignalsWarmupOnlyAsync() {
    var warmupId = SubscriptionWarmup.GenerateWarmupId();
    var (warmupAwaiter, testAwaiter, handler) =
      SubscriptionWarmup.CreateDiscriminatingAwaiters<TestPayload>(warmupId, p => p.Content);

    await handler(EnvelopeFactory.Create(warmupId), null, CancellationToken.None);

    await Assert.That(warmupAwaiter.IsSignaled).IsTrue();
    await Assert.That(testAwaiter.IsCompleted).IsFalse();
  }

  [Test]
  public async Task CreateDiscriminatingAwaiters_TestMessage_ResolvesTestAwaiterOnlyAsync() {
    var warmupId = SubscriptionWarmup.GenerateWarmupId();
    var (warmupAwaiter, testAwaiter, handler) =
      SubscriptionWarmup.CreateDiscriminatingAwaiters<TestPayload>(warmupId, p => p.Content);
    var envelope = EnvelopeFactory.Create("real-message");

    await handler(envelope, null, CancellationToken.None);

    await Assert.That(warmupAwaiter.IsSignaled).IsFalse();
    await Assert.That(testAwaiter.IsCompleted).IsTrue();
    var received = await testAwaiter.WaitAsync(_longTimeout);
    await Assert.That(ReferenceEquals(received, envelope)).IsTrue();
  }

  [Test]
  public async Task CreateDiscriminatingAwaiters_WrongPayloadType_IsIgnoredAsync() {
    var warmupId = SubscriptionWarmup.GenerateWarmupId();
    var (warmupAwaiter, testAwaiter, handler) =
      SubscriptionWarmup.CreateDiscriminatingAwaiters<TestPayload>(warmupId, p => p.Content);
    var otherEnvelope = EnvelopeFactory.CreateFor(new OtherPayload { Content = "other" });

    await handler(otherEnvelope, null, CancellationToken.None);

    await Assert.That(warmupAwaiter.IsSignaled).IsFalse();
    await Assert.That(testAwaiter.IsCompleted).IsFalse();
  }

  [Test]
  public async Task SignalAwaiter_Signal_CompletesWaitAsync() {
    var awaiter = new SignalAwaiter();
    await Assert.That(awaiter.IsSignaled).IsFalse();

    awaiter.Signal();
    awaiter.Signal(); // Idempotent.

    await Assert.That(awaiter.IsSignaled).IsTrue();
    await awaiter.WaitAsync(_longTimeout);
    await Assert.That(awaiter.AwaiterId).IsNotEqualTo(Guid.Empty);
  }

  [Test]
  public async Task SignalAwaiter_WaitWithZeroTimeout_NotSignaled_ThrowsTimeoutAsync() {
    var awaiter = new SignalAwaiter();

    var ex = await Assert.ThrowsAsync<TimeoutException>(
      async () => await awaiter.WaitAsync(TimeSpan.Zero));

    await Assert.That(ex!.Message).Contains("Signal not received within");
  }

  [Test]
  public async Task WarmupAsync_TransportDeliversFirstMessage_CompletesAfterSinglePublishAsync() {
    var warmupAwaiter = new SignalAwaiter();
    var transport = new FakeTransport { OnPublish = _ => warmupAwaiter.Signal() };

    await SubscriptionWarmup.WarmupAsync(
      transport,
      _destination,
      () => EnvelopeFactory.Create(SubscriptionWarmup.GenerateWarmupId()),
      warmupAwaiter);

    await Assert.That(transport.Published.Count).IsEqualTo(1);
    await Assert.That(warmupAwaiter.IsSignaled).IsTrue();
  }

  [Test]
  public async Task WarmupAsync_AlreadySignaled_ReturnsWithoutPublishingAsync() {
    var warmupAwaiter = new SignalAwaiter();
    warmupAwaiter.Signal();
    var transport = new FakeTransport();

    await SubscriptionWarmup.WarmupAsync(
      transport,
      _destination,
      () => EnvelopeFactory.Create(SubscriptionWarmup.GenerateWarmupId()),
      warmupAwaiter,
      timeout: _longTimeout);

    await Assert.That(transport.Published.Count).IsEqualTo(0);
  }

  [Test]
  public async Task WarmupAsync_EchoAlreadyArrived_CompletesWithoutWaitingOnTheClockAsync() {
    // A zero budget leaves no time to spend: warmup publishes, and the echo the transport delivered
    // during that publish is already the signal, so it completes rather than timing out.
    var warmupAwaiter = new SignalAwaiter();
    var transport = new FakeTransport { OnPublish = _ => warmupAwaiter.Signal() };

    await SubscriptionWarmup.WarmupAsync(
      transport,
      _destination,
      () => EnvelopeFactory.Create(SubscriptionWarmup.GenerateWarmupId()),
      warmupAwaiter,
      timeout: TimeSpan.Zero);

    await Assert.That(transport.Published.Count).IsEqualTo(1);
    await Assert.That(warmupAwaiter.IsSignaled).IsTrue();
  }

  [Test]
  public async Task WarmupAsync_EchoNeverArrives_PublishesOnceAndReportsATimeoutAsync() {
    // The subscription is durable once SubscribeAsync returns, so one warmup message is enough:
    // it waits on the broker until the subscriber takes it. Nothing echoes it back here, so the
    // budget ends the wait, after exactly one publish.
    var warmupAwaiter = new SignalAwaiter();
    var transport = new FakeTransport();

    var ex = await Assert.ThrowsAsync<TimeoutException>(async () =>
      await SubscriptionWarmup.WarmupAsync(
        transport,
        _destination,
        () => EnvelopeFactory.Create(SubscriptionWarmup.GenerateWarmupId()),
        warmupAwaiter,
        timeout: TimeSpan.Zero));

    await Assert.That(ex!.Message).Contains("Subscription warmup timed out");
    await Assert.That(transport.Published.Count).IsEqualTo(1);
    await Assert.That(warmupAwaiter.IsSignaled).IsFalse();
  }

  [Test]
  public async Task WarmupAsync_PublishesBeforeAnyWait_AndCompletesOnTheEchoSignalAsync(CancellationToken cancellationToken) {
    // The fake transport publishes synchronously, so a warmup that does not wait on the clock has
    // already published by the time the call hands back its task. A warmup that sleeps first (the
    // old 5-second initial delay) has published nothing yet. After the publish the warmup is still
    // waiting, and it finishes once the subscriber signals.
    var warmupAwaiter = new SignalAwaiter();
    var transport = new FakeTransport();

    var warmup = SubscriptionWarmup.WarmupAsync(
      transport,
      _destination,
      () => EnvelopeFactory.Create(SubscriptionWarmup.GenerateWarmupId()),
      warmupAwaiter,
      timeout: _longTimeout,
      cancellationToken: cancellationToken);

    await Assert.That(transport.Published.Count).IsEqualTo(1)
      .Because("the warmup message is published straight away, with no wait on the clock first");
    await Assert.That(warmup.IsCompleted).IsFalse()
      .Because("the warmup message has been published but not echoed back yet");

    warmupAwaiter.Signal();
    await warmup.WaitAsync(_longTimeout, cancellationToken);

    await Assert.That(transport.Published.Count).IsEqualTo(1);
  }

  [Test]
  public async Task WarmupAsync_PreCanceledToken_ThrowsBeforePublishingAsync() {
    var warmupAwaiter = new SignalAwaiter();
    var transport = new FakeTransport();
    using var cts = new CancellationTokenSource();
    await cts.CancelAsync();

    await Assert.ThrowsAsync<OperationCanceledException>(async () =>
      await SubscriptionWarmup.WarmupAsync(
        transport,
        _destination,
        () => EnvelopeFactory.Create(SubscriptionWarmup.GenerateWarmupId()),
        warmupAwaiter,
        timeout: _longTimeout,
        cancellationToken: cts.Token));

    await Assert.That(transport.Published.Count).IsEqualTo(0);
  }

  [Test]
  public async Task WarmupAsync_DefaultTimeout_IsTheDocumentedValueAsync() {
    await Assert.That(SubscriptionWarmup.DefaultWarmupTimeout).IsEqualTo(TimeSpan.FromSeconds(30));
  }

}
