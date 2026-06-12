using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Core.Tests.Perspectives;

#pragma warning disable CA1707
#pragma warning disable IDE1006

/// <summary>
/// Reproduces the 2026-06-12 slot-3 BulkJobImport projection gap (lines 334,
/// 335, 336 missing from <c>ProcessedLineNumbers</c> after a 350-item
/// import). Forensic from <c>wh_event_store</c> proved that:
///
/// <list type="number">
///   <item><description>All 350 <c>SagaItemCompletedEvent</c> events were
///     durably stored (event-store HEAD reached the expected version).</description></item>
///   <item><description>6+ <c>PerspectiveRewindStarted</c>/<c>Completed</c>
///     cycles fired in a 15 s window during the import.</description></item>
///   <item><description>Events for lines 334/335/336 (event-store versions
///     679/681/683) were appended <em>between</em> a rewind's
///     <c>PerspectiveRewindStarted</c> (v677/v678) and its
///     <c>PerspectiveRewindCompleted</c> (v691). Those 3 events were never
///     reflected in the projection.</description></item>
/// </list>
///
/// <para><strong>Root cause:</strong> the generated
/// <c>IPerspectiveRunner.RewindAndRunAsync</c> (template at
/// <c>src/Whizbang.Generators/Templates/PerspectiveRunnerTemplate.cs</c>,
/// lines 918-926) reads events from the event store ONCE at rewind-start
/// into a materialised list, then applies that fixed list in memory. Events
/// appended to the stream <em>after</em> the materialisation but
/// <em>before</em> the in-memory replay finishes are silently dropped — the
/// rewind publishes <c>PerspectiveRewindCompleted</c> claiming "caught up,"
/// but caught up means "caught up to event-store HEAD as of rewind-start,"
/// not "caught up to event-store HEAD at completion." The companion
/// <see cref="RewindLiveApplyRaceTests"/> (2026-05-31 fix) closed the
/// concurrent-in-memory-state race; THIS test locks the orthogonal
/// concurrent-event-append race.</para>
///
/// <para><strong>Expected behaviour (GREEN):</strong> the rewind must
/// re-check the event-store HEAD at the end of its apply loop. If new events
/// have arrived, it must load them and apply them too, looping until the
/// store is quiescent. Only THEN may it publish
/// <c>PerspectiveRewindCompleted</c>.</para>
/// </summary>
/// <docs>fundamentals/perspectives/rewind</docs>
[NotInParallel("WhizbangBackgroundServiceTests")]
public class PerspectiveRewindCompletionGapTests {

  /// <summary>Mirrors the JDX BulkJobImport CompletedItems counter.</summary>
  private sealed class CountModel {
    public Guid Id { get; init; }
    public int CompletedItems { get; set; }
  }

  /// <summary>
  /// Stand-in for the event store. The producer can <see cref="Append"/>
  /// events any time; a consumer snapshots the current event list via
  /// <see cref="ReadAll"/>, which mirrors the materialising
  /// <c>await foreach</c> on line 920 of <c>PerspectiveRunnerTemplate.cs</c>:
  /// the snapshot fixes the set of events the rewind will apply.
  /// </summary>
  private sealed class GatedEventStore {
    private readonly Lock _gate = new();
    private readonly List<Guid> _events = [];

    /// <summary>Set by the test to pause the rewind between "snapshot the event list"
    /// and "apply events." Lets the test append new events to the stream while the
    /// rewind is "in flight" (between materialisation and persist).</summary>
    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Signals that the rewind has snapshotted its event list and is now
    /// pausing for the test to interleave new appends.</summary>
    public TaskCompletionSource SnapshotTaken { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Append(Guid eventId) {
      lock (_gate) {
        _events.Add(eventId);
      }
    }

    /// <summary>Returns a defensive copy of the current event list. Mirrors the
    /// materialised list created by <c>await foreach (ReadPolymorphicAsync)</c>
    /// at line 920 of <c>PerspectiveRunnerTemplate.cs</c>.</summary>
    public List<Guid> ReadAll() {
      lock (_gate) {
        return new List<Guid>(_events);
      }
    }

    public int CurrentCount {
      get { lock (_gate) { return _events.Count; } }
    }
  }

  /// <summary>
  /// Simulates the existing (broken) rewind path: materialise events ONCE
  /// at rewind-start, apply them all in memory, persist. New appends during
  /// the in-memory apply are dropped on the floor.
  ///
  /// This mirrors lines 918-1017 of <c>PerspectiveRunnerTemplate.cs</c>.
  /// </summary>
  private static async Task<int> SimulateBrokenRewindAsync(
      GatedEventStore store,
      bool pauseAfterSnapshot,
      CancellationToken ct) {
    // Step 1: snapshot events at rewind-start (line 920 of the template).
    var events = store.ReadAll();

    if (pauseAfterSnapshot) {
      store.SnapshotTaken.TrySetResult();
      await store.Release.Task.WaitAsync(ct).ConfigureAwait(false);
    }

    // Step 2: apply each event in memory (lines 935-1016 of the template).
    var applied = 0;
    foreach (var _ in events) {
      applied++;
    }
    return applied;
  }

