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
}
