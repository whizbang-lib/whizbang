// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Whizbang.Data.Postgres;

/// <summary>
/// The pre-migration step that moves a database an earlier release migrated onto normalized migration hashes, so the
/// first start after the upgrade applies nothing again.
/// </summary>
/// <remarks>
/// <para>
/// Earlier releases recorded each migration's hash over its raw text (<see cref="MigrationSqlNormalizer.LegacyContentHash"/>);
/// change detection now compares <see cref="MigrationSqlNormalizer.ContentHash"/>. A row whose hash equals the legacy
/// hash of the file as shipped now belongs to a file that did not change, so it is rewritten to the file's normalized
/// hash. A row matching neither form belongs to a file that really changed and is left for change detection to apply.
/// Idempotent: once no row holds a legacy hash, the statement updates nothing.
/// </para>
/// <para>
/// Each driver runs it inside its migration lock and before it reads the ledger to decide what to run, so two
/// instances starting together rewrite once. The rewritten rows take this release's version row: an instance of the
/// earlier release still in the fleet then reads them as written by a newer build and does not apply its files over
/// them (where its driver checks), rather than reading every file as changed.
/// </para>
/// </remarks>
/// <docs>operations/infrastructure/migrations#what-counts-as-a-change</docs>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/NormalizedMigrationHashTests.cs</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Migrations/NormalizedMigrationHashUpgradeTests.cs</tests>
public static class MigrationHashUpgrade {
  /// <summary>
  /// Rewrites every ledger row whose hash is the legacy hash of its file as shipped to the file's normalized hash.
  /// </summary>
  /// <param name="connection">An open connection, inside the caller's migration lock (and its transaction, when it has one).</param>
  /// <param name="schema">The schema holding <c>wh_schema_migrations</c>; <c>public</c> or empty leaves the name unqualified.</param>
  /// <param name="migrations">Each migration's name and the text its hash is computed over.</param>
  /// <param name="versionId">This release's <c>wh_schema_versions</c> row, recorded on each rewritten row.</param>
  /// <param name="logger">Optional; told how many rows were rewritten when there were any.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>How many rows were rewritten.</returns>
  public static async Task<int> RewriteLegacyHashesAsync(
      NpgsqlConnection connection,
      string schema,
      IEnumerable<(string Name, string HashedText)> migrations,
      int versionId,
      ILogger? logger = null,
      CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(connection);
    ArgumentNullException.ThrowIfNull(migrations);

    var names = new List<string>();
    var legacy = new List<string>();
    var normalized = new List<string>();
    foreach (var (name, text) in migrations) {
      var legacyHash = MigrationSqlNormalizer.LegacyContentHash(text);
      var normalizedHash = MigrationSqlNormalizer.ContentHash(text);
      if (legacyHash != normalizedHash) {
        names.Add(name);
        legacy.Add(legacyHash);
        normalized.Add(normalizedHash);
      }
    }
    if (names.Count == 0) {
      return 0;
    }

#pragma warning disable S2077 // The schema cannot be a bind parameter; PgIdentifier.Qualify quotes it. Every value is bound.
    await using var command = new NpgsqlCommand(
      $"""
      UPDATE {PgIdentifier.Qualify(schema, "wh_schema_migrations")} AS m
      SET content_hash = r.normalized, version_id = $4, updated_at = NOW()
      FROM unnest($1::text[], $2::text[], $3::text[]) AS r(file_name, legacy, normalized)
      WHERE m.file_name = r.file_name AND m.content_hash = r.legacy
      """, connection);
#pragma warning restore S2077
    command.Parameters.AddWithValue(names.ToArray());
    command.Parameters.AddWithValue(legacy.ToArray());
    command.Parameters.AddWithValue(normalized.ToArray());
    command.Parameters.AddWithValue(versionId);
    var rewritten = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    if (rewritten > 0 && logger is not null) {
      MigrationHashUpgradeLog.Rewritten(logger, rewritten, schema);
    }
    return rewritten;
  }
}

/// <summary>Source-generated logging for the migration hash rewrite.</summary>
internal static partial class MigrationHashUpgradeLog {
  [LoggerMessage(
      EventId = 1648,
      Level = LogLevel.Information,
      Message = "Rewrote {Count} migration hash(es) in schema {Schema} from the raw-text form an earlier release recorded "
              + "to the normalized form; none of those migrations changed, so none is applied again")]
  public static partial void Rewritten(ILogger logger, int count, string schema);
}
