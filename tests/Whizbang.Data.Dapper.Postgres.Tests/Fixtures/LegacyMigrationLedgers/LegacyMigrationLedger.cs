// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Data.Postgres.TestFixtures;

/// <summary>
/// The migration ledger hashes a published release recorded, captured from that release's own packages by
/// <c>scripts/Capture-LegacyMigrationLedger.ps1</c> (#2125). Never derived from today's migration text: the point of
/// the fixture is that today's code cannot reproduce it unless it recognizes what the release really recorded.
/// </summary>
/// <remarks>
/// One file per driver and schema: <c>dapper-public</c>, <c>efcore-public</c>, <c>efcore-named_svc</c>. Linked into
/// the EF Core test project, so both drivers read the same capture.
/// </remarks>
internal static class LegacyMigrationLedger {
  /// <summary>The release whose upgrade #2125 broke: the last one before the normalized hash and the license header.</summary>
  public const string V0_2623_1 = "v0.2623.1";

  /// <summary>An earlier release, whose copy of 204 differs from v0.2623.1's by an executable statement.</summary>
  public const string V0_2619_0 = "v0.2619.0";

  /// <summary>The file names and hashes <paramref name="release"/> recorded for <paramref name="form"/>, in file order.</summary>
  /// <param name="release">The release folder, for example <see cref="V0_2623_1"/>.</param>
  /// <param name="form">The driver and schema, for example <c>efcore-named_svc</c>.</param>
  public static IReadOnlyList<(string FileName, string Hash)> Read(string release, string form) {
    var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "LegacyMigrationLedgers", release, form + ".tsv");
    return [.. File.ReadAllLines(path)
      .Where(line => line.Length > 0 && !line.StartsWith('#'))
      .Select(line => line.Split('\t'))
      .Select(parts => (parts[0], parts[1]))];
  }
}
