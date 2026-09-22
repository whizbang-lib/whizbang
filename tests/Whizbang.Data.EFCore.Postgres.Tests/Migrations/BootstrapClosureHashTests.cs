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
  }
}
