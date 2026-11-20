// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Serialization;
using Whizbang.Rules;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// #2377: the pair guard's halt history, against the real table. A service keeps one open halt per peer and side: a
/// second instance that halts the same pair, or the same pair halting for another reason, updates it and keeps the
/// earliest halted-at; the alert and the resumption are stamped on it; the open halts are what a new process loads.
/// </summary>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/535_PairHaltsSurviveARestart.sql</code-under-test>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/EFCoreWorkCoordinator.cs</code-under-test>
/// <docs>resilience/stream-integrity#the-pair-guard</docs>
[Category("Integration")]
[Category("Shard5")]
public class IntegrityPairHaltSqlTests : EFCoreTestBase {
  // Fixed instants, not the host clock: every call here takes the caller's clock.
  private static readonly DateTimeOffset _t0 = new(2026, 11, 20, 9, 0, 0, TimeSpan.Zero);

  private readonly List<WorkCoordinationDbContext> _contexts = [];

  private EFCoreWorkCoordinator<WorkCoordinationDbContext> _coordinator() {
    var ctx = CreateDbContext();
    _contexts.Add(ctx);
    return new EFCoreWorkCoordinator<WorkCoordinationDbContext>(ctx, JsonContextRegistry.CreateCombinedOptions());
  }

  [After(Test)]
  public async Task DisposeContextsAsync() {
    foreach (var ctx in _contexts) {
      await ctx.DisposeAsync();
    }
  }

  private static IntegrityPairHaltRecord _halt(string peer, IntegrityPairSide side, DateTimeOffset at,
      RulePairHaltReason reason = RulePairHaltReason.TypeRules) =>
    new(peer, side, reason, reason == RulePairHaltReason.TypeRules ? ["Contracts.A", "Contracts.B"] : [],
      RuleSemantics.VERSION, RuleSemantics.ELIGIBILITY_SQL_VERSION, "local-fp",
      reason == RulePairHaltReason.NoFingerprint ? null : RuleSemantics.VERSION,
      reason == RulePairHaltReason.NoFingerprint ? null : RuleSemantics.ELIGIBILITY_SQL_VERSION,
      reason == RulePairHaltReason.NoFingerprint ? null : "peer-fp", at, null);

