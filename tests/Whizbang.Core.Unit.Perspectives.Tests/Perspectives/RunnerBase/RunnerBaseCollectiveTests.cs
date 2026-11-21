// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Perspectives.RunnerBase;

/// <summary>
/// The runner base's handling of collectives (#2469): other streams' set-based mutations folded into the row on the live
/// path, on a replay and on the reap-boundary bootstrap, and the snapshots and fence reads that depend on them.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Perspectives/PerspectiveRunnerBase.cs</code-under-test>
public class RunnerBaseCollectiveTests {
  private static RbHarness _withCollectives(RbApplier applier, PerspectiveSnapshotOptions? options = null, RbSnapshots? snapshots = null) =>
    new() { Collectives = applier, Snapshots = snapshots ?? new RbSnapshots(), SnapshotOptions = options ?? new PerspectiveSnapshotOptions { Enabled = true, SnapshotEveryNEvents = 100 } };

  // ===== Live =====

  [Test]
  public async Task CollectivesFoldedAfterTheLastEvent_LeaveTheRowAtThatEventAside_AndDisposeItWhenNoSnapshotIsDueAsync() {
    var applier = new RbApplier { LiveTail = 2 };
    var harness = _withCollectives(applier);
    var marked = harness.Add(harness.Marked("a"));

    var completion = await harness.Runner.RunWithEventsAsync(harness.StreamId, RbHarness.NAME, null, [marked]);

    await Assert.That(harness.Rows.Row(harness.StreamId).Marks).IsEqualTo("a++");
    await Assert.That(completion.EventsProcessed).IsEqualTo(3);
    await Assert.That(harness.Snapshots!.Created).IsEmpty();
  }

  [Test]
  public async Task CollectivesFoldedAfterTheLastEvent_OfAWriteTheGuardRefuses_AreDisposedBeforeTheRewindAsync() {
    var applier = new RbApplier { LiveTail = 1 };
    var harness = _withCollectives(applier);
    harness.Rows.RefuseWrites = true;
    var marked = harness.Add(harness.Marked("a"));

    await harness.Runner.RunWithEventsAsync(harness.StreamId, RbHarness.NAME, null, [marked]);

    await Assert.That(harness.Log.Count(LogLevel.Warning, "[diag.write.refused]")).IsEqualTo(1);
  }

  [Test]
  public async Task APairWhoseWriteCannotBePrepared_ReleasesTheRowItKeptAtItsLastEventAsync() {
    var applier = new RbApplier { LiveTail = 1 };
    var harness = _withCollectives(applier);
    var marked = harness.Add(harness.Marked("a"));

    var prepared = await harness.Runner.PrepareAsync(harness.StreamId, RbHarness.NAME, null, [marked], null);

    await Assert.That(prepared.PerPairReason).IsEqualTo(PerspectivePerPairReason.WriteNotPreparable);
  }

  [Test]
  public async Task ABatchOfCollectivesOnly_WritesTheRowAtTheCursor_WithNoEventTypeOrTimeOfItsOwnAsync() {
    var applier = new RbApplier { ReplaceLive = true, LiveTail = 1 };
    var harness = new RbHarness { Collectives = applier };
    var cursor = harness.Add(harness.Marked("a"));
    harness.Rows.Seed(harness.StreamId, new RbModel { Id = harness.StreamId, Marks = "a" }, cursor.MessageId.Value, eventType: null);
    harness.Rows.Versioned = false;
    var next = harness.Add(harness.Marked("b"));

    await harness.Runner.RunWithEventsAsync(harness.StreamId, RbHarness.NAME, cursor.MessageId.Value, [next]);

    await Assert.That(harness.Rows.Row(harness.StreamId).Marks).IsEqualTo("a+");
    await Assert.That(harness.Rows.MetadataOf(harness.StreamId).EventType).IsEqualTo(string.Empty);
    await Assert.That(harness.Rows.MetadataOf(harness.StreamId).EventId).IsEqualTo(cursor.MessageId.Value.ToString("D"))
      .Because("a collective is another stream's event and moves no checkpoint");
  }

