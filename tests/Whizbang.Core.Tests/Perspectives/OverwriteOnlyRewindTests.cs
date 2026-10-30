// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Attributes;
using Whizbang.Core.Lenses;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Perspectives;

/// <summary>A user's whole permission set, as it stands after the change.</summary>
public sealed record PermissionsSet : IEvent {
  [StreamId]
  public Guid UserId { get; init; }
  public int Revision { get; init; }
  public string Permissions { get; init; } = string.Empty;
}

/// <summary>A note about the user that changes nothing in the read model.</summary>
public sealed record PermissionsNoted : IEvent {
  [StreamId]
  public Guid UserId { get; init; }
}

/// <summary>The user was removed; the row goes with them.</summary>
public sealed record PermissionsRevoked : IEvent {
  [StreamId]
  public Guid UserId { get; init; }
}

public sealed record PermissionSetModel {
  [StreamId]
  public Guid UserId { get; init; }
  public int Revision { get; init; }
  public string Permissions { get; init; } = string.Empty;
}

/// <summary>Counts the Apply calls a perspective made; a perspective takes one from DI.</summary>
public sealed class ApplyCounter {
  public int Applies { get; private set; }

  /// <summary>Runs once, on the first Apply: the seam a test appends a newer event through while a rewind runs.</summary>
  public Action? OnFirstApply { get; set; }

  public void Count() {
    Applies++;
    if (Applies == 1) {
      OnFirstApply?.Invoke();
    }
  }
}

/// <summary>Every Apply builds the row from its event alone, and says so.</summary>
[OverwriteOnly]
public sealed class OverwritePermissionsPerspective(ApplyCounter counter) :
    IPerspectiveFor<PermissionSetModel, PermissionsSet>,
    IPerspectiveFor<PermissionSetModel, PermissionsNoted>,
    IPerspectiveWithActionsFor<PermissionSetModel, PermissionsRevoked> {
  public PermissionSetModel Apply(PermissionSetModel currentData, PermissionsSet @event) {
    counter.Count();
    return new PermissionSetModel { UserId = @event.UserId, Revision = @event.Revision, Permissions = @event.Permissions };
  }

  public PermissionSetModel Apply(PermissionSetModel currentData, PermissionsNoted @event) {
    counter.Count();
    return null!;  // no model: the row stays as the events before this one left it
  }

  public ApplyResult<PermissionSetModel> Apply(PermissionSetModel currentData, PermissionsRevoked @event) {
    counter.Count();
    return ApplyResult<PermissionSetModel>.Purge();
  }
}

/// <summary>The same Applies without the marker: the full replay every rewind of it makes.</summary>
public sealed class FoldedPermissionsPerspective(ApplyCounter counter) :
    IPerspectiveFor<PermissionSetModel, PermissionsSet>,
    IPerspectiveFor<PermissionSetModel, PermissionsNoted>,
    IPerspectiveWithActionsFor<PermissionSetModel, PermissionsRevoked> {
  public PermissionSetModel Apply(PermissionSetModel currentData, PermissionsSet @event) {
    counter.Count();
    return new PermissionSetModel { UserId = @event.UserId, Revision = @event.Revision, Permissions = @event.Permissions };
  }

  public PermissionSetModel Apply(PermissionSetModel currentData, PermissionsNoted @event) {
    counter.Count();
    return null!;  // no model: the row stays as the events before this one left it
  }

  public ApplyResult<PermissionSetModel> Apply(PermissionSetModel currentData, PermissionsRevoked @event) {
    counter.Count();
    return ApplyResult<PermissionSetModel>.Purge();
  }
}

/// <summary>
/// A perspective marked <see cref="OverwriteOnlyAttribute"/> builds its row from each event alone, so the row after any
/// event is that event's (#1869). A rewind of it applies only the stream's last event, in the order a full replay folds
/// the stream, instead of loading and applying every event to a model that only the last one decides. These lock the
/// owner's decisions of 2026-10-29: the marker is explicit, a rewind applies the last event only, and the receptors of
/// the events it skips do not run. Each checks the row against the full replay the same Applies make without the marker.
/// </summary>
/// <docs>fundamentals/perspectives/rewind#overwrite-only</docs>
public class OverwriteOnlyRewindTests {
  private const string OVERWRITE_RUNNER = "Whizbang.Core.Tests.Generated.OverwritePermissionsPerspectiveRunner";
  private const string FOLDED_RUNNER = "Whizbang.Core.Tests.Generated.FoldedPermissionsPerspectiveRunner";

