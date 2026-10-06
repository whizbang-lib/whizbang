// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Diagnostics;
using Whizbang.Core.Messaging;
using Whizbang.Core.Perspectives.Sync;
using Whizbang.Core.Tests.Workers;

namespace Whizbang.Core.Tests.Perspectives.Sync;

/// <summary>
/// Branch coverage for <see cref="PerspectiveSyncAwaiter"/>: the sync span's tags on every exit of
/// <see cref="PerspectiveSyncAwaiter.WaitAsync"/> and on the applied-event wait (which only exist when a
/// tracing listener samples <c>Whizbang.Tracing</c>), and the debug-level stream-wait log that renders the
/// requested event types both when a caller passes a type filter and when it passes none.
/// </summary>
/// <remarks>
/// Every span assertion is filtered to the trace of a parent activity this test starts, so spans
/// produced by other tests running in parallel can never satisfy (or spoil) an assertion here.
/// </remarks>
/// <code-under-test>src/Whizbang.Core/Perspectives/Sync/PerspectiveSyncAwaiter.cs</code-under-test>
public class PerspectiveSyncAwaiterBranchCoverageTests {
  private const string TRACING_SOURCE = "Whizbang.Tracing";
  private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(30);

  private sealed class BranchPerspective;
  private sealed class BranchProbeEvent;

  // ==========================================================================
  // WaitAsync: span tags on each exit
  // ==========================================================================

  [Test]
  public async Task WaitAsync_NoEmittedEvents_WithListener_TagsSpanNoPendingEventsAsync() {
    using var capture = new SpanCapture();
    var awaiter = _awaiter(new ScopedEventTracker(), new DecidingSyncEventTracker(answer: true));
    var options = SyncFilter.All().WithTimeout(TimeSpan.FromSeconds(7)).Build();

    var result = await awaiter.WaitAsync(typeof(BranchPerspective), options);

    await Assert.That(result.Outcome).IsEqualTo(SyncOutcome.NoPendingEvents);
    var span = capture.Single("PerspectiveSync BranchPerspective");
    await Assert.That(span.GetTagItem("whizbang.sync.perspective"))
      .IsEqualTo(TypeNameFormatter.DisplayName(typeof(BranchPerspective)))
      .Because("the span names the perspective a caller blocked on, or a trace cannot say what it waited for");
    await Assert.That((double?)span.GetTagItem("whizbang.sync.timeout_ms")).IsEqualTo(7000d);
    await Assert.That(span.GetTagItem("whizbang.sync.outcome")).IsEqualTo("NoPendingEvents");
    await Assert.That((int?)span.GetTagItem("whizbang.sync.event_count")).IsEqualTo(0);
  }

  [Test]
  public async Task WaitAsync_EventsReportedButNoInquiries_WithListener_TagsSpanNoPendingEventsAsync() {
    using var capture = new SpanCapture();
    var syncTracker = new DecidingSyncEventTracker(answer: true);
    var awaiter = _awaiter(new CountDisagreesScopedEventTracker(), syncTracker);

    var result = await awaiter.WaitAsync(typeof(BranchPerspective), SyncFilter.All().Build());

    await Assert.That(result.Outcome).IsEqualTo(SyncOutcome.NoPendingEvents);
    await Assert.That(result.EventsEmitted).IsEqualTo(1);
    await Assert.That(syncTracker.WaitCalls).IsEqualTo(0)
      .Because("with nothing to wait for the awaiter must return before the event-driven wait");
    var span = capture.Single("PerspectiveSync BranchPerspective");
    await Assert.That(span.GetTagItem("whizbang.sync.outcome")).IsEqualTo("NoPendingEvents");
    await Assert.That((int?)span.GetTagItem("whizbang.sync.event_count")).IsEqualTo(0)
      .Because("zero inquiries means zero events were actually awaited, whatever the tracker's count said");
  }

