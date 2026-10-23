// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using Npgsql;

namespace Whizbang.Data.Postgres;

/// <summary>Releases a session-level advisory lock and says whether this session held it.</summary>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/AdvisoryLockReleaseTests.cs</tests>
internal static partial class AdvisoryLockRelease {
  /// <summary>Releases the session-level advisory lock on <paramref name="key"/>.</summary>
  /// <param name="connection">The session that took the lock.</param>
  /// <param name="key">The lock's key.</param>
  /// <param name="lockName">What the lock is, for the log.</param>
  /// <param name="logger">Where a release that found nothing to release is reported.</param>
  /// <param name="cancellationToken">Cancels the release.</param>
  /// <returns>True when this session held the lock and released it.</returns>
  public static async Task<bool> ReleaseAsync(
      NpgsqlConnection connection, long key, string lockName, ILogger logger, CancellationToken cancellationToken) {
    await using var command = new NpgsqlCommand("SELECT pg_advisory_unlock(@key)", connection);
    command.Parameters.AddWithValue("key", key);
    return await command.ExecuteScalarAsync(cancellationToken) is true;
  }
}
