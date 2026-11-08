// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;
using NpgsqlTypes;
using Whizbang.Core.RunControl;

namespace Whizbang.Data.Postgres;

/// <summary>
/// The record of one exit (#2126), one call for every driver: <c>wh_record_instance_exit</c> writes the row of
/// <c>wh_instance_exits</c> and reads the instance's eviction tombstone in the same round trip.
/// </summary>
/// <docs>operations/startup/instance-exits#the-exit-record</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/InstanceExitRecordSqlTests.cs</tests>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperInstanceExitTests.cs</tests>
public static class InstanceExitSql {
  /// <summary>The function's name, unqualified.</summary>
  public const string FUNCTION_NAME = "wh_record_instance_exit";

  /// <summary>Writes the row and reads the tombstone.</summary>
  /// <param name="connection">An open connection.</param>
  /// <param name="qualifiedFunctionName">The function's schema-qualified name.</param>
  /// <param name="exitRecord">The facts of the exit.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>The row was written, with the instance's tombstone when it has one.</returns>
  public static async Task<InstanceExitRecordResult> RecordAsync(
      NpgsqlConnection connection, string qualifiedFunctionName, InstanceExitRecord exitRecord, CancellationToken cancellationToken) {
    ArgumentNullException.ThrowIfNull(connection);
    ArgumentNullException.ThrowIfNull(exitRecord);
#pragma warning disable S2077 // The name is schema-qualified by the caller through PgIdentifier; every value is bound.
    await using var command = new NpgsqlCommand(
      $"SELECT * FROM {qualifiedFunctionName}($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, $14, $15, $16, $17, $18)",
      connection);
#pragma warning restore S2077
    command.CommandTimeout = CoordinatorCommandTimeout.SECONDS;
    command.Parameters.AddWithValue(exitRecord.InstanceId);
    command.Parameters.AddWithValue(exitRecord.ServiceName);
    command.Parameters.AddWithValue(exitRecord.HostName);
    command.Parameters.AddWithValue(exitRecord.ProcessId);
    command.Parameters.AddWithValue(exitRecord.Cause.ToString());
    command.Parameters.AddWithValue(exitRecord.ExitCode);
    command.Parameters.AddWithValue(exitRecord.Intended);
    command.Parameters.AddWithValue(exitRecord.Reason);
    command.Parameters.Add(_text(exitRecord.ExceptionType));
    command.Parameters.Add(_text(exitRecord.ExceptionMessage));
    command.Parameters.Add(_text(exitRecord.StartupStage));
    command.Parameters.Add(_text(exitRecord.LifecyclePhase));
    command.Parameters.Add(_double(exitRecord.UptimeSeconds));
    command.Parameters.Add(_double(exitRecord.SecondsSinceLastHeartbeat));
    command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text, Value = exitRecord.HeldRoles.ToArray() });
    command.Parameters.AddWithValue(exitRecord.HeldLeases);
    command.Parameters.AddWithValue(exitRecord.InHand);
    command.Parameters.Add(_text(exitRecord.LibraryVersion));

    await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
    _ = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);   // one row: the function always writes one
    if (await reader.IsDBNullAsync(1, cancellationToken).ConfigureAwait(false)) {
      return InstanceExitRecordResult.Written(null);
    }
    return InstanceExitRecordResult.Written(new InstanceEviction(
      reader.GetFieldValue<DateTimeOffset>(1),
      await reader.IsDBNullAsync(2, cancellationToken).ConfigureAwait(false) ? null : reader.GetGuid(2),
      await reader.IsDBNullAsync(3, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(3),
      await reader.IsDBNullAsync(4, cancellationToken).ConfigureAwait(false) ? null : reader.GetFieldValue<DateTimeOffset>(4),
      await reader.IsDBNullAsync(5, cancellationToken).ConfigureAwait(false) ? null : reader.GetInt32(5)));
  }

  private static NpgsqlParameter _text(string? value) =>
    new() { NpgsqlDbType = NpgsqlDbType.Text, Value = (object?)value ?? DBNull.Value };

  private static NpgsqlParameter _double(double? value) =>
    new() { NpgsqlDbType = NpgsqlDbType.Double, Value = (object?)value ?? DBNull.Value };
}
