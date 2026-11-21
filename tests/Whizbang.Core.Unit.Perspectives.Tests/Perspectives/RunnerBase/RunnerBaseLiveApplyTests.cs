// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Tracing;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Perspectives.RunnerBase;

/// <summary>
/// The live apply of the runner base (#2469): the idempotency filter over the row's recorded position, the fold's
/// treatment of each Apply outcome, scope events, traces, and the write that ends the batch.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Perspectives/PerspectiveRunnerBase.cs</code-under-test>
[NotInParallel("RunnerBaseActivity")]
public class RunnerBaseLiveApplyTests {
  // ===== The idempotency filter =====

  [Test]
  public async Task ABatchTheRowAlreadyHolds_IsReportedCompleted_AndWarnedAboutAsync() {
    var harness = new RbHarness();
    var applied = harness.Add(harness.Marked("a"));
    harness.Rows.Seed(harness.StreamId, new RbModel { Id = harness.StreamId, Marks = "a" }, applied.MessageId.Value, applied.LocalCommitSequence);

    var completion = await harness.Runner.RunWithEventsAsync(harness.StreamId, RbHarness.NAME, null, [applied]);

    await Assert.That(completion.Status).IsEqualTo(PerspectiveProcessingStatus.Completed);
    await Assert.That(completion.EventsProcessed).IsEqualTo(0);
    await Assert.That(completion.LastEventId).IsEqualTo(applied.MessageId.Value);
    await Assert.That(completion.ProcessedEventIds).IsEquivalentTo([applied.MessageId.Value]);
    await Assert.That(harness.Log.Count(LogLevel.Warning, "[diag.filter.all-dropped]")).IsEqualTo(1);
    await Assert.That(harness.Rows.Writes).IsEqualTo(0);
  }

  [Test]
  public async Task ABatchTheRowAlreadyHolds_WithWarningsOff_LogsNothing_AndFallsBackToTheCursorAsync() {
    var harness = new RbHarness();
    harness.Log.Minimum = LogLevel.Error;
    var applied = harness.Add(harness.Marked("a"));
    // A recorded position whose event id is not a Guid: the completion reports the cursor it was given.
    harness.Rows.Seed(harness.StreamId, new RbModel { Id = harness.StreamId, Marks = "a" }, null, applied.LocalCommitSequence, stampText: "not-a-guid");
    var cursor = TrackedGuid.New().Value;

    var completion = await harness.Runner.RunWithEventsAsync(harness.StreamId, RbHarness.NAME, cursor, [applied]);
    var withNoCursor = await harness.Runner.RunWithEventsAsync(harness.StreamId, RbHarness.NAME, null, [applied]);

    await Assert.That(harness.Log.Lines).IsEmpty();
    await Assert.That(completion.LastEventId).IsEqualTo(cursor);
    await Assert.That(withNoCursor.LastEventId).IsEqualTo(Guid.Empty);
  }

  [Test]
  public async Task ABatchPartlyHeld_FoldsOnlyTheRest_AndLogsTheDroppedOnesAtDebugAsync() {
    var harness = new RbHarness();
    var applied = harness.Add(harness.Marked("a"));
    var fresh = harness.Add(harness.Marked("b"));
    harness.Rows.Seed(harness.StreamId, new RbModel { Id = harness.StreamId, Marks = "a" }, applied.MessageId.Value, applied.LocalCommitSequence);

    await harness.Runner.RunWithEventsAsync(harness.StreamId, RbHarness.NAME, null, [applied, fresh]);

    await Assert.That(harness.Rows.Row(harness.StreamId).Marks).IsEqualTo("ab");
    await Assert.That(harness.Log.Count(LogLevel.Debug, "[diag.filter.partial-dropped]")).IsEqualTo(1);
  }

  [Test]
  public async Task ABatchPartlyHeld_WithDebugOff_FoldsTheRestSilentlyAsync() {
    var harness = new RbHarness();
    harness.Log.Minimum = LogLevel.Information;
    var applied = harness.Add(harness.Marked("a"));
    var fresh = harness.Add(harness.Marked("b"));
    harness.Rows.Seed(harness.StreamId, new RbModel { Id = harness.StreamId, Marks = "a" }, applied.MessageId.Value, applied.LocalCommitSequence);

    await harness.Runner.RunWithEventsAsync(harness.StreamId, RbHarness.NAME, null, [applied, fresh]);

    await Assert.That(harness.Rows.Row(harness.StreamId).Marks).IsEqualTo("ab");
    await Assert.That(harness.Log.Lines.Where(l => l.Message.Contains("[diag.filter"))).IsEmpty();
  }