  [Test]
  public async Task WaitAsync_EventsProcessed_WithListenerAndDebugLogger_TagsSpanSyncedAndLogsEachStreamAsync() {
    using var capture = new SpanCapture();
    var scoped = new ScopedEventTracker();
    var firstStream = Guid.CreateVersion7();
    var secondStream = Guid.CreateVersion7();
    var firstEvent = Guid.CreateVersion7();
    var secondEvent = Guid.CreateVersion7();
    scoped.TrackEmittedEvent(firstStream, typeof(BranchProbeEvent), firstEvent);
    scoped.TrackEmittedEvent(secondStream, typeof(BranchProbeEvent), secondEvent);
    var syncTracker = new DecidingSyncEventTracker(answer: true);
    var logger = new FakeLogger<PerspectiveSyncAwaiter>();
    var awaiter = _awaiter(scoped, syncTracker, logger);
    var perspectiveName = TypeNameFormatter.GetPerspectiveName(typeof(BranchPerspective));

    var result = await awaiter.WaitAsync(typeof(BranchPerspective), SyncFilter.All().Build());

    await Assert.That(result.Outcome).IsEqualTo(SyncOutcome.Synced);
    await Assert.That(result.EventsAwaited).IsEqualTo(2);
    await Assert.That(syncTracker.LastPerspectiveName).IsEqualTo(perspectiveName);
    await Assert.That(syncTracker.LastEventIds).IsEquivalentTo(new[] { firstEvent, secondEvent });

    var span = capture.Single("PerspectiveSync BranchPerspective");
    await Assert.That(span.GetTagItem("whizbang.sync.outcome")).IsEqualTo("Synced");
    await Assert.That((int?)span.GetTagItem("whizbang.sync.event_count")).IsEqualTo(2);
    await Assert.That((int?)span.GetTagItem("whizbang.sync.stream_count")).IsEqualTo(2)
      .Because("the two events sit on two streams, so two inquiries were built");
    await Assert.That(span.GetTagItem("whizbang.sync.elapsed_ms") is double).IsTrue()
      .Because("a synced wait records how long the caller was blocked");

    var waiting = logger.Collector.GetSnapshot()
      .Where(r => r.Message.Contains("[SYNC_DEBUG] WaitAsync: Waiting for", StringComparison.Ordinal))
      .Select(r => r.Message)
      .ToList();
    await Assert.That(waiting).Count().IsEqualTo(2)
      .Because("debug logging writes one line per stream inquiry");
    await Assert.That(waiting.Any(m => m.Contains(firstEvent.ToString(), StringComparison.Ordinal)
                                    && m.Contains(perspectiveName, StringComparison.Ordinal))).IsTrue();
    await Assert.That(waiting.Any(m => m.Contains(secondEvent.ToString(), StringComparison.Ordinal))).IsTrue();
  }

  [Test]
  public async Task WaitAsync_EventsNeverProcessed_WithListener_TagsSpanTimedOutAsync() {
    using var capture = new SpanCapture();
    var scoped = new ScopedEventTracker();
    scoped.TrackEmittedEvent(Guid.CreateVersion7(), typeof(BranchProbeEvent), Guid.CreateVersion7());
    var awaiter = _awaiter(scoped, new DecidingSyncEventTracker(answer: false));

    var result = await awaiter.WaitAsync(typeof(BranchPerspective), SyncFilter.All().Build());

    await Assert.That(result.Outcome).IsEqualTo(SyncOutcome.TimedOut);
    await Assert.That(result.EventsAwaited).IsEqualTo(1);
    var span = capture.Single("PerspectiveSync BranchPerspective");
    await Assert.That(span.GetTagItem("whizbang.sync.outcome")).IsEqualTo("TimedOut")
      .Because("a timed-out wait and a synced wait must be told apart in a trace");
    await Assert.That((int?)span.GetTagItem("whizbang.sync.event_count")).IsEqualTo(1);
    await Assert.That(span.GetTagItem("whizbang.sync.elapsed_ms") is double).IsTrue();
  }

  // ==========================================================================
  // WaitForAppliedAsync: span tags
  // ==========================================================================

  [Test]
  public async Task WaitForAppliedAsync_LedgerSaysApplied_WithListener_TagsSpanWithPerspectiveAndTimeoutAsync() {
    using var capture = new SpanCapture();
    var perspectiveName = TypeNameFormatter.GetPerspectiveName(typeof(BranchPerspective));
    var awaiter = new PerspectiveSyncAwaiter(
      new AppliedLedger(),
      _clock(),
      NullLogger<PerspectiveSyncAwaiter>.Instance,
      new SyncEventTracker(),
      new ScopedEventTracker(),
      new AsyncLocalLifecycleContextAccessor(),
      new FakeTimeProvider());

    var result = await awaiter.WaitForAppliedAsync(typeof(BranchPerspective), Guid.CreateVersion7(), _timeout);

    await Assert.That(result.Outcome).IsEqualTo(SyncOutcome.Synced);
    var span = capture.Single($"PerspectiveSync {perspectiveName} Applied");
    await Assert.That(span.GetTagItem("whizbang.sync.perspective")).IsEqualTo(perspectiveName);
    await Assert.That((double?)span.GetTagItem("whizbang.sync.timeout_ms")).IsEqualTo(_timeout.TotalMilliseconds);
    await Assert.That(span.GetTagItem("whizbang.sync.outcome")).IsEqualTo("Synced");
  }

