// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Npgsql;
using Whizbang.Core.Archival;

namespace Whizbang.Data.Postgres;

/// <summary>
/// Turns the store's archived-stream refusal into <see cref="StreamArchivedException"/>: SQLSTATE <c>WHA01</c> when the
/// stream's type refuses appends, <c>WHA02</c> when it restores the stream first (migration 271).
/// </summary>
/// <docs>fundamentals/events/archive-store#appending-to-an-archived-stream</docs>
/// <tests>tests/Whizbang.Core.Tests/Archival/StreamArchivedRefusalTests.cs</tests>
internal static class StreamArchivedRefusal {
  /// <summary>The SQLSTATE the store raises for an archived stream whose type refuses appends.</summary>
  internal const string REFUSE_SQL_STATE = "WHA01";

  /// <summary>The SQLSTATE the store raises for an archived stream whose type restores it first.</summary>
  internal const string AUTO_RESTORE_SQL_STATE = "WHA02";

  /// <summary>Runs a store, rethrowing the archived-stream refusal as <see cref="StreamArchivedException"/>.</summary>
  /// <param name="store">The store call.</param>
  /// <returns>A task that completes when the store does.</returns>
  internal static Task RunAsync(Func<Task> store) => throw new NotImplementedException();

  /// <summary>Builds the typed exception from the store's refusal.</summary>
  /// <param name="refusal">The refusal the store raised.</param>
  /// <returns>The exception.</returns>
  internal static StreamArchivedException ToException(PostgresException refusal) => throw new NotImplementedException();
}
