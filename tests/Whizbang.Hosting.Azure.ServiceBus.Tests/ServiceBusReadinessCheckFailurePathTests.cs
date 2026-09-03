using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Observability;
using Whizbang.Core.Transports;
using Whizbang.Core.Workers;
using Whizbang.Hosting.Azure.ServiceBus;

namespace Whizbang.Hosting.Azure.ServiceBus.Tests;

/// <summary>
/// Failure-path and lifecycle tests for <see cref="ServiceBusReadinessCheck"/>.
/// Complements <see cref="ServiceBusReadinessCheckTests"/> (healthy client,
/// closed client, cancellation, cache hit/expiry) by locking the remaining
/// branches: constructor guards, the transport-not-initialized early return,
/// both post-lock double-checks, and the Dispose branches.
/// </summary>
public class ServiceBusReadinessCheckFailurePathTests {

  // ---------------------------------------------------------------------
  // Constructor guards
  // ---------------------------------------------------------------------

  [Test]
  public async Task Constructor_NullTransport_ThrowsArgumentNullExceptionAsync() {
    var client = new TestServiceBusClient(isHealthy: true);

    Action act = () => _ = new ServiceBusReadinessCheck(null!, client, NullLogger<ServiceBusReadinessCheck>.Instance);

    var ex = await Assert.That(act).ThrowsExactly<ArgumentNullException>();
    await Assert.That(ex!.ParamName).IsEqualTo("transport");
  }

  [Test]
  public async Task Constructor_NullClient_ThrowsArgumentNullExceptionAsync() {
    var transport = new TestTransport(isInitialized: true);

    Action act = () => _ = new ServiceBusReadinessCheck(transport, null!, NullLogger<ServiceBusReadinessCheck>.Instance);

    var ex = await Assert.That(act).ThrowsExactly<ArgumentNullException>();
    await Assert.That(ex!.ParamName).IsEqualTo("client");
  }

  [Test]
  public async Task Constructor_NullLogger_ThrowsArgumentNullExceptionAsync() {
    var transport = new TestTransport(isInitialized: true);
    var client = new TestServiceBusClient(isHealthy: true);

    Action act = () => _ = new ServiceBusReadinessCheck(transport, client, null!);

    var ex = await Assert.That(act).ThrowsExactly<ArgumentNullException>();
    await Assert.That(ex!.ParamName).IsEqualTo("logger");
  }

  // ---------------------------------------------------------------------
  // Transport-not-initialized branches
  // ---------------------------------------------------------------------

  [Test]
  public async Task IsReadyAsync_TransportNotInitialized_ReturnsFalseWithoutProbingClientAsync() {
    var transport = new TestTransport(isInitialized: false);
    var client = new TestServiceBusClient(isHealthy: true);
    using var check = new ServiceBusReadinessCheck(transport, client, NullLogger<ServiceBusReadinessCheck>.Instance);

    var isReady = await check.IsReadyAsync();

    await Assert.That(isReady).IsFalse()
      .Because("An uninitialized transport means connectivity was never verified — the check must report not-ready.");
    await Assert.That(client.IsClosedAccessCount).IsEqualTo(0)
      .Because("The pre-lock early return must short-circuit before the client is probed at all.");
  }

  [Test]
  public async Task IsReadyAsync_TransportNotInitialized_ReturnsFalseEvenWithCanceledTokenAsync() {
    var transport = new TestTransport(isInitialized: false);
    var client = new TestServiceBusClient(isHealthy: true);
    using var check = new ServiceBusReadinessCheck(transport, client, NullLogger<ServiceBusReadinessCheck>.Instance);
    using var cts = new CancellationTokenSource();
    cts.Cancel();

    // The not-initialized early return happens BEFORE the lock wait — the
    // only point that observes the token — so this returns false instead of
    // throwing OperationCanceledException (contrast with
    // ServiceBusReadinessCheckTests.IsReadyAsync_RespectsCancellationTokenAsync,
    // where the transport IS initialized and the token throws).
    var isReady = await check.IsReadyAsync(cts.Token);

    await Assert.That(isReady).IsFalse()
      .Because("Branch ordering: the early not-ready return wins over cancellation because the token is only observed at lock acquisition.");
  }

