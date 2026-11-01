// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Tests.Observability;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Perspectives;

/// <summary>
/// How the generated runner folds the collectives a replay interleaves into a row. A collective is another stream's
/// event: it folds into the row, and it never moves the row's checkpoint, as on the live path, where the set-based sink
/// never touches a row's cursor.
/// </summary>
/// <docs>fundamentals/messaging/collective-events#collective-replay-from-a-start</docs>
public class CollectiveReplayRunnerTests {
  private const string FOLDED_RUNNER = "Whizbang.Core.Tests.Generated.FoldedPermissionsPerspectiveRunner";
  private const string OVERWRITE_RUNNER = "Whizbang.Core.Tests.Generated.OverwritePermissionsPerspectiveRunner";

  // The last UUIDv7 there is (a message id must be one), so any read anchored on it reads nothing.
  private static readonly Guid _lastId = Guid.Parse("ffffffff-ffff-7fff-bfff-ffffffffffff");

  // #1957: the fold after a purge took the collective as the row's last event, so the checkpoint named another stream's
  // event, which may be archived and is never one of the row's own.
  [Test]
  public async Task ARebuild_ACollectiveAfterAPurge_LeavesTheCheckpointOnTheStreamsOwnEventAsync() {
    var applier = new FakeCollectiveApplier();
    var harness = new Harness(applier);
    var userId = TrackedGuid.New().Value;
    await harness.AppendAsync(userId, new PermissionsSet { UserId = userId, Revision = 1, Permissions = "read" });
    var revoked = await harness.AppendAsync(userId, new PermissionsRevoked { UserId = userId });
    var collective = applier.Add();

    var completions = await harness.RebuildAsync(userId);

    await Assert.That(harness.Rows.Has(userId)).IsFalse().Because("the control: the purge folded, so the collective met a purged row");
    await Assert.That(completions.Single().EventsProcessed).IsEqualTo(3)
      .Because("the control: the collective was interleaved after the purge and taken by the fold");
    await Assert.That(completions.Single().LastEventId).IsEqualTo(revoked.MessageId.Value)
      .Because("a collective folds without moving the row's checkpoint, after a purge as everywhere else");
    await Assert.That(completions.Single().LastEventId).IsNotEqualTo(collective.MessageId.Value);
  }

  // ===== #1956: a rewind folds the collectives committed after where it starts =====

  [Test]
  public async Task ARewindWithoutASnapshot_FoldsTheCollectivesFromTheStreamsStart_WithoutMovingTheCheckpointAsync() {
    var applier = new FakeCollectiveApplier();
    var harness = new Harness(applier);
    var userId = TrackedGuid.New().Value;
    await harness.AppendAsync(userId, new PermissionsSet { UserId = userId, Revision = 1, Permissions = "read" });
    var last = await harness.AppendAsync(userId, new PermissionsSet { UserId = userId, Revision = 2, Permissions = "write" });
    applier.Add();

    var completion = await harness.RewindAsync(userId);

    await Assert.That(harness.Rows.Row(userId).Permissions).IsEqualTo("write+c")
      .Because("a rewind is a replay, and a replay folds the row's collectives as a rebuild does");
    await Assert.That(applier.Starts.Single().AfterEventId).IsNull().Because("a replay from the start folds every collective");
    await Assert.That(completion.LastEventId).IsEqualTo(last.MessageId.Value)
      .Because("a collective folds into the row without moving its checkpoint");
  }

  [Test]
  public async Task ARewindFromASnapshot_FoldsTheCollectivesCommittedAfterTheSnapshotsEventAsync() {
    var applier = new FakeCollectiveApplier();
    var snapshotEventId = TrackedGuid.New().Value;
    var snapshots = new OneSnapshot(snapshotEventId, new PermissionSetModel { Revision = 5, Permissions = "from-snapshot" });
    var harness = new Harness(applier, snapshots: snapshots);
    var userId = TrackedGuid.New().Value;
    await harness.AppendAsync(userId, new PermissionsSet { UserId = userId, Revision = 6, Permissions = "admin" });
    applier.Add();

    _ = await harness.RewindAsync(userId);

    await Assert.That(applier.Starts.Single().AfterEventId).IsEqualTo(snapshotEventId)
      .Because("the snapshot already holds the collectives committed before its event");
    await Assert.That(applier.Starts.Single().CollectivesThroughSequence).IsNull()
      .Because("a snapshot that records no collectives mark keeps the snapshot-event rule (#1963)");
    await Assert.That(harness.Rows.Row(userId).Permissions).IsEqualTo("admin+c");
  }

  [Test]
  public async Task ARewindOfAnOverwriteOnlyPerspective_WhoseModelHasCollectives_FoldsEveryEventAndTheCollectivesAsync() {
    var applier = new FakeCollectiveApplier { NamesCollectives = true };
    var harness = new Harness(applier, OVERWRITE_RUNNER);
    var userId = TrackedGuid.New().Value;
    for (var revision = 1; revision <= 3; revision++) {
      await harness.AppendAsync(userId, new PermissionsSet { UserId = userId, Revision = revision, Permissions = $"perm-{revision}" });
    }
    applier.Add();

    _ = await harness.RewindAsync(userId);

    await Assert.That(harness.Rows.Row(userId).Permissions).IsEqualTo("perm-3+c")
      .Because("a collective committed after the last event changes the row the last event alone would write");
    await Assert.That(harness.Counter.Applies).IsEqualTo(3).Because("the stream is folded in full, collectives and all");
  }

  [Test]
  public async Task ARewindOfAnOverwriteOnlyPerspective_WhoseModelHasNoCollectives_StillAppliesOnlyTheLastEventAsync() {
    var applier = new FakeCollectiveApplier { NamesCollectives = false };
    var harness = new Harness(applier, OVERWRITE_RUNNER);
    var userId = TrackedGuid.New().Value;
    for (var revision = 1; revision <= 3; revision++) {
      await harness.AppendAsync(userId, new PermissionsSet { UserId = userId, Revision = revision, Permissions = $"perm-{revision}" });
    }

    _ = await harness.RewindAsync(userId);

    await Assert.That(harness.Counter.Applies).IsEqualTo(1)
      .Because("an applier is registered for the service, but this model has no collective to fold");
    await Assert.That(applier.Starts).IsEmpty();
  }

