using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

#pragma warning disable CA1707
#pragma warning disable IDE1006

/// <summary>
/// Reproduces the a consumer bulk-import "5 lost events with 0 rewinds" symptom on a cold
/// in-memory cursor cache. Before this fix: <see cref="PerspectiveCursorCache.TryGet"/>
/// returns false on a cold lookup. The inversion detector in
/// <c>PerspectiveWorker._resolveInversionAnchor</c> then sees a null cursor, can't
/// compare incoming events against any cursor, and forwards everything to
/// <c>RunWithEventsAsync</c>. The runner's own idempotency filter then reads the
/// persisted <c>metadata.EventId</c> (which IS current) and drops any event whose
/// id is lexically less than it. The drop is silent — <c>wh_perspective_events
/// .processed_at</c> gets stamped and the event_work_id is gone forever.
///
/// The two checks (inversion detector + runner filter) use different truth
/// sources: the detector consults the in-memory cache; the filter consults the
/// persisted metadata. When the cache is cold and persisted metadata is ahead,
/// they disagree — the detector misses the inversion that the filter would
/// otherwise reject as "already applied", so no rewind fires, the late event is
/// dropped, and the projection silently undercounts.
///
/// The fix: a single <see cref="IPerspectiveCursorResolver"/> that callers use
/// to obtain the current cursor. On a cold cache miss, the resolver reads
/// <see cref="IWorkCoordinator.GetPerspectiveCursorAsync"/> (which carries
/// the persisted <c>last_event_id</c> + <c>last_commit_sequence</c> from
/// <c>wh_perspective_cursors</c>) and warms the cache. Inversion detector
/// and runner filter now see the same cursor; late events trigger rewinds
/// instead of being silently dropped.
/// </summary>
/// <docs>fundamentals/perspectives/cursor-inversion</docs>
public class PerspectiveCursorResolverTests {

  private const string PERSPECTIVE_NAME = "a consumer.Test.SagaProjection";

  [Test]
  public async Task ColdCache_PersistedCursorPresent_ResolverReturnsPersistedValuesAsync() {
    // Cold cache + a persisted cursor in the coordinator. The resolver MUST
    // surface the persisted values so the inversion detector and the runner
    // filter look at the same cursor. Today's worker calls
    // _cursorCache.TryGet directly and would see null on this scenario,
    // missing every late event.
    var streamId = Guid.Parse("019e8047-2512-75e7-afd2-210f5a42c3d0");
    var persistedEventId = Guid.Parse("019e8048-7777-7777-7777-777777777777");
    long persistedCommitSeq = 571800;

    var cache = new PerspectiveCursorCache();
    var coordinator = new _StubCoordinator {
      CursorByKey = {
        [(streamId, PERSPECTIVE_NAME)] = new PerspectiveCursorInfo {
          StreamId = streamId,
          PerspectiveName = PERSPECTIVE_NAME,
          LastEventId = persistedEventId,
          LastCommitSequence = persistedCommitSeq,
          Status = PerspectiveProcessingStatus.Completed,
        },
      },
    };

    var resolver = new PerspectiveCursorResolver(cache, coordinator, NullLogger<PerspectiveCursorResolver>.Instance);
    var (eventId, commitSeq) = await resolver.GetAsync(streamId, PERSPECTIVE_NAME);

    await Assert.That(eventId).IsEqualTo(persistedEventId);
    await Assert.That(commitSeq).IsEqualTo(persistedCommitSeq);
  }