  [Test]
  public async Task IsReadyAsync_TransportUninitializedAfterLockAcquired_ReturnsFalseFromDoubleCheckAsync() {
    // The transport reports initialized on the pre-lock read, then flips to
    // uninitialized — the post-lock double-check must catch the transition.
    var transport = new UninitializedAfterFirstReadTransport();
    var client = new TestServiceBusClient(isHealthy: true);
    using var check = new ServiceBusReadinessCheck(transport, client, NullLogger<ServiceBusReadinessCheck>.Instance);

    var isReady = await check.IsReadyAsync();

    await Assert.That(isReady).IsFalse()
      .Because("A transport that disconnects between the pre-lock check and lock acquisition must be caught by the double-check.");
    await Assert.That(transport.ReadCount).IsEqualTo(2)
      .Because("Exactly two IsInitialized reads: the pre-lock check (true) and the post-lock double-check (false).");
    await Assert.That(client.IsClosedAccessCount).IsEqualTo(0)
      .Because("The double-check returns before the client is ever probed.");
  }

  // ---------------------------------------------------------------------
  // Post-lock cache double-check
  // ---------------------------------------------------------------------

  [Test]
  public async Task IsReadyAsync_CachePopulatedWhileWaitingForLock_ReturnsCachedResultWithoutReprobeAsync() {
    var transport = new TestTransport(isInitialized: true);
    var client = new GatedServiceBusClient();
    using var check = new ServiceBusReadinessCheck(
      transport,
      client,
      NullLogger<ServiceBusReadinessCheck>.Instance,
      cacheDuration: TimeSpan.FromMinutes(5));

    // First call runs on the pool and parks INSIDE the lock, mid-IsClosed
    // probe, until we release the gate.
    var firstCall = Task.Run(() => check.IsReadyAsync());
    var firstCallInsideLock = await client.WaitForIsClosedEnteredAsync();
    await Assert.That(firstCallInsideLock).IsTrue()
      .Because("Setup: the first call must hold the lock mid-probe before the second call starts.");

    // Second call executes synchronously up to the lock wait: it passes the
    // pre-lock cache check (cache not yet populated — the first call is still
    // gated) and then parks on the lock. Deterministic — no timing involved.
    var secondCall = check.IsReadyAsync();
    await Assert.That(secondCall.IsCompleted).IsFalse()
      .Because("Setup: the second call must be parked on the lock, past its pre-lock cache check.");

    // Let the first call finish: it records the successful check and
    // releases the lock, so the second call resumes into the post-lock
    // cache double-check.
    client.ReleaseIsClosed();

    await Assert.That(await firstCall).IsTrue();
    await Assert.That(await secondCall).IsTrue()
      .Because("The second call is served by the cache populated while it waited for the lock.");
    await Assert.That(client.IsClosedAccessCount).IsEqualTo(1)
      .Because("The post-lock cache double-check must satisfy the second call — a second IsClosed probe would defeat the cache under lock contention.");
  }

  // ---------------------------------------------------------------------
  // Dispose branches
  // ---------------------------------------------------------------------

  [Test]
  public async Task Dispose_CalledTwice_SecondCallIsNoOpAndLockStaysDisposedAsync() {
    var transport = new TestTransport(isInitialized: true);
    var client = new TestServiceBusClient(isHealthy: true);
    var check = new ServiceBusReadinessCheck(transport, client, NullLogger<ServiceBusReadinessCheck>.Instance);

    check.Dispose();
    check.Dispose(); // second call takes the _disposed early return — must not throw

    // The lock really was disposed by the FIRST call (and the second didn't
    // resurrect or double-dispose anything): an uncached readiness probe
    // needs the lock and now surfaces ObjectDisposedException.
    await Assert.That(async () => await check.IsReadyAsync())
      .ThrowsExactly<ObjectDisposedException>()
      .Because("After dispose, the uncached path requires the disposed semaphore — proving dispose completed and the repeat call was a no-op.");
  }

  [Test]
  public async Task IsReadyAsync_AfterDisposeWithFreshCachedSuccess_ReturnsTrueFromCacheAsync() {
    var transport = new TestTransport(isInitialized: true);
    var client = new TestServiceBusClient(isHealthy: true);
    var check = new ServiceBusReadinessCheck(
      transport,
      client,
      NullLogger<ServiceBusReadinessCheck>.Instance,
      cacheDuration: TimeSpan.FromMinutes(5));

    var beforeDispose = await check.IsReadyAsync();
    await Assert.That(beforeDispose).IsTrue();

    check.Dispose();

    var afterDispose = await check.IsReadyAsync();

    await Assert.That(afterDispose).IsTrue()
      .Because("The pre-lock cache hit never touches the disposed semaphore — a fresh successful check keeps answering ready.");
    await Assert.That(client.IsClosedAccessCount).IsEqualTo(1)
      .Because("The post-dispose call must be served purely from cache, not a fresh probe.");
  }