  [Test]
  public async Task AnEventBelowTheRowsPosition_RewindsTheStreamInsteadOfDroppingItAsync() {
    var harness = new RbHarness();
    var late = harness.Add(harness.Marked("a"));
    var later = harness.Add(harness.Marked("b"));
    harness.Rows.Seed(harness.StreamId, new RbModel { Id = harness.StreamId, Marks = "b" }, later.MessageId.Value, later.LocalCommitSequence);

    var completion = await harness.Runner.RunWithEventsAsync(harness.StreamId, RbHarness.NAME, null, [late]);

    await Assert.That(harness.Log.Count(LogLevel.Warning, "[diag.filter.rewind-below-position]")).IsEqualTo(1);
    await Assert.That(harness.Rows.Row(harness.StreamId).Marks).IsEqualTo("ab");
    await Assert.That(completion.EventsProcessed).IsEqualTo(2);
  }

  // ===== The fold =====

  [Test]
  public async Task AnApplyThatThrows_IsSkipped_AndTheCheckpointMovesPastItAsync() {
    var harness = new RbHarness();
    harness.Runner.Apply = (model, e) => e is RbOther ? throw new InvalidOperationException("bad") : (model! with { Marks = model.Marks + "a" }, ModelAction.None);
    var good = harness.Add(harness.Marked("a"));
    var bad = harness.Add(new RbOther { StreamId = harness.StreamId }, hops: false);

    var completion = await harness.Runner.RunWithEventsAsync(harness.StreamId, RbHarness.NAME, null, [good, bad]);

    await Assert.That(completion.LastEventId).IsEqualTo(bad.MessageId.Value);
    await Assert.That(completion.EventsProcessed).IsEqualTo(2);
    await Assert.That(harness.Rows.Row(harness.StreamId).Marks).IsEqualTo("a");
    await Assert.That(harness.Log.Count(LogLevel.Error, "Error applying RbOther")).IsEqualTo(1);
  }

  [Test]
  public async Task AnEventWithNoHops_IsStampedAtTheApplyAsync() {
    var harness = new RbHarness();
    var bare = harness.Add(harness.Marked("a"), hops: false);

    await harness.Runner.RunWithEventsAsync(harness.StreamId, RbHarness.NAME, null, [bare]);

    await Assert.That(harness.Rows.Row(harness.StreamId).Marks).IsEqualTo("a");
    await Assert.That(harness.Rows.MetadataOf(harness.StreamId).Timestamp).IsGreaterThan(Rb.Minted.UtcDateTime)
      .Because("an event that carries no hop has no time of its own, so the row is stamped at the apply");
  }

  [Test]
  public async Task ASoftDelete_KeepsTheModelItReturns_OrTheRowWhenItReturnsNoneAsync() {
    var harness = new RbHarness();
    harness.Rows.Seed(harness.StreamId, new RbModel { Id = harness.StreamId, Marks = "kept" });
    harness.Runner.Apply = (model, e) => e is RbOther ? (null, ModelAction.Delete) : (model! with { Marks = "deleted" }, ModelAction.Delete);
    var noModel = harness.Add(new RbOther { StreamId = harness.StreamId });

    await harness.Runner.RunWithEventsAsync(harness.StreamId, RbHarness.NAME, null, [noModel]);
    await Assert.That(harness.Rows.Row(harness.StreamId).Marks).IsEqualTo("kept");

    var withModel = harness.Add(harness.Marked());
    await harness.Runner.RunWithEventsAsync(harness.StreamId, RbHarness.NAME, null, [withModel]);
    await Assert.That(harness.Rows.Row(harness.StreamId).Marks).IsEqualTo("deleted");
  }

  [Test]
  public async Task AScopeEvent_SetsTheRowsScope_AndAScopePerspectiveDecidesItAsync() {
    var harness = new RbHarness { Traits = new RbTraits { HasScopeInterface = true } };
    harness.Rows.Seed(harness.StreamId, new RbModel { Id = harness.StreamId }, scope: new PerspectiveScope { TenantId = "stored" });
    var first = harness.Add(new RbScoped { StreamId = harness.StreamId, Scope = new() { TenantId = "t1", UserId = "u1" } });
    var second = harness.Add(new RbScoped { StreamId = harness.StreamId, Scope = new() { TenantId = "t2", UserId = "u2" } });

    await harness.Runner.RunWithEventsAsync(harness.StreamId, RbHarness.NAME, null, [first, second]);

    var (scope, force) = harness.Rows.WrittenScopes.Single();
    await Assert.That(scope!.TenantId).IsEqualTo("stored")
      .Because("the perspective keeps the current tenant: the row's stored scope, then the first scope event's result");
    await Assert.That(scope.UserId).IsEqualTo("u2");
    await Assert.That(force).IsTrue();
  }