  private async Task<long> _countAsync(string sql) {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    return Convert.ToInt64(await cmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
  }

  [Test]
  public async Task AHalt_IsKeptWithEverythingItSays_AndLoadedAsAnOpenHaltAsync() {
    var coordinator = _coordinator();

    var kept = await coordinator.IntegrityRecordPairHaltAsync(_halt("orders-svc", IntegrityPairSide.Consumer, _t0));
    var loaded = await _coordinator().IntegrityLoadPairHaltsAsync();

    await Assert.That(kept).IsEqualTo(_halt("orders-svc", IntegrityPairSide.Consumer, _t0) with { DifferingTypes = kept!.DifferingTypes });
    await Assert.That(kept.DifferingTypes).IsEquivalentTo(["Contracts.A", "Contracts.B"]);
    var open = loaded!.Single();
    await Assert.That(open.Peer).IsEqualTo("orders-svc");
    await Assert.That(open.Side).IsEqualTo(IntegrityPairSide.Consumer);
    await Assert.That(open.Reason).IsEqualTo(RulePairHaltReason.TypeRules);
    await Assert.That(open.DifferingTypes).IsEquivalentTo(["Contracts.A", "Contracts.B"]);
    await Assert.That(open.LocalFingerprint).IsEqualTo("local-fp");
    await Assert.That(open.PeerFingerprint).IsEqualTo("peer-fp");
    await Assert.That(open.PeerSemanticsVersion).IsEqualTo(RuleSemantics.VERSION);
    await Assert.That(open.HaltedAt).IsEqualTo(_t0);
    await Assert.That(open.AlertedAt).IsNull();
  }

  [Test]
  public async Task TheSamePairHaltedAgain_UpdatesItsOneOpenHalt_KeepingTheEarliestHaltedAtAsync() {
    var coordinator = _coordinator();
    _ = await coordinator.IntegrityRecordPairHaltAsync(_halt("orders-svc", IntegrityPairSide.Origin, _t0));

    var later = await coordinator.IntegrityRecordPairHaltAsync(
      _halt("orders-svc", IntegrityPairSide.Origin, _t0.AddMinutes(30), RulePairHaltReason.NoFingerprint));
    var earlier = await coordinator.IntegrityRecordPairHaltAsync(_halt("orders-svc", IntegrityPairSide.Origin, _t0.AddMinutes(-10)));

    await Assert.That(later!.HaltedAt).IsEqualTo(_t0).Because("the pair has been halted since the first halt");
    await Assert.That(later.Reason).IsEqualTo(RulePairHaltReason.NoFingerprint).Because("the latest reason is what it says now");
    await Assert.That(later.PeerFingerprint).IsNull();
    await Assert.That(earlier!.HaltedAt).IsEqualTo(_t0.AddMinutes(-10))
      .Because("another instance that saw the halt earlier moves it back");
    await Assert.That(await _countAsync("SELECT COUNT(*) FROM wh_integrity_pair_halts")).IsEqualTo(1L);
  }

  [Test]
  public async Task EachPeerAndSide_HasAHaltOfItsOwnAsync() {
    var coordinator = _coordinator();

    _ = await coordinator.IntegrityRecordPairHaltAsync(_halt("orders-svc", IntegrityPairSide.Origin, _t0));
    _ = await coordinator.IntegrityRecordPairHaltAsync(_halt("orders-svc", IntegrityPairSide.Consumer, _t0));
    _ = await coordinator.IntegrityRecordPairHaltAsync(_halt("billing-svc", IntegrityPairSide.Origin, _t0));

    await Assert.That((await coordinator.IntegrityLoadPairHaltsAsync())!.Count).IsEqualTo(3);
  }

  [Test]
  public async Task TheAlert_IsStampedOnce_OnTheOpenHaltAsync() {
    var coordinator = _coordinator();
    _ = await coordinator.IntegrityRecordPairHaltAsync(_halt("orders-svc", IntegrityPairSide.Origin, _t0));

    await coordinator.IntegrityRecordPairHaltAlertAsync("orders-svc", IntegrityPairSide.Origin, _t0.AddHours(2));
    await coordinator.IntegrityRecordPairHaltAlertAsync("orders-svc", IntegrityPairSide.Origin, _t0.AddHours(3));
    var kept = await coordinator.IntegrityRecordPairHaltAsync(_halt("orders-svc", IntegrityPairSide.Origin, _t0.AddHours(4)));

    await Assert.That(kept!.AlertedAt).IsEqualTo(_t0.AddHours(2)).Because("the first alert is when it was raised");
    await Assert.That((await coordinator.IntegrityLoadPairHaltsAsync())!.Single().AlertedAt).IsEqualTo(_t0.AddHours(2));
  }

  [Test]
  public async Task Resuming_ClosesTheHalt_AndTheNextHaltOpensANewOneAsync() {
    var coordinator = _coordinator();
    _ = await coordinator.IntegrityRecordPairHaltAsync(_halt("orders-svc", IntegrityPairSide.Origin, _t0));

    await coordinator.IntegrityRecordPairResumedAsync("orders-svc", IntegrityPairSide.Origin, _t0.AddMinutes(45));
    var loadedAfterResume = await coordinator.IntegrityLoadPairHaltsAsync();
    var next = await coordinator.IntegrityRecordPairHaltAsync(_halt("orders-svc", IntegrityPairSide.Origin, _t0.AddHours(5)));

    await Assert.That(loadedAfterResume).IsEmpty().Because("a resumed halt is history, not state a new process takes up");
    await Assert.That(next!.HaltedAt).IsEqualTo(_t0.AddHours(5)).Because("a new halt starts its own clock");
    await Assert.That(await _countAsync(
        "SELECT COUNT(*) FROM wh_integrity_pair_halts WHERE resumed_at = '2026-11-20T09:45:00Z'::timestamptz"))
      .IsEqualTo(1L).Because("the history keeps how long the pair was halted");
    await Assert.That(await _countAsync("SELECT COUNT(*) FROM wh_integrity_pair_halts")).IsEqualTo(2L);
  }
}