  // The rewind re-reads the stream after each pass for events appended meanwhile, and the applier hands back every
  // collective after the start on each pass: each folds once. A collective is another stream's event, so it never anchors
  // the next read, even when its id sorts after every event of the stream.
  [Test]
  public async Task ARewindsCatchUpPasses_FoldEachCollectiveOnce_AndNeverAnchorTheReadOnOneAsync() {
    var applier = new FakeCollectiveApplier();
    var harness = new Harness(applier);
    var userId = TrackedGuid.New().Value;
    await harness.AppendAsync(userId, new PermissionsSet { UserId = userId, Revision = 1, Permissions = "read" });
    applier.Collectives.Add(GeneratedRunners.Envelope(new PermissionsBulkGranted(), messageId: MessageId.From(_lastId)));
    // Another writer appends while the first pass applies, so the rewind makes a catch-up pass.
    harness.Counter.OnFirstApply = () => harness.AppendAsync(
      userId, new PermissionsSet { UserId = userId, Revision = 2, Permissions = "write" }).GetAwaiter().GetResult();

    _ = await harness.RewindAsync(userId);

    await Assert.That(applier.Starts.Count).IsEqualTo(2).Because("the control: the catch-up pass asked for the collectives again");
    await Assert.That(applier.Applied).IsEqualTo(1).Because("the collective the first pass folded is not folded again");
    await Assert.That(harness.Rows.Row(userId).Permissions).IsEqualTo("write")
      .Because("the catch-up read resumes after the stream's own events; anchored on the collective's id it would read nothing after it");
  }

  // A snapshot taken at an event holds the collectives committed before it and none after (#1956), or a rewind from it
  // would fold one of those again.
  [Test]
  public async Task ARewindsSnapshot_IsTheRowAtItsLastEvent_BeforeTheCollectivesCommittedAfterItAsync() {
    var applier = new FakeCollectiveApplier();
    var snapshots = new RecordingSnapshots();
    var harness = new Harness(applier, snapshots: snapshots, snapshotOptions: new PerspectiveSnapshotOptions { Enabled = true });
    var userId = TrackedGuid.New().Value;
    var last = await harness.AppendAsync(userId, new PermissionsSet { UserId = userId, Revision = 1, Permissions = "read" });
    applier.Add();

    _ = await harness.RewindAsync(userId);

    await Assert.That(harness.Rows.Row(userId).Permissions).IsEqualTo("read+c").Because("the control: the row holds the collective");
    await Assert.That(snapshots.Taken.Single().EventId).IsEqualTo(last.MessageId.Value);
    await Assert.That(snapshots.Taken.Single().Model.Permissions).IsEqualTo("read")
      .Because("the collective was committed after the snapshot's event, so a rewind from the snapshot folds it");
  }

  [Test]
  public async Task ARebuildsSnapshot_IsTheRowAtItsLastEvent_BeforeTheCollectivesCommittedAfterItAsync() {
    var applier = new FakeCollectiveApplier();
    var snapshots = new RecordingSnapshots();
    var harness = new Harness(applier, snapshots: snapshots,
      snapshotOptions: new PerspectiveSnapshotOptions { Enabled = true, SnapshotEveryNEvents = 1 });
    var userId = TrackedGuid.New().Value;
    var last = await harness.AppendAsync(userId, new PermissionsSet { UserId = userId, Revision = 1, Permissions = "read" });
    applier.Add();

    _ = await harness.RebuildAsync(userId);

    await Assert.That(harness.Rows.Row(userId).Permissions).IsEqualTo("read+c").Because("the control: the row holds the collective");
    await Assert.That(snapshots.Taken.Single().EventId).IsEqualTo(last.MessageId.Value);
    await Assert.That(snapshots.Taken.Single().Model.Permissions).IsEqualTo("read");
  }

  [Test]
  public async Task AnApplierWrittenBeforeRewindsFoldedCollectives_InterleavesNoneAfterAStartAsync() {
    IReadOnlyList<MessageEnvelope<IEvent>> streamEvents = [GeneratedRunners.Envelope(new PermissionsNoted())];

    ICollectiveReplayApplier minimal = new MinimalApplier();

    var merged = await minimal.InterleaveAfterAsync(
      typeof(PermissionSetModel), streamEvents, new CollectiveReplayStart(TenantId: null), CancellationToken.None);

    await Assert.That(merged).IsSameReferenceAs(streamEvents);
  }

  // ===== #1963: a live snapshot records how far the row's collectives were complete =====

  [Test]
  public async Task ALiveSnapshot_OfARowWithCollectives_RecordsTheFencesMark_ReadBeforeTheRowAndAgainAfterItsWriteAsync() {
    var fence = new FakeFence(_reading(30, false), _reading(30, false));
    var harness = _liveHarness(fence, everyNEvents: 1);
    var userId = TrackedGuid.New().Value;

    var set = await harness.ApplyAsync(userId, new PermissionsSet { UserId = userId, Revision = 1, Permissions = "read" });

    await Assert.That(harness.Snapshots.Taken.Single().EventId).IsEqualTo(set.MessageId.Value);
    await Assert.That(harness.Snapshots.Taken.Single().CollectivesThrough).IsEqualTo(30L)
      .Because("every collective of the row stamped at or below the first read's mark was complete, and none moved before the second");
    await Assert.That(fence.Calls).Count().IsEqualTo(2);
    await Assert.That(fence.Calls[0].After).IsNull();
    await Assert.That(fence.Calls[0].RowReads).IsEqualTo(0).Because("the first read comes before the row is read");
    await Assert.That(fence.Calls[1].After).IsEqualTo(30L).Because("the second read asks whether a collective was stamped past the first's mark");
    await Assert.That(fence.Calls[1].RowWrites).IsEqualTo(1).Because("the second read comes after the row's write");
    await Assert.That(fence.Calls[0].Types).IsEquivalentTo(["PermissionsBulkGranted"]).Because("only the model's collectives can make the snapshot wrong");
    await Assert.That(harness.Deferred()).IsEqualTo(0);
  }

