// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;
using Whizbang.Core.Messaging;
using Whizbang.Core.Workers;

namespace Whizbang.Data.Postgres;

/// <summary>
/// The beat of an instance that is still starting (#2126), one call for every driver: <c>wh_record_startup_heartbeat</c>,
/// Preflight's function, which refreshes the instance's heartbeat, connection mode and liveness window and nothing else.
/// </summary>
/// <docs>fundamentals/workers/instance-liveness</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StartingInstanceHeartbeatE2ETests.cs</tests>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperStartupHeartbeatTests.cs</tests>
public static class StartupHeartbeatSql {
  /// <summary>The function's name, unqualified.</summary>
  public const string FUNCTION_NAME = "wh_record_startup_heartbeat";

  /// <summary>
  /// Sends the beat. The request's instance id, connection mode and stale threshold (recorded as the liveness window)
  /// are read; its phase and versions are not, because the stage record is the one writer of the phase.
  /// </summary>
  /// <param name="connection">An open connection.</param>
  /// <param name="qualifiedFunctionName">The function's schema-qualified name.</param>
  /// <param name="request">The beat.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns><see langword="false"/> when the instance has been evicted; <see langword="true"/> otherwise.</returns>
  public static async Task<bool> RecordAsync(
      NpgsqlConnection connection, string qualifiedFunctionName, HeartbeatRequest request, CancellationToken cancellationToken) {
    ArgumentNullException.ThrowIfNull(connection);
    ArgumentNullException.ThrowIfNull(request);
#pragma warning disable S2077 // The name is schema-qualified by the caller through PgIdentifier; every value is bound.
    await using var command = new NpgsqlCommand($"SELECT {qualifiedFunctionName}($1, $2, $3)", connection);
#pragma warning restore S2077
    command.CommandTimeout = CoordinatorCommandTimeout.SECONDS;
    command.Parameters.AddWithValue(request.InstanceId);
    command.Parameters.Add(new NpgsqlParameter {
      NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text,
      Value = request.ConnectionMode is { } mode ? InstanceConnectionModes.ToDatabaseValue(mode) : DBNull.Value,
    });
    command.Parameters.Add(new NpgsqlParameter {
      NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Integer,
      Value = (object?)request.StaleThresholdSeconds ?? DBNull.Value,
    });
    return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true;
  }
}