  [Test]
  public async Task ARewindOfAnOverwriteOnlyPerspective_AppliesOnlyTheLastEvent_AndWritesTheRowAFullReplayWritesAsync() {
    var overwrite = new Harness(OVERWRITE_RUNNER);
    var folded = new Harness(FOLDED_RUNNER);
    var userId = TrackedGuid.New().Value;
    const int count = 40;
    for (var revision = 1; revision <= count; revision++) {
      var set = new PermissionsSet { UserId = userId, Revision = revision, Permissions = $"perm-{revision}" };
      await overwrite.AppendAsync(userId, set);
      await folded.AppendAsync(userId, set);
    }

    await overwrite.RewindAsync(userId);
    await folded.RewindAsync(userId);

    await Assert.That(overwrite.Rows.Row(userId)).IsEqualTo(folded.Rows.Row(userId))
      .Because("the last event alone decides an overwrite-only row, so applying only it writes what a full replay writes");
    await Assert.That(overwrite.Rows.Row(userId).Revision).IsEqualTo(count);
    await Assert.That(folded.Counter.Applies).IsEqualTo(count)
      .Because("without the marker the rewind replays every event in the stream");
    await Assert.That(overwrite.Counter.Applies).IsEqualTo(1)
      .Because("with the marker the rewind applies the last event and nothing else (#1869)");
  }

  [Test]
  public async Task TheLastEvent_IsTheLastInTheOrderAFullReplayFolds_NotTheLastReadAsync() {
    var overwrite = new Harness(OVERWRITE_RUNNER);
    var folded = new Harness(FOLDED_RUNNER);
    var userId = TrackedGuid.New().Value;
    var origin = TrackedGuid.New().Value;
    // A single-origin stream replays in its origin's order: the event appended last carries the lowest origin sequence.
    long[] originSequences = [20, 30, 10];
    for (var i = 0; i < originSequences.Length; i++) {
      var set = new PermissionsSet { UserId = userId, Revision = i + 1, Permissions = $"perm-{i + 1}" };
      await overwrite.AppendAsync(userId, set, origin, originSequences[i]);
      await folded.AppendAsync(userId, set, origin, originSequences[i]);
    }

    await overwrite.RewindAsync(userId);
    await folded.RewindAsync(userId);

    await Assert.That(overwrite.Rows.Row(userId)).IsEqualTo(folded.Rows.Row(userId));
    await Assert.That(overwrite.Rows.Row(userId).Revision).IsEqualTo(2)
      .Because("the event at the highest origin sequence is the one a full replay folds last");
  }

  [Test]
  public async Task ALastEventWhoseApplyReturnsNoModel_IsReplayedInFull_AndWritesTheRowAFullReplayWritesAsync() {
    var overwrite = new Harness(OVERWRITE_RUNNER);
    var folded = new Harness(FOLDED_RUNNER);
    var userId = TrackedGuid.New().Value;
    foreach (var harness in new[] { overwrite, folded }) {
      await harness.AppendAsync(userId, new PermissionsSet { UserId = userId, Revision = 1, Permissions = "read" });
      await harness.AppendAsync(userId, new PermissionsSet { UserId = userId, Revision = 2, Permissions = "write" });
      await harness.AppendAsync(userId, new PermissionsNoted { UserId = userId });
    }

    await overwrite.RewindAsync(userId);
    await folded.RewindAsync(userId);

    await Assert.That(overwrite.Rows.Row(userId)).IsEqualTo(folded.Rows.Row(userId))
      .Because("an Apply that returns no model leaves the row to the events before it, which only the full replay folds");
    await Assert.That(overwrite.Rows.Row(userId).Revision).IsEqualTo(2);
  }

