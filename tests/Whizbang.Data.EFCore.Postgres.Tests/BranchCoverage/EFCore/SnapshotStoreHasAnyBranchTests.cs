// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Data.EFCore.Postgres.Tests.BranchCoverage.EFCore;

/// <summary>
/// Branch coverage for <see cref="EFCorePerspectiveSnapshotStore.HasAnySnapshotAsync"/>: both
/// answers, before and after a snapshot is taken, in one place.
/// </summary>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/EFCorePerspectiveSnapshotStore.cs</code-under-test>
[Category("Shard5")]
public class SnapshotStoreHasAnyBranchTests : EFCoreTestBase {

  [Test]
  public async Task HasAnySnapshot_FalseBeforeTheFirstSnapshot_TrueAfterAsync() {
    await using var dataSource = NpgsqlDataSource.Create(ConnectionString);
    var store = new EFCorePerspectiveSnapshotStore(dataSource);
    var streamId = Guid.CreateVersion7();
    const string perspective = "BranchCoveragePerspective";

    var before = await store.HasAnySnapshotAsync(streamId, perspective);
    using (var data = JsonDocument.Parse("""{"k":"v"}""")) {
      await store.CreateSnapshotAsync(streamId, perspective, Guid.CreateVersion7(), data);
    }
    var after = await store.HasAnySnapshotAsync(streamId, perspective);

    await Assert.That(before).IsFalse()
      .Because("a stream that was never snapshotted has nothing to rewind from");
    await Assert.That(after).IsTrue();
  }
}
