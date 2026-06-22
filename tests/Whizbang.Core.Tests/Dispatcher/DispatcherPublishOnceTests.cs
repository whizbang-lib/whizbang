using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Tests.Generated;

namespace Whizbang.Core.Tests.Dispatcher;

/// <summary>
/// Locks the orchestration contract of <see cref="IDispatcher.PublishOnceAsync{TEvent}(string, TEvent, CancellationToken)"/>:
/// the dispatcher composes <see cref="IClaimedEmissionStore"/> with the existing
/// <see cref="IDispatcher.PublishAsync{TEvent}(TEvent)"/> path, gating local
/// receptor invocation on the claim outcome.
/// </summary>
/// <remarks>
/// <para>
/// The storage-side atomicity guarantee (two concurrent writers collapse to
/// exactly one) is locked in
/// <c>Whizbang.Data.EFCore.Postgres.Tests.Dispatch.ClaimedEmissionStoreTests</c>
/// against a real Postgres testcontainer. These tests lock the dispatcher's
/// orchestration: it must <em>both</em> ask the store first <em>and</em> skip
/// the publish path when the claim is lost.
/// </para>
/// </remarks>
/// <docs>fundamentals/dispatcher/publish-once</docs>
[NotInParallel("PublishOnce")]
public class DispatcherPublishOnceTests {

  // ── Test event + receptor (discovered by source generator) ────────────

  public record PublishOnceTestEvent([property: StreamId] Guid StreamId) : IEvent;

  /// <summary>
  /// Thread-safe counter incremented on every successful PublishAsync of
  /// <see cref="PublishOnceTestEvent"/>. Tests assert against this counter
  /// to prove the dispatcher proceeded past the claim and through PublishAsync.
  /// </summary>
  internal static int FireCount;

  public class PublishOnceTestEventReceptor : IReceptor<PublishOnceTestEvent> {
    public ValueTask HandleAsync(PublishOnceTestEvent message, CancellationToken cancellationToken) {
      Interlocked.Increment(ref FireCount);
      return ValueTask.CompletedTask;
    }
  }

  // ── In-memory claim store (mimics ON CONFLICT DO NOTHING semantics) ──

  private sealed class InMemoryClaimedEmissionStore : IClaimedEmissionStore {
    private readonly HashSet<string> _claimed = new(StringComparer.Ordinal);
    private readonly Lock _lock = new();

    public List<string> AttemptedKeys { get; } = new();

    public Task<bool> TryClaimAsync(string claimKey, Guid claimedByEventId, CancellationToken cancellationToken) {
      lock (_lock) {
        AttemptedKeys.Add(claimKey);
        // HashSet.Add returns true if the value was not already present —
        // matches the ON CONFLICT DO NOTHING returning-affected-rows contract.
        return Task.FromResult(_claimed.Add(claimKey));
      }
    }
  }

  [Before(Test)]
  public Task ResetCounterAsync() {
    Interlocked.Exchange(ref FireCount, 0);
    return Task.CompletedTask;
  }

  // ── Happy path ────────────────────────────────────────────────────────

  [Test]
  public async Task PublishOnceAsync_FirstClaim_ReturnsTrueAndFiresHandlerAsync() {
    var store = new InMemoryClaimedEmissionStore();
    var dispatcher = _createDispatcher(store);

    var result = await dispatcher.PublishOnceAsync(
      claimKey: "saga-completed:abc",
      eventData: new PublishOnceTestEvent(Guid.NewGuid()),
      cancellationToken: CancellationToken.None);

    await Assert.That(result).IsTrue()
      .Because("The claim store reports the first claim as won; PublishOnceAsync forwards that outcome to the caller.");
    await Assert.That(FireCount).IsEqualTo(1)
      .Because("Winning the claim must proceed to PublishAsync, which invokes the in-process receptor exactly once.");
    await Assert.That(store.AttemptedKeys).Contains("saga-completed:abc");
  }

  // ── Second attempt with the same key is the no-op contract ───────────

