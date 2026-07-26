using Microsoft.EntityFrameworkCore;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.Perspectives;

namespace Whizbang.Data.EFCore.Postgres.Tests.Perspectives;

/// <summary>
/// RED confirmation of the cross-pod perspective lost-update (a stranded
/// production saga item).
///
/// <para>The per-item-stream design assumes <b>single-writer per stream</b>: while one instance is
/// applying a stream's events, no other instance applies it. That invariant is enforced only by an
/// in-process <see cref="PerspectiveApplyCoordinator"/> <c>SemaphoreSlim</c> keyed by
/// <c>(streamId, perspectiveName)</c> — a per-DI-container singleton. <b>Two instances (pods) share
/// no lock</b>, and the perspective UPSERT is <b>unconditional last-write-wins</b> (no version /
/// commit_sequence floor). So when two instances both load an empty model and write back, the
/// later (and possibly <i>staler</i>) write silently overwrites the earlier one — exactly how a
/// per-item row reverted from <c>Completed</c> to <c>Running</c> and stranded the saga.</para>
///
/// <para>This is RED until the apply/persistence path enforces single-writer — either by making the
/// stream lease authoritative cross-pod (no concurrent apply), or a conditional upsert that refuses
/// a write whose commit_sequence is below the stored row's. It uses two independent
/// <see cref="EFCorePostgresPerspectiveStore{TModel}"/> instances over one database to model two pods.</para>
/// </summary>
public class CrossPodLostUpdateTests : EFCoreTestBase {

  [Test]
  public async Task ConcurrentApplyFromTwoInstances_FresherApplyMustNotBeLost() {
    var streamId = (Guid)Whizbang.Core.ValueObjects.TrackedGuid.NewMedo();
    const string table = "action_test";

    // Two independent "pods": each its own DbContext + store over the SAME database. They share no
    // apply lock (the coordinator is a per-container singleton), mirroring two real instances.
    await using var ctxA = CreateDbContext();
    await using var ctxB = CreateDbContext();
    var podA = new EFCorePostgresPerspectiveStore<ActionTestModel>(ctxA, table);
    var podB = new EFCorePostgresPerspectiveStore<ActionTestModel>(ctxB, table);

    // Both instances load the model for the stream and each independently sees an EMPTY row — the
    // race precondition (neither has seen the other's apply).
    var aLoaded = await podA.GetByStreamIdAsync(streamId);
    var bLoaded = await podB.GetByStreamIdAsync(streamId);
    await Assert.That(aLoaded).IsNull();
    await Assert.That(bLoaded).IsNull();

    // Pod A applies the TERMINAL event (the SagaItemCompletedEvent: higher commit_sequence).
    var completed = new ActionTestModel { Id = streamId, Name = "Completed", Value = 2 };
    var metaCompleted = new PerspectiveMetadata {
      EventType = "SagaItemCompletedEvent",
      EventId = ((Guid)Whizbang.Core.ValueObjects.TrackedGuid.NewMedo()).ToString("D"),
      Timestamp = DateTime.UtcNow,
      CommitSequence = 841062,
    };

    // Pod B applies the STALER Started event off its empty read (lower commit_sequence).
    var running = new ActionTestModel { Id = streamId, Name = "Running", Value = 1 };
    var metaRunning = new PerspectiveMetadata {
      EventType = "SagaItemStartedEvent",
      EventId = ((Guid)Whizbang.Core.ValueObjects.TrackedGuid.NewMedo()).ToString("D"),
      Timestamp = DateTime.UtcNow,
      CommitSequence = 841014,
    };

    // A commits the fresher (Completed) write FIRST; B commits the staler (Running) write LAST.
    await podA.UpsertAsync(streamId, completed, new PerspectiveScope(), forceUpdateScope: false, metaCompleted, CancellationToken.None);
    await podB.UpsertAsync(streamId, running, new PerspectiveScope(), forceUpdateScope: false, metaRunning, CancellationToken.None);

    // The fresher apply (commit_sequence 841062) MUST win — a staler concurrent write must not be
    // able to revert a terminal row. Today the unconditional upsert lets B overwrite A wholesale.
    await using var verify = CreateDbContext();
    var row = await verify.Set<PerspectiveRow<ActionTestModel>>().AsNoTracking()
      .FirstAsync(r => r.Id == streamId);

    await Assert.That(row.Data.Name).IsEqualTo("Completed")
      .Because("a staler concurrent apply (commit_sequence 841014) from a second instance must not "
        + "overwrite the terminal apply (841062) — this is the cross-pod lost-update that stranded "
        + "a production saga item at Running");
    await Assert.That(row.Metadata.CommitSequence).IsEqualTo(841062L)
      .Because("the stored metadata must reflect the highest applied commit_sequence, not the staler write");
  }
}
