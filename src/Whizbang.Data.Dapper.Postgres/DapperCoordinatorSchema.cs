// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Data.Dapper.Postgres;

/// <summary>
/// The schema the Dapper work coordinator qualifies its statements with.
/// </summary>
internal static class DapperCoordinatorSchema {
  /// <summary>The schema named first by the connection string's <c>Search Path</c>, or null when it names none.</summary>
  internal static string? FromConnectionString(string connectionString) => null;
}
