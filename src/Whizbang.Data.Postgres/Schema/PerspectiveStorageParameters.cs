// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using Npgsql;

namespace Whizbang.Data.Postgres.Schema;

/// <summary>Stub (RED, #1747).</summary>
public sealed record TableStorageDeclaration(string Table, IReadOnlyDictionary<string, string> Parameters);

/// <summary>Stub (RED, #1747).</summary>
public sealed record TableStorageChange(string Table, IReadOnlyDictionary<string, string> Set, IReadOnlyList<string> Reset) {
  /// <summary>Stub.</summary>
  public bool TouchesFillFactor => throw new NotSupportedException(Table);
}

/// <summary>Stub (RED, #1747).</summary>
public sealed record PerspectiveStorageReport(
  IReadOnlyList<string> ManualTables, IReadOnlyList<TableStorageChange> Applied, IReadOnlyList<string> Failed);

/// <summary>Stub (RED, #1747).</summary>
public static class PerspectiveStorageParameters {
#pragma warning disable CA1707
  /// <summary>Stub.</summary>
  public const string MARKER = "-- @whizbang:table-storage ";
  /// <summary>Stub.</summary>
  public const string MANUAL_TABLES_SETTING = "perspective_storage_manual_tables";
#pragma warning restore CA1707

  /// <summary>Stub.</summary>
  public static IReadOnlyList<TableStorageDeclaration> Declarations(IEnumerable<string> perspectiveSql) =>
    throw new NotSupportedException(perspectiveSql.ToString());

  /// <summary>Stub.</summary>
  public static IReadOnlyList<string> ManualTables(string? setting) => throw new NotSupportedException(setting);

  /// <summary>Stub.</summary>
  public static TableStorageChange? Plan(TableStorageDeclaration declared, IReadOnlyCollection<string> held) =>
    throw new NotSupportedException($"{declared.Table} {held.Count}");

  /// <summary>Stub.</summary>
  public static string AlterStatement(string qualifiedTable, TableStorageChange change) =>
    throw new NotSupportedException($"{qualifiedTable} {change.Table}");

  /// <summary>Stub.</summary>
  public static Task<PerspectiveStorageReport> ReconcileAsync(
      NpgsqlConnection connection, string schema, IEnumerable<string> perspectiveSql, ILogger? logger,
      CancellationToken cancellationToken, TimeSpan? lockTimeout = null) =>
    throw new NotSupportedException($"{connection.Database} {schema} {perspectiveSql} {logger} {cancellationToken} {lockTimeout}");
}
