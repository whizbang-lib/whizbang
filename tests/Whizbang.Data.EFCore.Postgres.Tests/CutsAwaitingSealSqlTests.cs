// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// #2365, owner decision: <c>eligible_not_yet_deleted</c> counts the streams due for a cut that the seal gate holds (this
/// service's own seal, or a consumer's when the cut waits for consumers). The truncator records a stream when its cut
/// answers <c>awaiting_seal</c> and forgets it on any other answer; the reading is a bounded count per task.
/// </summary>
/// <docs>resilience/stream-integrity#integrity-instruments</docs>
[Category("Integration")]
[Category("Shard2")]
public class CutsAwaitingSealSqlTests : EFCoreTestBase {
  private static readonly DateTimeOffset _now = new(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);

  private IWorkCoordinator _coordinator() =>
    new EFCoreWorkCoordinator<WorkCoordinationDbContext>(new WorkCoordinationDbContext(DbContextOptions),
      Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions());

  [Test]
  public async Task AStreamHeldByTheSeal_IsCountedPerTask_UntilItsCutAnswersOtherwiseAsync() {
    var coordinator = _coordinator();
    var (a, b, c) = (TrackedGuid.New().Value, TrackedGuid.New().Value, TrackedGuid.New().Value);

    await coordinator.RecordCutAwaitingSealAsync(a, "truncate", _now);
    await coordinator.RecordCutAwaitingSealAsync(b, "truncate", _now);
    await coordinator.RecordCutAwaitingSealAsync(c, "archive", _now);
    await coordinator.RecordCutAwaitingSealAsync(a, "truncate", _now.AddMinutes(1));   // asked again: still one stream
    var held = await coordinator.GetCutsAwaitingSealAsync(cap: 100);

    await Assert.That(held.Reported).IsTrue();
    await Assert.That(held.Tasks["truncate"]).IsEqualTo(new Whizbang.Core.Observability.PendingRows(2, false));
    await Assert.That(held.Tasks["archive"]).IsEqualTo(new Whizbang.Core.Observability.PendingRows(1, false));

    await coordinator.RecordCutAwaitingSealAsync(a, null, _now.AddMinutes(2));
    await coordinator.RecordCutAwaitingSealAsync(c, null, _now.AddMinutes(2));
    var after = await coordinator.GetCutsAwaitingSealAsync(cap: 100);

    await Assert.That(after.Tasks["truncate"].Rows).IsEqualTo(1L).Because("a cut that answered otherwise is no longer held");
    await Assert.That(after.Tasks.ContainsKey("archive")).IsFalse();
  }

  [Test]
  public async Task TheCount_StopsAtItsCap_AndSaysSoAsync() {
    var coordinator = _coordinator();
    for (var i = 0; i < 3; i++) {
      await coordinator.RecordCutAwaitingSealAsync(TrackedGuid.New().Value, "truncate", _now);
    }

    var held = await coordinator.GetCutsAwaitingSealAsync(cap: 2);

    await Assert.That(held.Tasks["truncate"]).IsEqualTo(new Whizbang.Core.Observability.PendingRows(2, true))
      .Because("the count reads at most the cap's worth of the index, so a large backlog costs the same as a small one");
  }
}
