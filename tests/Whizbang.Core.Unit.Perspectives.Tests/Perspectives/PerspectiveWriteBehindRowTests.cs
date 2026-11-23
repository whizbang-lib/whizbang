// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Perspectives;

namespace Whizbang.Core.Tests.Perspectives;

/// <summary>A held write-behind row (#2247), and the defaults that keep anything other than a generated runner out of it.</summary>
/// <docs>fundamentals/perspectives/write-modes#write-behind</docs>
public class PerspectiveWriteBehindRowTests {
  private sealed class Row(PerspectiveApplyRead read, Guid last, long bytes) : PerspectiveWriteBehindRow(read, last, bytes) {
    public override PerspectiveStoredRow<TModel>? ReadAs<TModel>() => null;
  }

  private sealed class Prepared() : PreparedPerspectiveApply(
    Guid.CreateVersion7(), "Tests.Held", null, [], null, PerspectivePerPairReason.None);

  private sealed class BatchState : IPerspectiveBatchState {
    public bool IsKnownNew(Guid streamId, string perspectiveName) => false;
    public Task<PerspectiveStoredRow<TModel>?> ReadRowAsync<TModel>(IPerspectiveStore<TModel> store, Guid streamId, string perspectiveName, CancellationToken cancellationToken = default) where TModel : class =>
      Task.FromResult<PerspectiveStoredRow<TModel>?>(null);
    public Task<bool?> IsPurgedAsync(IPerspectivePurgeMarkerStore markers, Guid streamId, string perspectiveName, CancellationToken cancellationToken = default) =>
      Task.FromResult<bool?>(null);
    public Task<bool?> HasHistoryBeforeAsync(Messaging.IEventStore eventStore, Guid streamId, string perspectiveName, Guid beforeEventId, IReadOnlyList<Type> eventTypes, CancellationToken cancellationToken = default) =>
      Task.FromResult<bool?>(null);
    public Task<PerspectiveCommitSequence?> CommitSequenceOfAsync(Messaging.IEventStore eventStore, Guid eventId, CancellationToken cancellationToken = default) =>
      Task.FromResult<PerspectiveCommitSequence?>(null);
  }

  [Test]
  public async Task AHeldRow_CarriesItsReadPositionAndSizeAsync() {
    var last = Guid.CreateVersion7();
    var read = new PerspectiveApplyRead(PerspectiveRowVersion.Of(4), null);
    var row = new Row(read, last, 12);

    await Assert.That(row.ApplyRead).IsSameReferenceAs(read);
    await Assert.That(row.LastEventId).IsEqualTo(last);
    await Assert.That(row.Bytes).IsEqualTo(12);
    await Assert.That(() => new Row(null!, last, 0)).Throws<ArgumentNullException>();
    await Assert.That(() => new Row(read, last, -1)).Throws<ArgumentOutOfRangeException>();
  }

  [Test]
  public async Task NothingIsHeld_UnlessABatchAsksForItAsync() {
    await Assert.That(new Prepared().WriteBehindRow).IsNull()
      .Because("a hand-written runner's prepared pair is never held");
    await Assert.That(((IPerspectiveBatchState)new BatchState()).KeepsModels(Guid.CreateVersion7(), "Tests.Held")).IsFalse()
      .Because("keeping a model costs a serialization, so a batch keeps none unless it writes behind");
  }
}
