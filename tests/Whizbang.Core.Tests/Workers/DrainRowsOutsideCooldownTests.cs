// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lifecycle;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Issue #1759: the drain parses only the fetched rows that are not in the cooldown cache. A row in the cache was
/// applied by an earlier session of its stream and only waits for its completion to delete it, so parsing it again
/// buys nothing; the cooldown filter after parsing would discard it anyway. A skipped row's perspective completion is
/// still signaled on the lifecycle coordinator, from the row alone.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Workers/PerspectiveWorker.cs</code-under-test>
public class DrainRowsOutsideCooldownTests {
  private const string PERSPECTIVE_A = "PerspectiveA";
  private const string PERSPECTIVE_B = "PerspectiveB";

  /// <summary>Records perspective completions; nothing else is reached by the code under test.</summary>
  private sealed class SignalRecorder : ILifecycleCoordinator {
    public ConcurrentQueue<(Guid EventId, string Perspective)> Signaled { get; } = new();

    public bool SignalPerspectiveComplete(Guid eventId, string perspectiveName) {
      Signaled.Enqueue((eventId, perspectiveName));
      return false;
    }

    public ILifecycleTracking BeginTracking(LifecycleTrackingPath path, Guid eventId, IMessageEnvelope envelope, LifecycleStage entryStage, MessageSource source,
      Guid? streamId = null, Type? perspectiveType = null) => throw new NotSupportedException();
    public ILifecycleTracking? GetTracking(LifecycleTrackingPath path, Guid eventId) => null;
    public void ExpectCompletionsFrom(LifecycleTrackingPath path, Guid eventId, params PostLifecycleCompletionSource[] sources) {
      // Not reached: the rows filter only signals perspective completions.
    }
    public ValueTask SignalSegmentCompleteAsync(LifecycleTrackingPath path, Guid eventId, PostLifecycleCompletionSource source, IServiceProvider scopedProvider,
      CancellationToken ct) => ValueTask.CompletedTask;
    public void AbandonTracking(LifecycleTrackingPath path, Guid eventId) {
      // Not reached: the rows filter only signals perspective completions.
    }
    public void ExpectPerspectiveCompletions(Guid eventId, IReadOnlyList<string> perspectiveNames) {
      // Not reached: the rows filter only signals perspective completions.
    }
    public bool AreAllPerspectivesComplete(Guid eventId) => false;
    public int CleanupStaleTracking(TimeSpan inactivityThreshold) => 0;
  }

  private static StreamEventData _row(Guid eventId, string? perspectiveName) => new() {
    StreamId = (Guid)TrackedGuid.New(),
    EventId = eventId,
    EventType = "TestEvent",
    EventData = "{}",
    EventWorkId = (Guid)TrackedGuid.New(),
    Metadata = null,
    Scope = null,
    PerspectiveName = perspectiveName
  };

  private static RecentlyProcessedEventCache _cache() =>
    new(new SystemTimeProvider(new FakeTimeProvider(new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero))));

  [Test]
  public async Task WithoutACache_EveryRowIsParsedAsync() {
    var eventId = (Guid)TrackedGuid.New();
    List<StreamEventData> rows = [_row(eventId, PERSPECTIVE_A), _row(eventId, PERSPECTIVE_B)];

    var recorder = new SignalRecorder();

    var toParse = PerspectiveWorker._rowsOutsideCooldown(rows, cache: null, recorder);

    await Assert.That(toParse).IsEquivalentTo(rows);
    await Assert.That(recorder.Signaled).IsEmpty();
  }

  [Test]
  public async Task RowsInTheCache_AreNotParsed_AndTheRestAreAsync() {
    var cache = _cache();
    var eventId = (Guid)TrackedGuid.New();
    var applied = _row(eventId, PERSPECTIVE_A);
    var pending = _row(eventId, PERSPECTIVE_B);
    cache.MarkProcessed(applied.EventWorkId);
    var recorder = new SignalRecorder();

    var toParse = PerspectiveWorker._rowsOutsideCooldown([applied, pending], cache, recorder);

    await Assert.That(toParse).IsEquivalentTo([pending])
      .Because("the event is still parsed for the perspective that has not applied it, from its own row");
    await Assert.That(recorder.Signaled).IsEquivalentTo([(eventId, PERSPECTIVE_A)])
      .Because("the skipped row's perspective is signaled complete, as the cooldown path after parsing did");
  }

  [Test]
  public async Task EveryRowInTheCache_LeavesNothingToParseAsync() {
    var cache = _cache();
    var eventId = (Guid)TrackedGuid.New();
    List<StreamEventData> rows = [_row(eventId, PERSPECTIVE_A), _row(eventId, PERSPECTIVE_B)];
    cache.MarkProcessed(rows.Select(r => r.EventWorkId));
    var recorder = new SignalRecorder();

    var toParse = PerspectiveWorker._rowsOutsideCooldown(rows, cache, recorder);

    await Assert.That(toParse).IsEmpty();
    await Assert.That(recorder.Signaled).IsEquivalentTo([(eventId, PERSPECTIVE_A), (eventId, PERSPECTIVE_B)]);
  }

  [Test]
  public async Task ACooledRowWithoutAPerspective_OrWithoutACoordinator_IsSkippedWithoutASignalAsync() {
    var cache = _cache();
    var eventId = (Guid)TrackedGuid.New();
    var unnamed = _row(eventId, perspectiveName: null);
    var named = _row(eventId, PERSPECTIVE_A);
    cache.MarkProcessed([unnamed.EventWorkId, named.EventWorkId]);
    var recorder = new SignalRecorder();

    var withRecorder = PerspectiveWorker._rowsOutsideCooldown([unnamed], cache, recorder);
    var withoutCoordinator = PerspectiveWorker._rowsOutsideCooldown([named], cache, lifecycleCoordinator: null);

    await Assert.That(withRecorder).IsEmpty();
    await Assert.That(withoutCoordinator).IsEmpty();
    await Assert.That(recorder.Signaled).IsEmpty()
      .Because("a row that names no perspective has no completion to signal");
  }
}