  [Test]
  public async Task AScopeEvent_OnARowWithNoStoredScope_StartsFromAnEmptyScopeAsync() {
    var harness = new RbHarness { Traits = new RbTraits { HasScopeInterface = true } };
    var scoped = harness.Add(new RbScoped { StreamId = harness.StreamId, Scope = new() { TenantId = "t1", UserId = "u1" } });

    await harness.Runner.RunWithEventsAsync(harness.StreamId, RbHarness.NAME, null, [scoped]);

    await Assert.That(harness.Rows.WrittenScopes.Single().Scope!.TenantId).IsEqualTo("t1");
  }

  [Test]
  public async Task AScopeEvent_WithoutAScopePerspective_SetsTheProposedScopeAsync() {
    var harness = new RbHarness();
    harness.Rows.Seed(harness.StreamId, new RbModel { Id = harness.StreamId }, scope: new PerspectiveScope { TenantId = "stored" });
    var scoped = harness.Add(new RbScoped { StreamId = harness.StreamId, Scope = new() { TenantId = "t1" } });

    await harness.Runner.RunWithEventsAsync(harness.StreamId, RbHarness.NAME, null, [scoped]);

    await Assert.That(harness.Rows.WrittenScopes.Single().Scope!.TenantId).IsEqualTo("t1");
  }

  [Test]
  public async Task TheApplyIsTraced_PerEventWhenSpansAreOn_AndSummarizedOnTheCurrentActivityAsync() {
    var started = new List<Activity>();
    using var listener = new ActivityListener {
      ShouldListenTo = source => source.Name == WhizbangActivitySource.Tracing.Name,
      Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
      ActivityStarted = started.Add,
    };
    ActivitySource.AddActivityListener(listener);
    var harness = new RbHarness { Tracing = new TracingOptions { EnablePerspectiveEventSpans = true } };
    var first = harness.Add(harness.Marked("a"));
    var second = harness.Add(harness.Marked("b"));
    using var parent = new Activity("perspective-run").Start();

    await harness.Runner.RunWithEventsAsync(harness.StreamId, RbHarness.NAME, null, [first, second]);

    var spans = started.Where(a => a.OperationName == "Apply RbMarked" && a.GetTagItem("whizbang.perspective.stream_id") as string == harness.StreamId.ToString()).ToList();
    await Assert.That(spans).Count().IsEqualTo(2);
    await Assert.That(spans[0].GetTagItem("whizbang.perspective.event_type")).IsEqualTo(PerspectiveRunnerTypeNames.Recorded(typeof(RbMarked)));
    await Assert.That(spans[0].GetTagItem("whizbang.perspective.action")).IsEqualTo("None");
    await Assert.That(parent.GetTagItem("whizbang.perspective.events_applied")).IsEqualTo(2);
    await Assert.That(parent.GetTagItem("whizbang.perspective.event_types")).IsEqualTo("RbMarked:2");
  }

  [Test]
  public async Task WithSpansOff_TheApplyStartsNoSpanAsync() {
    var harness = new RbHarness { Tracing = new TracingOptions { EnablePerspectiveEventSpans = false } };
    var first = harness.Add(harness.Marked("a"));

    await harness.Runner.RunWithEventsAsync(harness.StreamId, RbHarness.NAME, null, [first]);

    await Assert.That(harness.Rows.Row(harness.StreamId).Marks).IsEqualTo("a");
  }

