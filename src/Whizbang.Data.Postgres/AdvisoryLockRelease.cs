// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using Npgsql;

namespace Whizbang.Data.Postgres;

/// <summary>Releases a session-level advisory lock and says whether this session held it.</summary>
/// <remarks>
/// <c>pg_advisory_unlock</c> returns false, with only a server-side warning, when the session running it does not hold
/// the lock. Behind a transaction-pooling pooler that is how a leaked lock shows itself: the release ran on another
/// backend, and the backend that took the lock still holds it. Every session-lock release goes through here so that
/// case is logged with the lock and its key instead of passing unnoticed.
/// </remarks>
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
    var released = await command.ExecuteScalarAsync(cancellationToken) is true;
    if (!released) {
      LogNotHeld(logger, lockName, key);
    }
    return released;
  }

  [LoggerMessage(Level = LogLevel.Warning,
    Message = "Released the {LockName} (key {Key}), but it was not held by this session: a pooler may have run the release on "
      + "a different backend from the one that took the lock, where the lock stays held until that session ends")]
  private static partial void LogNotHeld(ILogger logger, string lockName, long key);
}
