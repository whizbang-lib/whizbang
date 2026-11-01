// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Data.Postgres;

/// <summary>
/// The event store's version questions (#1933), shared by both Postgres drivers so they ask them the same way.
/// Parameters: <c>@StreamId</c>, and <c>@BeforeEventId</c> for the history probes.
/// </summary>
/// <docs>fundamentals/events/archive-store#version-questions</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchivedStreamHistoryProbeSqlTests.cs</tests>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperArchivedStreamHistoryProbeTests.cs</tests>
internal static class ArchivedStreamHistorySql {
  /// <summary>The stream's last version, or -1.</summary>
  /// <param name="prefix">The schema prefix (<see cref="PgIdentifier.QualifyPrefix"/>), empty for the search path.</param>
  /// <returns>The statement.</returns>
  internal static string LastSequence(string prefix) => $"""
    SELECT COALESCE((SELECT MAX(e.version) FROM {prefix}wh_event_store e WHERE e.stream_id = @StreamId), -1)
    """;
}
