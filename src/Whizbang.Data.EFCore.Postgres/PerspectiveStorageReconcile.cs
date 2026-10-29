// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using Whizbang.Data.Postgres.Schema;

namespace Whizbang.Data.EFCore.Postgres;

/// <summary>Stub (RED, #1747).</summary>
public static class PerspectiveStorageReconcile {
  /// <summary>Stub.</summary>
  public static Task<PerspectiveStorageReport> RunAsync(
      DbContext dbContext, string schema, IEnumerable<string> perspectiveSql, Func<NpgsqlConnection>? connectionFactory,
      ILogger? logger, CancellationToken cancellationToken) =>
    throw new NotSupportedException($"{dbContext} {schema} {perspectiveSql} {connectionFactory} {logger} {cancellationToken}");
}
