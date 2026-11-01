// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Archival;

/// <summary>How much of a stream is archived (#1931).</summary>
/// <docs>fundamentals/perspectives/rebuild#archived-data</docs>
public enum ArchivedStreamExtent {
  /// <summary>Nothing of the stream is archived.</summary>
  None = 0,

  /// <summary>A prefix is archived and the stream still has live events: the stream is alive.</summary>
  Prefix = 1,

  /// <summary>The whole stream is archived: nothing of it is live.</summary>
  Whole = 2,

  /// <summary>
  /// A prefix was archived and that archive has since been deleted (#1958): the stream is alive, and the history its
  /// archive held is gone for good. A replay from the stream's start would fold its hot events alone, a partial history.
  /// </summary>
  PrefixDeleted = 3,

  /// <summary>
  /// The whole stream was archived and that archive has since been deleted (#1958): nothing of the stream remains to
  /// replay.
  /// </summary>
  WholeDeleted = 4,
}

/// <summary>
/// Says how much of each stream is archived, from the stream lifecycle ledger. A rebuild asks once for the streams it is
/// about to replay and decides per stream what it may read (<see cref="Perspectives.RebuildArchivedData"/>). Both Postgres
/// drivers register one; a host without one rebuilds every stream as if nothing were archived.
/// </summary>
/// <docs>fundamentals/perspectives/rebuild#archived-data</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchivedStreamRebuildSqlTests.cs</tests>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperArchivedStreamRebuildTests.cs</tests>
public interface IArchivedStreamExtents {
  /// <summary>The archived streams among <paramref name="streamIds"/> and how much of each is archived.</summary>
  /// <param name="streamIds">The streams to look up.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>An entry for each stream with archived data, or whose archive was deleted; a stream that never had an archive,
  /// or whose archive was restored in full, has no entry.</returns>
  Task<IReadOnlyDictionary<Guid, ArchivedStreamExtent>> GetExtentsAsync(
    IReadOnlyCollection<Guid> streamIds, CancellationToken cancellationToken = default);
}