  // ==========================================================================
  // WaitForStreamAsync: the debug log's event-type rendering
  // ==========================================================================

  [Test]
  public async Task WaitForStreamAsync_NoEventTypeFilter_DebugLogRendersAnEmptyTypeListAsync() {
    var logger = new FakeLogger<PerspectiveSyncAwaiter>();
    var awaiter = _awaiter(new ScopedEventTracker(), new SyncEventTracker(), logger);

    var result = await awaiter.WaitForStreamAsync(
      typeof(BranchPerspective), Guid.CreateVersion7(), eventTypes: null, _timeout);

    await Assert.That(result.Outcome).IsEqualTo(SyncOutcome.NoPendingEvents);
    var queried = _singleQueriedLine(logger);
    await Assert.That(queried).Contains("EventTypes=[]")
      .Because("no type filter must render as an empty list, not throw on the missing array");
  }

  [Test]
  public async Task WaitForStreamAsync_EventTypeFilter_DebugLogRendersTheTypeNamesAsync() {
    var logger = new FakeLogger<PerspectiveSyncAwaiter>();
    var awaiter = _awaiter(new ScopedEventTracker(), new SyncEventTracker(), logger);

    var result = await awaiter.WaitForStreamAsync(
      typeof(BranchPerspective), Guid.CreateVersion7(), [typeof(BranchProbeEvent)], _timeout);

    await Assert.That(result.Outcome).IsEqualTo(SyncOutcome.NoPendingEvents);
    var queried = _singleQueriedLine(logger);
    await Assert.That(queried).Contains("EventTypes=[BranchProbeEvent]")
      .Because("the filter the caller asked for is what an operator needs to see when no events were found");
  }

  // ==========================================================================
  // Helpers
  // ==========================================================================

  private static string _singleQueriedLine(FakeLogger<PerspectiveSyncAwaiter> logger) =>
    logger.Collector.GetSnapshot()
      .Single(r => r.Message.Contains("Queried singleton tracker", StringComparison.Ordinal))
      .Message;

  private static DebuggerAwareClock _clock() =>
    new(new DebuggerAwareClockOptions { Mode = DebuggerDetectionMode.Disabled });

  private static PerspectiveSyncAwaiter _awaiter(
      IScopedEventTracker scoped,
      ISyncEventTracker syncTracker,
      Microsoft.Extensions.Logging.ILogger<PerspectiveSyncAwaiter>? logger = null) =>
    new(
      coordinator: new MockWorkCoordinator(),
      clock: _clock(),
      logger: logger ?? NullLogger<PerspectiveSyncAwaiter>.Instance,
      syncEventTracker: syncTracker,
      tracker: scoped,
      lifecycleContextAccessor: new AsyncLocalLifecycleContextAccessor());

  /// <summary>
  /// Samples <c>Whizbang.Tracing</c> and keeps only the spans in the trace of a parent activity it
  /// starts on construction, so a span is attributable to this test alone.
  /// </summary>
  private sealed class SpanCapture : IDisposable {
    private readonly Activity _parent;
    private readonly ActivityListener _listener;
    private readonly List<Activity> _stopped = [];

    public SpanCapture() {
      _parent = new Activity("perspective-sync-branch-coverage").Start();
      var traceId = _parent.TraceId;
      _listener = new ActivityListener {
        ShouldListenTo = source => source.Name == TRACING_SOURCE,
        Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
        ActivityStopped = activity => {
          if (activity.TraceId == traceId) {
            lock (_stopped) {
              _stopped.Add(activity);
            }
          }
        }
      };
      ActivitySource.AddActivityListener(_listener);
    }

    public Activity Single(string displayName) {
      lock (_stopped) {
        return _stopped.Single(a => a.DisplayName == displayName);
      }
    }

    public void Dispose() {
      _listener.Dispose();
      _parent.Dispose();
    }
  }