  [Test]
  public async Task ALiveSnapshot_IsDeferred_WhenACollectiveOfTheRowIsInFlightBeforeTheReadAsync() {
    var fence = new FakeFence(_reading(30, true));
    var harness = _liveHarness(fence, everyNEvents: 1);
    var userId = TrackedGuid.New().Value;

    await harness.ApplyAsync(userId, new PermissionsSet { UserId = userId, Revision = 1, Permissions = "read" });

    await Assert.That(harness.Rows.Row(userId).Permissions).IsEqualTo("read").Because("the control: the apply wrote the row");
    await Assert.That(harness.Snapshots.Taken).IsEmpty()
      .Because("the row read while a collective is in flight may hold some of it, and nothing says which");
    await Assert.That(fence.Calls).Count().IsEqualTo(1).Because("a snapshot already deferred needs no second read");
    await Assert.That(harness.Deferred()).IsEqualTo(1).Because("a deferral is counted, so a snapshot that keeps being deferred shows");
  }

  [Test]
  public async Task ALiveSnapshot_IsDeferred_WhenACollectiveOfTheRowMovedByTheReadAfterTheWriteAsync() {
    var fence = new FakeFence(_reading(30, false), _reading(31, true));
    var harness = _liveHarness(fence, everyNEvents: 1);
    var userId = TrackedGuid.New().Value;

    await harness.ApplyAsync(userId, new PermissionsSet { UserId = userId, Revision = 1, Permissions = "read" });

    await Assert.That(harness.Snapshots.Taken).IsEmpty()
      .Because("a collective that started or completed between the reads may have changed the row before it was read");
    await Assert.That(harness.Deferred()).IsEqualTo(1);
  }

  [Test]
  public async Task ADeferredSnapshot_StaysDue_AndTheNextQuietApplyTakesItAsync() {
    var fence = new FakeFence(_reading(30, true), _reading(40, false), _reading(40, false));
    var harness = _liveHarness(fence, everyNEvents: 2);
    var userId = TrackedGuid.New().Value;

    await harness.ApplyAsync(userId, new PermissionsSet { UserId = userId, Revision = 1, Permissions = "one" });
    await harness.ApplyAsync(userId, new PermissionsSet { UserId = userId, Revision = 2, Permissions = "two" });
    var third = await harness.ApplyAsync(userId, new PermissionsSet { UserId = userId, Revision = 3, Permissions = "three" });

    await Assert.That(fence.Calls).Count().IsEqualTo(3).Because("the control: the first apply was not due, and the second was deferred");
    await Assert.That(harness.Snapshots.Taken.Single().EventId).IsEqualTo(third.MessageId.Value)
      .Because("a deferred snapshot is still due, so the next apply takes it instead of starting the count again");
    await Assert.That(harness.Snapshots.Taken.Single().CollectivesThrough).IsEqualTo(40L);
    await Assert.That(harness.Deferred()).IsEqualTo(1);
  }

  [Test]
  public async Task ALiveSnapshot_OfAStoreWithNoStampedEvent_RecordsAMarkOfZero_AndTheSecondReadLooksPastZeroAsync() {
    var fence = new FakeFence(_reading(null, false), _reading(null, false));
    var harness = _liveHarness(fence, everyNEvents: 1);
    var userId = TrackedGuid.New().Value;

    await harness.ApplyAsync(userId, new PermissionsSet { UserId = userId, Revision = 1, Permissions = "read" });

    await Assert.That(harness.Snapshots.Taken.Single().CollectivesThrough).IsEqualTo(0L)
      .Because("no collective was complete, so a replay from the snapshot folds every one; no mark at all would mean the snapshot-event rule");
    await Assert.That(fence.Calls[1].After).IsEqualTo(0L).Because("any collective stamped between the reads is past zero");
  }

  [Test]
  public async Task ALiveSnapshot_OfAModelWithNoCollectives_ReadsNoFence_AndRecordsNoMarkAsync() {
    var fence = new FakeFence();
    var harness = _liveHarness(fence, everyNEvents: 1, applier: new FakeCollectiveApplier { NamesCollectives = false });
    var userId = TrackedGuid.New().Value;

    await harness.ApplyAsync(userId, new PermissionsSet { UserId = userId, Revision = 1, Permissions = "read" });

    await Assert.That(harness.Snapshots.Taken.Single().CollectivesThrough).IsNull();
    await Assert.That(fence.Calls).IsEmpty().Because("a model no collective changes pays nothing for the fence");
  }

  [Test]
  public async Task ALiveSnapshot_InAHostWithNoFence_RecordsNoMarkAsync() {
    var harness = _liveHarness(fence: null, everyNEvents: 1);
    var userId = TrackedGuid.New().Value;

    await harness.ApplyAsync(userId, new PermissionsSet { UserId = userId, Revision = 1, Permissions = "read" });

    await Assert.That(harness.Snapshots.Taken.Single().CollectivesThrough).IsNull()
      .Because("without the fence the snapshot cannot say what it holds, so it keeps the snapshot-event rule");
  }

  [Test]
  public async Task ARebuildsSnapshot_ReadsNoFence_AndRecordsNoMarkAsync() {
    var fence = new FakeFence();
    var harness = _liveHarness(fence, everyNEvents: 1);
    var userId = TrackedGuid.New().Value;
    await harness.AppendAsync(userId, new PermissionsSet { UserId = userId, Revision = 1, Permissions = "read" });

    _ = await harness.RebuildAsync(userId);

    await Assert.That(harness.Snapshots.Taken.Single().CollectivesThrough).IsNull()
      .Because("a full rebuild folds every collective in commit order, so its snapshot holds exactly those before its event");
    await Assert.That(fence.Calls).IsEmpty();
  }