  [Test]
  public async Task AStreamHoldingAnEventThatCanPurge_IsReplayedInFull_AndEndsAsAFullReplayEndsAsync() {
    var overwrite = new Harness(OVERWRITE_RUNNER);
    var folded = new Harness(FOLDED_RUNNER);
    var userId = TrackedGuid.New().Value;
    foreach (var harness in new[] { overwrite, folded }) {
      harness.Rows.Seed(userId, new PermissionSetModel { UserId = userId, Revision = 9, Permissions = "stale" });
      await harness.AppendAsync(userId, new PermissionsSet { UserId = userId, Revision = 1, Permissions = "read" });
      await harness.AppendAsync(userId, new PermissionsRevoked { UserId = userId });
      await harness.AppendAsync(userId, new PermissionsSet { UserId = userId, Revision = 2, Permissions = "write" });
    }

    await overwrite.RewindAsync(userId);
    await folded.RewindAsync(userId);

    await Assert.That(folded.Rows.Has(userId)).IsFalse()
      .Because("a full replay purges the row and skips the event after the purge");
    await Assert.That(overwrite.Rows.Has(userId)).IsFalse()
      .Because("the last event alone would recreate a row the purge before it removed, so the stream is replayed in full");
  }

  [Test]
  public async Task ARewindFromASnapshot_WritesTheRowAFullReplayFromThatSnapshotWritesAsync() {
    var userId = TrackedGuid.New().Value;
    var snapshotEventId = TrackedGuid.New().Value;
    var snapshot = new PermissionSetModel { UserId = userId, Revision = 5, Permissions = "from-snapshot" };
    var overwrite = new Harness(OVERWRITE_RUNNER, new BeforeEverythingSnapshot(snapshotEventId, snapshot));
    var folded = new Harness(FOLDED_RUNNER, new BeforeEverythingSnapshot(snapshotEventId, snapshot));
    foreach (var harness in new[] { overwrite, folded }) {
      for (var revision = 6; revision <= 9; revision++) {
        await harness.AppendAsync(userId, new PermissionsSet { UserId = userId, Revision = revision, Permissions = $"perm-{revision}" });
      }
    }

    await overwrite.RewindAsync(userId);
    await folded.RewindAsync(userId);

    await Assert.That(overwrite.Rows.Row(userId)).IsEqualTo(folded.Rows.Row(userId));
    await Assert.That(overwrite.Rows.Row(userId).Revision).IsEqualTo(9);
    await Assert.That(folded.Counter.Applies).IsEqualTo(4);
    await Assert.That(overwrite.Counter.Applies).IsEqualTo(1);
  }

  [Test]
  public async Task AnEventAppendedWhileTheRewindRuns_IsAppliedByTheCatchUpPass_AsAFullReplayAppliesItAsync() {
    var overwrite = new Harness(OVERWRITE_RUNNER);
    var folded = new Harness(FOLDED_RUNNER);
    var userId = TrackedGuid.New().Value;
    foreach (var harness in new[] { overwrite, folded }) {
      await harness.AppendAsync(userId, new PermissionsSet { UserId = userId, Revision = 1, Permissions = "read" });
      await harness.AppendAsync(userId, new PermissionsSet { UserId = userId, Revision = 2, Permissions = "write" });
      // Another writer appends a newer event while the rewind applies the ones it read.
      harness.Counter.OnFirstApply = () => harness.AppendAsync(
        userId, new PermissionsSet { UserId = userId, Revision = 3, Permissions = "admin" }).GetAwaiter().GetResult();
    }

    await overwrite.RewindAsync(userId);
    await folded.RewindAsync(userId);

    await Assert.That(overwrite.Rows.Row(userId)).IsEqualTo(folded.Rows.Row(userId));
    await Assert.That(overwrite.Rows.Row(userId).Revision).IsEqualTo(3);
    await Assert.That(overwrite.Counter.Applies).IsEqualTo(2)
      .Because("each catch-up pass applies the last event it read, and nothing before it");
  }

  [Test]
  public async Task ARewind_RunsNoReceptorForTheEventsItSkipsAsync() {
    var invoker = new RecordingReceptorInvoker();
    var overwrite = new Harness(OVERWRITE_RUNNER, receptorInvoker: invoker);
    var userId = TrackedGuid.New().Value;
    for (var revision = 1; revision <= 3; revision++) {
      await overwrite.AppendAsync(userId, new PermissionsSet { UserId = userId, Revision = revision, Permissions = $"perm-{revision}" });
    }

    await overwrite.RewindAsync(userId);

    await Assert.That(invoker.Invocations).IsEqualTo(0)
      .Because("a rewind rebuilds state; it does not re-deliver the events it skips to their receptors (#1869)");
    await Assert.That(overwrite.Counter.Applies).IsEqualTo(1);
  }

  // -------------------------------------------------------------------------------------------