  [Test]
  public async Task ABatchOfCollectivesOnly_IsPreparedAtTheCursorAsync() {
    var applier = new RbApplier { ReplaceLive = true, LiveTail = 1 };
    var harness = new RbHarness { Collectives = applier };
    harness.Rows.Prepares = true;
    var cursor = harness.Add(harness.Marked("a"));
    harness.Rows.Seed(harness.StreamId, new RbModel { Id = harness.StreamId, Marks = "a" }, cursor.MessageId.Value, eventType: null);
    var next = harness.Add(harness.Marked("b"));

    var prepared = await harness.Runner.PrepareAsync(harness.StreamId, RbHarness.NAME, cursor.MessageId.Value, [next], null);

    await Assert.That(harness.Rows.Prepared.Single().Model.Marks).IsEqualTo("a+");
    await Assert.That(prepared.RunsPerPair).IsFalse();
  }

  [Test]
  public async Task TheLiveFence_IsReadForTheTenantOfTheBatchsFirstEvent_EvenWhenItHasNoneAsync() {
    var applier = new RbApplier();
    var fence = new RbFence();
    var harness = _withCollectives(applier, new PerspectiveSnapshotOptions { Enabled = true, SnapshotEveryNEvents = 1 });
    harness.Services.AddSingleton<ICollectiveSnapshotFence>(fence);
    var bare = harness.Add(harness.Marked("a"), hops: false);

    await harness.Runner.RunWithEventsAsync(harness.StreamId, RbHarness.NAME, null, [bare]);

    await Assert.That(fence.Tenants).Contains((string?)null);
    await Assert.That(harness.Snapshots!.Created.Single().CollectivesThrough).IsEqualTo(0)
      .Because("a fence with no stamped event marks zero");
  }

  [Test]
  public async Task ASnapshotDeferredByAnInFlightCollective_IsCountedAsync() {
    var applier = new RbApplier();
    var fence = new RbFence { InFlight = true };
    var harness = _withCollectives(applier, new PerspectiveSnapshotOptions { Enabled = true, SnapshotEveryNEvents = 1 });
    harness.Services.AddSingleton<ICollectiveSnapshotFence>(fence);
    var meterFactory = new Whizbang.Core.Tests.Observability.TestMeterFactory();
    harness.Services.AddSingleton(new PerspectiveMetrics(new WhizbangMetrics(meterFactory)));
    var metrics = new Whizbang.Core.Tests.Observability.MetricAssertionHelper(meterFactory.CreatedMeters[0]);
    var marked = harness.Add(harness.Marked("a"), tenant: "tenant-a");

    await harness.Runner.RunWithEventsAsync(harness.StreamId, RbHarness.NAME, null, [marked]);

    await Assert.That(harness.Snapshots!.Created).IsEmpty();
    await Assert.That(harness.Log.Count(LogLevel.Debug, "deferred: a collective of the row was in flight")).IsEqualTo(1);
    await Assert.That(metrics.GetByName("whizbang.perspective.snapshots_deferred")
      .Any(m => m.Tags.TryGetValue("perspective_name", out var name) && name == RbHarness.NAME)).IsTrue();
  }

  [Test]
  public async Task ASnapshotDeferredByAnInFlightCollective_WithNoMetricsRegistered_IsStillDeferredAsync() {
    var applier = new RbApplier();
    var harness = _withCollectives(applier, new PerspectiveSnapshotOptions { Enabled = true, SnapshotEveryNEvents = 1 });
    harness.Services.AddSingleton<ICollectiveSnapshotFence>(new RbFence { InFlight = true });
    var marked = harness.Add(harness.Marked("a"), tenant: "tenant-a");

    await harness.Runner.RunWithEventsAsync(harness.StreamId, RbHarness.NAME, null, [marked]);

    await Assert.That(harness.Snapshots!.Created).IsEmpty();
    await Assert.That(harness.Log.Count(LogLevel.Debug, "deferred: a collective of the row was in flight")).IsEqualTo(1);
  }