  [Test]
  public async Task ABootstrapSnapshot_OfARowWithCollectives_RecordsTheFencesMark_ForTheRowsTenantAsync() {
    var fence = new FakeFence(_reading(30, false), _reading(30, false));
    var harness = _liveHarness(fence, everyNEvents: 100, rowTenant: "tenant-a");
    var userId = TrackedGuid.New().Value;
    harness.Rows.Seed(userId, new PermissionSetModel { UserId = userId, Revision = 1, Permissions = "read" });
    var at = TrackedGuid.New().Value;

    await harness.Runner.BootstrapSnapshotAsync(userId, harness.PerspectiveName, at, CancellationToken.None);

    await Assert.That(harness.Snapshots.Taken.Single().EventId).IsEqualTo(at);
    await Assert.That(harness.Snapshots.Taken.Single().CollectivesThrough).IsEqualTo(30L);
    await Assert.That(fence.Calls.Select(c => c.Tenant ?? "(none)")).IsEquivalentTo(["tenant-a", "tenant-a"])
      .Because("the row's collectives are its tenant's, read from the row the snapshot is taken of");
    await Assert.That(fence.Calls[0].RowReads).IsEqualTo(0).Because("the first read comes before the model is read");
    await Assert.That(fence.Calls[1].RowReads).IsEqualTo(1);
  }

  [Test]
  public async Task ABootstrapSnapshot_IsDeferred_WhenACollectiveOfTheRowIsInFlightAsync() {
    var fence = new FakeFence(_reading(30, false), _reading(30, true));
    var harness = _liveHarness(fence, everyNEvents: 100, rowTenant: "tenant-a");
    var userId = TrackedGuid.New().Value;
    harness.Rows.Seed(userId, new PermissionSetModel { UserId = userId, Revision = 1, Permissions = "read" });

    await harness.Runner.BootstrapSnapshotAsync(userId, harness.PerspectiveName, TrackedGuid.New().Value, CancellationToken.None);

    await Assert.That(harness.Snapshots.Taken).IsEmpty();
    await Assert.That(harness.Deferred()).IsEqualTo(1);
  }

  [Test]
  public async Task ABootstrapSnapshot_OfARowWhoseTenantTheStoreDoesNotSay_IsDeferredAsync() {
    var fence = new FakeFence();
    var harness = _liveHarness(fence, everyNEvents: 100);
    var userId = TrackedGuid.New().Value;
    harness.Rows.Seed(userId, new PermissionSetModel { UserId = userId, Revision = 1, Permissions = "read" });

    await harness.Runner.BootstrapSnapshotAsync(userId, harness.PerspectiveName, TrackedGuid.New().Value, CancellationToken.None);

    await Assert.That(harness.Snapshots.Taken).IsEmpty()
      .Because("without the row's tenant the fence cannot be read, and a snapshot that cannot say what it holds is not taken");
    await Assert.That(fence.Calls).IsEmpty();
    await Assert.That(harness.Deferred()).IsEqualTo(1);
  }

  // ===== #1963: a replay starts from its snapshot's mark, and its own snapshots record the mark they hold =====

  [Test]
  public async Task ARewindFromASnapshotThatRecordsItsCollectives_StartsFromItsMarkAsync() {
    var applier = new FakeCollectiveApplier();
    var snapshotEventId = TrackedGuid.New().Value;
    var snapshots = new MarkedSnapshot(snapshotEventId, new PermissionSetModel { Revision = 5, Permissions = "from-snapshot" }, collectivesThrough: 20);
    var harness = new Harness(applier, snapshots: snapshots);
    var userId = TrackedGuid.New().Value;
    await harness.AppendAsync(userId, new PermissionsSet { UserId = userId, Revision = 6, Permissions = "admin" });

    _ = await harness.RewindAsync(userId);

    await Assert.That(applier.Starts.Single().CollectivesThroughSequence).IsEqualTo(20L)
      .Because("the snapshot holds the collectives complete through its mark, and a collective in flight when it was taken is after it");
    await Assert.That(applier.Starts.Single().AfterEventId).IsEqualTo(snapshotEventId);
  }

  [Test]
  public async Task ADefaultRebuildFromTheNewestSnapshot_StartsFromItsMarkAsync() {
    var applier = new FakeCollectiveApplier();
    var snapshotEventId = TrackedGuid.New().Value;
    var snapshots = new MarkedSnapshot(snapshotEventId, new PermissionSetModel { Revision = 5, Permissions = "from-snapshot" }, collectivesThrough: 20);
    var harness = new Harness(applier, snapshots: snapshots);
    var userId = TrackedGuid.New().Value;
    await harness.AppendAsync(userId, new PermissionsSet { UserId = userId, Revision = 6, Permissions = "admin" });

    _ = await harness.Runner.RunRebuildFromLatestSnapshotAsync(userId, harness.PerspectiveName, CancellationToken.None);

    await Assert.That(applier.Starts.Single().CollectivesThroughSequence).IsEqualTo(20L);
  }

  [Test]
  [Arguments(20L, 30L)]
  [Arguments(50L, 50L)]
  public async Task AReplaysSnapshot_RecordsTheMarkItHolds_TheStartsAndThatOfEverythingFoldedBeforeItsEventAsync(long startMark, long expected) {
    var applier = new FakeCollectiveApplier { MergeByCommitSequence = true };
    var snapshotEventId = TrackedGuid.New().Value;
    var snapshots = new MarkedSnapshot(snapshotEventId, new PermissionSetModel { Revision = 5, Permissions = "from-snapshot" }, collectivesThrough: startMark);
    var harness = new Harness(applier, snapshots: snapshots, snapshotOptions: new PerspectiveSnapshotOptions { Enabled = true });
    var userId = TrackedGuid.New().Value;
    var first = await harness.AppendAsync(userId, new PermissionsSet { UserId = userId, Revision = 6, Permissions = "six" });
    var last = await harness.AppendAsync(userId, new PermissionsSet { UserId = userId, Revision = 7, Permissions = "seven" });
    harness.Sequences[first.MessageId.Value] = 25;
    harness.Sequences[last.MessageId.Value] = 30;
    applier.Add(commitSequence: 22);
    applier.Add(commitSequence: 60);

    _ = await harness.RewindAsync(userId);

    await Assert.That(harness.Rows.Row(userId).Permissions).IsEqualTo("seven+c")
      .Because("the control: the collective after the last event folded into the row");
    await Assert.That(snapshots.Taken.Single().EventId).IsEqualTo(last.MessageId.Value);
    await Assert.That(snapshots.Taken.Single().Model.Permissions).IsEqualTo("seven").Because("the trailing collective is not in the snapshot");
    await Assert.That(snapshots.Taken.Single().CollectivesThrough).IsEqualTo(expected)
      .Because("the snapshot holds what its start held and every collective folded before its event, and no collective stamped above the mark");
  }

