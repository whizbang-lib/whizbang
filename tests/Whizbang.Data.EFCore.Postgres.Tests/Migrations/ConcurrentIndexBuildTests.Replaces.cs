// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Tests.Migrations;

/// <summary>
/// #2345: an index that supersedes another is built concurrently under its own name, and the one it replaces is dropped
/// concurrently only once the new one is valid, so a read that used the old index always has one to use. The inbox
/// held-lanes index is rebuilt this way to cover the column the re-offer walk tests, and the table keeps its index count.
/// </summary>
/// <docs>operations/infrastructure/migrations#indexes-built-after-the-migrations</docs>
public partial class ConcurrentIndexBuildTests {
  private const string HELD_LANES = "idx_inbox_state_held_lanes";
  private const string HELD_LANES_COVERING = "idx_inbox_state_held_lanes_covering";

  [Test]
  public async Task Ensure_AnIndexThatReplacesAnother_DropsTheOldOne_OnceTheNewOneIsValidAsync() {
    await using var conn = await _openAsync();
    await _executeAsync(conn, "CREATE INDEX idx_replace_probe_old ON wh_event_store (event_type)");
    var index = new ConcurrentIndexDefinition("wh_event_store", "idx_replace_probe_new", "(event_type, stream_id)", null,
      Replaces: "idx_replace_probe_old");

    var outcome = await ConcurrentIndexBuild.EnsureAsync(conn, "public", index, 120);
    var again = await ConcurrentIndexBuild.EnsureAsync(conn, "public", index, 120);

    await Assert.That(outcome).IsEqualTo(ConcurrentIndexOutcome.Built);
    await Assert.That(again).IsEqualTo(ConcurrentIndexOutcome.AlreadyValid).Because("with the old one gone there is nothing left to do");
    await Assert.That(await _validityAsync(conn, "idx_replace_probe_new")).IsEqualTo(true);
    await Assert.That(await _validityAsync(conn, "idx_replace_probe_old")).IsNull()
      .Because("the superseded index costs every write and no longer serves a read the new one cannot");
  }

  [Test]
  public async Task Ensure_WhenTheNewIndexFailsToBuild_KeepsTheOneItReplacesAsync() {
    await using var conn = await _openAsync();
    await _executeAsync(conn, "CREATE INDEX idx_replace_probe_kept ON wh_event_store (event_type)");
    var failing = new ConcurrentIndexDefinition("wh_event_store", "idx_replace_probe_broken", "(no_such_column)", null,
      Replaces: "idx_replace_probe_kept");

    var outcome = await ConcurrentIndexBuild.EnsureAsync(conn, "public", failing, 120);

    await Assert.That(outcome).IsEqualTo(ConcurrentIndexOutcome.Failed);
    await Assert.That(await _validityAsync(conn, "idx_replace_probe_kept")).IsEqualTo(true)
      .Because("until the new index can serve, the reads that used the old one still need it");
  }

  [Test]
  public async Task TheHeldLanesIndex_IsRebuiltCovering_AndTheInboxStateKeepsItsIndexCountAsync() {
    await using var conn = await _openAsync();
    var before = await _inboxStateIndexCountAsync(conn);
    var covering = ConcurrentIndexBuild.FrameworkIndexes.SingleOrDefault(i => i.Name == HELD_LANES_COVERING);
    await Assert.That(covering).IsNotNull().Because("the re-offer walk's index must cover chain_emitted_at to stay index-only");
    await Assert.That(covering!.Replaces).IsEqualTo(HELD_LANES);

    await _runStepAsync();

    await Assert.That(await _validityAsync(conn, HELD_LANES_COVERING)).IsEqualTo(true);
    await Assert.That(await _validityAsync(conn, HELD_LANES)).IsNull();
    await Assert.That(await _scalarAsync(conn, $"SELECT pg_get_indexdef('public.{HELD_LANES_COVERING}'::regclass)") as string)
      .Contains("chain_emitted_at");
    await Assert.That(await _inboxStateIndexCountAsync(conn)).IsEqualTo(before)
      .Because("every index on wh_inbox_state is paid on every claim and settle; the rebuild replaces one, it adds none");
  }

  private static async Task<long> _inboxStateIndexCountAsync(NpgsqlConnection conn) =>
    (long)(await _scalarAsync(conn, "SELECT COUNT(*) FROM pg_indexes WHERE schemaname = 'public' AND tablename = 'wh_inbox_state'"))!;
}