  /// <summary>
  /// A real <see cref="SyncEventTracker"/> whose event-driven wait answers with a fixed result, so the
  /// synced and timed-out exits are reached without any real waiting.
  /// </summary>
  private sealed class DecidingSyncEventTracker(bool answer) : ISyncEventTracker {
    private readonly SyncEventTracker _inner = new();

    public int WaitCalls { get; private set; }
    public IReadOnlyList<Guid> LastEventIds { get; private set; } = [];
    public string? LastPerspectiveName { get; private set; }

    public Task<bool> WaitForPerspectiveEventsAsync(
        IReadOnlyList<Guid> eventIds, string perspectiveName, TimeSpan timeout,
        Guid? awaiterId = null, CancellationToken cancellationToken = default) {
      WaitCalls++;
      LastEventIds = eventIds;
      LastPerspectiveName = perspectiveName;
      return Task.FromResult(answer);
    }

    public void TrackEvent(Type eventType, Guid eventId, Guid streamId, string perspectiveName) =>
      _inner.TrackEvent(eventType, eventId, streamId, perspectiveName);
    public IReadOnlyList<TrackedSyncEvent> GetPendingEvents(Guid streamId, string perspectiveName, Type[]? eventTypes = null) =>
      _inner.GetPendingEvents(streamId, perspectiveName, eventTypes);
    public void MarkProcessed(IEnumerable<Guid> eventIds) => _inner.MarkProcessed(eventIds);
    public IReadOnlyList<Guid> GetAllTrackedEventIds() => _inner.GetAllTrackedEventIds();
    public Task<bool> WaitForEventsAsync(IReadOnlyList<Guid> eventIds, TimeSpan timeout,
        Guid? awaiterId = null, CancellationToken cancellationToken = default) =>
      _inner.WaitForEventsAsync(eventIds, timeout, awaiterId, cancellationToken);
    public void MarkProcessedByPerspective(IEnumerable<Guid> eventIds, string perspectiveName) =>
      _inner.MarkProcessedByPerspective(eventIds, perspectiveName);
    public Task<bool> WaitForAllPerspectivesAsync(IReadOnlyList<Guid> eventIds, TimeSpan timeout,
        Guid? awaiterId = null, CancellationToken cancellationToken = default) =>
      _inner.WaitForAllPerspectivesAsync(eventIds, timeout, awaiterId, cancellationToken);
    public void UnregisterAwaiter(Guid awaiterId) => _inner.UnregisterAwaiter(awaiterId);
    public int CleanupStaleEntries(TimeSpan maxAge) => _inner.CleanupStaleEntries(maxAge);
    public void MarkPerspectiveStreamProcessed(string perspectiveName, Guid streamId) =>
      _inner.MarkPerspectiveStreamProcessed(perspectiveName, streamId);
  }

  // The one real way inquiries come out empty after a non-empty count: the count and the
  // enumeration of a live tracker disagree (same stand-in as PerspectiveSyncAwaiterCoverageTests).
  private sealed class CountDisagreesWithEnumerationList : IReadOnlyList<TrackedEvent> {
    public int Count => 1;
    public TrackedEvent this[int index] => throw new ArgumentOutOfRangeException(nameof(index));
    public IEnumerator<TrackedEvent> GetEnumerator() { yield break; }
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
  }

  private sealed class CountDisagreesScopedEventTracker : IScopedEventTracker {
    public void TrackEmittedEvent(Guid streamId, Type eventType, Guid eventId) { }
    public IReadOnlyList<TrackedEvent> GetEmittedEvents() => [];
    public IReadOnlyList<TrackedEvent> GetEmittedEvents(SyncFilterNode filter) => new CountDisagreesWithEnumerationList();
    public bool AreAllProcessed(SyncFilterNode filter, IReadOnlySet<Guid> processedEventIds) => true;
  }

  /// <summary>A ledger that reports every inquired event already applied on the first read.</summary>
  private sealed class AppliedLedger : NoOpWorkCoordinator, IWorkCoordinator {
    ValueTask<AppliedEventStatus?> IWorkCoordinator.GetAppliedEventStatusAsync(
        AppliedEventInquiry inquiry, CancellationToken cancellationToken) =>
      ValueTask.FromResult<AppliedEventStatus?>(new AppliedEventStatus(AppliedEventState.Applied, inquiry.EventId));
  }
}