  [Test]
  public async Task AReplaysSnapshot_AfterAnUnstampedCollectiveFoldedBeforeItsEvent_RecordsNoMarkAsync() {
    var applier = new FakeCollectiveApplier { MergeByCommitSequence = true };
    var snapshotEventId = TrackedGuid.New().Value;
    var snapshots = new MarkedSnapshot(snapshotEventId, new PermissionSetModel { Revision = 5, Permissions = "from-snapshot" }, collectivesThrough: 20);
    var harness = new Harness(applier, snapshots: snapshots, snapshotOptions: new PerspectiveSnapshotOptions { Enabled = true });
    var userId = TrackedGuid.New().Value;
    applier.Add(commitSequence: null);
    var last = await harness.AppendAsync(userId, new PermissionsSet { UserId = userId, Revision = 6, Permissions = "six" });
    harness.Sequences[last.MessageId.Value] = 25;
    applier.PutFirst = true;

    _ = await harness.RewindAsync(userId);

    await Assert.That(snapshots.Taken.Single().Model.Permissions).IsEqualTo("six")
      .Because("the control: the collective folded before the event, which overwrites the row");
    await Assert.That(snapshots.Taken.Single().CollectivesThrough).IsNull()
      .Because("a commit sequence cannot say the snapshot holds a collective that had none, so it records no mark");
  }

  // ===== CollectiveReplayStart =====  // ===== CollectiveReplayStart =====

  [Test]
  public async Task AReplayFromTheStart_FollowsEveryCollectiveAsync() {
    var collective = GeneratedRunners.Envelope(new PermissionsBulkGranted());

    await Assert.That(new CollectiveReplayStart("tenant-a").Follows(collective)).IsTrue();
  }

  [Test]
  public async Task AStart_ComparesCommitSequences_WhenBothCarryOneAsync() {
    var start = new CollectiveReplayStart("tenant-a", TrackedGuid.New().Value, AfterCommitSequence: 10);
    // Ids minted before the start's, so only the commit sequence can put them after it.
    var before = _collective(commitSequence: 10);
    var after = _collective(commitSequence: 11);

    await Assert.That(start.Follows(after)).IsTrue();
    await Assert.That(start.Follows(before)).IsFalse().Because("the start's own position is not after it");
  }

  [Test]
  public async Task AStart_ComparesMessageIds_WhenTheCollectiveCarriesNoCommitSequenceAsync() {
    var earlier = _collective(commitSequence: null);
    var start = new CollectiveReplayStart("tenant-a", TrackedGuid.New().Value, AfterCommitSequence: 10);
    var later = _collective(commitSequence: null);

    await Assert.That(start.Follows(earlier)).IsFalse();
    await Assert.That(start.Follows(later)).IsTrue();
  }

  [Test]
  public async Task AStart_ComparesMessageIds_WhenItCarriesNoCommitSequenceAsync() {
    var earlier = _collective(commitSequence: 5);
    var start = new CollectiveReplayStart("tenant-a", TrackedGuid.New().Value, AfterCommitSequence: null);
    var later = _collective(commitSequence: 1);

    await Assert.That(start.Follows(earlier)).IsFalse();
    await Assert.That(start.Follows(later)).IsTrue().Because("without the start's sequence the ids decide, whatever the collective's");
  }

  // #1963: a snapshot that records how far its row's collectives were complete is started from that mark, not from its
  // event: a collective in flight when it was taken counts as after it, whatever its position against the event.
  [Test]
  public async Task AStart_FromASnapshotThatRecordsItsCollectives_FollowsTheStampedCollectivesAboveTheMark_OnlyAsync() {
    var start = new CollectiveReplayStart("tenant-a", TrackedGuid.New().Value, AfterCommitSequence: 50) { CollectivesThroughSequence = 20 };
    var held = _collective(commitSequence: 20);
    var inFlight = _collective(commitSequence: 21);

    await Assert.That(start.Follows(inFlight)).IsTrue()
      .Because("a collective not complete when the snapshot was taken counts as after it, though it sits before the snapshot's event");
    await Assert.That(start.Follows(held)).IsFalse().Because("the snapshot holds every collective complete through its mark");
  }

  [Test]
  public async Task AStart_FromASnapshotThatRecordsItsCollectives_FollowsAnUnstampedCollective_WhateverItsIdAsync() {
    var earlier = _collective(commitSequence: null);
    var start = new CollectiveReplayStart("tenant-a", TrackedGuid.New().Value, AfterCommitSequence: 50) { CollectivesThroughSequence = 20 };

    await Assert.That(start.Follows(earlier)).IsTrue()
      .Because("every collective the snapshot holds was stamped when it was taken, so one still unstamped came after it");
  }

  [Test]
  public async Task AStart_RefusesANullCollectiveAsync() {
    await Assert.That(() => new CollectiveReplayStart(null).Follows(null!)).ThrowsExactly<ArgumentNullException>();
  }

  private static MessageEnvelope<IEvent> _collective(long? commitSequence) {
    var envelope = GeneratedRunners.Envelope(new PermissionsBulkGranted());
    envelope.LocalCommitSequence = commitSequence;
    return envelope;
  }