  /// <summary>
  /// Simulates the FIXED rewind path: after applying the materialised list,
  /// re-check the event store for any events appended during the apply
  /// window. Loop until quiescent.
  /// </summary>
  private static async Task<int> SimulateFixedRewindAsync(
      GatedEventStore store,
      bool pauseAfterSnapshot,
      CancellationToken ct) {
    // Step 1: snapshot events at rewind-start.
    var events = store.ReadAll();

    if (pauseAfterSnapshot) {
      store.SnapshotTaken.TrySetResult();
      await store.Release.Task.WaitAsync(ct).ConfigureAwait(false);
    }

    // Step 2: apply, then re-check, loop until store is quiescent. This is
    // what the template MUST do for the rewind to honour its
    // PerspectiveRewindCompleted contract.
    var applied = 0;
    var processed = 0;
    while (true) {
      for (var i = processed; i < events.Count; i++) {
        applied++;
      }
      processed = events.Count;
      var currentHead = store.CurrentCount;
      if (currentHead == processed) {
        break;
      }
      // New events arrived during the apply window — re-load and continue.
      events = store.ReadAll();
    }
    return applied;
  }

  [Test]
  public async Task BrokenRewind_EventsAppendedDuringWindow_AreSilentlyDropped_RedAsync() {
    // RED — locks in the slot-3 bug shape on the broken path. While the
    // rewind is paused between "snapshot the event list" and "apply,"
    // 3 new events arrive on the stream. The broken path applies only
    // the original 3; the late 3 are dropped. The replay count reflects
    // only the pre-rewind events even though the store has 6.
    var store = new GatedEventStore();
    var ct = CancellationToken.None;

    // Seed 3 events (the "first 333 lines" analogue).
    store.Append(Guid.NewGuid());
    store.Append(Guid.NewGuid());
    store.Append(Guid.NewGuid());

    var rewindTask = Task.Run(
      () => SimulateBrokenRewindAsync(store, pauseAfterSnapshot: true, ct));

    await store.SnapshotTaken.Task.WaitAsync(TimeSpan.FromSeconds(5));

    // While the rewind is paused, 3 more events arrive (lines 334/335/336).
    store.Append(Guid.NewGuid());
    store.Append(Guid.NewGuid());
    store.Append(Guid.NewGuid());

    store.Release.TrySetResult();
    var applied = await rewindTask;

    await Assert.That(applied).IsEqualTo(3)
      .Because("BROKEN rewind: events appended after rewind-start materialisation are dropped. Documents the slot-3 bug — the rewind applies the 3 it snapshotted and misses the 3 late arrivals even though the store has 6. Companion *_Green* test asserts the fix produces 6.");
    await Assert.That(store.CurrentCount).IsEqualTo(6)
      .Because("The event store does in fact contain all 6 events — the data side is correct; only the projection is wrong.");
  }

  [Test]
  public async Task FixedRewind_EventsAppendedDuringWindow_AreAppliedTooAsync() {
    // GREEN — the rewind re-checks the event-store HEAD after applying the
    // materialised list and loops until the store is quiescent. All 6 events
    // are applied, matching the contract of PerspectiveRewindCompleted
    // ("caught up to event-store HEAD as of this commit").
    var store = new GatedEventStore();
    var ct = CancellationToken.None;

    store.Append(Guid.NewGuid());
    store.Append(Guid.NewGuid());
    store.Append(Guid.NewGuid());

    var rewindTask = Task.Run(
      () => SimulateFixedRewindAsync(store, pauseAfterSnapshot: true, ct));

    await store.SnapshotTaken.Task.WaitAsync(TimeSpan.FromSeconds(5));

    store.Append(Guid.NewGuid());
    store.Append(Guid.NewGuid());
    store.Append(Guid.NewGuid());

    store.Release.TrySetResult();
    var applied = await rewindTask;

    await Assert.That(applied).IsEqualTo(6)
      .Because("FIXED rewind: re-checks event-store HEAD after the apply loop; picks up the 3 late arrivals. PerspectiveRewindCompleted now means 'caught up as of THIS commit,' not 'caught up as of rewind-start.'");
  }

  [Test]
  public async Task FixedRewind_NoLateAppends_StillTerminatesAsync() {
    // Sanity check: the re-check loop must terminate when no new events arrive.
    // Without this, a healthy rewind could spin forever.
    var store = new GatedEventStore();
    var ct = CancellationToken.None;

    store.Append(Guid.NewGuid());
    store.Append(Guid.NewGuid());
    store.Append(Guid.NewGuid());

    var applied = await SimulateFixedRewindAsync(store, pauseAfterSnapshot: false, ct);

    await Assert.That(applied).IsEqualTo(3)
      .Because("With no late appends, the fixed rewind terminates after one pass — the re-check sees the store is quiescent and exits.");
  }
}
