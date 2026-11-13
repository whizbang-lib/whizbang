// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.Messaging;
using Whizbang.Core.Perspectives;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests.Perspectives;

/// <summary>
/// A rebuild whose write the batch write's ordering guard refuses (#2252 on the set-based write of #2246): the stored row
/// is stamped at a removed event's position, above every event the stream still holds. The completion says why the row
/// was left as it was, exactly as the per-pair write does: the fold equal to the stored row is AlreadyCorrect, a fold
/// that differs is WriteRefused.
/// </summary>
/// <docs>fundamentals/perspectives/rebuild#reading-the-result</docs>
public partial class PerspectivePrepareCompleteIntegrationTests {
  // The removed duplicate's position, above every event the stream still holds.
  private const long REMOVED_EVENT_SEQUENCE = 900_000;

  [Test]
  public async Task ARebuildWriteTheGuardRefuses_WithAFoldEqualToTheStoredRow_CompletesAlreadyCorrectAsync() {
    var completion = await _rebuildOverStaleRowAsync(new ItemProgress { Started = true, Attempts = 1, Completed = true });

    await Assert.That(completion.RowChanged).IsFalse();
    await Assert.That(completion.UnchangedReason).IsEqualTo(PerspectiveRowUnchangedReason.AlreadyCorrect)
      .Because("the stored row already holds what the rebuild folded");
  }

  [Test]
  public async Task ARebuildWriteTheGuardRefuses_WithAFoldThatDiffersFromTheStoredRow_CompletesWriteRefusedAsync() {
    var completion = await _rebuildOverStaleRowAsync(new ItemProgress { Started = true, Attempts = 2, Completed = true });

    await Assert.That(completion.RowChanged).IsFalse();
    await Assert.That(completion.UnchangedReason).IsEqualTo(PerspectiveRowUnchangedReason.WriteRefused)
      .Because("the stored row differs from the fold, and the guard kept it");
  }

  /// <summary>
  /// Seeds a row stamped above the stream's events, then rebuilds the whole stream through prepare, the batch write and
  /// complete. Returns the completion after checking the batch write was the one refused.
  /// </summary>
  private async Task<PerspectiveCursorCompletion> _rebuildOverStaleRowAsync(ItemProgress stored) {
    var streamId = Guid.CreateVersion7();
    var inner = new InMemoryEventStore();
    var started = await _appendAsync(inner, streamId, new ItemProgressStartedEvent { StreamId = streamId }, 1, Guid.Parse("019f0000-0000-7aaa-8000-000000002252"));
    var completed = await _appendAsync(inner, streamId, new ItemProgressCompletedEvent { StreamId = streamId }, 2, Guid.Parse("019f0001-0000-7aaa-8000-000000002252"));
    var eventStore = _stamped(inner, (started, 10), (completed, 11));
    stored.Id = streamId;
    await _seedProgressRowAsync(streamId, stored);

    var previous = ProcessingModeAccessor.Current;
    ProcessingModeAccessor.Current = ProcessingMode.Rebuild;
    try {
      var run = await _setBasedAsync(_progressRunner, eventStore, streamId, "item_progress", null,
        [_copy(started, eventStore), _copy(completed, eventStore)]);
      await Assert.That(run.Outcome!.Value.Status).IsEqualTo(PerspectiveWriteStatus.Refused)
        .Because("the batch write, not a per-pair rerun, is the write the guard turned away");
      return run.Completion;
    } finally {
      ProcessingModeAccessor.Current = previous;
    }
  }

  private async Task _seedProgressRowAsync(Guid streamId, ItemProgress data) {
    await using var seed = CreateDbContext();
    var then = new DateTime(2025, 12, 31, 0, 0, 0, DateTimeKind.Utc);
    seed.Set<PerspectiveRow<ItemProgress>>().Add(new PerspectiveRow<ItemProgress> {
      Id = streamId,
      Data = data,
      Metadata = new PerspectiveMetadata {
        EventId = TrackedGuid.New().Value.ToString("D"),
        EventType = "Whizbang.Data.EFCore.Postgres.Tests.Perspectives.ItemProgressStartedEvent",
        CommitSequence = REMOVED_EVENT_SEQUENCE,
        Timestamp = then
      },
      Scope = new PerspectiveScope(),
      CreatedAt = then,
      UpdatedAt = then,
      Version = 1
    });
    _ = await seed.SaveChangesAsync();
  }
}