  // -------------------------------------------------------------------------------------------

  /// <summary>A collective the fake applier interleaves; its mutation appends a mark to the permissions.</summary>
  private sealed record PermissionsBulkGranted : ICollectiveEvent {
    public CollectiveScope Scope { get; init; } = new TenantCollectiveScope("tenant-a");
  }

  /// <summary>Interleaves the collectives a test adds after the stream's events and marks the row each one folds into.</summary>
  private sealed class FakeCollectiveApplier : ICollectiveReplayApplier {
    public List<MessageEnvelope<IEvent>> Collectives { get; } = [];
    public List<CollectiveReplayStart> Starts { get; } = [];
    public bool NamesCollectives { get; init; } = true;

    /// <summary>Merges the collectives that follow the start among the stream's events in commit order, as the real applier does.</summary>
    public bool MergeByCommitSequence { get; init; }

    /// <summary>Puts the collectives before the stream's events, for a collective with no commit sequence to merge by.</summary>
    public bool PutFirst { get; set; }

    public MessageEnvelope<IEvent> Add(long? commitSequence = null) {
      var envelope = GeneratedRunners.Envelope(new PermissionsBulkGranted());
      envelope.LocalCommitSequence = commitSequence;
      Collectives.Add(envelope);
      return envelope;
    }

    public Task<IReadOnlyList<MessageEnvelope<IEvent>>> InterleaveForReplayAsync(
        Type modelType, IReadOnlyList<MessageEnvelope<IEvent>> streamEvents, CancellationToken cancellationToken) =>
      Task.FromResult<IReadOnlyList<MessageEnvelope<IEvent>>>([.. streamEvents, .. Collectives]);

    public Task<IReadOnlyList<MessageEnvelope<IEvent>>> InterleaveAfterAsync(
        Type modelType, IReadOnlyList<MessageEnvelope<IEvent>> streamEvents, CollectiveReplayStart start, CancellationToken cancellationToken) {
      Starts.Add(start);
      if (!MergeByCommitSequence) {
        return Task.FromResult<IReadOnlyList<MessageEnvelope<IEvent>>>([.. streamEvents, .. Collectives]);
      }
      var following = Collectives.Where(start.Follows).ToList();
      Collectives.RemoveAll(following.Contains);
      return Task.FromResult<IReadOnlyList<MessageEnvelope<IEvent>>>(PutFirst
        ? [.. following, .. streamEvents]
        : [.. streamEvents.Concat(following).OrderBy(e => e.LocalCommitSequence ?? long.MaxValue)]);
    }

    public int Applied { get; private set; }

    public object ApplyInMemory(Type modelType, object currentModel, Guid streamId, IEvent collectiveEvent) {
      Applied++;
      return (PermissionSetModel)currentModel with { Permissions = ((PermissionSetModel)currentModel).Permissions + "+c" };
    }

    public IReadOnlyList<string> CollectiveEventTypeNamesFor(string modelTypeName) =>
      NamesCollectives && modelTypeName == typeof(PermissionSetModel).FullName ? ["PermissionsBulkGranted"] : [];
  }

  /// <summary>An applier that implements only what every applier must.</summary>
  private sealed class MinimalApplier : ICollectiveReplayApplier {
    public Task<IReadOnlyList<MessageEnvelope<IEvent>>> InterleaveForReplayAsync(
        Type modelType, IReadOnlyList<MessageEnvelope<IEvent>> streamEvents, CancellationToken cancellationToken) =>
      Task.FromResult(streamEvents);

    public object ApplyInMemory(Type modelType, object currentModel, Guid streamId, IEvent collectiveEvent) => currentModel;
  }

  private static System.Text.Json.Serialization.Metadata.JsonTypeInfo<PermissionSetModel> _modelInfo =>
    (System.Text.Json.Serialization.Metadata.JsonTypeInfo<PermissionSetModel>)
      Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions().GetTypeInfo(typeof(PermissionSetModel));

  /// <summary>Holds one snapshot, placed before every event the test appends.</summary>
  private sealed class OneSnapshot(Guid snapshotEventId, PermissionSetModel model) : SnapshotsBase {
    private static JsonDocument _snapshotData(PermissionSetModel snapshot) {
      using var doc = JsonSerializer.SerializeToDocument(snapshot, _modelInfo);
      return SnapshotEnvelope.Wrap(doc.RootElement, Whizbang.Core.Serialization.SerializationVersion.CURRENT);
    }

    public override Task<(Guid SnapshotEventId, JsonDocument SnapshotData)?> GetLatestSnapshotAsync(Guid streamId, string perspectiveName, CancellationToken ct = default) =>
      Task.FromResult<(Guid, JsonDocument)?>((snapshotEventId, _snapshotData(model)));

    public override Task<(Guid SnapshotEventId, int? SnapshotVersion, JsonDocument SnapshotData)?> GetLatestSnapshotBeforeEventInStreamOrderAsync(
        Guid streamId, string perspectiveName, Guid beforeEventId, CancellationToken ct = default) =>
      Task.FromResult<(Guid, int?, JsonDocument)?>((snapshotEventId, 0, _snapshotData(model)));
  }

  /// <summary>Holds no snapshot and records each one a run takes, as the model it holds.</summary>
  private sealed class RecordingSnapshots : SnapshotsBase;

  /// <summary>Holds one snapshot that records its collectives mark (#1963), placed before every event the test appends.</summary>
  private sealed class MarkedSnapshot(Guid snapshotEventId, PermissionSetModel model, long collectivesThrough) : SnapshotsBase, IPerspectiveSnapshotStore {
    private static JsonDocument _snapshotData(PermissionSetModel snapshot) {
      using var doc = JsonSerializer.SerializeToDocument(snapshot, _modelInfo);
      return SnapshotEnvelope.Wrap(doc.RootElement, Whizbang.Core.Serialization.SerializationVersion.CURRENT);
    }

