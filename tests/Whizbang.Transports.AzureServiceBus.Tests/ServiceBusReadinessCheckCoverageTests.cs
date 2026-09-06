#pragma warning disable CA1707 // Test method names can contain underscores

using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Core;
using Whizbang.Core.Observability;
using Whizbang.Core.Transports;
using Whizbang.Core.Workers;
using Whizbang.Transports.AzureServiceBus;

namespace Whizbang.Transports.AzureServiceBus.Tests;

/// <summary>
/// Coverage-focused tests for <see cref="ServiceBusReadinessCheck"/>'s double-checked-locking
/// paths (the pre-lock and post-lock re-checks of transport initialization and the result
/// cache) and its idempotent <see cref="ServiceBusReadinessCheck.Dispose"/>. The happy-path and
/// single-threaded cache behaviors are already covered by
/// <c>ServiceBusReadinessCheckTests</c>; these tests reach the branches that only fire when a
/// dependency's state changes between the pre-lock check and the post-lock re-check.
/// </summary>
public class ServiceBusReadinessCheckCoverageTests {

  // If the pre-lock initialization gate were skipped or short-circuited past, an uninitialized
  // transport would fall through toward the client check and could report ready before startup
  // (or a reconnect) has actually finished — the dangerous false "ready" this check exists to
  // prevent, since it would send a host down the path of routing traffic to a transport that
  // cannot carry it.
  [Test]
  public async Task IsReadyAsync_WithUninitializedTransport_ReturnsFalseWithoutTouchingClientAsync() {
    // Arrange
    var transport = new TestTransport(isInitialized: false);
    var client = new TestServiceBusClient(isHealthy: true);
    var check = new ServiceBusReadinessCheck(transport, client, NullLogger<ServiceBusReadinessCheck>.Instance);

    // Act
    var isReady = await check.IsReadyAsync();

    // Assert
    await Assert.That(isReady).IsFalse()
      .Because("an uninitialized transport must never be reported ready");
    await Assert.That(client.IsClosedAccessCount).IsEqualTo(0)
      .Because("the pre-lock initialization gate must short-circuit before the client is ever consulted, and before the lock is ever touched");
  }

  // A transport whose initialization flag flips false between the pre-lock and post-lock
  // checks (e.g., a concurrent shutdown discovered while this call was waiting on the lock)
  // must still report not-ready for THIS call — reporting a stale "ready" here is exactly the
  // dangerous direction: the host would keep routing traffic to a transport that just went down.
  [Test]
  public async Task IsReadyAsync_TransportBecomesUninitializedBetweenChecks_ReturnsFalseAsync() {
    // Arrange: true on the pre-lock read, false on the post-lock re-check.
    var transport = new SequencedInitializationTransport(call => call == 1);
    var client = new TestServiceBusClient(isHealthy: true);
    var check = new ServiceBusReadinessCheck(transport, client, NullLogger<ServiceBusReadinessCheck>.Instance);

    // Act
    var isReady = await check.IsReadyAsync();

    // Assert
    await Assert.That(isReady).IsFalse()
      .Because("the post-lock re-check observed the transport as no longer initialized and must not report stale readiness");
    await Assert.That(transport.IsInitializedCallCount).IsEqualTo(2)
      .Because("both the pre-lock and post-lock initialization checks must execute for this call");
    await Assert.That(client.IsClosedAccessCount).IsEqualTo(0)
      .Because("the post-lock transport re-check must short-circuit before the client is ever consulted");
  }

