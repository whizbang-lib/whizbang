using System;
using System.Collections.Generic;
using System.Text;

namespace Whizbang.Generators.Shared.Models;

/// <summary>
/// Extracts the parts of a migration that have to exist before an instance can be elected to do
/// the migrating.
/// </summary>
/// <remarks>
/// <para>
/// Schema initialization has a cycle in it. Deciding which instance migrates is a duty election,
/// the elector records a win through <c>record_capability</c>, and that function is created by a
/// migration. So the ordinary migration pass cannot be the thing that makes election possible,
/// and a small subset has to be applied first by whichever instance gets there, the way an
/// operating system brings up just enough of itself to load the rest.
/// </para>
/// <para>
/// The subset is marked in the SQL rather than listed here. Two reasons. A list in the generator
/// goes stale silently the first time a migration gains a dependency, whereas a marker sits next to
/// the statement it describes and is edited by whoever changes it. And the boundary is not always a
/// whole file: the migration that creates the eviction tombstone table also redefines two functions
/// that need objects the bootstrap deliberately does not create, so the table is in and the
/// functions are out.
/// </para>
/// <para>
/// An unclosed region deliberately runs to the end of the file. That way a mistyped end marker
/// makes the bootstrap too big, which fails loudly the first time it runs, rather than too small,
/// which would silently drop an object and leave the cycle in place. A test asserts the markers
/// balance so neither happens.
/// </para>
/// </remarks>
/// <tests>tests/Whizbang.Generators.Tests/MigrationBootstrapRegionsTests.cs</tests>
public static class MigrationBootstrapRegions {
  /// <summary>Opens a region that must be applied before any instance can be elected.</summary>
  public const string BEGIN = "-- @whizbang:bootstrap-begin";

  /// <summary>Closes a region opened by <see cref="BEGIN"/>.</summary>
  public const string END = "-- @whizbang:bootstrap-end";

  /// <summary>
  /// The marked regions of <paramref name="sql"/>, joined in file order.
  /// </summary>
  /// <param name="sql">One migration file's text.</param>
  /// <returns>
  /// The bootstrap SQL, or <see langword="null"/> when the file carries no marker at all, which is
  /// the case for all but a handful of migrations.
  /// </returns>
  public static string? Extract(string sql) {
    if (sql is null || sql.IndexOf(BEGIN, StringComparison.Ordinal) < 0) {
      return null;
    }

    var kept = new List<string>();
    var inside = false;

    foreach (var line in sql.Split('\n')) {
      var trimmed = line.Trim();

      if (trimmed.Equals(BEGIN, StringComparison.Ordinal)) {
        inside = true;
        continue;
      }

      if (trimmed.Equals(END, StringComparison.Ordinal)) {
        inside = false;
        continue;
      }

      if (inside) {
        kept.Add(line);
      }
    }

    var builder = new StringBuilder();
    foreach (var line in kept) {
      builder.Append(line).Append('\n');
    }

    var extracted = builder.ToString();
    return string.IsNullOrWhiteSpace(extracted) ? null : extracted;
  }

  /// <summary>
  /// Whether every <see cref="BEGIN"/> in <paramref name="sql"/> is matched by an <see cref="END"/>,
  /// with no nesting.
  /// </summary>
  /// <param name="sql">One migration file's text.</param>
  /// <returns><see langword="true"/> when the markers are well formed.</returns>
  public static bool MarkersAreBalanced(string sql) {
    if (sql is null) {
      return true;
    }

    var depth = 0;
    foreach (var line in sql.Split('\n')) {
      var trimmed = line.Trim();
      if (trimmed.Equals(BEGIN, StringComparison.Ordinal)) {
        depth++;
        if (depth > 1) {
          return false;
        }
      } else if (trimmed.Equals(END, StringComparison.Ordinal)) {
        depth--;
        if (depth < 0) {
          return false;
        }
      }
    }

    return depth == 0;
  }
}
