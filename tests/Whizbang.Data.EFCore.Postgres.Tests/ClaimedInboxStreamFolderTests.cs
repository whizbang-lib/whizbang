using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Priority;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// The per-stream fold the coordinator hands the batch hooks (priority step 1): one entry per inbox stream in
/// the order the claim returned them, carrying the stream's most urgent row, its oldest arrival and how many
/// of its rows the batch holds. Rows of other sources never fold into an inbox stream, and a row a claim
/// without the columns returns folds to the standard band.
/// </summary>
/// <docs>fundamentals/messaging/message-priority#hooks</docs>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/ClaimedInboxStreamFolder.cs</code-under-test>
[Category("Shard2")]
public class ClaimedInboxStreamFolderTests {
  private static readonly DateTimeOffset _t0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

  private static WorkBatchRow _row(string source, Guid? streamId, int? priority, DateTimeOffset? receivedAt) =>
    new() { Source = source, StreamId = streamId, Priority = priority, ReceivedAt = receivedAt };

  [Test]
  public async Task Fold_GroupsInboxRowsByStream_InFirstAppearanceOrderAsync() {
    var a = (Guid)TrackedGuid.NewMedo();
    var b = (Guid)TrackedGuid.NewMedo();
    var rows = new List<WorkBatchRow> {
      _row("inbox", a, WorkPriority.STANDARD, _t0.AddMinutes(-2)),
      _row("inbox", b, WorkPriority.BACKGROUND, _t0.AddMinutes(-9)),
      _row("inbox", a, WorkPriority.INTERACTIVE, _t0.AddMinutes(-7)),
      _row("inbox", a, WorkPriority.STANDARD, _t0.AddMinutes(-1)),
    };

    var folds = ClaimedInboxStreamFolder.Fold(rows);

    await Assert.That(folds.Count).IsEqualTo(2);
    await Assert.That(folds[0].StreamId).IsEqualTo(a)
      .Because("streams keep the order the claim returned them in, which is its bucket order");
    await Assert.That(folds[0].FoldedPriority).IsEqualTo(WorkPriority.INTERACTIVE)
      .Because("the stream's most urgent row folds the stream");
    await Assert.That(folds[0].OldestReceivedAt).IsEqualTo(_t0.AddMinutes(-7))
      .Because("the oldest arrival, not the first row's");
    await Assert.That(folds[0].PendingRows).IsEqualTo(3);
    await Assert.That(folds[1].StreamId).IsEqualTo(b);
    await Assert.That(folds[1].FoldedPriority).IsEqualTo(WorkPriority.BACKGROUND);
    await Assert.That(folds[1].OldestReceivedAt).IsEqualTo(_t0.AddMinutes(-9));
    await Assert.That(folds[1].PendingRows).IsEqualTo(1);
  }

  [Test]
  public async Task Fold_SkipsOtherSources_AndInboxRowsWithoutAStreamAsync() {
    var a = (Guid)TrackedGuid.NewMedo();
    var rows = new List<WorkBatchRow> {
      _row("outbox", a, WorkPriority.INTERACTIVE, _t0),
      _row("perspective", a, null, null),
      _row("inbox", null, WorkPriority.INTERACTIVE, _t0),
      _row("inbox", Guid.Empty, WorkPriority.INTERACTIVE, _t0),
      _row("inbox", a, WorkPriority.STANDARD, _t0),
    };

    var folds = ClaimedInboxStreamFolder.Fold(rows);

    await Assert.That(folds.Count).IsEqualTo(1);
    await Assert.That(folds[0].StreamId).IsEqualTo(a);
    await Assert.That(folds[0].FoldedPriority).IsEqualTo(WorkPriority.STANDARD)
      .Because("an outbox row's number never folds into the inbox stream that shares its id");
    await Assert.That(folds[0].PendingRows).IsEqualTo(1);
  }

  [Test]
  public async Task Fold_RowsWithoutANumberOrAnArrival_FoldToTheStandardBandAsync() {
    // What a claim_work that predates migration 150 returns: the rows without the two columns.
    var a = (Guid)TrackedGuid.NewMedo();
    var rows = new List<WorkBatchRow> {
      _row("inbox", a, null, null),
      _row("inbox", a, null, null),
    };

    var folds = ClaimedInboxStreamFolder.Fold(rows);

    await Assert.That(folds.Count).IsEqualTo(1);
    await Assert.That(folds[0].FoldedPriority).IsEqualTo(WorkPriority.STANDARD)
      .Because("an undeclared number is the standard band everywhere the framework reads one");
    await Assert.That(folds[0].OldestReceivedAt.HasValue).IsFalse();
    await Assert.That(folds[0].PendingRows).IsEqualTo(2);
  }

  [Test]
  public async Task Fold_AnUnknownArrival_NeverDisplacesAKnownOldestAsync() {
    var a = (Guid)TrackedGuid.NewMedo();
    var rows = new List<WorkBatchRow> {
      _row("inbox", a, WorkPriority.STANDARD, null),
      _row("inbox", a, WorkPriority.STANDARD, _t0),
      _row("inbox", a, WorkPriority.STANDARD, null),
      _row("inbox", a, WorkPriority.STANDARD, _t0.AddMinutes(1)),
    };

    var folds = ClaimedInboxStreamFolder.Fold(rows);

    await Assert.That(folds[0].OldestReceivedAt).IsEqualTo(_t0)
      .Because("the first known arrival stands until an older one is seen; a missing arrival is not an old one");
    await Assert.That(folds[0].PendingRows).IsEqualTo(4);
  }
}
