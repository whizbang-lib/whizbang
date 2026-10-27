// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Data.Dapper.Postgres;

/// <summary>
/// One canonical spelling for a PostgreSQL column type.
/// </summary>
internal static class PostgresColumnType {
  /// <summary>
  /// The canonical spelling of <paramref name="typeName"/>.
  /// </summary>
  public static string Canonicalize(string typeName) => typeName;
}
