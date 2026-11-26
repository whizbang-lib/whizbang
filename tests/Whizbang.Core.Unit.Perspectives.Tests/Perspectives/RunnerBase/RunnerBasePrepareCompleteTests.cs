// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Archival;
using Whizbang.Core.Messaging;
using Whizbang.Core.Perspectives;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Perspectives.RunnerBase;

/// <summary>
/// The set-based split of an apply (#2246), as the runner base makes it (#2469): a prepare reads and folds a pair and
/// hands back the row write, or hands the pair to the per-pair path with a reason; a completion finishes it from the
/// write's outcome.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Perspectives/PerspectiveRunnerBase.cs</code-under-test>
public class RunnerBasePrepareCompleteTests {
  [Test]
  public async Task AKnownNewPair_IsPreparedWithoutARowRead_AndCompletesAsWrittenAsync() {
    var harness = new RbHarness();
    harness.Rows.Prepares = true;
    var batch = new RbBatch { KnownNew = true };
    var marked = harness.Add(harness.Marked("a"));

    var prepared = await harness.Runner.PrepareAsync(harness.StreamId, RbHarness.NAME, null, [marked], batch);

    await Assert.That(prepared.RunsPerPair).IsFalse();
    await Assert.That(prepared.Write).IsNotNull();
    await Assert.That(prepared.Write!.Operation).IsEqualTo(PerspectiveWriteOperation.Insert)
      .Because("the batch knew the stream is new, so the write inserts against an absent row");
    await Assert.That(harness.Rows.Prepared.Single().Model.Marks).IsEqualTo("a");
    await Assert.That(harness.Rows.Prepared.Single().Force).IsFalse()
      .Because("a write with no scope forces no scope update");

    var completion = await harness.Runner.CompleteAsync(prepared, PerspectiveWriteOutcome.Written);

    await Assert.That(completion.Status).IsEqualTo(PerspectiveProcessingStatus.Completed);
    await Assert.That(completion.RowChanged).IsTrue();
    await Assert.That(completion.LastEventId).IsEqualTo(marked.MessageId.Value);
  }

  [Test]
  public async Task AFencedWrite_EndsTheApplyWithoutFinishing_AndNeverRunsPerPairAsync() {
    var harness = new RbHarness();
    harness.Rows.Prepares = true;
    var marked = harness.Add(harness.Marked("a"));
    var prepared = await harness.Runner.PrepareAsync(harness.StreamId, RbHarness.NAME, null, [marked], null);

    var thrown = await Assert.ThrowsAsync<PartitionOwnershipLostException>(
      async () => await harness.Runner.CompleteAsync(prepared, PerspectiveWriteOutcome.Fenced));

    await Assert.That(thrown!.StreamId).IsEqualTo(harness.StreamId)
      .Because("#2556: a write fenced by its partition's epoch is the current owner's to apply, never re-run here per pair");
  }

  [Test]
  public async Task APairTheBatchReadARowFor_FoldsOntoThatRowAsync() {
    var harness = new RbHarness();
    harness.Rows.Prepares = true;
    var stored = harness.Add(harness.Marked("a"));
    var row = new RbModel { Id = harness.StreamId, Marks = "a" };
    var batch = new RbBatch {
      Row = new PerspectiveStoredRow<RbModel>(
        new PerspectiveApplyRead(PerspectiveRowVersion.Of(7), new() { EventId = stored.MessageId.Value.ToString("D"), EventType = "x", CommitSequence = stored.LocalCommitSequence }),
        row),
      CommitSequence = new PerspectiveCommitSequence(4242),
    };
    var next = harness.Add(harness.Marked("b"));

    var prepared = await harness.Runner.PrepareAsync(harness.StreamId, RbHarness.NAME, stored.MessageId.Value, [next], batch);

    await Assert.That(harness.Rows.Prepared.Single().Model.Marks).IsEqualTo("ab");
    await Assert.That(prepared.Write!.ExpectedVersion).IsEqualTo(PerspectiveRowVersion.Of(7));
    await Assert.That(batch.CommitSequenceAsks).IsEqualTo(1)
      .Because("the commit sequence of the last applied event comes from the batch's one read");
  }

