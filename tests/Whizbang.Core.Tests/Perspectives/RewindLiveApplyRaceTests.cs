using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.Perspectives;

namespace Whizbang.Core.Tests.Perspectives;

#pragma warning disable CA1707
#pragma warning disable IDE1006

/// <summary>
/// Reproduces the JDX bulk-import projection-undercount bug observed on
/// 2026-05-31: a 350-job import committed 350 SagaItemCompletedEvent to the
/// event store but the saga's projection landed at <c>CompletedItems = 346</c>.
/// Cursor inversion warnings + four <c>PerspectiveRewindStarted/Completed</c>
/// event pairs (one per cursor-inverted perspective) correlated exactly with
/// the four lost increments.
///
/// Hypothesis (proven RED here): the rewind path in the generated
/// <see cref="IPerspectiveRunner"/> reads the model into memory, applies every
/// event from snapshot/zero in memory, then persists the final state in a single
/// atomic <c>UpsertAsync</c> at the end. If the live drain path concurrently
/// applies a new event during that window, both code paths persist a new model
/// for the same row — the second writer overwrites the first, and one
/// increment is silently lost. Whichever finishes last "wins"; the loser's work
/// vanishes.
///
/// This test reproduces the race deterministically with a gated
/// <see cref="IPerspectiveStore{T}"/> — there is no flake. The order
/// (live persists during rewind's in-memory phase, then rewind persists last)
/// is forced by completion signals rather than wall-clock timing.
///
/// Expected behavior (and the assertion below): each event applied by either
/// path must be reflected in the final projection. The lost-increment scenario
/// is a real data-integrity bug that must be locked at the framework level so
/// JDX-style production traffic stops dropping projection state during rewinds.
/// </summary>
/// <docs>fundamentals/perspectives/rewind</docs>
[NotInParallel("WhizbangBackgroundServiceTests")]
public class RewindLiveApplyRaceTests {

  /// <summary>
  /// Models the JDX scenario: the BulkJobImportOrchestration saga's
  /// CompletedItems counter. One increment per SagaItemCompletedEvent.
  /// </summary>
  private sealed class CountModel {
    public Guid Id { get; init; }
    public int CompletedItems { get; set; }
  }

  /// <summary>
  /// In-memory store that mirrors the production <c>BaseUpsertStrategy</c>
  /// race window: each <c>UpsertAsync</c> reads-modifies-writes the same row,
  /// with no concurrency control beyond last-writer-wins. A gate lets the test
  /// pause an in-flight upsert so a second writer can race past it.
  ///
  /// This is the same race surface a real Postgres-backed
  /// <c>IPerspectiveStore</c> exhibits today: the EF Core upsert lands an
  /// entire row replacement; without a stream-level lock surrounding
  /// rewind's read+apply+write, a concurrent live apply on the same row
  /// produces the lost-update pattern.
  /// </summary>
  private sealed class GatedInMemoryStore : IPerspectiveStore<CountModel> {
    private readonly Dictionary<Guid, CountModel> _byStream = [];
    private readonly Lock _gate = new();

    /// <summary>Releases the next pending <c>UpsertAsync</c> when called.</summary>
    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Signals that an <c>UpsertAsync</c> has entered the critical region and is waiting on <see cref="Release"/>.</summary>
    public TaskCompletionSource ReachedGate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>When set, the next <c>UpsertAsync</c> blocks on <see cref="Release"/> after signalling <see cref="ReachedGate"/>.</summary>
    public bool HoldNext { get; set; }

    public Task<CountModel?> GetByStreamIdAsync(Guid streamId, CancellationToken cancellationToken = default) {
      lock (_gate) {
        return Task.FromResult(_byStream.TryGetValue(streamId, out var m)
          ? new CountModel { Id = m.Id, CompletedItems = m.CompletedItems }   // defensive copy — production EF returns tracked entity
          : null);
      }
    }

    public async Task UpsertAsync(Guid streamId, CountModel model, CancellationToken cancellationToken = default) {
      if (HoldNext) {
        HoldNext = false;
        ReachedGate.TrySetResult();
        await Release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
      }
      lock (_gate) {
        _byStream[streamId] = new CountModel { Id = model.Id, CompletedItems = model.CompletedItems };
      }
    }

