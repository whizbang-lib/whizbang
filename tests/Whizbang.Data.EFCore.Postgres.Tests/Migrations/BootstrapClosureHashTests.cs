// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Tests.Migrations;

/// <summary>
/// The closure hash names what a bootstrap would apply, and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// Two instances of one release must compute one hash, or the recorded closure never matches and
/// every start applies the bootstrap DDL under the lock, which is the deadlock the record exists to
/// prevent. A comment carries no statement, so it takes no part: a header a builder writes, a
/// note a migration author adds, or a clock stamp must not turn one release into two closures.
/// </para>
/// </remarks>
/// <docs>operations/infrastructure/migrations</docs>
[Category("Unit")]
[Category("Shard1")]
public class BootstrapClosureHashTests {
  private static readonly (string Name, string Sql)[] _scripts = [
    ("core", "CREATE TABLE IF NOT EXISTS wh_a (id uuid);\nCREATE INDEX IF NOT EXISTS ix_a ON wh_a (id);"),
    ("000", "CREATE TABLE IF NOT EXISTS wh_b (id uuid);"),
  ];

  /// <summary>Independently built lists with the same content hash the same.</summary>
  [Test]
  public async Task EqualScriptsHashEqualAsync() {
    var again = _scripts.Select(s => (s.Name, new string(s.Sql.AsSpan()))).ToArray();

    await Assert.That(SchemaBootstrapPhase.ClosureHash(again)).IsEqualTo(SchemaBootstrapPhase.ClosureHash(_scripts));
  }

  /// <summary>A comment-only line, wherever it sits, is not part of the closure.</summary>
  [Test]
  public async Task CommentOnlyLinesTakeNoPartAsync() {
    var commented = new[] {
      ("core", "-- Infrastructure schema\n-- Generated: 2026-01-02 03:04:05 UTC\n" + _scripts[0].Sql + "\n  -- trailing note"),
      ("000", "CREATE TABLE IF NOT EXISTS wh_b (id uuid);\n-- another note"),
    };

    await Assert.That(SchemaBootstrapPhase.ClosureHash(commented)).IsEqualTo(SchemaBootstrapPhase.ClosureHash(_scripts))
      .Because("a header or a clock stamp changes no statement, so it must not change the closure");
  }

  /// <summary>Line endings are not content either.</summary>
  [Test]
  public async Task LineEndingsTakeNoPartAsync() {
    var crlf = _scripts.Select(s => (s.Name, s.Sql.Replace("\n", "\r\n", StringComparison.Ordinal))).ToArray();

    await Assert.That(SchemaBootstrapPhase.ClosureHash(crlf)).IsEqualTo(SchemaBootstrapPhase.ClosureHash(_scripts));
  }

  /// <summary>A statement that differs is a different closure.</summary>
  [Test]
  public async Task AChangedStatementIsADifferentClosureAsync() {
    var changed = new[] { _scripts[0], ("000", "CREATE TABLE IF NOT EXISTS wh_c (id uuid);") };

    await Assert.That(SchemaBootstrapPhase.ClosureHash(changed)).IsNotEqualTo(SchemaBootstrapPhase.ClosureHash(_scripts));
  }

  /// <summary>The same text under another name, or in another order, is a different closure.</summary>
  [Test]
  public async Task NamesAndOrderArePartOfTheClosureAsync() {
    var renamed = new[] { _scripts[0], ("001", _scripts[1].Sql) };
    var reordered = new[] { _scripts[1], _scripts[0] };

    await Assert.That(SchemaBootstrapPhase.ClosureHash(renamed)).IsNotEqualTo(SchemaBootstrapPhase.ClosureHash(_scripts));
    await Assert.That(SchemaBootstrapPhase.ClosureHash(reordered)).IsNotEqualTo(SchemaBootstrapPhase.ClosureHash(_scripts));
  }

  /// <summary>A missing script list is a caller error.</summary>
  [Test]
  public async Task MissingScriptsAreRefusedAsync() {
    await Assert.That(() => SchemaBootstrapPhase.ClosureHash(null!)).Throws<ArgumentNullException>();
    await Assert.That(() => SchemaBootstrapPhase.LegacyClosureHash(null!)).Throws<ArgumentNullException>();
  }

  /// <summary>
  /// The closure hashes the same normalized text a migration's ledger row does, so a block comment, a comment at the
  /// end of a statement's line and a run of spaces take no part either.
  /// </summary>
  [Test]
  public async Task TheClosureHashesNormalizedTextAsync() {
    var commented = new[] {
      ("core", "/* header */\nCREATE TABLE IF NOT EXISTS   wh_a (id uuid); -- why\nCREATE INDEX IF NOT EXISTS ix_a ON wh_a (id);   "),
      ("000", "CREATE TABLE IF NOT EXISTS wh_b /* inline */ (id uuid);"),
    };

    await Assert.That(SchemaBootstrapPhase.ClosureHash(commented)).IsEqualTo(SchemaBootstrapPhase.ClosureHash(_scripts))
      .Because("only what executes names a closure");
  }

  /// <summary>A change inside a literal is a different closure, though it looks like a comment.</summary>
  [Test]
  public async Task AChangeInsideALiteralIsADifferentClosureAsync() {
    var before = new[] { ("000", "SELECT 'a -- b';") };
    var after = new[] { ("000", "SELECT 'a -- c';") };

    await Assert.That(SchemaBootstrapPhase.ClosureHash(after)).IsNotEqualTo(SchemaBootstrapPhase.ClosureHash(before));
  }

  /// <summary>
  /// The legacy closure hash is what earlier releases recorded: comment-only and blank lines dropped, line endings
  /// ignored, and everything else hashed as written. The step that rewrites a recorded closure reads it.
  /// </summary>
  [Test]
  public async Task TheLegacyClosureHashIsWhatEarlierReleasesRecordedAsync() {
    var scripts = new[] { ("core", "-- header\r\nCREATE TABLE wh_a (id uuid); -- why\r\n\r\n  SELECT 1;  ") };
    using var sha = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
    sha.AppendData(System.Text.Encoding.UTF8.GetBytes("core\nCREATE TABLE wh_a (id uuid); -- why\n  SELECT 1;  \n\n"));
    var expected = Convert.ToHexStringLower(sha.GetHashAndReset());

    await Assert.That(SchemaBootstrapPhase.LegacyClosureHash(scripts)).IsEqualTo(expected);
    await Assert.That(SchemaBootstrapPhase.ClosureHash(scripts)).IsNotEqualTo(expected)
      .Because("the trailing comment and the line-edge spaces are part of the legacy hash and not of the current one");
  }
}