  [Test]
  public async Task ARebuildEndingInAnUnconditionalPurge_FoldsOnlyThePurgeAsync() {
    var harness = new RbHarness();
    var markers = new RbMarkers();
    harness.Services.AddSingleton<IPerspectivePurgeMarkerStore>(markers);
    harness.Runner.Purges = e => e is RbOther;
    var folds = 0;
    harness.Runner.Apply = (model, e) => {
      folds++;
      return e is RbOther ? (null, ModelAction.Purge) : (model, ModelAction.None);
    };
    var first = harness.Add(harness.Marked("a"));
    var purge = harness.Add(new RbOther { StreamId = harness.StreamId });

    PerspectiveCursorCompletion? completion = null;
    await Rb.WithModeAsync(ProcessingMode.Rebuild, async () =>
      completion = await harness.Runner.RunWithEventsAsync(harness.StreamId, RbHarness.NAME, null, [first, purge]));

    await Assert.That(folds).IsEqualTo(1).Because("the events before an unconditional purge fold into a row it removes");
    await Assert.That(completion!.EventsProcessed).IsEqualTo(2);
  }

  [Test]
  public async Task ARebuildOverAStoredRow_StartsEmpty_AndRecordsTheHighestCommitSequenceFoldedAsync() {
    var harness = new RbHarness();
    var first = harness.Add(harness.Marked("a"));
    var second = harness.Add(harness.Marked("b"));
    var unstamped = harness.Add(harness.Marked("c"), stamped: false);
    harness.Rows.Seed(harness.StreamId, new RbModel { Id = harness.StreamId, Marks = "stale" }, first.MessageId.Value, first.LocalCommitSequence);
    // Stamped after the fact: the last version carries the lowest commit sequence on the stream.
    first.LocalCommitSequence = 9000;

    await Rb.WithModeAsync(ProcessingMode.Rebuild, () =>
      harness.Runner.RunWithEventsAsync(harness.StreamId, RbHarness.NAME, null, [first, second, unstamped]));

    await Assert.That(harness.Rows.Row(harness.StreamId).Marks).IsEqualTo("abc");
    await Assert.That(harness.Rows.MetadataOf(harness.StreamId).CommitSequence).IsEqualTo(9000);
  }

  [Test]
  public async Task ARebuildWhoseWriteIsRefused_ReportsWhetherTheRowWasAlreadyRightAsync() {
    var harness = new RbHarness();
    harness.Rows.RefuseWrites = true;
    var first = harness.Add(harness.Marked("a"));
    harness.Rows.Seed(harness.StreamId, new RbModel { Id = harness.StreamId, Marks = "a" }, first.MessageId.Value, first.LocalCommitSequence);

    PerspectiveCursorCompletion? completion = null;
    await Rb.WithModeAsync(ProcessingMode.Rebuild, async () =>
      completion = await harness.Runner.RunWithEventsAsync(harness.StreamId, RbHarness.NAME, null, [first]));

    await Assert.That(completion!.RowChanged).IsFalse();
    await Assert.That(completion.UnchangedReason).IsEqualTo(PerspectiveRowUnchangedReason.AlreadyCorrect);
  }

  [Test]
  public async Task ARebuildOfASplitModelWhoseWriteIsRefused_ComparesTheFoldTakenBeforeTheWriteAsync() {
    var harness = new RbHarness { Traits = new RbTraits { SnapshotsBeforeWrite = true } };
    harness.Rows.RefuseWrites = true;
    var first = harness.Add(harness.Marked("a"));
    harness.Rows.Seed(harness.StreamId, new RbModel { Id = harness.StreamId, Marks = "z" }, first.MessageId.Value, first.LocalCommitSequence);

    PerspectiveCursorCompletion? completion = null;
    await Rb.WithModeAsync(ProcessingMode.Rebuild, async () =>
      completion = await harness.Runner.RunWithEventsAsync(harness.StreamId, RbHarness.NAME, null, [first]));

    await Assert.That(completion!.UnchangedReason).IsEqualTo(PerspectiveRowUnchangedReason.WriteRefused);
  }

  [Test]
  public async Task APhysicalFieldModel_IsWrittenWithItsValues_AndItsDocumentStrippedAsync() {
    var harness = new RbHarness();
    harness.Runner.Physical = m => new Dictionary<string, object?> { ["marks"] = m.Marks };
    harness.Runner.Strip = m => m with { Marks = "" };
    var marked = harness.Add(harness.Marked("a"));

    await harness.Runner.RunWithEventsAsync(harness.StreamId, RbHarness.NAME, null, [marked]);

    await Assert.That(harness.Rows.PhysicalWrites.Single()["marks"]).IsEqualTo("a");
    await Assert.That(harness.Rows.Row(harness.StreamId).Marks).IsEqualTo("");
  }