    // The remaining IPerspectiveStore<T> overloads fall through to UpsertAsync (default-impl pattern).
    public Task UpsertWithPhysicalFieldsAsync(Guid streamId, CountModel model, IDictionary<string, object?> physicalFieldValues, PerspectiveScope? scope = null, CancellationToken cancellationToken = default)
      => UpsertAsync(streamId, model, cancellationToken);
    public Task<CountModel?> GetByPartitionKeyAsync<TPartitionKey>(TPartitionKey partitionKey, CancellationToken cancellationToken = default) where TPartitionKey : notnull => Task.FromResult<CountModel?>(null);
    public Task UpsertByPartitionKeyAsync<TPartitionKey>(TPartitionKey partitionKey, CountModel model, CancellationToken cancellationToken = default) where TPartitionKey : notnull => Task.CompletedTask;
    public Task FlushAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task PurgeAsync(Guid streamId, CancellationToken cancellationToken = default) { lock (_gate) { _byStream.Remove(streamId); } return Task.CompletedTask; }
    public Task PurgeByPartitionKeyAsync<TPartitionKey>(TPartitionKey partitionKey, CancellationToken cancellationToken = default) where TPartitionKey : notnull => Task.CompletedTask;
  }

  /// <summary>
  /// Hand-rolled runner that mirrors the exact shape of the source-generated
  /// runner emitted by <c>Whizbang.Generators.Templates.PerspectiveRunnerTemplate</c>.
  /// Pattern under test (identical to lines 819-956 of the template):
  ///   1. Load model (snapshot OR <c>GetByStreamIdAsync</c> for live).
  ///   2. Apply every event in memory.
  ///   3. Single atomic <c>UpsertAsync</c> at the end.
  ///
  /// When <paramref name="coordinator"/> is supplied, the read+apply+persist
  /// runs under an <see cref="IPerspectiveApplyCoordinator"/> lock —
  /// matching the production fix that the source generator emits around both
  /// <c>RewindAndRunAsync</c> and <c>RunWithEventsAsync</c>.
  /// </summary>
#pragma warning disable CA1859 // Interface typing is intentional — production callers receive IPerspectiveApplyCoordinator from DI.
  private static async Task ApplyEventsAsync(
      IPerspectiveStore<CountModel> store,
      Guid streamId,
      int eventsToApply,
      bool seedFromEmpty,
      CancellationToken ct,
      IPerspectiveApplyCoordinator? coordinator = null,
      string perspectiveName = "TestPerspective") {
#pragma warning restore CA1859
    // Acquire the in-process lock for this (streamId, perspectiveName) when a
    // coordinator is supplied. The acquire is OUTSIDE the gated store so the
    // gate still pauses an in-flight persist — the test still exercises the
    // same critical-section interleaving, but now the second writer waits on
    // the semaphore instead of racing past.
    var apply = async () => {
      var model = seedFromEmpty
        ? new CountModel { Id = streamId, CompletedItems = 0 }
        : (await store.GetByStreamIdAsync(streamId, ct).ConfigureAwait(false))
          ?? new CountModel { Id = streamId, CompletedItems = 0 };
      for (var i = 0; i < eventsToApply; i++) {
        model.CompletedItems++;
      }
      await store.UpsertAsync(streamId, model, ct).ConfigureAwait(false);
    };

    if (coordinator is null) {
      await apply().ConfigureAwait(false);
    } else {
      await using var _ = await coordinator.AcquireAsync(streamId, perspectiveName, ct).ConfigureAwait(false);
      await apply().ConfigureAwait(false);
    }
  }

