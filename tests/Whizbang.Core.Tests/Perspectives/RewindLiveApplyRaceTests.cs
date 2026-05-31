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
/// Reproduces the a consumer bulk-import projection-undercount bug observed on
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
/// a consumer-style production traffic stops dropping projection state during rewinds.
/// </summary>
/// <docs>fundamentals/perspectives/rewind</docs>
[NotInParallel("WhizbangBackgroundServiceTests")]
public class RewindLiveApplyRaceTests {

  /// <summary>
  /// Models the a consumer scenario: the BulkImportOrchestration saga's
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
  /// No locking, no compare-and-swap, no read-version check.
  /// </summary>
  private static async Task ApplyEventsAsync(
      IPerspectiveStore<CountModel> store,
      Guid streamId,
      int eventsToApply,
      bool seedFromEmpty,
      CancellationToken ct) {
    // Live path: read existing → apply events → upsert.
    // Rewind path: seed from empty (snapshot-less full replay) → apply events → upsert.
    var model = seedFromEmpty
      ? new CountModel { Id = streamId, CompletedItems = 0 }
      : (await store.GetByStreamIdAsync(streamId, ct).ConfigureAwait(false))
        ?? new CountModel { Id = streamId, CompletedItems = 0 };
    for (var i = 0; i < eventsToApply; i++) {
      model.CompletedItems++;
    }
    await store.UpsertAsync(streamId, model, ct).ConfigureAwait(false);
  }

  [Test]
  public async Task Rewind_ConcurrentWithLiveApply_LosesOneIncrementAsync() {
    // Seed: live applied A, B, C → CompletedItems = 3. (cursor would be at C.)
    var store = new GatedInMemoryStore();
    var streamId = Guid.NewGuid();
    await ApplyEventsAsync(store, streamId, eventsToApply: 3, seedFromEmpty: true, CancellationToken.None);

    var seeded = await store.GetByStreamIdAsync(streamId);
    await Assert.That(seeded!.CompletedItems).IsEqualTo(3);

    // Late event L arrives (would trigger cursor inversion + rewind in production).
    // Rewind reads from snapshot/zero, replays [A, B, C, L] in memory → 4 increments.
    // Hold the rewind's final UpsertAsync at the gate so the live path can race past.
    store.HoldNext = true;
    var rewindTask = Task.Run(() => ApplyEventsAsync(
      store, streamId,
      eventsToApply: 4,           // A + B + C + L = 4 increments from empty
      seedFromEmpty: true,        // rewind starts from empty/snapshot
      CancellationToken.None));

    // Wait until the rewind is at the gate, mid-flight.
    await store.ReachedGate.Task.WaitAsync(TimeSpan.FromSeconds(5));

    // Live drain applies a fresh event D arriving on the side: reads current row (3) → applies 1 → writes 4.
    await ApplyEventsAsync(store, streamId, eventsToApply: 1, seedFromEmpty: false, CancellationToken.None);

    var afterLiveBeforeRewindResume = await store.GetByStreamIdAsync(streamId);
    await Assert.That(afterLiveBeforeRewindResume!.CompletedItems).IsEqualTo(4)
      .Because("live apply committed C+D = 4 while rewind is still gated");

    // Release the rewind — it writes its in-memory state (CompletedItems = 4, from A+B+C+L) on top.
    store.Release.TrySetResult();
    await rewindTask;

    // Total events ever applied: A, B, C, L (via rewind) + D (via live) = 5 unique increments.
    // Expected behavior: final CompletedItems = 5 (every event contributes to the projection).
    // Today's bug: 4. Rewind's persist clobbered live's increment OR vice versa — last-writer-wins
    // means one path's work is lost.
    var final = await store.GetByStreamIdAsync(streamId);
    await Assert.That(final!.CompletedItems).IsEqualTo(5)
      .Because("rewind applied 4 events, live applied 1, every applied event must contribute to the projection — " +
               "if this assertion fails at 4, the framework lost an increment to a last-writer-wins race");
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
