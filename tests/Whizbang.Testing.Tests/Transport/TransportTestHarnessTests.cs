using Whizbang.Core.Observability;
using Whizbang.Core.Transports;
using Whizbang.Core.Workers;
using Whizbang.Testing.Tests.TestSupport;
using Whizbang.Testing.Transport;

namespace Whizbang.Testing.Tests.Transport;

/// <summary>
/// Tests for <see cref="TransportTestHarness{TPayload}"/> and the
/// <see cref="TransportTestHarness"/> factory.
/// </summary>
/// <remarks>
/// The full happy-path of <c>SetupSubscriptionAsync</c> (warmup publish loop) is not
/// exercised end-to-end because the harness hard-codes SubscriptionWarmup's 5-second
/// initial delay with no way to configure it; instead setup is driven up to the warmup
/// delay with a pre-canceled token, which still wires the subscription and test awaiter.
/// </remarks>
public class TransportTestHarnessTests {
  private static readonly TransportDestination _subscribeDestination = new("topic/subscription");
  private static readonly TransportDestination _publishDestination = new("topic");
  private static readonly TimeSpan _longTimeout = TimeSpan.FromSeconds(30);

  private static TransportTestHarness<TestPayload> _createHarness(FakeTransport transport) {
    return TransportTestHarness.Create(
      transport,
      content => new TestPayload { Content = content },
      payload => payload.Content);
  }

  /// <summary>
  /// Runs setup far enough to register the subscription and test awaiter. The pre-canceled
  /// token aborts the warmup publish loop at its initial delay, deterministically.
  /// </summary>
  private static async Task _setupSubscriptionAsync(
    TransportTestHarness<TestPayload> harness,
    FakeTransport transport) {
    using var cts = new CancellationTokenSource();
    await cts.CancelAsync();

    await Assert.ThrowsAsync<TaskCanceledException>(async () =>
      await harness.SetupSubscriptionAsync(
        _subscribeDestination, _publishDestination, cancellationToken: cts.Token));

    await Assert.That(transport.BatchHandler).IsNotNull();
    await Assert.That(harness.TestAwaiter).IsNotNull();
  }

  [Test]
  public async Task Ctor_NullTransport_ThrowsAsync() {
    var ex = Assert.Throws<ArgumentNullException>(() => _ = new TransportTestHarness<TestPayload>(
      null!,
      content => EnvelopeFactory.Create(content),
      payload => payload.Content));

    await Assert.That(ex!.ParamName).IsEqualTo("transport");
  }

  [Test]
  public async Task Ctor_NullEnvelopeFactory_ThrowsAsync() {
    var ex = Assert.Throws<ArgumentNullException>(() => _ = new TransportTestHarness<TestPayload>(
      new FakeTransport(),
      null!,
      payload => payload.Content));

    await Assert.That(ex!.ParamName).IsEqualTo("envelopeFactory");
  }

  [Test]
  public async Task Ctor_NullContentSelector_ThrowsAsync() {
    var ex = Assert.Throws<ArgumentNullException>(() => _ = new TransportTestHarness<TestPayload>(
      new FakeTransport(),
      content => EnvelopeFactory.Create(content),
      null!));

    await Assert.That(ex!.ParamName).IsEqualTo("contentSelector");
  }

  [Test]
  public async Task PublishAndWaitAsync_BeforeSetup_ThrowsInvalidOperationAsync() {
    var transport = new FakeTransport();
    await using var harness = _createHarness(transport);

    var ex = await Assert.ThrowsAsync<InvalidOperationException>(
      async () => await harness.PublishAndWaitAsync(_publishDestination, _longTimeout));

    await Assert.That(ex!.Message).Contains("Call SetupSubscriptionAsync first");
  }

  [Test]
  public async Task TestAwaiter_BeforeSetup_IsNullAsync() {
    var transport = new FakeTransport();
    await using var harness = _createHarness(transport);

    await Assert.That(harness.TestAwaiter).IsNull();
  }

  [Test]
  public async Task SetupSubscriptionAsync_SubscribesAndCreatesAwaitersAsync() {
    var transport = new FakeTransport();
    await using var harness = _createHarness(transport);

    await _setupSubscriptionAsync(harness, transport);

    await Assert.That(transport.Subscription).IsNotNull();
    await Assert.That(transport.Subscription!.Disposed).IsFalse();
  }

  [Test]
  public async Task PublishAndWaitAsync_DefaultContent_RoundTripsThroughTransportAsync() {
    var transport = new FakeTransport { LoopbackOnPublish = true };
    await using var harness = _createHarness(transport);
    await _setupSubscriptionAsync(harness, transport);

    var received = await harness.PublishAndWaitAsync(_publishDestination, _longTimeout);

    var typed = (IMessageEnvelope<TestPayload>)received;
    await Assert.That(typed.Payload.Content).IsEqualTo("test-content");
  }

  [Test]
  public async Task PublishAndWaitAsync_CustomContent_RoundTripsAndBuildsFactoryEnvelopeAsync() {
    var transport = new FakeTransport { LoopbackOnPublish = true };
    await using var harness = _createHarness(transport);
    await _setupSubscriptionAsync(harness, transport);

    var received = await harness.PublishAndWaitAsync(_publishDestination, _longTimeout, content: "custom-content");

    var typed = (IMessageEnvelope<TestPayload>)received;
    await Assert.That(typed.Payload.Content).IsEqualTo("custom-content");
    // Envelope shape produced by TransportTestHarness.Create factory:
    await Assert.That(typed.Hops.Count).IsEqualTo(1);
    await Assert.That(typed.Hops[0].Topic).IsEqualTo("test-topic");
  }

  [Test]
  public async Task DisposeAsync_DisposesSubscriptionsAndAsyncDisposableTransportAsync() {
    var transport = new FakeTransport();
    var harness = _createHarness(transport);
    await _setupSubscriptionAsync(harness, transport);

    await harness.DisposeAsync();

    await Assert.That(transport.Subscription!.Disposed).IsTrue();
    await Assert.That(transport.Disposed).IsTrue();
  }

  [Test]
  public async Task DisposeAsync_NonAsyncDisposableTransport_CompletesAsync() {
    var transport = new MinimalTransport();
    var harness = TransportTestHarness.Create(
      transport,
      content => new TestPayload { Content = content },
      payload => payload.Content);

    await harness.DisposeAsync();

    await Assert.That(transport.IsInitialized).IsTrue();
  }
}

/// <summary>
/// Bare-bones transport that is NOT IAsyncDisposable, covering the harness dispose branch
/// that skips transport disposal.
/// </summary>
internal sealed class MinimalTransport : ITransport {
  public bool IsInitialized => true;

  public TransportCapabilities Capabilities => TransportCapabilities.PublishSubscribe;

  public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

  public Task PublishAsync(
    IMessageEnvelope envelope,
    TransportDestination destination,
    string? envelopeType = null,
    ReadOnlyMemory<byte>? preSerializedBytes = null,
    CancellationToken cancellationToken = default) => Task.CompletedTask;

  public Task<ISubscription> SubscribeBatchAsync(
    Func<IReadOnlyList<TransportMessage>, CancellationToken, Task> batchHandler,
    TransportDestination destination,
    TransportBatchOptions batchOptions,
    CancellationToken cancellationToken = default) =>
    Task.FromResult<ISubscription>(new FakeSubscription());

  public Task<IMessageEnvelope> SendAsync<TRequest, TResponse>(
    IMessageEnvelope requestEnvelope,
    TransportDestination destination,
    CancellationToken cancellationToken = default)
    where TRequest : notnull
    where TResponse : notnull =>
    throw new NotSupportedException();
}