  [Test]
  public async Task APairWhoseBatchReadFoundNoRow_IsAskedThePurgeFromTheBatch_AndAPurgedStreamRunsPerPairAsync() {
    var harness = new RbHarness();
    harness.Services.AddSingleton<IPerspectivePurgeMarkerStore>(new RbMarkers());
    var batch = new RbBatch { Row = new PerspectiveStoredRow<RbModel>(new PerspectiveApplyRead(PerspectiveRowVersion.Absent, null), null), Purged = true };
    var marked = harness.Add(harness.Marked("a"));

    var prepared = await harness.Runner.PrepareAsync(harness.StreamId, RbHarness.NAME, null, [marked], batch);

    await Assert.That(prepared.PerPairReason).IsEqualTo(PerspectivePerPairReason.PurgedStream);
    await Assert.That(batch.PurgeAsks).IsEqualTo(1);
  }

  [Test]
  public async Task APairTheBatchCannotAnswer_IsReadPerStreamAsync() {
    var harness = new RbHarness();
    harness.Rows.Prepares = true;
    var markers = new RbMarkers();
    harness.Services.AddSingleton<IPerspectivePurgeMarkerStore>(markers);
    var batch = new RbBatch();
    var marked = harness.Add(harness.Marked("a"));

    var prepared = await harness.Runner.PrepareAsync(harness.StreamId, RbHarness.NAME, null, [marked], batch);

    await Assert.That(prepared.RunsPerPair).IsFalse();
    await Assert.That(markers.Asks).IsEqualTo(1).Because("the batch had no purge answer, so the stream's own marker is asked");
  }

  [Test]
  public async Task APairWithNoPurgeMarkers_IsNotAskedTheBatchForAPurgeAsync() {
    var harness = new RbHarness();
    harness.Rows.Prepares = true;
    var batch = new RbBatch { Purged = true };
    var marked = harness.Add(harness.Marked("a"));

    var prepared = await harness.Runner.PrepareAsync(harness.StreamId, RbHarness.NAME, null, [marked], batch);

    await Assert.That(prepared.RunsPerPair).IsFalse().Because("without purge markers a purge is honored within its batch only");
    await Assert.That(batch.PurgeAsks).IsEqualTo(0);
  }

  [Test]
  public async Task AWriteTheStoreCannotPrepare_HandsThePairBack_AndItsCompletionRunsPerPairAsync() {
    var harness = new RbHarness();
    var marked = harness.Add(harness.Marked("a"));

    var prepared = await harness.Runner.PrepareAsync(harness.StreamId, RbHarness.NAME, null, [marked], null);

    await Assert.That(prepared.PerPairReason).IsEqualTo(PerspectivePerPairReason.WriteNotPreparable);
    await Assert.That(prepared.Write).IsNull();

    var completion = await harness.Runner.CompleteAsync(prepared, null);

    await Assert.That(completion.RowChanged).IsTrue();
    await Assert.That(harness.Rows.Row(harness.StreamId).Marks).IsEqualTo("a")
      .Because("the per-pair path writes the row the batch could not");
  }

  [Test]
  public async Task APurgeIsPrepared_AfterItsMarkerIsRecordedAsync() {
    var harness = new RbHarness();
    harness.Rows.Prepares = true;
    var markers = new RbMarkers();
    harness.Services.AddSingleton<IPerspectivePurgeMarkerStore>(markers);
    harness.Rows.Seed(harness.StreamId, new RbModel { Id = harness.StreamId });
    harness.Runner.Apply = (_, _) => (null, ModelAction.Purge);
    var marked = harness.Add(harness.Marked("a"));

    var prepared = await harness.Runner.PrepareAsync(harness.StreamId, RbHarness.NAME, null, [marked], null);

    await Assert.That(prepared.Write!.Operation).IsEqualTo(PerspectiveWriteOperation.Purge);
    await Assert.That(markers.Marked).IsEquivalentTo([(harness.StreamId, (Guid?)marked.MessageId.Value)]);

    var completion = await harness.Runner.CompleteAsync(prepared, PerspectiveWriteOutcome.Written);

    await Assert.That(completion.PurgedBy).IsEqualTo(PerspectiveRunnerTypeNames.Recorded(typeof(RbMarked)));
  }

