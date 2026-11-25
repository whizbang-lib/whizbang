// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text;
using Whizbang.Core.Data;

namespace Whizbang.Data.Postgres;

/// <summary>
/// The bootstrap closure a driver without generated code applies before it elects a migrator (#2561): the marked
/// regions of the library's own migrations, read from the embedded scripts at run time.
/// </summary>
/// <remarks>
/// <para>
/// The EF Core driver's closure is extracted at build time by the generator
/// (<c>Whizbang.Generators.Shared.Models.MigrationBootstrapRegions</c>), which runs as an analyzer and cannot be
/// referenced at run time. This reads the same markers the same way: a region opens at a line that is exactly
/// <see cref="BEGIN"/>, closes at one that is exactly <see cref="END"/>, and an unclosed region runs to the end of the
/// file, so a mistyped end marker makes the closure too big (loud) rather than too small (silent).
/// <c>MigrationBootstrapClosureParityTests</c> fails when the two readers disagree on any shipped migration.
/// </para>
/// </remarks>
/// <docs>operations/infrastructure/migrations#which-instance-migrates</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Migrations/MigrationBootstrapClosureParityTests.cs</tests>
internal static class MigrationBootstrapClosure {
  /// <summary>Opens a region that must be applied before any instance can be elected.</summary>
  internal const string BEGIN = "-- @whizbang:bootstrap-begin";

  /// <summary>Closes a region opened by <see cref="BEGIN"/>.</summary>
  internal const string END = "-- @whizbang:bootstrap-end";

  /// <summary>The marked regions of every migration <paramref name="provider"/> carries, in file order.</summary>
  /// <param name="provider">The migrations, rendered for the target schema.</param>
  /// <returns>Each migration with a region, by name, with its regions joined.</returns>
  internal static IEnumerable<(string Name, string Sql)> RegionsOf(IMigrationProvider provider) {
    foreach (var migration in provider.GetMigrations()) {
      if (Extract(migration.Sql) is { } regions) {
        yield return (migration.Name, regions);
      }
    }
  }

  /// <summary>The marked regions of one migration file, joined in file order; null when it carries none.</summary>
  /// <param name="sql">One migration file's text.</param>
  /// <returns>The bootstrap SQL, or null.</returns>
  internal static string? Extract(string sql) {
    if (!sql.Contains(BEGIN, StringComparison.Ordinal)) {
      return null;
    }
    var builder = new StringBuilder();
    var inside = false;
    foreach (var line in sql.Split('\n')) {
      var trimmed = line.Trim();
      if (trimmed.Equals(BEGIN, StringComparison.Ordinal)) {
        inside = true;
      } else if (trimmed.Equals(END, StringComparison.Ordinal)) {
        inside = false;
      } else if (inside) {
        builder.Append(line).Append('\n');
      }
    }
    var extracted = builder.ToString();
    return string.IsNullOrWhiteSpace(extracted) ? null : extracted;
  }
}