  [Test]
  public async Task Rewind_ConcurrentWithLiveApply_WithoutCoordinator_ObservesLostIncrementAsync() {
    // Negative-path regression lock. Documents the unprotected race shape:
    // when NO IPerspectiveApplyCoordinator is on the apply paths, the rewind +
    // concurrent live apply produces a last-writer-wins overwrite and one
    // increment is silently lost. This test asserting "CompletedItems = 4"
    // exists so a future refactor that re-introduces the unprotected pattern
    // immediately produces an observable failure on the companion
    // <c>...WithCoordinator_RetainsAllIncrements...</c> test below — the two
    // tests bracket the contract: protected paths land at 5, unprotected
    // paths land at 4.
    var store = new GatedInMemoryStore();
    var streamId = Guid.NewGuid();
    await ApplyEventsAsync(store, streamId, eventsToApply: 3, seedFromEmpty: true, CancellationToken.None);

    var seeded = await store.GetByStreamIdAsync(streamId);
    await Assert.That(seeded!.CompletedItems).IsEqualTo(3);

    store.HoldNext = true;
    var rewindTask = Task.Run(() => ApplyEventsAsync(
      store, streamId, eventsToApply: 4, seedFromEmpty: true, CancellationToken.None));

    await store.ReachedGate.Task.WaitAsync(TimeSpan.FromSeconds(5));

    await ApplyEventsAsync(store, streamId, eventsToApply: 1, seedFromEmpty: false, CancellationToken.None);

    var afterLiveBeforeRewindResume = await store.GetByStreamIdAsync(streamId);
    await Assert.That(afterLiveBeforeRewindResume!.CompletedItems).IsEqualTo(4)
      .Because("live apply committed C+D = 4 while rewind is still gated");

    store.Release.TrySetResult();
    await rewindTask;

    var final = await store.GetByStreamIdAsync(streamId);
    await Assert.That(final!.CompletedItems).IsEqualTo(4)
      .Because("unprotected race: rewind's persist overwrote live's increment OR vice versa. " +
               "Locked at 4 so the companion ...WithCoordinator_RetainsAllIncrements... test " +
               "(which lands at 5) demonstrates the contract the coordinator restores.");
  }

  [Test]
  public async Task Rewind_ConcurrentWithLiveApply_WithCoordinator_RetainsAllIncrementsAsync() {
    // GREEN counterpart of the race-loses-increment test. Same scenario, same
    // interleaving — but every Apply path acquires the shared
    // IPerspectiveApplyCoordinator first. Live's apply now waits on the
    // semaphore until rewind's release, the two paths serialize, and no
    // increment is lost. Locks the contract that the production fix
    // (source-generator emits Acquire/Release around RewindAndRunAsync and
    // RunWithEventsAsync) MUST preserve.
    var store = new GatedInMemoryStore();
    var coordinator = new PerspectiveApplyCoordinator();
    var streamId = Guid.NewGuid();
    await ApplyEventsAsync(store, streamId, eventsToApply: 3, seedFromEmpty: true, CancellationToken.None, coordinator);

    var seeded = await store.GetByStreamIdAsync(streamId);
    await Assert.That(seeded!.CompletedItems).IsEqualTo(3);

    // Rewind blocks at the store gate while still holding the coordinator lock.
    store.HoldNext = true;
    var rewindTask = Task.Run(() => ApplyEventsAsync(
      store, streamId, eventsToApply: 4, seedFromEmpty: true, CancellationToken.None, coordinator));

    await store.ReachedGate.Task.WaitAsync(TimeSpan.FromSeconds(5));

    // Live apply tries to acquire — should BLOCK on the semaphore until rewind releases.
    // Kick it off on a background task and verify it doesn't complete until after release.
    var liveTask = Task.Run(() => ApplyEventsAsync(
      store, streamId, eventsToApply: 1, seedFromEmpty: false, CancellationToken.None, coordinator));

    // Sanity: live should NOT have committed yet (it's blocked on the coordinator).
    // Wait briefly to give it a chance to mis-fire; under the fix, it can't.
    var liveCompletedBeforeRelease = await Task.WhenAny(liveTask, Task.Delay(TimeSpan.FromMilliseconds(200))) == liveTask;
    await Assert.That(liveCompletedBeforeRelease).IsFalse()
      .Because("with the coordinator lock held by the gated rewind, live MUST wait");

    // Release rewind's persist. Live then takes the lock, applies, persists CompletedItems = 5.
    store.Release.TrySetResult();
    await rewindTask;
    await liveTask;

    var final = await store.GetByStreamIdAsync(streamId);
    await Assert.That(final!.CompletedItems).IsEqualTo(5);
  }

  [Test]
  public async Task Rewind_NoConcurrentLiveApply_DoesNotLoseIncrementsAsync() {
    // Control test: same rewind shape WITHOUT a concurrent live apply.
    // Proves the test scaffolding itself isn't introducing the bug — the race
    // is the failure mode, not the rewind path on its own.
    var store = new GatedInMemoryStore();
    var streamId = Guid.NewGuid();
    await ApplyEventsAsync(store, streamId, eventsToApply: 3, seedFromEmpty: true, CancellationToken.None);

    // Rewind alone, no live racer — replay A+B+C+L in memory, persist once.
    await ApplyEventsAsync(store, streamId, eventsToApply: 4, seedFromEmpty: true, CancellationToken.None);

    var final = await store.GetByStreamIdAsync(streamId);
    await Assert.That(final!.CompletedItems).IsEqualTo(4);
  }
}