  [Test]
  public async Task APurgeTheStoreCannotPrepare_HandsThePairBack_WithoutAMarkerAsync() {
    var harness = new RbHarness();
    var markers = new RbMarkers();
    harness.Services.AddSingleton<IPerspectivePurgeMarkerStore>(markers);
    harness.Rows.Seed(harness.StreamId, new RbModel { Id = harness.StreamId });
    harness.Runner.Apply = (_, _) => (null, ModelAction.Purge);
    var marked = harness.Add(harness.Marked("a"));

    var prepared = await harness.Runner.PrepareAsync(harness.StreamId, RbHarness.NAME, null, [marked], null);

    await Assert.That(prepared.PerPairReason).IsEqualTo(PerspectivePerPairReason.WriteNotPreparable);
    await Assert.That(markers.Marked).IsEmpty().Because("the per-pair purge records the marker when it runs");
  }

  [Test]
  public async Task ABatchThatProducesNoModel_PreparesNoWrite_AndCompletesUntouchedAsync() {
    var harness = new RbHarness();
    harness.Runner.Apply = (_, _) => (null, ModelAction.None);
    var marked = harness.Add(harness.Marked("a"));

    var prepared = await harness.Runner.PrepareAsync(harness.StreamId, RbHarness.NAME, null, [marked], null);

    await Assert.That(prepared.RunsPerPair).IsFalse();
    await Assert.That(prepared.Write).IsNull();

    var completion = await harness.Runner.CompleteAsync(prepared, null);

    await Assert.That(completion.RowChanged).IsFalse();
    await Assert.That(completion.UnchangedReason).IsEqualTo(PerspectiveRowUnchangedReason.NoModel);
  }

  [Test]
  public async Task AnEmptyBatch_PreparesNothing_AndCompletesWithNoStatusAsync() {
    var harness = new RbHarness();

    var prepared = await harness.Runner.PrepareAsync(harness.StreamId, RbHarness.NAME, null, [], null);
    var completion = await harness.Runner.CompleteAsync(prepared, null);

    await Assert.That(completion.Status).IsEqualTo(PerspectiveProcessingStatus.None);
  }

  [Test]
  public async Task APhysicalFieldModel_IsPreparedWithItsValues_AndItsDocumentStrippedAsync() {
    var harness = new RbHarness();
    harness.Rows.Prepares = true;
    harness.Runner.Physical = m => new Dictionary<string, object?> { ["marks"] = m.Marks };
    harness.Runner.Strip = m => m with { Marks = "" };
    var marked = harness.Add(harness.Marked("a"));

    await harness.Runner.PrepareAsync(harness.StreamId, RbHarness.NAME, null, [marked], null);

    var write = harness.Rows.Prepared.Single();
    await Assert.That(write.Physical!["marks"]).IsEqualTo("a");
    await Assert.That(write.Model.Marks).IsEqualTo("").Because("a Split model's document omits its promoted fields");
  }

  [Test]
  public async Task AScopedPair_IsPreparedWithItsScope_AndForcesAnUpdateForAScopeEventAsync() {
    var harness = new RbHarness();
    harness.Rows.Prepares = true;
    harness.Rows.Seed(harness.StreamId, new RbModel { Id = harness.StreamId });
    var scoped = harness.Add(new RbScoped { StreamId = harness.StreamId, Scope = new() { TenantId = "tenant-b" } });

    await harness.Runner.PrepareAsync(harness.StreamId, RbHarness.NAME, null, [scoped], null);

    var write = harness.Rows.Prepared.Single();
    await Assert.That(write.Scope.TenantId).IsEqualTo("tenant-b");
    await Assert.That(write.Force).IsTrue();
  }

