// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Dapper;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Perspectives;

namespace Whizbang.Data.Dapper.Postgres.Tests.Perspectives;

/// <summary>
/// The table work a staging load (#2248) needs: the shadow's plain indexes are dropped before the load and built after
/// it, under names the swap matches to the live ones, while the key and the unique index stay so the load refuses what
/// the per-row write refuses; and the swap carries the live table's owner and grants to the table it installs.
/// </summary>
public partial class PostgresPerspectiveTableSwapperTests {
  [Test]
  public async Task DeferIndexes_DropsOnlyThePlainIndexes_AndTheBuildRestoresThem_UnderTheLiveNamesAfterTheSwapAsync() {
    await _createLiveAsync();
    IPerspectiveTableSwapper swapper = _swapper();
    await swapper.CreateShadowAsync(LIVE, CancellationToken.None);

    var deferred = await swapper.DeferIndexesAsync(SHADOW, CancellationToken.None);

    await Assert.That(deferred.Count).IsEqualTo(1);
    await Assert.That(deferred[0]).Contains("(lane)");
    await using (var during = await _openAsync()) {
      await Assert.That(await _indexCountAsync(during, SHADOW)).IsEqualTo(2)
        .Because("the primary key and the unique index are what the load's conflicts and refusals are decided by");
    }

    await swapper.BuildDeferredIndexesAsync(SHADOW, deferred, CancellationToken.None);
    await swapper.SwapAsync(new PerspectiveTableSwap(LIVE, SHADOW, KeepPrevious: false, TimeSpan.FromSeconds(10)),
      _ => Task.CompletedTask, CancellationToken.None);

    await using var verify = await _openAsync();
    await Assert.That(await _indexNamesAsync(verify, LIVE)).IsEquivalentTo([LIVE + "_pkey", "idx_swap_order_data", "idx_swap_order_lane"])
      .Because("an index built after the load has the live index's shape, so the swap gives it the live index's name");
  }

  [Test]
  public async Task DeferIndexes_OfATableWithOnlyItsKey_DefersNothing_AndTheBuildOfNothingIsANoOpAsync() {
    await using (var conn = await _openAsync()) {
      await conn.ExecuteAsync($"CREATE TABLE {SHADOW} (id uuid PRIMARY KEY)");
    }
    IPerspectiveTableSwapper swapper = _swapper();

    var deferred = await swapper.DeferIndexesAsync(SHADOW, CancellationToken.None);
    await swapper.BuildDeferredIndexesAsync(SHADOW, deferred, CancellationToken.None);

    await Assert.That(deferred).IsEmpty();
    await using var verify = await _openAsync();
    await Assert.That(await _indexCountAsync(verify, SHADOW)).IsEqualTo(1);
  }

  [Test]
  public async Task Swap_GivesTheInstalledTableTheLiveTablesOwnerAndGrants_AndNoneItDidNotHaveAsync() {
    var suffix = Guid.NewGuid().ToString("N")[..12];
    var (owner, reader, stray) = ("whiz_own_" + suffix, "whiz_read_" + suffix, "whiz_stray_" + suffix);
    await _createLiveAsync();
    await using (var conn = await _openAsync()) {
      await conn.ExecuteAsync($"""
        CREATE ROLE {owner} NOLOGIN; CREATE ROLE {reader} NOLOGIN; CREATE ROLE {stray} NOLOGIN;
        GRANT {owner} TO current_user;
        ALTER TABLE {LIVE} OWNER TO {owner};
        GRANT SELECT ON {LIVE} TO {reader};
        GRANT INSERT ON {LIVE} TO {reader} WITH GRANT OPTION;
        GRANT SELECT ON {LIVE} TO PUBLIC;
        """);
    }
    try {
      var swapper = _swapper();
      await swapper.CreateShadowAsync(LIVE, CancellationToken.None);
      await using (var conn = await _openAsync()) {
        await conn.ExecuteAsync($"GRANT UPDATE ON {SHADOW} TO {stray}");
      }

      await swapper.SwapAsync(new PerspectiveTableSwap(LIVE, SHADOW, KeepPrevious: true, TimeSpan.FromSeconds(10)),
        _ => Task.CompletedTask, CancellationToken.None);

      await using var verify = await _openAsync();
      await Assert.That(await verify.QuerySingleAsync<string>("SELECT tableowner FROM pg_tables WHERE tablename = @t", new { t = LIVE }))
        .IsEqualTo(owner).Because("the installed table belongs to whoever owned the table it replaced");
      await Assert.That(await _privilegeAsync(verify, reader, "SELECT")).IsTrue();
      await Assert.That(await _privilegeAsync(verify, reader, "INSERT WITH GRANT OPTION")).IsTrue();
      await Assert.That(await _privilegeAsync(verify, "public", "SELECT")).IsTrue();
      await Assert.That(await _privilegeAsync(verify, reader, "UPDATE")).IsFalse();
      await Assert.That(await _privilegeAsync(verify, stray, "UPDATE")).IsFalse()
        .Because("the installed table holds exactly the live table's grants, not one more");
    } finally {
      await using var cleanup = await _openAsync();
      await cleanup.ExecuteAsync($"""
        DROP TABLE IF EXISTS {LIVE}, {PREVIOUS}, {SHADOW};
        DROP OWNED BY {owner}, {reader}, {stray};
        DROP ROLE {owner}; DROP ROLE {reader}; DROP ROLE {stray};
        """);
    }
  }

  private static async Task<bool> _privilegeAsync(Npgsql.NpgsqlConnection conn, string role, string privilege) =>
    await conn.QuerySingleAsync<bool>("SELECT has_table_privilege(@role, @table, @privilege)", new { role, table = LIVE, privilege });
}