  [Test]
  public async Task PublishOnceAsync_SecondClaimSameKey_ReturnsFalseAndDoesNotFireHandlerAsync() {
    var store = new InMemoryClaimedEmissionStore();
    var dispatcher = _createDispatcher(store);

    var first = await dispatcher.PublishOnceAsync("once:k1", new PublishOnceTestEvent(Guid.NewGuid()), CancellationToken.None);
    var second = await dispatcher.PublishOnceAsync("once:k1", new PublishOnceTestEvent(Guid.NewGuid()), CancellationToken.None);

    await Assert.That(first).IsTrue();
    await Assert.That(second).IsFalse()
      .Because("The losing caller intentionally no-ops without throwing — that's the entire point of the primitive over a SELECT-then-INSERT pattern.");
    await Assert.That(FireCount).IsEqualTo(1)
      .Because("Only one event reaches PublishAsync; the second event must not invoke the in-process receptor — otherwise PublishOnceAsync would publish twice.");
  }

  // ── Distinct keys are independent ────────────────────────────────────

  [Test]
  public async Task PublishOnceAsync_DistinctKeys_BothReturnTrueAndBothFireAsync() {
    var store = new InMemoryClaimedEmissionStore();
    var dispatcher = _createDispatcher(store);

    var a = await dispatcher.PublishOnceAsync("once:a", new PublishOnceTestEvent(Guid.NewGuid()), CancellationToken.None);
    var b = await dispatcher.PublishOnceAsync("once:b", new PublishOnceTestEvent(Guid.NewGuid()), CancellationToken.None);

    await Assert.That(a).IsTrue();
    await Assert.That(b).IsTrue();
    await Assert.That(FireCount).IsEqualTo(2);
  }

  // ── Input validation ─────────────────────────────────────────────────

  [Test]
  public async Task PublishOnceAsync_NullClaimKey_ThrowsAsync() {
    var dispatcher = _createDispatcher(new InMemoryClaimedEmissionStore());

    await Assert.That(() => dispatcher.PublishOnceAsync(null!, new PublishOnceTestEvent(Guid.NewGuid()), CancellationToken.None))
      .ThrowsExactly<ArgumentNullException>()
      .Because("Claim keys must be non-null — they are the storage primary key; null would be a bug.");
  }

  [Test]
  public async Task PublishOnceAsync_EmptyClaimKey_ThrowsAsync() {
    var dispatcher = _createDispatcher(new InMemoryClaimedEmissionStore());

    await Assert.That(() => dispatcher.PublishOnceAsync(string.Empty, new PublishOnceTestEvent(Guid.NewGuid()), CancellationToken.None))
      .ThrowsExactly<ArgumentException>()
      .Because("Empty/whitespace claim keys silently collapse to a single global claim; reject them at the boundary.");
  }

  [Test]
  public async Task PublishOnceAsync_NullEvent_ThrowsAsync() {
    var dispatcher = _createDispatcher(new InMemoryClaimedEmissionStore());

    await Assert.That(() => dispatcher.PublishOnceAsync<PublishOnceTestEvent>("k", null!, CancellationToken.None))
      .ThrowsExactly<ArgumentNullException>();
  }

  // ── Missing IClaimedEmissionStore registration ───────────────────────

  [Test]
  public async Task PublishOnceAsync_NoClaimStoreRegistered_ThrowsInvalidOperationAsync() {
    var dispatcher = _createDispatcher(claimStore: null);

    await Assert.That(() => dispatcher.PublishOnceAsync("k", new PublishOnceTestEvent(Guid.NewGuid()), CancellationToken.None))
      .ThrowsExactly<InvalidOperationException>()
      .Because("Without a registered claim store the framework cannot enforce exactly-once semantics; failing loudly is the only safe outcome.");
  }

  // ── Cancellation honored up front ────────────────────────────────────

  [Test]
  public async Task PublishOnceAsync_CancelledToken_ThrowsAsync() {
    var dispatcher = _createDispatcher(new InMemoryClaimedEmissionStore());
    using var cts = new CancellationTokenSource();
    cts.Cancel();

    await Assert.That(() => dispatcher.PublishOnceAsync("k", new PublishOnceTestEvent(Guid.NewGuid()), cts.Token))
      .ThrowsExactly<OperationCanceledException>();
  }

  // ── Helper ───────────────────────────────────────────────────────────

  private static IDispatcher _createDispatcher(IClaimedEmissionStore? claimStore) {
    var services = new ServiceCollection();
    services.AddSingleton<IServiceInstanceProvider>(new ServiceInstanceProvider(configuration: null));
    services.AddReceptors();
    services.AddWhizbangDispatcher();
    if (claimStore is not null) {
      services.AddSingleton(claimStore);
    }
    return services.BuildServiceProvider().GetRequiredService<IDispatcher>();
  }
}