  [Test]
  public async Task AStoreThatTracksNoVersions_ReadsTheMetadataOnItsOwnAsync() {
    var harness = new RbHarness();
    harness.Rows.Versioned = false;
    var applied = harness.Add(harness.Marked("a"));
    harness.Rows.Seed(harness.StreamId, new RbModel { Id = harness.StreamId, Marks = "a" }, applied.MessageId.Value, applied.LocalCommitSequence);

    var completion = await harness.Runner.RunWithEventsAsync(harness.StreamId, RbHarness.NAME, null, [applied]);

    await Assert.That(completion.EventsProcessed).IsEqualTo(0)
      .Because("the metadata read on its own says the row holds the event");
  }

  [Test]
  public async Task ALiveWriteTheOrderingGuardRefuses_WaitsForItsStages_ThenRewindsAsync() {
    var harness = new RbHarness();
    var registry = new Whizbang.Core.Tests.Helpers.AlwaysReceptorRegistry();
    harness.Services.AddSingleton<IReceptorRegistry>(registry);
    harness.Rows.RefuseWrites = true;
    var marked = harness.Add(harness.Marked("a"));

    var completion = await harness.Runner.RunWithEventsAsync(harness.StreamId, RbHarness.NAME, null, [marked]);

    await Assert.That(harness.Log.Count(LogLevel.Warning, "[diag.write.refused]")).IsEqualTo(1);
    await Assert.That(completion.RowChanged).IsFalse();
  }

  [Test]
  public async Task ASlowWrite_IsLoggedWithItsTimingAsync() {
    var clock = new FakeTimeProvider();
    var harness = new RbHarness();
    harness.Services.AddSingleton<TimeProvider>(clock);
    harness.Rows.OnFlush = () => clock.Advance(TimeSpan.FromMilliseconds(80));
    var marked = harness.Add(harness.Marked("a"));

    await harness.Runner.RunWithEventsAsync(harness.StreamId, RbHarness.NAME, null, [marked]);

    await Assert.That(harness.Log.Count(LogLevel.Debug, "PERF SaveModelAndCheckpoint")).IsEqualTo(1);
  }

  [Test]
  public async Task ALargeBatch_IsLoggedWithItsTiming_AndAFastSmallOneIsNotAsync() {
    var clock = new FakeTimeProvider();
    var harness = new RbHarness();
    harness.Services.AddSingleton<TimeProvider>(clock);
    var events = Enumerable.Range(0, 10).Select(i => harness.Add(harness.Marked("x"))).ToList();
    var small = new RbHarness();
    small.Services.AddSingleton<TimeProvider>(new FakeTimeProvider());

    await harness.Runner.RunWithEventsAsync(harness.StreamId, RbHarness.NAME, null, events);
    await small.Runner.RunWithEventsAsync(small.StreamId, RbHarness.NAME, null, [small.Add(small.Marked("a"))]);

    await Assert.That(harness.Log.Count(LogLevel.Debug, "PERF SaveModelAndCheckpoint")).IsEqualTo(1);
    await Assert.That(small.Log.Count(LogLevel.Debug, "PERF SaveModelAndCheckpoint")).IsEqualTo(0);
  }

  [Test]
  public async Task AFailedApply_SavesItsPartialProgress_AndAFailedSaveIsThrownInItsPlaceAsync() {
    var harness = new RbHarness();
    harness.Rows.FlushFails = new InvalidOperationException("flush failed");
    harness.Rows.WriteFails = null;
    var marked = harness.Add(harness.Marked("a"));
    harness.Rows.BeforeWrite = _ => {
      if (harness.Rows.Writes > 1) {
        harness.Rows.WriteFails = new TimeoutException("save failed");
      }
    };

    await Assert.That(async () => await harness.Runner.RunWithEventsAsync(harness.StreamId, RbHarness.NAME, null, [marked]))
      .Throws<TimeoutException>();
    await Assert.That(harness.Log.Count(LogLevel.Warning, "Failed to save partial progress")).IsEqualTo(1);
  }

  [Test]
  public async Task AFailedApplyWithNoModel_SavesNothing_AndRethrowsAsync() {
    var harness = new RbHarness();
    harness.Rows.FlushFails = new InvalidOperationException("flush failed");
    harness.Runner.Apply = (_, _) => (null, ModelAction.None);
    var marked = harness.Add(harness.Marked("a"));

    await Assert.That(async () => await harness.Runner.RunWithEventsAsync(harness.StreamId, RbHarness.NAME, null, [marked]))
      .Throws<InvalidOperationException>();
    await Assert.That(harness.Rows.Writes).IsEqualTo(0);
  }
}
