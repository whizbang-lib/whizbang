// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Npgsql;
using Whizbang.Core.Startup;

namespace Whizbang.Data.Postgres;

/// <summary>
/// The database's side of heavy startup work (#2596) for every Postgres driver: the once-per-build record
/// (<c>wh_startup_work</c>) and the fleet's heavy-start permits (<c>wh_startup_permits</c>), read and written only
/// through the functions migration 630 installs, so a mixed-version fleet runs one rule.
/// </summary>
/// <remarks>Each call is one statement on a connection of its own, so it is safe behind a transaction pooler.</remarks>
/// <docs>operations/startup/startup-pipeline#heavy-startup-work</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StartupWorkLedgerSqlTests.cs</tests>
public sealed class PgStartupWorkLedger : IStartupWorkLedger {
  private readonly Func<NpgsqlConnection> _connectionFactory;
  private readonly string _schema;

  /// <summary>Creates the ledger over <paramref name="schema"/>.</summary>
  /// <param name="connectionFactory">Opens a connection of its own for each call.</param>
  /// <param name="schema">The schema, bare.</param>
  public PgStartupWorkLedger(Func<NpgsqlConnection> connectionFactory, string schema) {
    ArgumentNullException.ThrowIfNull(connectionFactory);
    ArgumentException.ThrowIfNullOrWhiteSpace(schema);
    _connectionFactory = connectionFactory;
    _schema = schema;
  }

  /// <inheritdoc />
  // IS NOT NULL always answers true or false, never null
  public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken) =>
    (bool)(await _scalarAsync("SELECT to_regprocedure($1) IS NOT NULL", cancellationToken,
      PgIdentifier.Qualify(_schema, "wh_try_acquire_startup_permit") + "(uuid, integer)").ConfigureAwait(false))!;

  /// <inheritdoc />
  public async Task<StartupWorkClaim> ClaimAsync(
      string work, string fingerprint, Guid instanceId, bool force, CancellationToken cancellationToken) {
    var answer = (string?)await _scalarAsync(
      $"SELECT {PgIdentifier.Qualify(_schema, "wh_claim_startup_work")}($1, $2, $3, $4)", cancellationToken,
      work, fingerprint, instanceId, force).ConfigureAwait(false);
    return answer switch {
      "done" => StartupWorkClaim.Done,
      "running" => StartupWorkClaim.RunningElsewhere,
      _ => StartupWorkClaim.Claimed,
    };
  }

  /// <inheritdoc />
  public async Task CompleteAsync(string work, string fingerprint, Guid instanceId, bool succeeded, CancellationToken cancellationToken) =>
    _ = await _scalarAsync(
      $"SELECT {PgIdentifier.Qualify(_schema, "wh_complete_startup_work")}($1, $2, $3, $4)", cancellationToken,
      work, fingerprint, instanceId, succeeded).ConfigureAwait(false);

  /// <inheritdoc />
  public async Task<StartupPermitAnswer> TryAcquirePermitAsync(Guid instanceId, int cap, CancellationToken cancellationToken) {
    await using var connection = _connectionFactory();
    await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
#pragma warning disable S2077 // The schema cannot be a bind parameter; PgIdentifier.Qualify quotes it. Every value is bound.
    await using var command = new NpgsqlCommand(
      $"SELECT granted, held FROM {PgIdentifier.Qualify(_schema, "wh_try_acquire_startup_permit")}($1, $2)", connection);
#pragma warning restore S2077
    command.Parameters.AddWithValue(instanceId);
    command.Parameters.AddWithValue(cap);
    await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
    _ = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
    return new StartupPermitAnswer(reader.GetBoolean(0), reader.GetInt32(1));
  }

  /// <inheritdoc />
  public async Task ReleasePermitAsync(Guid instanceId, CancellationToken cancellationToken) =>
    _ = await _scalarAsync(
      $"SELECT {PgIdentifier.Qualify(_schema, "wh_release_startup_permit")}($1)", cancellationToken, instanceId)
      .ConfigureAwait(false);

  private async Task<object?> _scalarAsync(string sql, CancellationToken cancellationToken, params object[] values) {
    await using var connection = _connectionFactory();
    await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
#pragma warning disable S2077 // The schema cannot be a bind parameter; PgIdentifier.Qualify quotes it. Every value is bound.
    await using var command = new NpgsqlCommand(sql, connection);
#pragma warning restore S2077
    foreach (var value in values) {
      command.Parameters.AddWithValue(value);
    }
    return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
  }
}
