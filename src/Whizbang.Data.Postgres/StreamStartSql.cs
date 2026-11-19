// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Data.Postgres;

/// <summary>
/// The event store's question for a stream's start once its first event is gone (#2152), shared by both Postgres drivers
/// so they ask it the same way. Parameter: <c>@StreamId</c>.
/// </summary>
/// <docs>fundamentals/events/superseding-events#what-a-rebuild-keeps</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamStartedAtSqlTests.cs:EventStore_GetStreamStartedAt_ReadsTheStreamsStartAsync</tests>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperStreamStartedAtTests.cs</tests>
internal static class StreamStartSql {
  /// <summary>
  /// The stream's start (<c>wh_stream_started_at</c>, migration 358): null while the stream holds its first event or has
  /// no ledger row. Two probes by key.
  /// </summary>
  /// <param name="prefix">The schema prefix (<see cref="PgIdentifier.QualifyPrefix"/>), empty for the search path.</param>
  /// <returns>The statement; one <c>timestamptz</c> column.</returns>
  internal static string StartedAt(string prefix) => $"SELECT {prefix}wh_stream_started_at(@StreamId)";
}
