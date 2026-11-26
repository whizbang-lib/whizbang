// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Perspectives;

namespace Whizbang.Core.Tests.Perspectives;

/// <summary>
/// The staging load of a blue-green rebuild (#2248): the first pass writes the empty shadow table through a staging load,
/// in chunks, with the shadow's secondary indexes built after the load and before the catch-up; it is the default, and a
/// service can only switch it off.
/// </summary>
public partial class PerspectiveRebuilderBlueGreenTests {
  private const string DEFERRED_INDEX = "CREATE INDEX ix_test_bg ON wh_per_test_bg USING btree (x)";

  [Test]
  public async Task Staging_ByDefault_LoadsTheFirstPass_WithItsIndexesDeferred_AndBuildsThemBeforeTheCatchUpAsync() {
    var timeline = new List<string>();
    var swapper = new FakeSwapper { Deferred = [DEFERRED_INDEX] };
    var sink = new StagingSink(timeline);
    var fixture = new Fixture(swapper);
    swapper.Progress = () => string.Join(",", timeline);
    fixture.Runner.Stage = sink;
    fixture.Add(_a, 1);
    fixture.Add(_b, 2);
    fixture.Runner.OnFirstRun = () => fixture.Add(_a, 10);

    var result = await fixture.Rebuilder.RebuildBlueGreenAsync(PERSPECTIVE);

    await Assert.That(result.Success).IsTrue().Because(result.Error ?? "");
    await Assert.That(fixture.Runner.Runs.Where(r => r.Phase == RebuildPhase.Replaying).Select(r => r.Staging)).All().Satisfy(s => s.IsTrue())
      .Because("the first pass writes an empty table only the rebuild writes, which is what a staging load is for");
    await Assert.That(fixture.Runner.Runs.Where(r => r.Phase != RebuildPhase.Replaying).Select(r => r.Staging)).All().Satisfy(s => s.IsFalse())
      .Because("the catch-up rewrites rows the load already wrote, so it takes the per-row write and its ordering guard");
    await Assert.That(sink.Loads.Select(l => string.Join(",", l))).IsEquivalentTo([$"{_a},{_b}"]);
    await Assert.That(swapper.IndexCalls).IsEquivalentTo(
      ["defer " + SHADOW, $"build {SHADOW} [{DEFERRED_INDEX}] after load 2"], TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("the deferred indexes are built once, after every row is loaded and before the catch-up writes through them");
    await Assert.That(fixture.Runner.Runs.Count(r => r.Phase == RebuildPhase.CatchingUp)).IsEqualTo(1);
  }

  [Test]
  public async Task Staging_LoadsTheHeldRowsInChunks_EachBeforeItsCursorsAreCompletedAsync() {
    var timeline = new List<string>();
    var sink = new StagingSink(timeline);
    var fixture = new Fixture(new FakeSwapper(), new BlueGreenRebuildOptions { StagingChunkRows = 2 }, completer: new RecordingCompleter(timeline));
    fixture.Runner.Stage = sink;
    foreach (var stream in new[] { _a, _b, _c, _d, Guid.Parse("00000000-0000-0000-0000-00000000000e") }) {
      fixture.Add(stream, 1);
    }

    var result = await fixture.Rebuilder.RebuildBlueGreenAsync(PERSPECTIVE);

    await Assert.That(result.Success).IsTrue().Because(result.Error ?? "");
    await Assert.That(timeline).IsEquivalentTo(
      ["load 2", "complete 2", "load 2", "complete 2", "load 1", "complete 1"], TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("a cursor is never written for a row the table does not hold yet, and a chunk is the configured size");
  }

  [Test]
  public async Task Staging_WithoutACompleter_StillLoadsTheHeldRowsInChunksAsync() {
    var timeline = new List<string>();
    var sink = new StagingSink(timeline);
    var fixture = new Fixture(new FakeSwapper(), new BlueGreenRebuildOptions { StagingChunkRows = 2 });
    fixture.Runner.Stage = sink;
    foreach (var stream in new[] { _a, _b, _c, _d, Guid.Parse("00000000-0000-0000-0000-00000000000e") }) {
      fixture.Add(stream, 1);
    }

    var result = await fixture.Rebuilder.RebuildBlueGreenAsync(PERSPECTIVE);

    await Assert.That(result.Success).IsTrue().Because(result.Error ?? "");
    await Assert.That(timeline).IsEquivalentTo(["load 2", "load 2", "load 1"], TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("the rows held are bounded by the chunk, whether or not cursors are written");
  }

  [Test]
  public async Task Staging_ARowTheTableRefuses_FailsItsStreamOnly_AndItsCursorIsNeverCompletedAsync() {
    var timeline = new List<string>();
    var sink = new StagingSink(timeline) { Refuse = [_b] };
    var completer = new RecordingCompleter(timeline);
    var swapper = new FakeSwapper();
    var fixture = new Fixture(swapper, completer: completer);
    fixture.Runner.Stage = sink;
    fixture.Add(_a, 1);
    fixture.Add(_b, 2);
    fixture.Add(_c, 3);

    var result = await fixture.Rebuilder.RebuildBlueGreenAsync(PERSPECTIVE);

    await Assert.That(result.Success).IsFalse().Because("a stream whose row the table refused was not rebuilt");
    await Assert.That(result.Error).Contains(_b.ToString());
    await Assert.That(result.StreamsProcessed).IsEqualTo(2).Because("the other streams' rows were loaded and count as rebuilt");
    await Assert.That(completer.Completed).IsEquivalentTo([_a, _c])
      .Because("a refused row has no row in the table, so its cursor must not say it was rebuilt");
    await Assert.That(swapper.Calls).Contains(c => c.StartsWith("swap ", StringComparison.Ordinal))
      .Because("as with a stream that fails on the per-row path, the rows that were rebuilt are kept");
  }

  [Test]
  public async Task Staging_ARefusedReKeyedRow_FailsThePhysicalStreamItWasReadFromAsync() {
    var timeline = new List<string>();
    var sink = new StagingSink(timeline) { Refuse = [_d] };
    var fixture = new Fixture(new FakeSwapper(), completer: new RecordingCompleter(timeline));
    fixture.Runner.Stage = sink;
    fixture.Runner.ExtraRows[_a] = _d;
    fixture.Add(_a, 1);
    fixture.Add(_b, 2);

    var result = await fixture.Rebuilder.RebuildBlueGreenAsync(PERSPECTIVE);

    await Assert.That(result.Success).IsFalse();
    await Assert.That(result.Error).Contains(_a.ToString())
      .Because("the failed stream is the one the rebuild read, which is the one a rerun replays");
    await Assert.That(result.StreamsProcessed).IsEqualTo(1);
  }

  [Test]
  public async Task Staging_ARefusedRow_WithoutACompleter_StillFailsItsStream_Once_ForEveryRowOfItAsync() {
    var timeline = new List<string>();
    var sink = new StagingSink(timeline) { Refuse = [_a, _d] };
    var fixture = new Fixture(new FakeSwapper());
    fixture.Runner.Stage = sink;
    fixture.Runner.ExtraRows[_a] = _d;
    fixture.Add(_a, 1);
    fixture.Add(_b, 2);

    var result = await fixture.Rebuilder.RebuildBlueGreenAsync(PERSPECTIVE);

    await Assert.That(result.Success).IsFalse();
    await Assert.That(result.StreamsProcessed).IsEqualTo(1)
      .Because("a stream two of whose rows were refused failed once, not twice");
    await Assert.That(result.Error).Contains(_a.ToString());
  }

  [Test]
  public async Task Staging_SwitchedOff_WritesRowByRow_AndDefersNoIndexesAsync() {
    var swapper = new FakeSwapper { Deferred = [DEFERRED_INDEX] };
    var timeline = new List<string>();
    var fixture = new Fixture(swapper, new BlueGreenRebuildOptions { StagingLoad = false });
    fixture.Runner.Stage = new StagingSink(timeline);
    fixture.Add(_a, 1);

    var result = await fixture.Rebuilder.RebuildBlueGreenAsync(PERSPECTIVE);

    await Assert.That(result.Success).IsTrue().Because(result.Error ?? "");
    await Assert.That(fixture.Runner.Runs.Select(r => r.Staging)).All().Satisfy(s => s.IsFalse());
    await Assert.That(swapper.IndexCalls).IsEmpty();
    await Assert.That(timeline).IsEmpty();
  }

  [Test]
  public async Task Staging_AFailedIndexBuild_DropsTheShadow_AndNeverSwapsAsync() {
    var swapper = new FakeSwapper { Deferred = [DEFERRED_INDEX], BuildFailure = new InvalidOperationException("index build failed") };
    var fixture = new Fixture(swapper);
    fixture.Add(_a, 1);

    var result = await fixture.Rebuilder.RebuildBlueGreenAsync(PERSPECTIVE);

    await Assert.That(result.Success).IsFalse();
    await Assert.That(result.Error).Contains("index build failed");
    await Assert.That(swapper.Calls).Contains("drop " + SHADOW);
    await Assert.That(swapper.Calls).DoesNotContain(c => c.StartsWith("swap ", StringComparison.Ordinal))
      .Because("the live table is left exactly as it was");
  }

  [Test]
  public async Task Staging_AnAbandonedRebuild_DropsTheShadow_AndLoadsNothingMoreAsync() {
    using var cts = new CancellationTokenSource();
    var timeline = new List<string>();
    var swapper = new FakeSwapper();
    var fixture = new Fixture(swapper);
    fixture.Runner.Stage = new StagingSink(timeline);
    fixture.Runner.OnFirstRun = cts.Cancel;
    fixture.Add(_a, 1);
    fixture.Add(_b, 2);

    var result = await fixture.Rebuilder.RebuildBlueGreenAsync(PERSPECTIVE, cts.Token);

    await Assert.That(result.Success).IsFalse();
    await Assert.That(swapper.Calls).Contains("drop " + SHADOW);
    await Assert.That(timeline).IsEmpty().Because("the rows held when the rebuild was abandoned are discarded with the shadow");
  }

  private sealed class StagingSink(List<string> timeline) : IPerspectiveBulkLoadSink {
    public List<Guid[]> Loads { get; } = [];
    public HashSet<Guid> Refuse { get; init; } = [];

    public Task<IReadOnlyList<PerspectiveWriteOutcome>> LoadAsync(
        IReadOnlyList<PendingPerspectiveWrite> writes, CancellationToken cancellationToken = default) {
      Loads.Add([.. writes.Select(w => w.StreamId)]);
      timeline.Add("load " + writes.Count);
      return Task.FromResult<IReadOnlyList<PerspectiveWriteOutcome>>([.. writes.Select(w => Refuse.Contains(w.StreamId)
        ? PerspectiveWriteOutcome.Failed(new InvalidOperationException("refused " + w.StreamId))
        : PerspectiveWriteOutcome.Written)]);
    }
  }

  private sealed class RecordingCompleter(List<string> timeline) : IPerspectiveCheckpointCompleter {
    public List<Guid> Completed { get; } = [];

    public Task CompleteAsync(IReadOnlyList<PerspectiveCursorCompletion> completions, CancellationToken cancellationToken = default) {
      Completed.AddRange(completions.Select(c => c.StreamId));
      timeline.Add("complete " + completions.Count);
      return Task.CompletedTask;
    }
  }
}
