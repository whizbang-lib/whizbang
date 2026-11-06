// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using Npgsql;

namespace Whizbang.Data.Postgres;

/// <summary>A framework index built concurrently after the migrations, never inside a migration's transaction.</summary>
/// <param name="Table">The table, unqualified.</param>
/// <param name="Name">The index name.</param>
/// <param name="Columns">The key, parenthesized, as <c>CREATE INDEX</c> takes it.</param>
/// <param name="Predicate">The partial-index predicate, or null.</param>
/// <param name="Comment">The index comment, or null.</param>
/// <docs>operations/infrastructure/migrations#indexes-built-after-the-migrations</docs>
public sealed record ConcurrentIndexDefinition(string Table, string Name, string Columns, string? Predicate, string? Comment = null);

/// <summary>What one <see cref="ConcurrentIndexBuild.EnsureAsync"/> did.</summary>
/// <docs>operations/infrastructure/migrations#indexes-built-after-the-migrations</docs>
public enum ConcurrentIndexOutcome {
  /// <summary>The index was already there and valid.</summary>
  AlreadyValid,

  /// <summary>The index was built.</summary>
  Built,

  /// <summary>The table does not exist; nothing to index.</summary>
  TableAbsent,

  /// <summary>Another session holds the build; this one stepped aside.</summary>
  Busy,

  /// <summary>The build failed; no invalid index was left behind.</summary>
  Failed,
}

/// <summary>Builds framework indexes concurrently after the migrations (#2092).</summary>
/// <docs>operations/infrastructure/migrations#indexes-built-after-the-migrations</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Migrations/ConcurrentIndexBuildTests.cs</tests>
public static class ConcurrentIndexBuild {
  /// <summary>The framework indexes built this way.</summary>
  public static IReadOnlyList<ConcurrentIndexDefinition> FrameworkIndexes { get; } = [];

#pragma warning disable RCS1163, IDE0060 // RED stub (#2092)
  /// <summary>The session advisory lock key one build of <paramref name="name"/> in <paramref name="schema"/> holds.</summary>
  public static long LockKey(string schema, string name) => 0;

  /// <summary>Ensures the index exists and is valid.</summary>
  public static Task<ConcurrentIndexOutcome> EnsureAsync(
      NpgsqlConnection connection, string schema, ConcurrentIndexDefinition index, int commandTimeoutSeconds,
      ILogger? logger = null, CancellationToken cancellationToken = default) =>
    Task.FromResult(ConcurrentIndexOutcome.Failed);
#pragma warning restore RCS1163, IDE0060
}
