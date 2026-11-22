// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Core.Tests.Archival;

/// <summary>
/// #2367: the orphan sweep deletes an object only once storage shows it was created at least the deletion delay ago. An
/// object storage reports no creation time for cannot be shown to have outlived the delay, so it is kept.
/// </summary>
/// <docs>operations/deployment/restoring-an-environment-with-an-archive#the-orphan-sweep</docs>
/// <code-under-test>src/Whizbang.Data.Postgres/PostgresObjectArchiveStore.cs</code-under-test>
public class ObjectArchiveOrphanAgeTests {
  private static readonly DateTimeOffset _cutoff = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

  [Test]
  public async Task AnObjectCreatedAtOrBeforeTheCutoff_IsPastTheDelayAsync() {
    await Assert.That(Whizbang.Data.Postgres.PostgresObjectArchiveStore.IsPastDelay(_cutoff.AddDays(-1), _cutoff)).IsTrue();
    await Assert.That(Whizbang.Data.Postgres.PostgresObjectArchiveStore.IsPastDelay(_cutoff, _cutoff)).IsTrue();
  }

  [Test]
  public async Task AnObjectCreatedAfterTheCutoff_IsNotPastTheDelayAsync() {
    await Assert.That(Whizbang.Data.Postgres.PostgresObjectArchiveStore.IsPastDelay(_cutoff.AddSeconds(1), _cutoff)).IsFalse();
  }

  [Test]
  public async Task AnObjectWithNoCreationTime_IsNeverPastTheDelayAsync() {
    await Assert.That(Whizbang.Data.Postgres.PostgresObjectArchiveStore.IsPastDelay(null, _cutoff)).IsFalse()
      .Because("the sweep cannot show such an object outlived the delay, so it keeps it");
  }

  [Test]
  [Arguments("0199a1b2-0000-7000-8000-000000000001/0000000001-0000000002-ab.jsonl.br", "0199a1b2-0000-7000-8000-000000000001")]
  [Arguments("0199a1b2-0000-7000-8000-000000000001/0000000001-0000000002-ab.jsonl", "0199a1b2-0000-7000-8000-000000000001")]
  [Arguments("0199a1b2-0000-7000-8000-000000000001/0000000001-0000000002-ab.txt", null)]
  [Arguments("0199a1b2-0000-7000-8000-000000000001/nested/0000000001-0000000002-ab.jsonl.br", null)]
  [Arguments("notes/0000000001-0000000002-ab.jsonl.br", null)]
  [Arguments("/0000000001-0000000002-ab.jsonl.br", null)]
  [Arguments("0000000001-0000000002-ab.jsonl.br", null)]
  public async Task StreamOf_ReadsTheStreamOfANameTheCoordinatorWrites_AndNothingElseAsync(string name, string? stream) {
    await Assert.That(Whizbang.Data.Postgres.PostgresObjectArchiveStore.StreamOf(name)).IsEqualTo(stream is null ? null : Guid.Parse(stream))
      .Because("the orphan sweep only ever touches an object whose name is one the coordinator writes");
  }
}