  // If two overlapping readiness probes raced the internal lock and the loser re-verified the
  // client instead of trusting the cache the winner had just populated, every concurrent health
  // probe during a busy period would re-hit the Service Bus client — defeating the caching this
  // check exists to provide, and doing the exact double work the post-lock re-check is there to
  // avoid.
  [Test]
  public async Task IsReadyAsync_SecondCallerSeesCacheSetWhileWaitingForLock_ReturnsTrueWithoutRecheckingClientAsync() {
    // Arrange
    using var reachedPostLockCheck = new ManualResetEventSlim(initialState: false);
    using var releaseFirstCaller = new ManualResetEventSlim(initialState: false);
    var transport = new SequencedInitializationTransport(call => {
      if (call == 2) {
        // The first caller has acquired the lock and is about to re-check initialization.
        // Park it here so the second caller can observe a cache miss before the first
        // caller populates the cache — the only way to force the post-lock cache-hit
        // branch deterministically, without sleeps or polling.
        reachedPostLockCheck.Set();
        releaseFirstCaller.Wait(TimeSpan.FromSeconds(10));
      }
      return true;
    });
    var client = new TestServiceBusClient(isHealthy: true);
    var check = new ServiceBusReadinessCheck(
      transport,
      client,
      NullLogger<ServiceBusReadinessCheck>.Instance,
      cacheDuration: TimeSpan.FromMinutes(5));

    // Act
    var firstCallTask = Task.Run(() => check.IsReadyAsync());
    var reachedGate = reachedPostLockCheck.Wait(TimeSpan.FromSeconds(10));
    await Assert.That(reachedGate).IsTrue()
      .Because("the first call must be parked inside the lock at its post-lock re-check, or this test's premise of a genuine race is false");

    // Runs synchronously on this thread: the outer transport check (true) and outer cache
    // check (miss — the first caller hasn't populated it yet) both complete here, before
    // this call suspends on the lock the first caller still holds.
    var secondCallTask = check.IsReadyAsync();

    releaseFirstCaller.Set();
    var firstResult = await firstCallTask;
    var secondResult = await secondCallTask;

    // Assert
    await Assert.That(firstResult).IsTrue()
      .Because("the first caller found the client open and populated the cache");
    await Assert.That(secondResult).IsTrue()
      .Because("the second caller must see the cache the first caller just populated while holding the lock");
    await Assert.That(transport.IsInitializedCallCount).IsEqualTo(4)
      .Because("each caller reads IsInitialized twice (pre-lock, post-lock); a count of 4 proves the second caller reached its post-lock re-check rather than returning from its own outer cache check");
    await Assert.That(client.IsClosedAccessCount).IsEqualTo(1)
      .Because("the second caller must resolve from the cache its post-lock re-check just observed, not by asking the client again");
  }

  // A host that disposes the readiness check explicitly on shutdown, and again through a
  // `using` block's automatic cleanup, must not throw on the second call — regressing this
  // guard turns an ordinary double-dispose into an unhandled exception during graceful
  // shutdown.
  [Test]
  public async Task Dispose_CalledTwice_SecondCallIsNoOpAsync() {
    // Arrange
    var transport = new TestTransport(isInitialized: true);
    var client = new TestServiceBusClient(isHealthy: true);
    var check = new ServiceBusReadinessCheck(transport, client, NullLogger<ServiceBusReadinessCheck>.Instance);

    // Act
    check.Dispose();
    check.Dispose();

    // Assert: the second Dispose() must have hit the _disposed guard and returned immediately
    // (no exception above this line) rather than disposing the semaphore twice. A subsequent
    // call proves the lock really was disposed by the first call, not silently skipped.
    await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
      await check.IsReadyAsync());
  }
}

/// <summary>
/// Test implementation of ITransport whose IsInitialized getter can vary per call ordinal
/// (1-based) via a caller-supplied callback, and can pause the calling thread on a specific
/// call to force a deterministic interleaving with a second, concurrent caller. Used to reach
/// ServiceBusReadinessCheck's post-lock double-check branches, which only diverge from the
/// pre-lock checks when a dependency's state changes between them.
/// </summary>
internal sealed class SequencedInitializationTransport(Func<int, bool> isInitializedByCall) : ITransport {
  private int _callCount;

  public int IsInitializedCallCount => _callCount;

  public bool IsInitialized {
    get {
      var call = Interlocked.Increment(ref _callCount);
      return isInitializedByCall(call);
    }
  }

  public TransportCapabilities Capabilities => TransportCapabilities.PublishSubscribe;

  public Task InitializeAsync(CancellationToken cancellationToken = default) {
    return Task.CompletedTask;
  }

  public Task PublishAsync(IMessageEnvelope envelope, TransportDestination destination, string? envelopeType = null, ReadOnlyMemory<byte>? preSerializedBytes = null, CancellationToken cancellationToken = default) {
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