    private PerspectiveSnapshotFloor _floor() =>
      new(snapshotEventId, 0, _snapshotData(model), ArchivedThroughVersion: null) { CollectivesThroughSequence = collectivesThrough };

    public Task<PerspectiveSnapshotFloor?> GetSnapshotFloorAsync(Guid streamId, string perspectiveName, Guid beforeEventId, CancellationToken ct = default) =>
      Task.FromResult<PerspectiveSnapshotFloor?>(_floor());

    public Task<PerspectiveSnapshotFloor?> GetLatestSnapshotFloorAsync(Guid streamId, string perspectiveName, CancellationToken ct = default) =>
      Task.FromResult<PerspectiveSnapshotFloor?>(_floor());
  }

  private abstract class SnapshotsBase : IPerspectiveSnapshotStore {
    /// <summary>Every snapshot a run took: its event, the model it holds and its collectives mark.</summary>
    public List<(Guid EventId, PermissionSetModel Model, long? CollectivesThrough)> Taken { get; } = [];

    public Task CreateSnapshotAsync(Guid streamId, string perspectiveName, Guid snapshotEventId, JsonDocument snapshotData, CancellationToken ct = default) =>
      CreateSnapshotAsync(streamId, perspectiveName, snapshotEventId, null, snapshotData, collectivesThroughSequence: null, ct);

    public Task CreateSnapshotAsync(
        Guid streamId, string perspectiveName, Guid snapshotEventId, long? snapshotCommitSequence, JsonDocument snapshotData,
        long? collectivesThroughSequence, CancellationToken ct = default) {
      var read = SnapshotEnvelope.Read(snapshotData, Whizbang.Core.Serialization.SerializationVersion.CURRENT, SnapshotUpgradePolicy.RebuildFromEvents);
      Taken.Add((snapshotEventId, JsonSerializer.Deserialize(read.Model.GetRawText(), _modelInfo)!, collectivesThroughSequence));
      return Task.CompletedTask;
    }


    public virtual Task<(Guid SnapshotEventId, JsonDocument SnapshotData)?> GetLatestSnapshotAsync(Guid streamId, string perspectiveName, CancellationToken ct = default) =>
      Task.FromResult<(Guid, JsonDocument)?>(null);

    public Task<(Guid SnapshotEventId, JsonDocument SnapshotData)?> GetLatestSnapshotBeforeAsync(Guid streamId, string perspectiveName, Guid beforeEventId, CancellationToken ct = default) =>
      GetLatestSnapshotAsync(streamId, perspectiveName, ct);

    public virtual Task<(Guid SnapshotEventId, int? SnapshotVersion, JsonDocument SnapshotData)?> GetLatestSnapshotBeforeEventInStreamOrderAsync(
        Guid streamId, string perspectiveName, Guid beforeEventId, CancellationToken ct = default) =>
      Task.FromResult<(Guid, int?, JsonDocument)?>(null);

    public Task<bool> HasAnySnapshotAsync(Guid streamId, string perspectiveName, CancellationToken ct = default) => Task.FromResult(false);

    public Task PruneOldSnapshotsAsync(Guid streamId, string perspectiveName, int keepCount, CancellationToken ct = default) => Task.CompletedTask;

    public Task DeleteAllSnapshotsAsync(Guid streamId, string perspectiveName, CancellationToken ct = default) => Task.CompletedTask;
  }

  private static Harness _liveHarness(
      ICollectiveSnapshotFence? fence, int everyNEvents, FakeCollectiveApplier? applier = null, string? rowTenant = null) =>
    new(applier ?? new FakeCollectiveApplier(), snapshots: new RecordingSnapshots(),
      snapshotOptions: new PerspectiveSnapshotOptions { Enabled = true, SnapshotEveryNEvents = everyNEvents },
      fence: fence, rowTenant: rowTenant);

  private static CollectiveSnapshotFenceReading _reading(long? completeThrough, bool inFlight) => new(completeThrough, inFlight);

  /// <summary>Answers each read with the next reading it was given, and records what each read asked and when.</summary>
  private sealed class FakeFence(params CollectiveSnapshotFenceReading[] readings) : ICollectiveSnapshotFence {
    private readonly Queue<CollectiveSnapshotFenceReading> _readings = new(readings);
    public List<(string? Tenant, IReadOnlyList<string> Types, long? After, int RowReads, int RowWrites)> Calls { get; } = [];
    public InMemoryRows<PermissionSetModel>? Rows { get; set; }

    public Task<CollectiveSnapshotFenceReading> ReadAsync(
        string? tenantId, IReadOnlyList<string> collectiveEventTypes, long? afterSequence, CancellationToken cancellationToken = default) {
      Calls.Add((tenantId, collectiveEventTypes, afterSequence, Rows!.Reads, Rows.Writes));
      return Task.FromResult(_readings.Dequeue());
    }
  }

  /// <summary>The in-memory store, with each event's commit sequence the test gave it.</summary>
  private sealed class SequencedEvents(IEventStore inner, Dictionary<Guid, long> sequences) : ForwardingEventStoreDecorator(inner) {
    public override async IAsyncEnumerable<MessageEnvelope<IEvent>> ReadPolymorphicAsync(
        Guid streamId, Guid? fromEventId, IReadOnlyList<Type> eventTypes,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default) {
      await foreach (var envelope in Inner.ReadPolymorphicAsync(streamId, fromEventId, eventTypes, cancellationToken)) {
        envelope.LocalCommitSequence = sequences.TryGetValue(envelope.MessageId.Value, out var sequence) ? sequence : null;
        yield return envelope;
      }
    }

    public override Task<long?> GetCommitSequenceAsync(Guid eventId, CancellationToken cancellationToken = default) =>
      Task.FromResult(sequences.TryGetValue(eventId, out var sequence) ? sequence : (long?)null);
  }

