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
  public static Task<InstanceExitRecordResult> RecordAsync(
      NpgsqlConnection connection, string qualifiedFunctionName, InstanceExitRecord exitRecord, CancellationToken cancellationToken) {
    ArgumentNullException.ThrowIfNull(connection);
    ArgumentNullException.ThrowIfNull(exitRecord);
    _ = qualifiedFunctionName;
    _ = cancellationToken;
    throw new NotImplementedException();
  }
}