  [Test]
  public async Task IsReadyAsync_AfterDisposeFollowingFailedCheck_ThrowsObjectDisposedExceptionAsync() {
    var transport = new TestTransport(isInitialized: true);
    var client = new TestServiceBusClient(isHealthy: false);
    var check = new ServiceBusReadinessCheck(transport, client, NullLogger<ServiceBusReadinessCheck>.Instance);

    // Failed checks are deliberately NOT cached...
    var failedResult = await check.IsReadyAsync();
    await Assert.That(failedResult).IsFalse();

    check.Dispose();

    // ...so the next probe cannot ride a cache hit and must take the
    // (now disposed) lock.
    await Assert.That(async () => await check.IsReadyAsync())
      .ThrowsExactly<ObjectDisposedException>()
      .Because("Only successful checks populate the cache; after a failure + dispose, the re-probe hits the disposed semaphore.");
  }
}

/// <summary>
/// Reports <c>IsInitialized == true</c> on the first read and <c>false</c>
/// on every subsequent read — simulates a transport that disconnects between
/// the readiness check's pre-lock read and its post-lock double-check.
/// </summary>
internal sealed class UninitializedAfterFirstReadTransport : ITransport {
  private int _reads;

  public bool IsInitialized => Interlocked.Increment(ref _reads) == 1;
  public int ReadCount => Volatile.Read(ref _reads);
  public TransportCapabilities Capabilities => TransportCapabilities.PublishSubscribe;

  public Task InitializeAsync(CancellationToken cancellationToken = default) {
    return Task.CompletedTask;
  }

  public Task PublishAsync(IMessageEnvelope envelope, TransportDestination destination, string? envelopeType = null, ReadOnlyMemory<byte>? preSerializedBytes = null, CancellationToken cancellationToken = default) {
    throw new NotImplementedException();
  }

  public Task<ISubscription> SubscribeAsync(Func<IMessageEnvelope, string?, CancellationToken, Task> handler, TransportDestination destination, CancellationToken cancellationToken = default) {
    throw new NotImplementedException();
  }

  public Task<ISubscription> SubscribeBatchAsync(
    Func<IReadOnlyList<TransportMessage>, CancellationToken, Task> batchHandler,
    TransportDestination destination,
    TransportBatchOptions batchOptions,
    CancellationToken cancellationToken = default) =>
    throw new NotSupportedException();

  public Task<IMessageEnvelope> SendAsync<TRequest, TResponse>(IMessageEnvelope requestEnvelope, TransportDestination destination, CancellationToken cancellationToken = default)
    where TRequest : notnull where TResponse : notnull {
    throw new NotImplementedException();
  }
}

/// <summary>
/// Healthy <see cref="ServiceBusClient"/> whose <see cref="IsClosed"/> getter
/// signals entry and then blocks on a gate — lets a test hold the readiness
/// check's internal lock at a deterministic point (mid-probe) with completion
/// signals instead of timing.
/// </summary>
internal sealed class GatedServiceBusClient : ServiceBusClient {
  private readonly SemaphoreSlim _entered = new(0);
  private readonly SemaphoreSlim _proceed = new(0);
  private int _isClosedAccessCount;

  public int IsClosedAccessCount => Volatile.Read(ref _isClosedAccessCount);

  public override bool IsClosed {
    get {
      Interlocked.Increment(ref _isClosedAccessCount);
      _entered.Release();
      if (!_proceed.Wait(TimeSpan.FromSeconds(30))) {
        throw new InvalidOperationException("GatedServiceBusClient gate was never released — test wiring error.");
      }
      return false; // healthy
    }
  }

  /// <summary>Completes when a caller is inside the IsClosed probe (and therefore inside the readiness check's lock).</summary>
  public Task<bool> WaitForIsClosedEnteredAsync() {
    return _entered.WaitAsync(TimeSpan.FromSeconds(30));
  }

  /// <summary>Releases the gated IsClosed probe so the in-flight readiness check can finish.</summary>
  public void ReleaseIsClosed() {
    _proceed.Release();
  }
}
