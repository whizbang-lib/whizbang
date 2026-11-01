// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
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
/// <docs>fundamentals/messaging/collective-events</docs>
public class CollectiveReplayRunnerTests {
  private const string FOLDED_RUNNER = "Whizbang.Core.Tests.Generated.FoldedPermissionsPerspectiveRunner";

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

  // -------------------------------------------------------------------------------------------

  /// <summary>A collective the fake applier interleaves; its mutation appends a mark to the permissions.</summary>
  private sealed record PermissionsBulkGranted : ICollectiveEvent {
    public CollectiveScope Scope { get; init; } = new TenantCollectiveScope("tenant-a");
  }

  /// <summary>Interleaves the collectives a test adds after the stream's events and marks the row each one folds into.</summary>
  private sealed class FakeCollectiveApplier : ICollectiveReplayApplier {
    public List<MessageEnvelope<IEvent>> Collectives { get; } = [];

    public MessageEnvelope<IEvent> Add() {
      var envelope = GeneratedRunners.Envelope(new PermissionsBulkGranted());
      Collectives.Add(envelope);
      return envelope;
    }

    public Task<IReadOnlyList<MessageEnvelope<IEvent>>> InterleaveForReplayAsync(
        Type modelType, IReadOnlyList<MessageEnvelope<IEvent>> streamEvents, CancellationToken cancellationToken) =>
      Task.FromResult<IReadOnlyList<MessageEnvelope<IEvent>>>([.. streamEvents, .. Collectives]);

    public object ApplyInMemory(Type modelType, object currentModel, Guid streamId, IEvent collectiveEvent) =>
      (PermissionSetModel)currentModel with { Permissions = ((PermissionSetModel)currentModel).Permissions + "+c" };
  }

  private sealed class Harness {
    public InMemoryEventStore Events { get; } = new();
    public InMemoryRows<PermissionSetModel> Rows { get; } = new();
    public IPerspectiveRunner Runner { get; }

    public Harness(ICollectiveReplayApplier applier) {
      var services = new ServiceCollection();
      services.AddLogging();
      services.AddSingleton<FoldedPermissionsPerspective>();
      Runner = GeneratedRunners.Create(FOLDED_RUNNER, services.BuildServiceProvider(), Events, Rows, collectiveReplayApplier: applier);
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
        return await Runner.RunRebuildAsync(userId, nameof(FoldedPermissionsPerspective), CancellationToken.None);
      } finally {
        ProcessingModeAccessor.Current = previous;
      }
    }
  }
}