  [Test]
  [Arguments(false)]
  [Arguments(true)]
  public async Task ALiveCollective_WithSnapshotsNotConfigured_KeepsNoRowAsideAsync(bool optionsDisabled) {
    var applier = new RbApplier { LiveTail = 2 };
    var harness = new RbHarness {
      Collectives = applier,
      Snapshots = new RbSnapshots(),
      SnapshotOptions = optionsDisabled ? new PerspectiveSnapshotOptions { Enabled = false } : null,
    };
    var marked = harness.Add(harness.Marked("a"));

    await harness.Runner.RunWithEventsAsync(harness.StreamId, RbHarness.NAME, null, [marked]);

    await Assert.That(harness.Rows.Row(harness.StreamId).Marks).IsEqualTo("a++");
    await Assert.That(harness.Snapshots!.Created).IsEmpty();
  }

  // ===== Replay =====

  [Test]
  public async Task AReplayFoldingAnInFlightCollective_TakesNoSnapshot_AndReleasesTheRowAtItsLastEventAsync() {
    var applier = new RbApplier { ReplayTail = 1, InFlight = true };
    var harness = _withCollectives(applier, new PerspectiveSnapshotOptions { Enabled = true, RewindSnapshotIntervalEvents = 0 });
    var first = harness.Add(harness.Marked("a"));

    var completion = await harness.Runner.RewindAndRunAsync(harness.StreamId, RbHarness.NAME, first.MessageId.Value);

    await Assert.That(harness.Rows.Row(harness.StreamId).Marks).IsEqualTo("a+");
    await Assert.That(harness.Snapshots!.Created).IsEmpty()
      .Because("a collective still in flight may yet reach the row live, so no snapshot could say what it holds");
    await Assert.That(completion.EventsProcessed).IsEqualTo(2);
  }

  [Test]
  public async Task AReplay_HoldsTheHighestCommitSequenceOfTheCollectivesBeforeAnEventAsync() {
    var applier = new RbApplier { ReplayHead = [700, 650] };
    var harness = _withCollectives(applier);
    var first = harness.Add(harness.Marked("a"), tenant: "tenant-a");
    first.LocalCommitSequence = 600;

    await harness.Runner.RewindAndRunAsync(harness.StreamId, RbHarness.NAME, first.MessageId.Value);

    await Assert.That(harness.Snapshots!.Created.Single().CollectivesThrough).IsEqualTo(700);
  }

  [Test]
  public async Task AReplayPass_ReadsTheCollectivesOfTheTenantOfItsFirstEvent_EvenWhenItHasNoneAsync() {
    var applier = new RbApplier();
    var harness = _withCollectives(applier);
    var first = harness.Add(harness.Marked("a"), hops: false);

    await harness.Runner.RewindAndRunAsync(harness.StreamId, RbHarness.NAME, first.MessageId.Value);

    await Assert.That(applier.Starts[0].TenantId).IsNull();
  }

  [Test]
  [Arguments(true, "tenant-row")]
  [Arguments(true, null)]
  [Arguments(false, null)]
  public async Task AFirstPassFromASnapshotThatReadsNoEvent_FoldsTheCollectivesOfTheRowsTenantAsync(bool rowPresent, string? tenant) {
    var applier = new RbApplier();
    var harness = _withCollectives(applier);
    var floor = harness.Add(harness.Marked("a"));
    var trigger = harness.Add(harness.Marked("b"));
    harness.Events.NothingAfterAnAnchor = true;
    harness.Snapshots!.Floor = new PerspectiveSnapshotFloor(floor.MessageId.Value, 1, Rb.SnapshotOf(new RbModel { Id = harness.StreamId, Marks = "a" }), null);
    if (rowPresent) {
      harness.Rows.Seed(harness.StreamId, new RbModel { Id = harness.StreamId, Marks = "a" }, floor.MessageId.Value,
        scope: tenant is null ? null : new PerspectiveScope { TenantId = tenant });
    }

    await harness.Runner.RewindAndRunAsync(harness.StreamId, RbHarness.NAME, trigger.MessageId.Value);

    if (rowPresent) {
      await Assert.That(applier.Starts.Select(s => s.TenantId)).Contains(tenant);
    } else {
      await Assert.That(applier.Starts).IsEmpty().Because("with no events and no row, the tenant is not known");
    }
  }

