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

  // ===== CollectiveReplayStart =====

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

    public MessageEnvelope<IEvent> Add() {
      var envelope = GeneratedRunners.Envelope(new PermissionsBulkGranted());
      Collectives.Add(envelope);
      return envelope;
    }

    public Task<IReadOnlyList<MessageEnvelope<IEvent>>> InterleaveForReplayAsync(
        Type modelType, IReadOnlyList<MessageEnvelope<IEvent>> streamEvents, CancellationToken cancellationToken) =>
      Task.FromResult<IReadOnlyList<MessageEnvelope<IEvent>>>([.. streamEvents, .. Collectives]);

    public Task<IReadOnlyList<MessageEnvelope<IEvent>>> InterleaveAfterAsync(
        Type modelType, IReadOnlyList<MessageEnvelope<IEvent>> streamEvents, CollectiveReplayStart start, CancellationToken cancellationToken) {
      Starts.Add(start);
      return Task.FromResult<IReadOnlyList<MessageEnvelope<IEvent>>>([.. streamEvents, .. Collectives]);
    }

    public object ApplyInMemory(Type modelType, object currentModel, Guid streamId, IEvent collectiveEvent) =>
      (PermissionSetModel)currentModel with { Permissions = ((PermissionSetModel)currentModel).Permissions + "+c" };

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
  private sealed class RecordingSnapshots : SnapshotsBase {
    public List<(Guid EventId, PermissionSetModel Model)> Taken { get; } = [];

    public override Task CreateSnapshotAsync(Guid streamId, string perspectiveName, Guid snapshotEventId, JsonDocument snapshotData, CancellationToken ct = default) {
      var read = SnapshotEnvelope.Read(snapshotData, Whizbang.Core.Serialization.SerializationVersion.CURRENT, SnapshotUpgradePolicy.RebuildFromEvents);
      Taken.Add((snapshotEventId, JsonSerializer.Deserialize(read.Model.GetRawText(), _modelInfo)!));
      return Task.CompletedTask;
    }
  }

  private abstract class SnapshotsBase : IPerspectiveSnapshotStore {
    public virtual Task CreateSnapshotAsync(Guid streamId, string perspectiveName, Guid snapshotEventId, JsonDocument snapshotData, CancellationToken ct = default) =>
      Task.CompletedTask;

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

  private sealed class Harness {
    public InMemoryEventStore Events { get; } = new();
    public InMemoryRows<PermissionSetModel> Rows { get; } = new();
    public ApplyCounter Counter { get; } = new();
    public IPerspectiveRunner Runner { get; }
    private readonly string _perspectiveName;

    public Harness(
        ICollectiveReplayApplier applier,
        string runnerTypeName = FOLDED_RUNNER,
        IPerspectiveSnapshotStore? snapshots = null,
        PerspectiveSnapshotOptions? snapshotOptions = null) {
      var services = new ServiceCollection();
      services.AddLogging();
      services.AddSingleton<FoldedPermissionsPerspective>();
      services.AddSingleton<OverwritePermissionsPerspective>();
      _perspectiveName = runnerTypeName == OVERWRITE_RUNNER ? nameof(OverwritePermissionsPerspective) : nameof(FoldedPermissionsPerspective);
      Runner = GeneratedRunners.Create(
        runnerTypeName, services.BuildServiceProvider(), Events, Rows, snapshots,
        Options.Create(snapshotOptions ?? new PerspectiveSnapshotOptions { Enabled = false }), collectiveReplayApplier: applier);
    }

    public async Task<PerspectiveCursorCompletion> RewindAsync(Guid userId) {
      ApplyCounter.Current.Value = Counter;
      return await Runner.RewindAndRunAsync(userId, _perspectiveName, TrackedGuid.New().Value, CancellationToken.None);
    }

    public async Task<MessageEnvelope<IEvent>> AppendAsync(Guid userId, IEvent payload) {
      var envelope = GeneratedRunners.Envelope(payload);
      await Events.AppendAsync(userId, envelope);
      return envelope;
    }

    public async Task<IReadOnlyList<PerspectiveCursorCompletion>> RebuildAsync(Guid userId) {
      var previous = ProcessingModeAccessor.Current;
      ProcessingModeAccessor.Current = ProcessingMode.Rebuild;
      try {
        return await Runner.RunRebuildAsync(userId, _perspectiveName, CancellationToken.None);
      } finally {
        ProcessingModeAccessor.Current = previous;
      }
    }
  }
}
