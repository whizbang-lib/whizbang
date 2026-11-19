// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Perspectives;

namespace Whizbang.Core.Tests.Perspectives;

/// <summary>
/// #1734: whether a snapshot is live, so a rebuild that reads no archived data may start from it. A snapshot no archive
/// covers is live; one at the archive's last version (the base a new generation continues from) is live; one strictly
/// inside the archive, or one that records no version, is not.
/// </summary>
/// <docs>fundamentals/perspectives/rebuild#archived-data</docs>
public class PerspectiveSnapshotFloorTests {
  private static PerspectiveSnapshotFloor _floor(int? version, long? archivedThrough) =>
    new(Guid.Empty, version, JsonDocument.Parse("{}"), archivedThrough);

  [Test]
  public async Task IsLive_NoArchive_AtTheBoundary_AndPastIt_StrictlyInside_OrWithNoVersion_IsNotAsync() {
    await Assert.That(_floor(2, null).IsLive).IsTrue().Because("no archive covers it");
    await Assert.That(_floor(3, 3).IsLive).IsTrue().Because("at the archive's last version every later event is hot");
    await Assert.That(_floor(4, 3).IsLive).IsTrue();
    await Assert.That(_floor(2, 3).IsLive).IsFalse().Because("version 3 is archived and after it");
    await Assert.That(_floor(null, 3).IsLive).IsFalse().Because("a snapshot with no version cannot be placed against the archive");
  }
}