  [Test]
  public async Task AReplayFromASnapshotThatFoldsOnlyCollectives_WritesWithNoEventTypeOrTimeOfItsOwnAsync() {
    var applier = new RbApplier { ReplayTail = 1 };
    var harness = _withCollectives(applier);
    var floor = harness.Add(harness.Marked("a"));
    harness.Snapshots!.Floor = new PerspectiveSnapshotFloor(floor.MessageId.Value, 1, Rb.SnapshotOf(new RbModel { Id = harness.StreamId, Marks = "a" }), null);
    harness.Rows.Seed(harness.StreamId, new RbModel { Id = harness.StreamId, Marks = "a" }, floor.MessageId.Value, scope: new PerspectiveScope { TenantId = "tenant-a" });
    var trigger = harness.Add(harness.Marked("b"));
    harness.Events.NothingAfterAnAnchor = true;

    var completion = await harness.Runner.RewindAndRunAsync(harness.StreamId, RbHarness.NAME, trigger.MessageId.Value);

    await Assert.That(completion.RowChanged).IsTrue();
    await Assert.That(harness.Rows.Row(harness.StreamId).Marks).IsEqualTo("a+");
    await Assert.That(harness.Rows.MetadataOf(harness.StreamId).EventType).IsEqualTo(string.Empty);
  }

  // ===== Bootstrap =====

  [Test]
  public async Task ABootstrapOfARowWithCollectives_ReadsTheFenceForTheRowsTenant_EvenWhenItHasNoneAsync() {
    var applier = new RbApplier();
    var fence = new RbFence();
    var harness = _withCollectives(applier);
    harness.Services.AddSingleton<ICollectiveSnapshotFence>(fence);
    var marked = harness.Add(harness.Marked("a"));
    harness.Rows.Seed(harness.StreamId, new RbModel { Id = harness.StreamId, Marks = "a" }, marked.MessageId.Value);

    var outcome = await harness.Runner.TryBootstrapSnapshotAsync(harness.StreamId, RbHarness.NAME, marked.MessageId.Value);

    await Assert.That(outcome).IsEqualTo(SnapshotBootstrapOutcome.Written);
    await Assert.That(fence.Tenants).Contains((string?)null);
  }

  [Test]
  public async Task ABootstrapThroughTheBoundary_FoldsTheCollectivesAfterTheFloor_ForTheTenantOfItsEventsAsync() {
    var applier = new RbApplier { ReplayHead = [10] };
    var harness = _withCollectives(applier);
    var floor = harness.Add(harness.Marked("a"), hops: false);
    var boundary = harness.Add(harness.Marked("b"), hops: false);
    var cursor = harness.Add(harness.Marked("c"));
    harness.Snapshots!.Floor = new PerspectiveSnapshotFloor(floor.MessageId.Value, 1, Rb.SnapshotOf(new RbModel { Id = harness.StreamId, Marks = "a" }), null) {
      CollectivesThroughSequence = 5,
    };

    var outcome = await harness.Runner.TryBootstrapSnapshotThroughAsync(harness.StreamId, RbHarness.NAME, cursor.MessageId.Value, boundary.MessageId.Value);

    await Assert.That(outcome).IsEqualTo(SnapshotBootstrapOutcome.Written);
    var start = applier.Starts.Single();
    await Assert.That(start.TenantId).IsNull();
    await Assert.That(start.CollectivesThroughSequence).IsEqualTo(5);
    await Assert.That(Rb.ModelOf(harness.Snapshots.Created.Single().Data).Marks).IsEqualTo("a+b");
  }

