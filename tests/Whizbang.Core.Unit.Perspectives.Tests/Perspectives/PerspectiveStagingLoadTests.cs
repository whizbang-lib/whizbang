// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Perspectives;

namespace Whizbang.Core.Tests.Perspectives;

/// <summary>
/// The staging load of a blue-green rebuild (#2248): the rows a rebuild writes into its empty shadow table are held and
/// loaded in chunks through a bulk-load sink, a row is known absent until the load has written it, and a row the table
/// refuses is reported once so the rebuild can fail its stream.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Perspectives/PerspectiveStagingLoad.cs</code-under-test>
[Category("Unit")]
[Category("Perspectives")]
public class PerspectiveStagingLoadTests {
  private const string SHADOW = "wh_per_test_bg";

  private static readonly Guid _a = Guid.Parse("019f0000-0000-7000-8000-00000000000a");
  private static readonly Guid _b = Guid.Parse("019f0000-0000-7000-8000-00000000000b");
  private static readonly Guid _c = Guid.Parse("019f0000-0000-7000-8000-00000000000c");

  [Test]
  public async Task For_FindsTheLoadOfItsTable_InAnyRendering_OnlyWhileItIsBegunAsync() {
    var load = new PerspectiveStagingLoad(SHADOW, 10);
    await Assert.That(PerspectiveStagingLoad.For(SHADOW)).IsNull().Because("no load is begun in this flow yet");

    using (PerspectiveStagingLoad.Begin(load)) {
      await Assert.That(PerspectiveStagingLoad.For(SHADOW)).IsSameReferenceAs(load);
      await Assert.That(PerspectiveStagingLoad.For("\"" + SHADOW + "\"")).IsSameReferenceAs(load);
      await Assert.That(PerspectiveStagingLoad.For("\"inventory\".\"" + SHADOW + "\"")).IsSameReferenceAs(load);
      await Assert.That(PerspectiveStagingLoad.For("inventory." + SHADOW)).IsSameReferenceAs(load);
      await Assert.That(PerspectiveStagingLoad.For("wh_per_test")).IsNull()
        .Because("the live table is never written by the load, only its shadow");
    }

    await Assert.That(PerspectiveStagingLoad.For(SHADOW)).IsNull().Because("ending the load restores the flow as it was");
  }

  [Test]
  public async Task Constructor_RefusesABlankTable_OrAChunkOfNoRowsAsync() {
    await Assert.That(() => new PerspectiveStagingLoad(" ", 10)).Throws<ArgumentException>();
    await Assert.That(() => new PerspectiveStagingLoad(null!, 10)).Throws<ArgumentException>();
    await Assert.That(() => new PerspectiveStagingLoad(SHADOW, 0)).Throws<ArgumentOutOfRangeException>();
    await Assert.That(() => PerspectiveStagingLoad.Begin(null!)).Throws<ArgumentNullException>();
    await Assert.That(() => PerspectiveStagingLoad.For(null!)).Throws<ArgumentNullException>();
  }

  [Test]
  public async Task TryStage_HoldsAnAbsentRowsInsertOrUpsert_AndNothingElseAsync() {
    var load = new PerspectiveStagingLoad(SHADOW, 10);
    var sink = new RecordingSink();

    await Assert.That(load.StateOf(_a)).IsEqualTo(PerspectiveStagedRowState.Absent);
    await Assert.That(load.TryStage(_write(_a, PerspectiveRowVersion.Absent), sink)).IsTrue();
    await Assert.That(load.TryStage(_write(_b, PerspectiveRowVersion.Unchecked), sink)).IsTrue();

    await Assert.That(load.StateOf(_a)).IsEqualTo(PerspectiveStagedRowState.Staged);
    await Assert.That(load.StagedCount).IsEqualTo(2);
    await Assert.That(load.TryStage(_write(_a, PerspectiveRowVersion.Unchecked), sink)).IsFalse()
      .Because("a second write of a held row takes the per-row write, after the held one is loaded, so the ordering guard sees it");
    await Assert.That(load.TryStage(_write(_c, PerspectiveRowVersion.Of(7)), sink)).IsFalse()
      .Because("an update reads a row that exists, which a staged row never does until it is loaded");
    await Assert.That(load.TryStage(PendingPerspectiveWrite.Purge(SHADOW, _c), sink)).IsFalse();
    await Assert.That(load.StagedCount).IsEqualTo(2);
    await Assert.That(sink.Loads).IsEmpty().Because("staging writes nothing; only a flush loads");
  }

  [Test]
  public async Task TryStage_RefusesANullWriteOrSinkAsync() {
    var load = new PerspectiveStagingLoad(SHADOW, 10);

    await Assert.That(() => load.TryStage(null!, new RecordingSink())).Throws<ArgumentNullException>();
    await Assert.That(() => load.TryStage(_write(_a, PerspectiveRowVersion.Absent), null!)).Throws<ArgumentNullException>();
  }

