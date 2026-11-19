// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Serialization;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// #2208: the per-stream ledger rows with no window that a database held before this release were compared over the
/// whole history under an older comparison rule, many of them while this service held nothing of the stream. The paced
/// drain no longer repairs such a row until a comparison under the current rule has sighted it again (the same wait an
/// answered row keeps), so it stops asking origins for events this service already holds; the idle re-check compares
/// them over the whole history and clears the ones that match. Rows written after the upgrade are not held.
/// </summary>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/375_LedgerRowsWithoutAWindowAreRechecked.sql</code-under-test>
/// <docs>resilience/stream-integrity#idle-ledger-recheck</docs>
[Category("Integration")]
[Category("Shard5")]
public class IntegrityLedgerUnconfirmedHoldSqlTests : EFCoreTestBase {
  private static readonly DateTimeOffset _t0 = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);
  private static readonly TimeSpan _cooldown = TimeSpan.FromMinutes(60);
  private readonly List<WorkCoordinationDbContext> _contexts = [];

  [Test]
  public async Task Hold_ALegacyRowWithNoWindow_WaitsForAComparisonBeforeTheDrainRepairsIt_OnceAsync() {
    var coordinator = _coordinator();
    var origin = TrackedGuid.New().Value;
    var (legacy, windowed, surplus, lane) = (_key(origin), _key(origin), _key(origin), _key(origin, Guid.Empty));
    await _reportAsync(coordinator, legacy, isDeficit: true);
    await _reportAsync(coordinator, windowed, isDeficit: true);
    await coordinator.IntegrityStampRepairWindowsAsync(origin, [windowed], 100, 500);
    await _reportAsync(coordinator, surplus, isDeficit: false);
    await _reportAsync(coordinator, lane, isDeficit: true);
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    // The test database ran 375 when it was made; this test runs the step as a database that has not.
    await _executeAsync(conn, "DELETE FROM wh_settings WHERE setting_key = 'integrity_hold_unconfirmed_rows_version'");

    var held = await _scalarAsync<int>(conn, "SELECT wh_integrity_hold_unconfirmed_rows()");
    var claimedWhileHeld = await _drainAsync(coordinator, origin, _t0.AddMinutes(1));
    await _reportAsync(coordinator, legacy, isDeficit: true, at: _t0.AddMinutes(2));   // a comparison sights it again
    var claimedAfterSighting = await _drainAsync(coordinator, origin, _t0.AddMinutes(3));
    var later = _key(origin);
    await _reportAsync(coordinator, later, isDeficit: true, at: _t0.AddMinutes(4));
    var again = await _scalarAsync<int>(conn, "SELECT wh_integrity_hold_unconfirmed_rows()");

    await Assert.That(held).IsEqualTo(1)
      .Because("only the per-stream deficit with no window is held: a windowed row, a surplus and the type lane are not");
    await Assert.That(claimedWhileHeld.Select(c => c.StreamId).ToList()).IsEquivalentTo([windowed.StreamId])
      .Because("the drain does not repair a whole-history row no comparison under the current rule has confirmed");
    await Assert.That(claimedAfterSighting.Select(c => c.StreamId).ToList()).IsEquivalentTo([legacy.StreamId])
      .Because("once a comparison sights it again the deficit is confirmed, and the drain repairs it");
    await Assert.That(again).IsEqualTo(0).Because("the step runs once per database");
    await Assert.That(await _scalarAsync<bool>(conn,
      "SELECT answered_at IS NULL FROM wh_integrity_ledger WHERE stream_id = $1", later.StreamId)).IsTrue()
      .Because("a row written after the upgrade was compared under the current rule and is not held");
  }

  [After(Test)]
  public async Task DisposeContextsAsync() {
    foreach (var ctx in _contexts) {
      await ctx.DisposeAsync();
    }
  }

  private EFCoreWorkCoordinator<WorkCoordinationDbContext> _coordinator() {
    var ctx = CreateDbContext();
    _contexts.Add(ctx);
    return new EFCoreWorkCoordinator<WorkCoordinationDbContext>(ctx, JsonContextRegistry.CreateCombinedOptions());
  }

  private static IntegrityRepairLedger.DivergenceKey _key(Guid origin, Guid? stream = null) =>
    new(origin, null, "Contracts.HoldProbe", stream ?? TrackedGuid.New().Value);

  private static async Task _reportAsync(
      IWorkCoordinator coordinator, IntegrityRepairLedger.DivergenceKey key, bool isDeficit, DateTimeOffset? at = null) =>
    _ = await coordinator.IntegrityTryBeginReportBatchAsync(
      key.OriginServiceId,
      [new IntegrityReportObservation(key, 11, 21, 0, 0, isDeficit, isDeficit ? 1 : -1)], at ?? _t0, _cooldown)
      ?? throw new InvalidOperationException("the report batch failed");

  private static Task<IReadOnlyList<IntegrityRepairDrainItem>> _drainAsync(
      IWorkCoordinator coordinator, Guid origin, DateTimeOffset now) =>
    coordinator.IntegrityClaimRepairDrainAsync([origin], now, TimeSpan.FromHours(1), 10, 10);   // a claimed row waits out the hour

  private static async Task _executeAsync(NpgsqlConnection conn, string sql) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    await cmd.ExecuteNonQueryAsync();
  }

  private static async Task<T> _scalarAsync<T>(NpgsqlConnection conn, string sql, params object[] args) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    foreach (var arg in args) {
      cmd.Parameters.Add(new NpgsqlParameter { Value = arg });
    }
    return (T)(await cmd.ExecuteScalarAsync())!;
  }
}
