// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Perspectives;

namespace Whizbang.Core.Tests.Perspectives;

/// <summary>
/// A pending perspective write's operation follows from the version the apply read, so a write whose operation and
/// version disagree cannot be built (#2246).
/// </summary>
public class PendingPerspectiveWriteTests {
  private const string TABLE = "wh_per_sample";

  private static readonly PerspectiveRowValues _row = new(
    Data: "{}", Metadata: "{}", Scope: "{}", ForceUpdateScope: false, OrdersOnEventId: false,
    CreatedAt: new DateTime(2026, 11, 10, 0, 0, 0, DateTimeKind.Utc), UpdatedAt: new DateTime(2026, 11, 10, 0, 0, 0, DateTimeKind.Utc),
    SuppressActivity: false, BumpVersion: true, ExpiresAt: null, PhysicalValues: []);

  [Test]
  public async Task Write_WithAnUncheckedVersion_IsAnUpsertAsync() {
    var id = Guid.CreateVersion7();
    var write = PendingPerspectiveWrite.Write(TABLE, id, PerspectiveRowVersion.Unchecked, _row);

    await Assert.That(write.Operation).IsEqualTo(PerspectiveWriteOperation.Upsert);
    await Assert.That(write.Table).IsEqualTo(TABLE);
    await Assert.That(write.StreamId).IsEqualTo(id);
    await Assert.That(write.Row).IsSameReferenceAs(_row);
    await Assert.That(write.ExpectedVersion).IsEqualTo(PerspectiveRowVersion.Unchecked);
  }

  [Test]
  public async Task ForSink_KeysTheSameWriteToTheContextsSinkAsync() {
    var write = PendingPerspectiveWrite.Write(TABLE, Guid.CreateVersion7(), PerspectiveRowVersion.Of(5), _row);
    var purge = PendingPerspectiveWrite.Purge(TABLE, Guid.CreateVersion7());

    var keyed = write.ForSink(typeof(string));
    var keyedPurge = purge.ForSink("context-b");

    await Assert.That(write.SinkKey).IsNull().Because("a write names no context until its store keys it");
    await Assert.That(keyed.SinkKey).IsEqualTo(typeof(string));
    await Assert.That(keyed.Operation).IsEqualTo(write.Operation);
    await Assert.That(keyed.Table).IsEqualTo(write.Table);
    await Assert.That(keyed.StreamId).IsEqualTo(write.StreamId);
    await Assert.That(keyed.ExpectedVersion).IsEqualTo(write.ExpectedVersion);
    await Assert.That(keyed.Row).IsSameReferenceAs(write.Row);
    await Assert.That(keyedPurge.Operation).IsEqualTo(PerspectiveWriteOperation.Purge);
    await Assert.That(keyedPurge.SinkKey).IsEqualTo("context-b");
    await Assert.That(() => write.ForSink(null!)).Throws<ArgumentNullException>();
  }

  [Test]
  public async Task Write_WithAnAbsentVersion_IsAnInsertAsync() {
    var write = PendingPerspectiveWrite.Write(TABLE, Guid.CreateVersion7(), PerspectiveRowVersion.Absent, _row);

    await Assert.That(write.Operation).IsEqualTo(PerspectiveWriteOperation.Insert);
  }

  [Test]
  public async Task Write_WithAPresentVersion_IsAnUpdateOnThatVersionAsync() {
    var write = PendingPerspectiveWrite.Write(TABLE, Guid.CreateVersion7(), PerspectiveRowVersion.Of(42), _row);

    await Assert.That(write.Operation).IsEqualTo(PerspectiveWriteOperation.Update);
    await Assert.That(write.ExpectedVersion).IsEqualTo(PerspectiveRowVersion.Of(42));
  }

  [Test]
  public async Task Purge_CarriesNoRowAndNoVersionAsync() {
    var write = PendingPerspectiveWrite.Purge(TABLE, Guid.CreateVersion7());

    await Assert.That(write.Operation).IsEqualTo(PerspectiveWriteOperation.Purge);
    await Assert.That(write.Row).IsNull();
    await Assert.That(write.ExpectedVersion).IsEqualTo(PerspectiveRowVersion.Unchecked);
  }

  [Test]
  [Arguments(null)]
  [Arguments("")]
  [Arguments(" ")]
  public async Task Write_WithoutATable_ThrowsAsync(string? table) {
    await Assert.That(() => PendingPerspectiveWrite.Write(table!, Guid.CreateVersion7(), PerspectiveRowVersion.Unchecked, _row))
      .Throws<ArgumentException>();
    await Assert.That(() => PendingPerspectiveWrite.Purge(table!, Guid.CreateVersion7())).Throws<ArgumentException>();
  }

  [Test]
  public async Task Write_WithoutARow_ThrowsAsync() {
    await Assert.That(() => PendingPerspectiveWrite.Write(TABLE, Guid.CreateVersion7(), PerspectiveRowVersion.Unchecked, null!))
      .Throws<ArgumentNullException>();
  }

  [Test]
  public async Task Outcomes_CarryTheirStatusVersionAndErrorAsync() {
    var error = new InvalidOperationException("rejected");

    await Assert.That(PerspectiveWriteOutcome.Written.Status).IsEqualTo(PerspectiveWriteStatus.Written);
    await Assert.That(PerspectiveWriteOutcome.Refused.Status).IsEqualTo(PerspectiveWriteStatus.Refused);
    var conflict = PerspectiveWriteOutcome.Conflict(PerspectiveRowVersion.Of(7));
    await Assert.That(conflict.Status).IsEqualTo(PerspectiveWriteStatus.Conflict);
    await Assert.That(conflict.ActualVersion).IsEqualTo(PerspectiveRowVersion.Of(7));
    var failed = PerspectiveWriteOutcome.Failed(error);
    await Assert.That(failed.Status).IsEqualTo(PerspectiveWriteStatus.Failed);
    await Assert.That(failed.Error).IsSameReferenceAs(error);
    await Assert.That(() => PerspectiveWriteOutcome.Failed(null!)).Throws<ArgumentNullException>();
  }