  [Test]
  public async Task Flush_LoadsTheHeldRowsThroughTheSinkTheyWereStagedWith_AndTheyAreThenLoadedAsync() {
    var load = new PerspectiveStagingLoad(SHADOW, 10);
    var sink = new RecordingSink();
    load.TryStage(_write(_a, PerspectiveRowVersion.Absent), sink);
    load.TryStage(_write(_b, PerspectiveRowVersion.Absent), new RecordingSink());

    await load.FlushAsync(CancellationToken.None);

    await Assert.That(sink.Loads.Single()).IsEquivalentTo([_a, _b]);
    await Assert.That(load.StateOf(_a)).IsEqualTo(PerspectiveStagedRowState.Loaded);
    await Assert.That(load.StateOf(_b)).IsEqualTo(PerspectiveStagedRowState.Loaded);
    await Assert.That(load.StagedCount).IsEqualTo(0);
    await Assert.That(load.RowsLoaded).IsEqualTo(2);
    await Assert.That(load.TryStage(_write(_a, PerspectiveRowVersion.Unchecked), sink)).IsFalse()
      .Because("a loaded row is in the table, so its next write is the per-row write and its ordering guard");
    await Assert.That(load.TakeRefused()).IsEmpty();
  }

  [Test]
  public async Task Flush_WithNothingHeld_LoadsNothingAsync() {
    var load = new PerspectiveStagingLoad(SHADOW, 10);
    var sink = new RecordingSink();
    load.TryStage(_write(_a, PerspectiveRowVersion.Absent), sink);
    await load.FlushAsync(CancellationToken.None);

    await load.FlushAsync(CancellationToken.None);

    await Assert.That(sink.Loads.Count).IsEqualTo(1);
  }

  [Test]
  public async Task Flush_ARowTheSinkDoesNotWrite_IsReportedOnce_AndIsAbsentAsync() {
    var load = new PerspectiveStagingLoad(SHADOW, 10);
    var rejected = new InvalidOperationException("check constraint");
    var outcomes = new Dictionary<Guid, PerspectiveWriteOutcome> {
      [_a] = PerspectiveWriteOutcome.Failed(rejected),
      [_b] = PerspectiveWriteOutcome.Conflict(PerspectiveRowVersion.Of(3)),
      [_c] = PerspectiveWriteOutcome.Written,
    };
    var sink = new RecordingSink { Outcome = id => outcomes[id] };
    load.TryStage(_write(_a, PerspectiveRowVersion.Absent), sink);
    load.TryStage(_write(_b, PerspectiveRowVersion.Absent), sink);
    load.TryStage(_write(_c, PerspectiveRowVersion.Absent), sink);

    await load.FlushAsync(CancellationToken.None);
    var refused = load.TakeRefused();

    await Assert.That(refused.Select(r => r.RowId)).IsEquivalentTo([_a, _b]);
    await Assert.That(refused.Single(r => r.RowId == _a).Error).IsSameReferenceAs(rejected);
    await Assert.That(refused.Single(r => r.RowId == _b).Error.Message).Contains("Conflict")
      .Because("a row the load did not write without an error of its own is still reported with the outcome it had");
    await Assert.That(load.StateOf(_a)).IsEqualTo(PerspectiveStagedRowState.Absent).Because("the table does not hold it");
    await Assert.That(load.StateOf(_c)).IsEqualTo(PerspectiveStagedRowState.Loaded);
    await Assert.That(load.RowsLoaded).IsEqualTo(1);
    await Assert.That(load.TakeRefused()).IsEmpty().Because("each refusal is reported once");
  }

  [Test]
  public async Task Unstage_DropsAHeldRow_AndNothingElseAsync() {
    var load = new PerspectiveStagingLoad(SHADOW, 10);
    var sink = new RecordingSink();
    load.TryStage(_write(_a, PerspectiveRowVersion.Absent), sink);

    await Assert.That(load.Unstage(_a)).IsTrue();
    await Assert.That(load.Unstage(_b)).IsFalse();
    await Assert.That(load.StateOf(_a)).IsEqualTo(PerspectiveStagedRowState.Absent);
    await load.FlushAsync(CancellationToken.None);
    await Assert.That(sink.Loads).IsEmpty().Because("a purged row that was only held is never loaded");
  }

  [Test]
  public async Task ChunkRows_IsTheChunkTheLoadWasBegunWithAsync() {
    await Assert.That(new PerspectiveStagingLoad(SHADOW, 250).ChunkRows).IsEqualTo(250);
    await Assert.That(new PerspectiveStagingLoad(SHADOW, 250).Table).IsEqualTo(SHADOW);
  }

  private static PendingPerspectiveWrite _write(Guid id, PerspectiveRowVersion expected) =>
    PendingPerspectiveWrite.Write("\"public\".\"" + SHADOW + "\"", id, expected, new PerspectiveRowValues(
      "{}", "{}", "{}", false, false, DateTime.UnixEpoch, DateTime.UnixEpoch, false, true, null, []));

  private sealed class RecordingSink : IPerspectiveBulkLoadSink {
    public List<Guid[]> Loads { get; } = [];
    public Func<Guid, PerspectiveWriteOutcome> Outcome { get; init; } = _ => PerspectiveWriteOutcome.Written;

    public Task<IReadOnlyList<PerspectiveWriteOutcome>> LoadAsync(
        IReadOnlyList<PendingPerspectiveWrite> writes, CancellationToken cancellationToken = default) {
      Loads.Add([.. writes.Select(w => w.StreamId)]);
      return Task.FromResult<IReadOnlyList<PerspectiveWriteOutcome>>([.. writes.Select(w => Outcome(w.StreamId))]);
    }
  }
}
