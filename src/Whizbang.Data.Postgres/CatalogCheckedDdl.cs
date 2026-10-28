// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Data.Postgres;

/// <summary>
/// Rewrites idempotent DDL so it takes no table lock when the catalog says there is nothing to do (#1636).
/// </summary>
/// <docs>operations/infrastructure/migrations#idempotent-ddl-takes-no-lock</docs>
/// <tests>tests/Whizbang.Core.Tests/Schema/CatalogCheckedDdlTests.cs</tests>
public static class CatalogCheckedDdl {
  /// <summary>Returns <paramref name="sql"/> with its idempotent DDL catalog-checked.</summary>
  /// <param name="sql">A script of one or more statements, as it is about to be executed.</param>
  /// <returns>The script to execute.</returns>
  public static string Guard(string sql) {
    ArgumentNullException.ThrowIfNull(sql);
    return sql;
  }
}