  [Test]
  public async Task FollowedBy_WritesTheLaterValuesOnTheEarlierRowAsync() {
    var id = Guid.CreateVersion7();
    var first = PendingPerspectiveWrite.Write(TABLE, id, PerspectiveRowVersion.Absent, _row).ForSink("context-a");
    var laterRow = _row with {
      Data = "{\"n\":2}",
      Metadata = "{\"e\":2}",
      Scope = "{\"t\":2}",
      OrdersOnEventId = true,
      CreatedAt = _row.CreatedAt.AddDays(1),
      UpdatedAt = _row.UpdatedAt.AddDays(1),
      ExpiresAt = _row.UpdatedAt.AddDays(9),
      PhysicalValues = [new PerspectivePhysicalValue("amount", 2, "integer", false)],
    };
    // The later write was prepared on the held model, so it reads the row as present at the same base version.
    var later = PendingPerspectiveWrite.Write(TABLE, id, PerspectiveRowVersion.Absent, laterRow);

    var merged = first.FollowedBy(later);

    await Assert.That(merged.Operation).IsEqualTo(PerspectiveWriteOperation.Insert)
      .Because("the database still has no row: the merged write inserts it");
    await Assert.That(merged.ExpectedVersion).IsEqualTo(PerspectiveRowVersion.Absent);
    await Assert.That(merged.SinkKey).IsEqualTo("context-a");
    await Assert.That(merged.StreamId).IsEqualTo(id);
    await Assert.That(merged.Table).IsEqualTo(TABLE);
    await Assert.That(merged.Row!.Data).IsEqualTo(laterRow.Data);
    await Assert.That(merged.Row.Metadata).IsEqualTo(laterRow.Metadata);
    await Assert.That(merged.Row.Scope).IsEqualTo(laterRow.Scope);
    await Assert.That(merged.Row.OrdersOnEventId).IsTrue();
    await Assert.That(merged.Row.ExpiresAt).IsEqualTo(laterRow.ExpiresAt);
    await Assert.That(merged.Row.PhysicalValues).IsSameReferenceAs(laterRow.PhysicalValues);
    await Assert.That(merged.Row.CreatedAt).IsEqualTo(_row.CreatedAt)
      .Because("an inserted row is dated at the first event folded into it, which the first write resolved");
    await Assert.That(merged.Row.UpdatedAt).IsEqualTo(laterRow.UpdatedAt);
  }

  [Test]
  public async Task FollowedBy_KeepsTheLastStampedActivity_AndAnyScopeOrVersionMoveAsync() {
    var id = Guid.CreateVersion7();
    var first = PendingPerspectiveWrite.Write(TABLE, id, PerspectiveRowVersion.Of(7), _row with { ForceUpdateScope = true, BumpVersion = false });
    var quiet = PendingPerspectiveWrite.Write(TABLE, id, PerspectiveRowVersion.Of(7), _row with {
      UpdatedAt = _row.UpdatedAt.AddDays(3),
      SuppressActivity = true,
      ForceUpdateScope = false,
      BumpVersion = true,
    });

    var merged = first.FollowedBy(quiet);

    await Assert.That(merged.Operation).IsEqualTo(PerspectiveWriteOperation.Update);
    await Assert.That(merged.ExpectedVersion).IsEqualTo(PerspectiveRowVersion.Of(7));
    await Assert.That(merged.Row!.UpdatedAt).IsEqualTo(_row.UpdatedAt)
      .Because("a non-activity event keeps the business time the earlier write stamped");
    await Assert.That(merged.Row.SuppressActivity).IsFalse();
    await Assert.That(merged.Row.ForceUpdateScope).IsTrue().Because("the earlier write replaced the stored scope");
    await Assert.That(merged.Row.BumpVersion).IsTrue();

    var bothQuiet = PendingPerspectiveWrite.Write(TABLE, id, PerspectiveRowVersion.Of(7), _row with { SuppressActivity = true })
      .FollowedBy(quiet);
    await Assert.That(bothQuiet.Row!.SuppressActivity).IsTrue().Because("neither write stamped a business time");
  }

  [Test]
  public async Task FollowedBy_RefusesAPurgeOrAnotherRowAsync() {
    var id = Guid.CreateVersion7();
    var write = PendingPerspectiveWrite.Write(TABLE, id, PerspectiveRowVersion.Of(1), _row);
    var purge = PendingPerspectiveWrite.Purge(TABLE, id);

    await Assert.That(() => write.FollowedBy(null!)).Throws<ArgumentNullException>();
    await Assert.That(() => write.FollowedBy(purge)).Throws<ArgumentException>();
    await Assert.That(() => purge.FollowedBy(write)).Throws<ArgumentException>();
    await Assert.That(() => write.FollowedBy(PendingPerspectiveWrite.Write(TABLE, Guid.CreateVersion7(), PerspectiveRowVersion.Of(1), _row)))
      .Throws<ArgumentException>();
    await Assert.That(() => write.FollowedBy(PendingPerspectiveWrite.Write("wh_per_other", id, PerspectiveRowVersion.Of(1), _row)))
      .Throws<ArgumentException>();
  }
}