  private sealed class Harness {
    public InMemoryEventStore Events { get; } = new();
    public InMemoryRows<PermissionSetModel> Rows { get; } = new();
    public ApplyCounter Counter { get; } = new();
    public IPerspectiveRunner Runner { get; }
    private readonly string _perspectiveName;

    public Harness(string runnerTypeName, IPerspectiveSnapshotStore? snapshots = null, IReceptorInvoker? receptorInvoker = null) {
      var services = new ServiceCollection();
      services.AddLogging();
      services.AddSingleton(Counter);
      services.AddSingleton<OverwritePermissionsPerspective>();
      services.AddSingleton<FoldedPermissionsPerspective>();
      if (receptorInvoker is not null) {
        services.AddSingleton(receptorInvoker);
      }
      var provider = services.BuildServiceProvider();
      _perspectiveName = runnerTypeName.Contains("Overwrite", StringComparison.Ordinal)
        ? nameof(OverwritePermissionsPerspective)
        : nameof(FoldedPermissionsPerspective);
      Runner = GeneratedRunners.Create(
        runnerTypeName, provider, Events, Rows, snapshots,
        snapshots is null ? null : Options.Create(new PerspectiveSnapshotOptions { Enabled = false }));
    }

    public async Task AppendAsync(Guid userId, IEvent payload, Guid? origin = null, long originSequence = 0) {
      await Events.AppendAsync(userId, GeneratedRunners.Envelope(payload, origin ?? Guid.Empty, originSequence));
    }

    public Task RewindAsync(Guid userId) =>
      Runner.RewindAndRunAsync(userId, _perspectiveName, TrackedGuid.New().Value, CancellationToken.None);
  }

  /// <summary>Holds one snapshot, placed before every event the test appends.</summary>
  private sealed class BeforeEverythingSnapshot(Guid snapshotEventId, PermissionSetModel model) : IPerspectiveSnapshotStore {
    private JsonDocument _data() {
      var info = (System.Text.Json.Serialization.Metadata.JsonTypeInfo<PermissionSetModel>)
        Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions().GetTypeInfo(typeof(PermissionSetModel));
      using var doc = JsonSerializer.SerializeToDocument(model, info);
      return SnapshotEnvelope.Wrap(doc.RootElement, Whizbang.Core.Serialization.SerializationVersion.CURRENT);
    }

    public Task CreateSnapshotAsync(Guid streamId, string perspectiveName, Guid snapshotEventId, JsonDocument snapshotData, CancellationToken ct = default) =>
      Task.CompletedTask;

    public Task<(Guid SnapshotEventId, JsonDocument SnapshotData)?> GetLatestSnapshotAsync(Guid streamId, string perspectiveName, CancellationToken ct = default) =>
      Task.FromResult<(Guid, JsonDocument)?>((snapshotEventId, _data()));

    public Task<(Guid SnapshotEventId, JsonDocument SnapshotData)?> GetLatestSnapshotBeforeAsync(Guid streamId, string perspectiveName, Guid beforeEventId, CancellationToken ct = default) =>
      GetLatestSnapshotAsync(streamId, perspectiveName, ct);

    public Task<(Guid SnapshotEventId, int? SnapshotVersion, JsonDocument SnapshotData)?> GetLatestSnapshotBeforeEventInStreamOrderAsync(
        Guid streamId, string perspectiveName, Guid beforeEventId, CancellationToken ct = default) =>
      Task.FromResult<(Guid, int?, JsonDocument)?>((snapshotEventId, 0, _data()));

    public Task<bool> HasAnySnapshotAsync(Guid streamId, string perspectiveName, CancellationToken ct = default) =>
      Task.FromResult(true);

    public Task PruneOldSnapshotsAsync(Guid streamId, string perspectiveName, int keepCount, CancellationToken ct = default) =>
      Task.CompletedTask;

    public Task DeleteAllSnapshotsAsync(Guid streamId, string perspectiveName, CancellationToken ct = default) =>
      Task.CompletedTask;
  }

  /// <summary>Counts every receptor invocation a run asks for, at any stage.</summary>
  private sealed class RecordingReceptorInvoker : IReceptorInvoker {
    public int Invocations { get; private set; }

    public ValueTask InvokeAsync(IMessageEnvelope envelope, LifecycleStage stage, ILifecycleContext? context = null, CancellationToken cancellationToken = default) {
      Invocations++;
      return ValueTask.CompletedTask;
    }
  }
}