  /// <summary>The in-memory rows, reporting each row present under one tenant, as a Postgres store reads a row's scope.</summary>
  private sealed class TenantRows(InMemoryRows<PermissionSetModel> inner, string tenant) : IPerspectiveStore<PermissionSetModel> {
    public Task<PerspectiveApplyRead> ReadForApplyAsync(Guid streamId, CancellationToken cancellationToken = default) =>
      Task.FromResult(new PerspectiveApplyRead(PerspectiveRowVersion.Of(1), null, new Whizbang.Core.Lenses.PerspectiveScope { TenantId = tenant }));

    public Task<PermissionSetModel?> GetByStreamIdAsync(Guid streamId, CancellationToken cancellationToken = default) =>
      inner.GetByStreamIdAsync(streamId, cancellationToken);

    public Task UpsertAsync(Guid streamId, PermissionSetModel model, CancellationToken cancellationToken = default) =>
      inner.UpsertAsync(streamId, model, cancellationToken);

    public Task UpsertWithPhysicalFieldsAsync(Guid streamId, PermissionSetModel model, IDictionary<string, object?> physicalFieldValues, Whizbang.Core.Lenses.PerspectiveScope? scope = null, CancellationToken cancellationToken = default) =>
      inner.UpsertWithPhysicalFieldsAsync(streamId, model, physicalFieldValues, scope, cancellationToken);

    public Task<PermissionSetModel?> GetByPartitionKeyAsync<TPartitionKey>(TPartitionKey partitionKey, CancellationToken cancellationToken = default) where TPartitionKey : notnull =>
      inner.GetByPartitionKeyAsync(partitionKey, cancellationToken);

    public Task UpsertByPartitionKeyAsync<TPartitionKey>(TPartitionKey partitionKey, PermissionSetModel model, CancellationToken cancellationToken = default) where TPartitionKey : notnull =>
      inner.UpsertByPartitionKeyAsync(partitionKey, model, cancellationToken);

    public Task FlushAsync(CancellationToken cancellationToken = default) => inner.FlushAsync(cancellationToken);

    public Task PurgeAsync(Guid streamId, CancellationToken cancellationToken = default) => inner.PurgeAsync(streamId, cancellationToken);

    public Task PurgeByPartitionKeyAsync<TPartitionKey>(TPartitionKey partitionKey, CancellationToken cancellationToken = default) where TPartitionKey : notnull =>
      inner.PurgeByPartitionKeyAsync(partitionKey, cancellationToken);
  }

  private sealed class Harness {
    public InMemoryEventStore Events { get; } = new();
    public Dictionary<Guid, long> Sequences { get; } = [];
    public InMemoryRows<PermissionSetModel> Rows { get; } = new();
    public ApplyCounter Counter { get; } = new();
    public IPerspectiveRunner Runner { get; }
    public RecordingSnapshots Snapshots { get; }
    public string PerspectiveName { get; }
    public MetricAssertionHelper Metrics { get; }

    public Harness(
        ICollectiveReplayApplier applier,
        string runnerTypeName = FOLDED_RUNNER,
        IPerspectiveSnapshotStore? snapshots = null,
        PerspectiveSnapshotOptions? snapshotOptions = null,
        ICollectiveSnapshotFence? fence = null,
        string? rowTenant = null) {
      var services = new ServiceCollection();
      services.AddLogging();
      services.AddSingleton<FoldedPermissionsPerspective>();
      services.AddSingleton<OverwritePermissionsPerspective>();
      var meterFactory = new TestMeterFactory();
      services.AddSingleton(new PerspectiveMetrics(new WhizbangMetrics(meterFactory)));
      Metrics = new MetricAssertionHelper(meterFactory.CreatedMeters[0]);
      if (fence is not null) {
        services.AddSingleton(fence);
      }
      if (fence is FakeFence fake) {
        fake.Rows = Rows;
      }
      Snapshots = snapshots as RecordingSnapshots ?? new RecordingSnapshots();
      PerspectiveName = runnerTypeName == OVERWRITE_RUNNER ? nameof(OverwritePermissionsPerspective) : nameof(FoldedPermissionsPerspective);
      IPerspectiveStore<PermissionSetModel> rows = rowTenant is null ? Rows : new TenantRows(Rows, rowTenant);
      Runner = GeneratedRunners.Create(
        runnerTypeName, services.BuildServiceProvider(), new SequencedEvents(Events, Sequences), rows, snapshots,
        Options.Create(snapshotOptions ?? new PerspectiveSnapshotOptions { Enabled = false }), collectiveReplayApplier: applier);
    }

    /// <summary>Snapshots deferred for this perspective, from the tagged series of the deferral counter.</summary>
    public double Deferred() =>
      Metrics.GetByName("whizbang.perspective.snapshots_deferred")
        .Where(m => m.Tags.TryGetValue("perspective_name", out var name) && name == PerspectiveName)
        .Sum(m => m.Value);

    public async Task<PerspectiveCursorCompletion> RewindAsync(Guid userId) {
      ApplyCounter.Current.Value = Counter;
      return await Runner.RewindAndRunAsync(userId, PerspectiveName, TrackedGuid.New().Value, CancellationToken.None);
    }

    public async Task<MessageEnvelope<IEvent>> AppendAsync(Guid userId, IEvent payload) {
      var envelope = GeneratedRunners.Envelope(payload);
      await Events.AppendAsync(userId, envelope);
      return envelope;
    }

    /// <summary>A live apply of one new event, as the worker hands the runner a batch.</summary>
    public async Task<MessageEnvelope<IEvent>> ApplyAsync(Guid userId, IEvent payload) {
      var envelope = await AppendAsync(userId, payload);
      _ = await Runner.RunWithEventsAsync(userId, PerspectiveName, null, [envelope], CancellationToken.None);
      return envelope;
    }

    public async Task<IReadOnlyList<PerspectiveCursorCompletion>> RebuildAsync(Guid userId) {
      var previous = ProcessingModeAccessor.Current;
      ProcessingModeAccessor.Current = ProcessingMode.Rebuild;
      try {
        return await Runner.RunRebuildAsync(userId, PerspectiveName, CancellationToken.None);
      } finally {
        ProcessingModeAccessor.Current = previous;
      }
    }
  }
}