  [Test]
  public async Task ABootstrapThroughTheBoundaryWithNoFloor_FoldsTheCollectivesFromTheStreamsStartAsync() {
    var applier = new RbApplier { ReplayHead = [10] };
    var harness = _withCollectives(applier);
    var first = harness.Add(harness.Marked("a"), tenant: "tenant-a");
    var boundary = harness.Add(harness.Marked("b"), tenant: "tenant-a");
    var cursor = harness.Add(harness.Marked("c"));

    var outcome = await harness.Runner.TryBootstrapSnapshotThroughAsync(harness.StreamId, RbHarness.NAME, cursor.MessageId.Value, boundary.MessageId.Value);

    await Assert.That(outcome).IsEqualTo(SnapshotBootstrapOutcome.Written);
    var start = applier.Starts.Single();
    await Assert.That(start.TenantId).IsEqualTo("tenant-a");
    await Assert.That(start.AfterEventId).IsNull();
    await Assert.That(start.CollectivesThroughSequence).IsNull();
    await Assert.That(Rb.ModelOf(harness.Snapshots!.Created.Single().Data).Marks).IsEqualTo("ab")
      .Because("a collective before the stream's first event, with no floor below, had no row to reach");
    await Assert.That(first.MessageId).IsNotEqualTo(boundary.MessageId);
  }
}

/// <summary>
/// A collective replay applier that names <see cref="RbCollective"/> for the model and adds collectives where a test
/// asks: after the live batch, in place of it, after a replay's events, or before them with given commit sequences.
/// Each collective folds as a <c>+</c> mark.
/// </summary>
internal sealed class RbApplier : ICollectiveReplayApplier {
  public int LiveTail { get; init; }
  public bool ReplaceLive { get; init; }
  public int ReplayTail { get; init; }
  public long[] ReplayHead { get; init; } = [];
  public bool InFlight { get; init; }
  public List<CollectiveReplayStart> Starts { get; } = [];
  private bool _headServed;

  private static MessageEnvelope<IEvent> _collective(long? sequence = null) {
    var envelope = Rb.Envelope(new RbCollective());
    envelope.LocalCommitSequence = sequence;
    return envelope;
  }

  public Task<IReadOnlyList<MessageEnvelope<IEvent>>> InterleaveForReplayAsync(
      Type modelType, IReadOnlyList<MessageEnvelope<IEvent>> streamEvents, CancellationToken cancellationToken) {
    var tail = Enumerable.Range(0, LiveTail).Select(_ => _collective()).ToList();
    return Task.FromResult<IReadOnlyList<MessageEnvelope<IEvent>>>(ReplaceLive ? tail : [.. streamEvents, .. tail]);
  }

  public Task<IReadOnlyList<MessageEnvelope<IEvent>>> InterleaveAfterAsync(
      Type modelType, IReadOnlyList<MessageEnvelope<IEvent>> streamEvents, CollectiveReplayStart start, CancellationToken cancellationToken) {
    Starts.Add(start);
    if (_headServed) {
      return Task.FromResult(streamEvents);
    }
    _headServed = true;
    var head = ReplayHead.Select(s => _collective(s));
    var tail = Enumerable.Range(0, ReplayTail).Select(_ => _collective());
    return Task.FromResult<IReadOnlyList<MessageEnvelope<IEvent>>>([.. head, .. streamEvents, .. tail]);
  }

  public object ApplyInMemory(Type modelType, object currentModel, Guid streamId, IEvent collectiveEvent) =>
    (RbModel)currentModel with { Marks = ((RbModel)currentModel).Marks + "+" };

  public IReadOnlyList<string> CollectiveEventTypeNamesFor(string modelTypeName) => ["RbCollective"];

  public bool IsInFlight(IEvent collectiveEvent) => InFlight;
}

/// <summary>A collective snapshot fence with a fixed reading, recording the tenant of each read.</summary>
internal sealed class RbFence : ICollectiveSnapshotFence {
  public bool InFlight { get; init; }
  public List<string?> Tenants { get; } = [];

  public Task<CollectiveSnapshotFenceReading> ReadAsync(string? tenantId, IReadOnlyList<string> collectiveEventTypes, long? afterSequence,
      CancellationToken cancellationToken = default) {
    Tenants.Add(tenantId);
    return Task.FromResult(new CollectiveSnapshotFenceReading(null, InFlight));
  }
}