  [Test]
  public async Task APairBelowTheRowsPosition_IsHandedToThePerPairRewindAsync() {
    var harness = new RbHarness();
    var late = harness.Add(harness.Marked("a"));
    harness.Rows.Seed(harness.StreamId, new RbModel { Id = harness.StreamId }, TrackedGuid.New().Value, late.LocalCommitSequence + 10);

    var prepared = await harness.Runner.PrepareAsync(harness.StreamId, RbHarness.NAME, null, [late], null);

    await Assert.That(prepared.PerPairReason).IsEqualTo(PerspectivePerPairReason.RewindBelowPosition);
  }

  [Test]
  public async Task AMissingRowWithHistory_IsHandedToThePerPairResurrectionAsync() {
    var name = "Rb.Archived." + TrackedGuid.New().Value;
    ArchivedStreamRowPolicies.Register(name, ArchivedStreamRowPolicy.Drop);
    var harness = new RbHarness { Traits = new RbTraits { ArchivedStreamRowsName = name } };
    harness.Add(harness.Marked("a"));
    var woke = harness.Add(harness.Marked("b"));

    var prepared = await harness.Runner.PrepareAsync(harness.StreamId, RbHarness.NAME, null, [woke], null);

    await Assert.That(prepared.PerPairReason).IsEqualTo(PerspectivePerPairReason.MissingRowWithHistory);
  }

  [Test]
  public async Task TheHistoryProbe_IsAnsweredByTheBatch_AndSkippedForAKnownNewStreamAsync() {
    var name = "Rb.Archived." + TrackedGuid.New().Value;
    ArchivedStreamRowPolicies.Register(name, ArchivedStreamRowPolicy.Drop);
    var harness = new RbHarness { Traits = new RbTraits { ArchivedStreamRowsName = name } };
    harness.Rows.Prepares = true;
    harness.Add(harness.Marked("a"));
    var woke = harness.Add(harness.Marked("b"));

    var answered = await harness.Runner.PrepareAsync(harness.StreamId, RbHarness.NAME, null, [woke], new RbBatch { History = false });
    var knownNew = await harness.Runner.PrepareAsync(harness.StreamId, RbHarness.NAME, null, [woke], new RbBatch { KnownNew = true });

    await Assert.That(answered.RunsPerPair).IsFalse().Because("the batch said the stream has no earlier history");
    await Assert.That(knownNew.RunsPerPair).IsFalse().Because("a stream the batch knows is new has no history to probe");
  }

  [Test]
  public async Task ABatchTheRowAlreadyHolds_IsDecidedByThePrepare_AndTheCompletionReturnsThatDecisionAsync() {
    var harness = new RbHarness();
    var applied = harness.Add(harness.Marked("a"));
    harness.Rows.Seed(harness.StreamId, new RbModel { Id = harness.StreamId, Marks = "a" }, applied.MessageId.Value, applied.LocalCommitSequence);

    var prepared = await harness.Runner.PrepareAsync(harness.StreamId, RbHarness.NAME, null, [applied], null);
    var completion = await harness.Runner.CompleteAsync(prepared, null);

    await Assert.That(prepared.Write).IsNull();
    await Assert.That(completion.EventsProcessed).IsEqualTo(0);
    await Assert.That(completion.Status).IsEqualTo(PerspectiveProcessingStatus.Completed);
    await Assert.That(completion.LastEventId).IsEqualTo(applied.MessageId.Value);
  }

  [Test]
  public async Task APrepareWhoseFoldFails_SavesNothingAndRethrowsAsync() {
    var harness = new RbHarness();
    var marked = harness.Add(harness.Marked("a"));
    using var canceled = new CancellationTokenSource();
    await canceled.CancelAsync();

    await Assert.That(async () => await harness.Runner.PrepareAsync(harness.StreamId, RbHarness.NAME, null, [marked], null, canceled.Token))
      .Throws<OperationCanceledException>();
    await Assert.That(harness.Log.Count(LogLevel.Warning, "no progress to save")).IsEqualTo(1);
  }

