// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// #2104: the store that keeps the integrity gap tracker across restarts: each origin's last checkpoint (name, request
/// address, time) and each origin's pending gaps, read back in one load.
/// </summary>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/EFCoreWorkCoordinator.cs</code-under-test>
/// <docs>resilience/stream-integrity#the-gap-tracker-survives-a-restart</docs>
[Category("Integration")]
[Category("Shard2")]
public class IntegrityGapTrackerStoreSqlTests : EFCoreTestBase {
  private static readonly DateTimeOffset _t0 = new(2026, 11, 6, 9, 0, 0, TimeSpan.Zero);

  private readonly List<WorkCoordinationDbContext> _contexts = [];

  private IWorkCoordinator _coordinator() {
    var ctx = CreateDbContext();
    _contexts.Add(ctx);
    return new EFCoreWorkCoordinator<WorkCoordinationDbContext>(ctx, Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions());
  }

  [After(Test)]
  public async Task DisposeContextsAsync() {
    foreach (var ctx in _contexts) {
      await ctx.DisposeAsync();
    }
  }

  private static IntegrityGapTracker.PendingGap _gap(Guid origin, string type, string? tenant = "tenant-a") => new() {
    OriginServiceId = origin,
    OriginServiceName = "origin-svc",
    TenantScope = tenant,
    EventType = type,
    FromCommitSequence = 10,
    ToCommitSequence = 20,
    ExpectedCount = 3,
  };

  [Test]
  public async Task NothingKept_LoadsAnEmptyStateAsync() {
    var state = await _coordinator().IntegrityLoadGapTrackerStateAsync();

    await Assert.That(state).IsNotNull();
    await Assert.That(state!.Origins).IsEmpty();
    await Assert.That(state.Pending).IsEmpty();
  }

  [Test]
  public async Task ACheckpoint_IsKept_AndALaterOneMovesTheTimeAndKeepsAKnownAddressAsync() {
    var coordinator = _coordinator();
    var origin = TrackedGuid.New().Value;

    await coordinator.IntegrityRecordOriginCheckpointAsync(origin, "origin-svc", "origin.requests", _t0);
    await coordinator.IntegrityRecordOriginCheckpointAsync(origin, "origin-svc", requestTopic: null, _t0.AddMinutes(1));

    var kept = (await _coordinator().IntegrityLoadGapTrackerStateAsync())!.Origins.Single();
    await Assert.That(kept).IsEqualTo(new IntegrityKnownOrigin(origin, "origin-svc", "origin.requests", _t0.AddMinutes(1)))
      .Because("an origin that announces no address keeps the one it announced before");
  }

  [Test]
  public async Task PendingGaps_AreReplacedPerOrigin_AndReadBackAsync() {
    var coordinator = _coordinator();
    var (origin, other) = (TrackedGuid.New().Value, TrackedGuid.New().Value);
    await coordinator.IntegrityReplacePendingGapsAsync(origin, [_gap(origin, "Contracts.A"), _gap(origin, "Contracts.B", tenant: null)]);
    await coordinator.IntegrityReplacePendingGapsAsync(other, [_gap(other, "Contracts.A")]);

    await coordinator.IntegrityReplacePendingGapsAsync(origin, [_gap(origin, "Contracts.B", tenant: null)]);

    var pending = (await _coordinator().IntegrityLoadGapTrackerStateAsync())!.Pending;
    await Assert.That(pending).IsEquivalentTo([_gap(origin, "Contracts.B", tenant: null), _gap(other, "Contracts.A")])
      .Because("a replace is per origin: the confirmed gap leaves, the other origin's stay, and no tenant reads back as none");
  }

  [Test]
  public async Task ReplacingWithNothing_ClearsTheOriginsPendingGapsAsync() {
    var coordinator = _coordinator();
    var origin = TrackedGuid.New().Value;
    await coordinator.IntegrityReplacePendingGapsAsync(origin, [_gap(origin, "Contracts.A")]);

    await coordinator.IntegrityReplacePendingGapsAsync(origin, []);

    await Assert.That((await _coordinator().IntegrityLoadGapTrackerStateAsync())!.Pending).IsEmpty();
  }
}