  [Test]
  public async Task ColdCache_PersistedCursorPresent_ResolverWarmsCacheAsync() {
    // Side-effect lock: once the resolver has populated the cache from
    // persisted state, follow-on TryGet calls hit the cache directly. The
    // coordinator must NOT be called again for the same (stream, perspective)
    // until invalidation.
    var streamId = Guid.NewGuid();
    var persistedEventId = Guid.Parse("019e8048-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    long persistedCommitSeq = 100;

    var cache = new PerspectiveCursorCache();
    var coordinator = new _StubCoordinator {
      CursorByKey = {
        [(streamId, PERSPECTIVE_NAME)] = new PerspectiveCursorInfo {
          StreamId = streamId,
          PerspectiveName = PERSPECTIVE_NAME,
          LastEventId = persistedEventId,
          LastCommitSequence = persistedCommitSeq,
          Status = PerspectiveProcessingStatus.Completed,
        },
      },
    };

    var resolver = new PerspectiveCursorResolver(cache, coordinator, NullLogger<PerspectiveCursorResolver>.Instance);
    _ = await resolver.GetAsync(streamId, PERSPECTIVE_NAME);
    var coordinatorCallsAfterFirst = coordinator.GetCursorCallCount;

    _ = await resolver.GetAsync(streamId, PERSPECTIVE_NAME);
    var coordinatorCallsAfterSecond = coordinator.GetCursorCallCount;

    await Assert.That(coordinatorCallsAfterFirst).IsEqualTo(1);
    await Assert.That(coordinatorCallsAfterSecond).IsEqualTo(1)
      .Because("second lookup must hit the cache without re-querying the coordinator");

    // And the cache reflects what was warmed.
    var cacheHit = cache.TryGet(streamId, PERSPECTIVE_NAME, out var cachedEventId);
    await Assert.That(cacheHit).IsTrue();
    await Assert.That(cachedEventId).IsEqualTo(persistedEventId);
    var seqHit = cache.TryGetCommitSequence(streamId, PERSPECTIVE_NAME, out var cachedSeq);
    await Assert.That(seqHit).IsTrue();
    await Assert.That(cachedSeq).IsEqualTo(persistedCommitSeq);
  }

  [Test]
  public async Task WarmCache_ResolverReturnsCachedValues_DoesNotCallCoordinatorAsync() {
    // Existing in-memory cache value MUST be returned without a coordinator
    // round trip. Locks the perf-critical hot path: warm caches stay free.
    var streamId = Guid.NewGuid();
    var cachedEventId = Guid.Parse("019e8048-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    long cachedSeq = 200;

    var cache = new PerspectiveCursorCache();
    cache.Set(streamId, PERSPECTIVE_NAME, cachedEventId);
    cache.SetCommitSequence(streamId, PERSPECTIVE_NAME, cachedSeq);

    var coordinator = new _StubCoordinator();
    var resolver = new PerspectiveCursorResolver(cache, coordinator, NullLogger<PerspectiveCursorResolver>.Instance);

    var (eventId, commitSeq) = await resolver.GetAsync(streamId, PERSPECTIVE_NAME);

    await Assert.That(eventId).IsEqualTo(cachedEventId);
    await Assert.That(commitSeq).IsEqualTo(cachedSeq);
    await Assert.That(coordinator.GetCursorCallCount).IsEqualTo(0)
      .Because("warm cache must not trigger coordinator I/O");
  }

  [Test]
  public async Task ColdCache_NoPersistedCursor_ResolverReturnsNullsAsync() {
    // First-ever scan for a stream/perspective: coordinator returns null
    // (no row in wh_perspective_cursors). Resolver returns (null, null) and
    // does NOT warm the cache with phantom values — a future Set call from
    // a real apply path must remain the source of truth.
    var streamId = Guid.NewGuid();
    var cache = new PerspectiveCursorCache();
    var coordinator = new _StubCoordinator { /* nothing configured */ };
    var resolver = new PerspectiveCursorResolver(cache, coordinator, NullLogger<PerspectiveCursorResolver>.Instance);

    var (eventId, commitSeq) = await resolver.GetAsync(streamId, PERSPECTIVE_NAME);

    await Assert.That(eventId).IsNull();
    await Assert.That(commitSeq).IsNull();
    await Assert.That(cache.TryGet(streamId, PERSPECTIVE_NAME, out _)).IsFalse()
      .Because("absent persisted cursor must not poison the cache");
  }

  /// <summary>
  /// Stub <see cref="IWorkCoordinator"/> that returns canned cursor info for
  /// known (stream, perspective) pairs and tracks call count for the
  /// warm-cache assertion.
  /// </summary>
  private sealed class _StubCoordinator : IWorkCoordinator {
    public Dictionary<(Guid, string), PerspectiveCursorInfo> CursorByKey { get; } = [];
    public int GetCursorCallCount;

    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string name, CancellationToken ct = default) {
      Interlocked.Increment(ref GetCursorCallCount);
      return Task.FromResult<PerspectiveCursorInfo?>(
        CursorByKey.TryGetValue((streamId, name), out var info) ? info : null);
    }

    // Minimal stubs for the rest of the interface (default-impl methods + a couple of required ones).
    public Task<WorkBatch> ProcessWorkBatchAsync(ProcessWorkBatchRequest request, CancellationToken ct = default) =>
      Task.FromResult(new WorkBatch { OutboxWork = [], InboxWork = [], PerspectiveWork = [] });
    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion c, CancellationToken ct = default) => Task.CompletedTask;
    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure f, CancellationToken ct = default) => Task.CompletedTask;
    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount = 2, CancellationToken ct = default) => Task.CompletedTask;
    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken ct = default) => Task.FromResult(new WorkCoordinatorStatistics());
    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken ct = default) => Task.CompletedTask;
  }
}
