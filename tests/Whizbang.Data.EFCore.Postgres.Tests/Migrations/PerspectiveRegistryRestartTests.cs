// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.EFCore.Postgres.Tests.Generated;

namespace Whizbang.Data.EFCore.Postgres.Tests.Migrations;

/// <summary>
/// #2561: a restart that finds every hash clean takes the fast path and skips the pass, registries step included, yet no
/// hash covers the perspective registry. A restored database is exactly that: its schema hashes match this build while
/// its registry rows say what another build wrote. Every start therefore checks the stored registry against what this
/// build declares, reconciles it only when they differ, and touches nothing when they match.
/// </summary>
/// <remarks>
/// The reconcile stamps <c>updated_at</c> on every row it touches, so a stamp set aside before the restart and still
/// there after it proves nothing was reconciled.
/// </remarks>
/// <code-under-test>src/Whizbang.Data.Postgres/PerspectiveRegistryCurrency.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres.Generators/Templates/DbContextSchemaExtensionTemplate.cs</code-under-test>
[Category("Integration")]
[Category("Shard3")]
public class PerspectiveRegistryRestartTests : EFCoreTestBase {
  private async Task<long> _executeAsync(string sql, CancellationToken ct) {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync(ct);
    await using var cmd = new NpgsqlCommand(sql, conn);
    return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct) ?? 0L, System.Globalization.CultureInfo.InvariantCulture);
  }

  /// <summary>A migrated database with every registry row's stamp set aside; answers how many rows it holds.</summary>
  private async Task<long> _registryWithItsStampSetAsideAsync(CancellationToken ct) {
    await using (var context = CreateDbContext()) {
      await context.EnsureWhizbangDatabaseInitializedAsync(null, null, null, ct);
    }
    await _executeAsync("UPDATE wh_perspective_registry SET updated_at = '1999-01-01T00:00:00Z'", ct);
    return await _executeAsync("SELECT count(*) FROM wh_perspective_registry", ct);
  }

  [Test]
  [Timeout(120_000)]
  public async Task ACleanHashRestart_AgainstAMatchingRegistry_TouchesNothingAsync(CancellationToken cancellationToken) {
    var rows = await _registryWithItsStampSetAsideAsync(cancellationToken);

    await using (var context = CreateDbContext()) {
      await context.EnsureWhizbangDatabaseInitializedAsync(null, null, null, cancellationToken);
    }

    await Assert.That(rows).IsGreaterThan(0L).Because("a positive control: this context declares perspectives");
    await Assert.That(await _executeAsync(
        "SELECT count(*) FROM wh_perspective_registry WHERE updated_at <> '1999-01-01T00:00:00Z'", cancellationToken))
      .IsEqualTo(0L).Because("the stored registry matches this build, so no row was reconciled");
  }

  [Test]
  [Timeout(120_000)]
  public async Task ACleanHashRestart_AgainstARegistryNoHashCovers_ReconcilesItAsync(CancellationToken cancellationToken) {
    await _registryWithItsStampSetAsideAsync(cancellationToken);
    // A restored registry row another build wrote: its schema hash is in no ledger row, so every hash stays clean.
    await _executeAsync(
      "UPDATE wh_perspective_registry SET schema_hash = 'from-another-build' WHERE id = (SELECT min(id::text)::uuid FROM wh_perspective_registry)",
      cancellationToken);

    await using (var context = CreateDbContext()) {
      await context.EnsureWhizbangDatabaseInitializedAsync(null, null, null, cancellationToken);
    }

    await Assert.That(await _executeAsync(
        "SELECT count(*) FROM wh_perspective_registry WHERE schema_hash = 'from-another-build'", cancellationToken))
      .IsEqualTo(0L).Because("the restart reconciled the registry to what this build declares");
    await Assert.That(await _executeAsync(
        "SELECT count(*) FROM wh_perspective_registry WHERE updated_at <> '1999-01-01T00:00:00Z'", cancellationToken))
      .IsGreaterThan(0L);
  }
}