  [Test]
  public async Task Complete_RefusesNoPreparedApply_AndOneAnotherRunnerPreparedAsync() {
    var harness = new RbHarness();
    var other = new RbHarness();
    var marked = other.Add(other.Marked("a"));
    var foreign = await other.Runner.PrepareAsync(other.StreamId, RbHarness.NAME, null, [marked], null);

    await Assert.That(async () => await harness.Runner.CompleteAsync(null!, null)).Throws<ArgumentNullException>();
    await Assert.That(async () => await harness.Runner.CompleteAsync(foreign, null)).Throws<ArgumentException>();
    await Assert.That(async () => await harness.Runner.CompleteAsync(
        PreparedPerspectiveApply.PerPair(harness.StreamId, RbHarness.NAME, null, [marked], PerspectivePerPairReason.PurgedStream), null))
      .Throws<ArgumentException>();
  }

  [Test]
  public async Task Complete_RefusesAPreparedWriteWithNoOutcomeAsync() {
    var harness = new RbHarness();
    harness.Rows.Prepares = true;
    var marked = harness.Add(harness.Marked("a"));
    var prepared = await harness.Runner.PrepareAsync(harness.StreamId, RbHarness.NAME, null, [marked], null);

    await Assert.That(async () => await harness.Runner.CompleteAsync(prepared, null)).Throws<ArgumentNullException>();
  }

  [Test]
  public async Task APairHandedBackBeforeItsFold_RunsPerPair_FiringItsStagesOnceAsync() {
    var harness = new RbHarness();
    var late = harness.Add(harness.Marked("a"));
    harness.Rows.Seed(harness.StreamId, new RbModel { Id = harness.StreamId }, late.MessageId.Value, late.LocalCommitSequence + 10);
    var prepared = await harness.Runner.PrepareAsync(harness.StreamId, RbHarness.NAME, null, [late], null);

    var completion = await harness.Runner.CompleteAsync(prepared, null);

    await Assert.That(completion.Status).IsEqualTo(PerspectiveProcessingStatus.Completed)
      .Because("the per-pair path rewinds the stream the prepare handed back");
  }

  [Test]
  [Arguments(PerspectiveWriteStatus.Conflict)]
  [Arguments(PerspectiveWriteStatus.Failed)]
  public async Task AWriteThatConflictsOrFails_IsReappliedPerPairOnTheRowAsReadAgainAsync(PerspectiveWriteStatus status) {
    var harness = new RbHarness();
    harness.Rows.Prepares = true;
    var marked = harness.Add(harness.Marked("a"));
    var prepared = await harness.Runner.PrepareAsync(harness.StreamId, RbHarness.NAME, null, [marked], null);
    var outcome = status == PerspectiveWriteStatus.Conflict
      ? PerspectiveWriteOutcome.Conflict(PerspectiveRowVersion.Of(9))
      : PerspectiveWriteOutcome.Failed(new InvalidOperationException("rejected"));

    var completion = await harness.Runner.CompleteAsync(prepared, outcome);

    await Assert.That(completion.RowChanged).IsTrue();
    await Assert.That(harness.Rows.Row(harness.StreamId).Marks).IsEqualTo("a");
    if (status == PerspectiveWriteStatus.Failed) {
      await Assert.That(harness.Log.Count(LogLevel.Warning, "[diag.write.failed]")).IsEqualTo(1);
    } else {
      await Assert.That(harness.Log.Count(LogLevel.Debug, "changed after it was read for a set-based write")).IsEqualTo(1);
    }
  }

  [Test]
  public async Task AWriteTheOrderingGuardRefused_RewindsTheStreamAsync() {
    var harness = new RbHarness();
    harness.Rows.Prepares = true;
    var marked = harness.Add(harness.Marked("a"));
    var prepared = await harness.Runner.PrepareAsync(harness.StreamId, RbHarness.NAME, null, [marked], null);

    var completion = await harness.Runner.CompleteAsync(prepared, PerspectiveWriteOutcome.Refused);

    await Assert.That(harness.Log.Count(LogLevel.Warning, "[diag.write.refused]")).IsEqualTo(1);
    await Assert.That(harness.Rows.Row(harness.StreamId).Marks).IsEqualTo("a")
      .Because("the rewind folds the batch the refused write did not land");
    await Assert.That(completion.Status).IsEqualTo(PerspectiveProcessingStatus.Completed);
  }

