// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Npgsql;
using Whizbang.Core.Startup;
using Whizbang.Core.Versioning;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.Dapper.Postgres;

/// <summary>
/// The Dapper driver's <see cref="IStartupAssessor"/> (#1992), so a Dapper instance stands down, skips the election and
/// the migration on a current schema, and refuses to migrate blind, exactly as an EF Core instance does. The verdict
/// table and the ledger read are shared with the EF Core driver (<see cref="PostgresStartupAssessment"/>); this binary's
/// version is the one this driver records in the ledger, and Current is the schema initializer's own lock-free check.
/// </summary>
/// <param name="connectionString">The database.</param>
/// <param name="createInitializer">Builds the schema initializer whose check says whether the schema is current.</param>
/// <param name="applicationVersion">This instance's application version, as recorded (#2022); null when unknown.</param>
/// <param name="instanceId">This instance's id (#2022): its own row is never a newer instance; null when it has none.</param>
/// <param name="overrideIncompatibility">Whether the incompatibility override is on (#2022).</param>
/// <docs>operations/startup/rolling-upgrades#one-verdict</docs>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperStartupAssessorTests.cs</tests>
internal sealed class DapperPostgresStartupAssessor(
    string connectionString, Func<PostgresSchemaInitializer> createInitializer, string? applicationVersion = null, Guid? instanceId = null,
    bool overrideIncompatibility = false)
    : IStartupAssessor, IOnlineVersionReader {
  /// <inheritdoc />
  public async Task<StartupAssessment> AssessAsync(CancellationToken cancellationToken) {
    await using var connection = new NpgsqlConnection(connectionString);
    await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
    return await PostgresStartupAssessment.AssessAsync(
      connection, await _schemaAsync(connection, cancellationToken).ConfigureAwait(false),
      new VersionSighting(new PostgresMigrationProvider().Version, applicationVersion, instanceId), overrideIncompatibility,
      ct => createInitializer().IsCurrentAsync(ct), cancellationToken).ConfigureAwait(false);
  }

  /// <inheritdoc />
  public async Task<IReadOnlyList<VersionSighting>> ReadOnlineAsync(CancellationToken cancellationToken) {
    await using var connection = new NpgsqlConnection(connectionString);
    await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
    return await PostgresStartupAssessment.ReadOnlineAsync(
      connection, await _schemaAsync(connection, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
  }

  private async Task<string?> _schemaAsync(NpgsqlConnection connection, CancellationToken cancellationToken) =>
    DapperCoordinatorSchema.FromConnectionString(connectionString)
      ?? await DapperCoordinatorSchema.CurrentAsync(connection, cancellationToken).ConfigureAwait(false);
}
