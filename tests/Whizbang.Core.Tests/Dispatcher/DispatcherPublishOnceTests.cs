// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Tests.Generated;
using Whizbang.Core.Tests.Observability;

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
  private static int _fireCount;
  internal static int FireCount => _fireCount;

  public class PublishOnceTestEventReceptor : IReceptor<PublishOnceTestEvent> {
    public ValueTask HandleAsync(PublishOnceTestEvent message, CancellationToken cancellationToken = default) {
      Interlocked.Increment(ref _fireCount);
      return ValueTask.CompletedTask;
    }
  }

  // ── In-memory claim store (mimics ON CONFLICT DO NOTHING semantics) ──

  private sealed class InMemoryClaimedEmissionStore : IClaimedEmissionStore {
    private readonly HashSet<string> _claimed = new(StringComparer.Ordinal);
    private readonly Lock _lock = new();

    public List<string> AttemptedKeys { get; } = [];

    public Task<bool> TryClaimAsync(string claimKey, Guid claimedByEventId, CancellationToken cancellationToken) {
      lock (_lock) {
        AttemptedKeys.Add(claimKey);
        // HashSet.Add returns true if the value was not already present —
        // matches the ON CONFLICT DO NOTHING returning-affected-rows contract.
        var won = _claimed.Add(claimKey);
        if (won) {
          RaiseClaimed();
        }
        return Task.FromResult(won);
      }
    }

    public List<string> ReleasedKeys { get; } = [];

    /// <summary>Called right after a claim is won, before the publish runs.</summary>
    public Action? OnClaimed { get; init; }

    /// <summary>When set, giving a claim back fails.</summary>
    public bool FailRelease { get; init; }

    public Task<bool> ReleaseAsync(string claimKey, CancellationToken cancellationToken) {
      if (FailRelease) {
        throw new TimeoutException("the claim store is unreachable");
      }
      lock (_lock) {
        ReleasedKeys.Add(claimKey);
        return Task.FromResult(_claimed.Remove(claimKey));
      }
    }

    internal void RaiseClaimed() => OnClaimed?.Invoke();
  }

  /// <summary>An event whose receptor fails while <see cref="SetFailNext"/> is set, then succeeds.</summary>
  public record PublishOnceFailingEvent([property: StreamId] Guid StreamId) : IEvent;

  private static int _failNext;
  internal static void SetFailNext(bool fail) => Interlocked.Exchange(ref _failNext, fail ? 1 : 0);

  public class PublishOnceFailingEventReceptor : IReceptor<PublishOnceFailingEvent> {
    public ValueTask HandleAsync(PublishOnceFailingEvent message, CancellationToken cancellationToken = default) {
      if (Interlocked.Exchange(ref _failNext, 0) == 1) {
        throw new InvalidOperationException("the publish fails once");
      }
      Interlocked.Increment(ref _fireCount);
      return ValueTask.CompletedTask;
    }
  }

  [Before(Test)]
  public Task ResetCounterAsync() {
    Interlocked.Exchange(ref _fireCount, 0);
    SetFailNext(false);
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

  // ── A failed publish gives its claim back (#1414) ─────────────────────

  [Test]
  public async Task PublishOnceAsync_PublishThrowsOutsideATransaction_ReleasesTheClaimSoARetryPublishesAsync() {
    var store = new InMemoryClaimedEmissionStore();
    var dispatcher = _createDispatcher(store);
    SetFailNext(true);

    await Assert.That(async () => await dispatcher.PublishOnceAsync("order-shipped:1", new PublishOnceFailingEvent(Guid.NewGuid())))
      .ThrowsExactly<InvalidOperationException>()
      .Because("the publish's failure reaches the caller");
    await Assert.That(store.ReleasedKeys).Contains("order-shipped:1")
      .Because("a claim kept after its publish failed would make every retry a duplicate, so the event would never be published");

    var retried = await dispatcher.PublishOnceAsync("order-shipped:1", new PublishOnceFailingEvent(Guid.NewGuid()));

    await Assert.That(retried).IsTrue().Because("the released key can be claimed again");
    await Assert.That(FireCount).IsEqualTo(1).Because("the retry publishes the event");
  }

  [Test]
  public async Task PublishOnceAsync_PublishAndReleaseBothFail_SurfacesBothFailuresAsync() {
    var store = new InMemoryClaimedEmissionStore { FailRelease = true };
    var dispatcher = _createDispatcher(store);
    SetFailNext(true);

    var failure = await Assert.That(async () => await dispatcher.PublishOnceAsync("order-shipped:4", new PublishOnceFailingEvent(Guid.NewGuid())))
      .ThrowsExactly<AggregateException>()
      .Because("neither the publish's failure nor the release's is swallowed");
    await Assert.That(failure!.InnerExceptions.Select(e => e.GetType().Name).ToArray())
      .IsEquivalentTo([nameof(InvalidOperationException), nameof(TimeoutException)]);
  }

  [Test]
  public async Task PublishOnceAsync_PublishSucceeds_KeepsTheClaimAsync() {
    var store = new InMemoryClaimedEmissionStore();
    var dispatcher = _createDispatcher(store);

    await dispatcher.PublishOnceAsync("order-shipped:2", new PublishOnceTestEvent(Guid.NewGuid()));

    await Assert.That(store.ReleasedKeys).IsEmpty().Because("only a failed publish gives its claim back");
  }

  [Test]
  public async Task PublishOnceAsync_PassesTheCallersTokenToThePublishAsync() {
    using var cts = new CancellationTokenSource();
    // The token is canceled once the claim is won, so only a publish that observes the caller's token stops
    var store = new InMemoryClaimedEmissionStore { OnClaimed = cts.Cancel };
    var dispatcher = _createDispatcher(store);

    await Assert.That(async () => await dispatcher.PublishOnceAsync("order-shipped:3", new PublishOnceTestEvent(Guid.NewGuid()), cts.Token))
      .Throws<OperationCanceledException>()
      .Because("the publish runs under the caller's token");
    await Assert.That(FireCount).IsEqualTo(0).Because("a canceled publish runs no handler");
    await Assert.That(store.ReleasedKeys).Contains("order-shipped:3").Because("a canceled publish published nothing, so its claim is given back");
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
  public async Task PublishOnceAsync_CanceledToken_ThrowsAsync() {
    var dispatcher = _createDispatcher(new InMemoryClaimedEmissionStore());
    using var cts = new CancellationTokenSource();
    await cts.CancelAsync();

    await Assert.That(() => dispatcher.PublishOnceAsync("k", new PublishOnceTestEvent(Guid.NewGuid()), cts.Token))
      .ThrowsExactly<OperationCanceledException>();
  }

  // ── Telemetry: claim outcomes recorded as counters ───────────────────

  [Test]
  public async Task PublishOnceAsync_EmitsClaimsWonMetricOnWinAsync() {
    using var factory = new TestMeterFactory();
    var (dispatcher, _) = _createDispatcherWithMetrics(new InMemoryClaimedEmissionStore(), factory);
    using var helper = new MetricAssertionHelper(factory.CreatedMeters[0]);

    var won = await dispatcher.PublishOnceAsync("once:metric-win", new PublishOnceTestEvent(Guid.NewGuid()), CancellationToken.None);
    await Assert.That(won).IsTrue();

    // Passive counters: the untagged series of each always reports (at zero); only a series that
    // counted a claim outcome is a measurement of it.
    var winMeasurements = helper.GetByName("whizbang.dispatcher.publish_once.claims_won").Where(m => m.Value > 0).ToList();
    var lostMeasurements = helper.GetByName("whizbang.dispatcher.publish_once.claims_lost").Where(m => m.Value > 0).ToList();

    await Assert.That(winMeasurements).Count().IsEqualTo(1)
      .Because("Winning the claim records exactly one win measurement; ops dashboards count this to compute the won-vs-lost race rate.");
    await Assert.That(winMeasurements[0].Tags["message_type"]).IsEqualTo(nameof(PublishOnceTestEvent));
    await Assert.That(lostMeasurements).Count().IsEqualTo(0);
  }

  [Test]
  public async Task PublishOnceAsync_EmitsClaimsLostMetricOnLossAsync() {
    using var factory = new TestMeterFactory();
    var store = new InMemoryClaimedEmissionStore();
    var (dispatcher, _) = _createDispatcherWithMetrics(store, factory);
    using var helper = new MetricAssertionHelper(factory.CreatedMeters[0]);

    await dispatcher.PublishOnceAsync("once:metric-loss", new PublishOnceTestEvent(Guid.NewGuid()), CancellationToken.None);
    var second = await dispatcher.PublishOnceAsync("once:metric-loss", new PublishOnceTestEvent(Guid.NewGuid()), CancellationToken.None);
    await Assert.That(second).IsFalse();

    // Passive counters: the untagged series of each always reports (at zero); only a series that
    // counted a claim outcome is a measurement of it.
    var winMeasurements = helper.GetByName("whizbang.dispatcher.publish_once.claims_won").Where(m => m.Value > 0).ToList();
    var lostMeasurements = helper.GetByName("whizbang.dispatcher.publish_once.claims_lost").Where(m => m.Value > 0).ToList();

    await Assert.That(winMeasurements).Count().IsEqualTo(1)
      .Because("First call wins; only one win recorded.");
    await Assert.That(lostMeasurements).Count().IsEqualTo(1)
      .Because("Second call's silent no-op MUST surface as a metric — otherwise an ops dashboard can't distinguish 'no concurrency happening' from 'metric never wired'.");
    await Assert.That(lostMeasurements[0].Tags["message_type"]).IsEqualTo(nameof(PublishOnceTestEvent));
  }

  // ── Helpers ──────────────────────────────────────────────────────────

  private static IDispatcher _createDispatcher(IClaimedEmissionStore? claimStore) {
    var services = new ServiceCollection();
    services.AddSingleton<IServiceInstanceProvider>(new ServiceInstanceProvider(configuration: new ConfigurationBuilder().Build()));
    services.AddReceptors();
    services.AddWhizbangDispatcher();
    if (claimStore is not null) {
      services.AddSingleton(claimStore);
    }
    return services.BuildServiceProvider().GetRequiredService<IDispatcher>();
  }

  private static (IDispatcher Dispatcher, DispatcherMetrics Metrics) _createDispatcherWithMetrics(
      IClaimedEmissionStore claimStore,
      TestMeterFactory factory) {
    var services = new ServiceCollection();
    services.AddSingleton<IServiceInstanceProvider>(new ServiceInstanceProvider(configuration: new ConfigurationBuilder().Build()));
    services.AddReceptors();
    services.AddWhizbangDispatcher();
    services.AddSingleton(claimStore);
    var whizbangMetrics = new WhizbangMetrics(factory);
    var dispatcherMetrics = new DispatcherMetrics(whizbangMetrics);
    services.AddSingleton(whizbangMetrics);
    services.AddSingleton(dispatcherMetrics);
    var sp = services.BuildServiceProvider();
    return (sp.GetRequiredService<IDispatcher>(), dispatcherMetrics);
  }
}