  [Test]
  public async Task ACompletionWhoseFinishFails_SavesPartialProgressAndRethrowsAsync() {
    var harness = new RbHarness();
    harness.Rows.Prepares = true;
    harness.Rows.FlushFails = new InvalidOperationException("flush failed");
    var marked = harness.Add(harness.Marked("a"));
    var prepared = await harness.Runner.PrepareAsync(harness.StreamId, RbHarness.NAME, null, [marked], null);

    await Assert.That(async () => await harness.Runner.CompleteAsync(prepared, PerspectiveWriteOutcome.Written))
      .Throws<InvalidOperationException>();
    await Assert.That(harness.Log.Count(LogLevel.Warning, "Saving partial progress")).IsEqualTo(1);
    await Assert.That(harness.Rows.Row(harness.StreamId).Marks).IsEqualTo("a");
  }
}

/// <summary>A set-based batch's planned reads, each answer configurable, and how often each was asked.</summary>
internal sealed class RbBatch : IPerspectiveBatchState {
  public bool KnownNew { get; init; }
  public PerspectiveStoredRow<RbModel>? Row { get; init; }
  public bool? Purged { get; init; }
  public bool? History { get; init; }
  public PerspectiveCommitSequence? CommitSequence { get; init; }
  public int PurgeAsks { get; private set; }
  public int CommitSequenceAsks { get; private set; }

  public bool IsKnownNew(Guid streamId, string perspectiveName) => KnownNew;

  public Task<PerspectiveStoredRow<TModel>?> ReadRowAsync<TModel>(
      IPerspectiveStore<TModel> store, Guid streamId, string perspectiveName, CancellationToken cancellationToken = default)
      where TModel : class => Task.FromResult(Row as PerspectiveStoredRow<TModel>);

  public Task<bool?> IsPurgedAsync(IPerspectivePurgeMarkerStore markers, Guid streamId, string perspectiveName,
      CancellationToken cancellationToken = default) {
    PurgeAsks++;
    return Task.FromResult(Purged);
  }

  public Task<bool?> HasHistoryBeforeAsync(IEventStore eventStore, Guid streamId, string perspectiveName, Guid beforeEventId,
      IReadOnlyList<Type> eventTypes, CancellationToken cancellationToken = default) => Task.FromResult(History);

  public Task<PerspectiveCommitSequence?> CommitSequenceOfAsync(IEventStore eventStore, Guid eventId, CancellationToken cancellationToken = default) {
    CommitSequenceAsks++;
    return Task.FromResult(CommitSequence);
  }
}

/// <summary>Purge markers in memory, recording each one marked and cleared.</summary>
internal sealed class RbMarkers : IPerspectivePurgeMarkerStore {
  public HashSet<Guid> PurgedStreams { get; } = [];
  public List<(Guid StreamId, Guid? EventId)> Marked { get; } = [];
  public List<Guid> Cleared { get; } = [];
  public int Asks { get; private set; }

  public Task<bool> IsPurgedAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default) {
    Asks++;
    return Task.FromResult(PurgedStreams.Contains(streamId));
  }

  public Task MarkPurgedAsync(Guid streamId, string perspectiveName, Guid? purgeEventId, CancellationToken cancellationToken = default) {
    Marked.Add((streamId, purgeEventId));
    PurgedStreams.Add(streamId);
    return Task.CompletedTask;
  }

  public Task<int> BackfillMarkersAsync(IReadOnlyList<(string PerspectiveName, string EventType)> purgingTypes, int batchSize,
      CancellationToken cancellationToken = default) => Task.FromResult(0);

  public Task<long> CountUnmarkedRowlessPairsAsync(IReadOnlyList<(string PerspectiveName, string EventType)> purgingTypes, int cap,
      CancellationToken cancellationToken = default) => Task.FromResult(0L);

  public Task ClearAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default) {
    Cleared.Add(streamId);
    PurgedStreams.Remove(streamId);
    return Task.CompletedTask;
  }
}
