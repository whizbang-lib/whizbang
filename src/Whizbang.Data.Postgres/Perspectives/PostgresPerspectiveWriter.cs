// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Npgsql;
using Whizbang.Core.Perspectives;

namespace Whizbang.Data.Postgres.Perspectives;

/// <summary>Writes a batch of pending perspective writes on one connection.</summary>
internal static class PostgresPerspectiveWriter {
  /// <summary>The parameters one statement may bind.</summary>
  internal const int MAX_PARAMETERS = 65_000;

  /// <summary>Writes the batch.</summary>
  internal static Task<IReadOnlyList<PerspectiveWriteOutcome>> WriteAsync(
      NpgsqlConnection connection,
      NpgsqlTransaction? transaction,
      IReadOnlyList<PendingPerspectiveWrite> writes,
      DateTime writeClock,
      CancellationToken cancellationToken,
      int maxParameters = MAX_PARAMETERS) =>
    throw new NotImplementedException();
}
