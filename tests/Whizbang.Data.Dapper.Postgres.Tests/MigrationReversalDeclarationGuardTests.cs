// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.RegularExpressions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Versioning;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.Dapper.Postgres.Tests;

/// <summary>
/// #2122, owner decision 2: the build guard. Every library migration numbered after
/// <see cref="MigrationReversal.UNDECLARED_THROUGH"/> declares how it is reversed; a reversible or lossy one ships its
/// reverse script beside it, an irreversible one ships none, and no reverse script stands alone. A reverse script is
/// never applied as a forward migration.
/// </summary>
/// <code-under-test>src/Whizbang.Data.Postgres/MigrationReversal.cs</code-under-test>
/// <docs>contributors/data-engines/writing-migrations#declaring-how-a-migration-is-reversed</docs>
[Category("Unit")]
public partial class MigrationReversalDeclarationGuardTests {
  [GeneratedRegex(@"^(\d{3})_.+\.sql$")]
  private static partial Regex _numbered();

  private static string _migrationsDirectory() => Path.GetFullPath(Path.Combine(
    AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "Whizbang.Data.Postgres", "Migrations"));

  private static IEnumerable<string> _files(string pattern) =>
    Directory.GetFiles(_migrationsDirectory(), pattern).Select(f => Path.GetFileName(f)!).Order(StringComparer.Ordinal);

  private static IEnumerable<string> _forward() => _files("*.sql").Where(f => !f.EndsWith(".down.sql", StringComparison.Ordinal));

  [Test]
  public async Task TheGuard_ReadsTheRealMigrationsAsync() {
    await Assert.That(_forward().Count()).IsGreaterThan(300).Because("a guard over no files proves nothing");
  }

  [Test]
  public async Task EveryNewMigration_DeclaresHowItIsReversedAsync() {
    var undeclared = new List<string>();
    foreach (var file in _forward()) {
      if (_numbered().Match(file) is { Success: true } m
          && int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) > MigrationReversal.UNDECLARED_THROUGH
          && MigrationReversal.DeclaredBy(await File.ReadAllTextAsync(Path.Combine(_migrationsDirectory(), file))) is null) {
        undeclared.Add(file);
      }
    }

    await Assert.That(undeclared).IsEmpty()
      .Because("a migration after " + MigrationReversal.UNDECLARED_THROUGH + " declares '-- Reversal: reversible', 'lossy' or 'irreversible' in its header");
  }

  [Test]
  public async Task AReversibleOrLossyMigration_ShipsItsReverse_AndAnIrreversibleOneShipsNoneAsync() {
    var downs = _files("*.down.sql").ToHashSet(StringComparer.Ordinal);
    var wrong = new List<string>();
    foreach (var file in _forward()) {
      var kind = MigrationReversal.DeclaredBy(await File.ReadAllTextAsync(Path.Combine(_migrationsDirectory(), file)));
      var down = file[..^".sql".Length] + ".down.sql";
      var needsOne = kind is SchemaChangeReversal.Reversible or SchemaChangeReversal.Lossy;
      if (needsOne != downs.Remove(down)) {
        wrong.Add($"{file} ({kind?.ToString() ?? "undeclared"}): {(needsOne ? "missing" : "must not have")} {down}");
      }
    }
    wrong.AddRange(downs.Select(d => $"{d}: no migration declares a reverse for it"));

    await Assert.That(wrong).IsEmpty();
  }

  [Test]
  public async Task AReverseScript_IsNeverAppliedAsAForwardMigrationAsync() {
    var forward = new PostgresMigrationProvider(typeof(PostgresMigrationProvider).Assembly, "public").GetMigrations();

    await Assert.That(forward.Where(m => m.Name.EndsWith(".down", StringComparison.Ordinal))).IsEmpty();
    await Assert.That(forward.Count).IsEqualTo(_forward().Count())
      .Because("every forward migration file is embedded, and nothing else is");
  }
}
