// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Security.Cryptography;
using System.Text;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Tests.Migrations;

/// <summary>
/// What a migration's change detection hashes: its SQL with comments, line-edge whitespace, blank lines and the line
/// ending convention removed, and every literal kept exactly as written.
/// </summary>
/// <remarks>
/// <para>
/// A comment, a license header or a blank line changes nothing a database executes, so an edit that only touches those
/// must not make every existing database re-apply the file. A change inside a literal does change what executes, and a
/// regular expression cannot tell <c>'a -- b'</c> from a comment, which is why the normalizer is a tokenizer and why
/// every literal kind is tested here with comment markers inside it.
/// </para>
/// <para>
/// The PowerShell port in scripts/lib/ReviewFindings.psm1 (<c>ConvertTo-NormalizedSql</c>) is held to the same cases
/// by .github/scripts/tests/ReviewFindings.Tests.ps1.
/// </para>
/// </remarks>
/// <code-under-test>src/Whizbang.Data.Postgres/MigrationSqlNormalizer.cs</code-under-test>
[Category("Unit")]
[Category("Shard1")]
public class MigrationSqlNormalizerTests {
  private const string BASE = "CREATE TABLE wh_a (id uuid);\nSELECT 1;";

  /// <summary>A line comment on its own line, at the end of a line, and as the first and last lines.</summary>
  [Test]
  public async Task LineComments_InEveryPosition_AreRemovedAsync() {
    var commented = "-- header\nCREATE TABLE wh_a (id uuid); -- trailing\n  -- indented\nSELECT 1;\n-- last";

    await Assert.That(MigrationSqlNormalizer.Normalize(commented)).IsEqualTo(BASE);
  }

  /// <summary>A block comment before, between, inside and after statements, across lines.</summary>
  [Test]
  public async Task BlockComments_InEveryPosition_AreRemovedAsync() {
    var commented = "/* license\n   header */\nCREATE TABLE /* inline */ wh_a (id uuid);\n/* between\n*/\nSELECT 1; /* after */";

    await Assert.That(MigrationSqlNormalizer.Normalize(commented)).IsEqualTo(BASE);
  }

  /// <summary>PostgreSQL block comments nest, so the first <c>*/</c> does not end an outer comment.</summary>
  [Test]
  public async Task BlockComments_Nest_AsPostgresReadsThemAsync() {
    var nested = "/* outer /* inner */ still a comment */\nCREATE TABLE wh_a (id uuid);\nSELECT 1;";

    await Assert.That(MigrationSqlNormalizer.Normalize(nested)).IsEqualTo(BASE);
  }

  /// <summary>An unterminated block comment runs to the end of the text, as the server would read it.</summary>
  [Test]
  public async Task BlockComment_Unterminated_RunsToTheEndAsync() {
    await Assert.That(MigrationSqlNormalizer.Normalize("SELECT 1; /* never closed\nSELECT 2;")).IsEqualTo("SELECT 1;");
  }

  /// <summary>A block comment between two tokens on one line separates them no differently from a space.</summary>
  [Test]
  public async Task BlockComment_BetweenTokens_ReadsAsOneSpaceAsync() {
    await Assert.That(MigrationSqlNormalizer.Normalize("SELECT/*x*/1;")).IsEqualTo("SELECT 1;");
    await Assert.That(MigrationSqlNormalizer.Normalize("SELECT  /* x */  1;")).IsEqualTo("SELECT 1;");
  }

  /// <summary>Leading and trailing whitespace on the file and on every line, and blank lines, are not content.</summary>
  [Test]
  public async Task Whitespace_AtLineEdgesAndBlankLines_IsRemovedAsync() {
    var padded = "\n\n   \t CREATE TABLE wh_a (id uuid);   \n\n\t\n  SELECT 1;\t \n\n";

    await Assert.That(MigrationSqlNormalizer.Normalize(padded)).IsEqualTo(BASE);
  }

  /// <summary>A run of spaces and tabs between tokens is one separator.</summary>
  [Test]
  public async Task Whitespace_RunsBetweenTokens_CollapseToOneSpaceAsync() {
    await Assert.That(MigrationSqlNormalizer.Normalize("CREATE  TABLE\twh_a  (id \t uuid);\nSELECT    1;")).IsEqualTo(BASE);
    await Assert.That(MigrationSqlNormalizer.Normalize("CREATE\fTABLE\vwh_a (id uuid);\nSELECT 1;")).IsEqualTo(BASE)
      .Because("a form feed and a vertical tab are whitespace too");
  }

  /// <summary>CRLF, a lone CR and LF are the same line ending.</summary>
  [Test]
  public async Task LineEndings_CrLfAndCr_ReadAsLfAsync() {
    await Assert.That(MigrationSqlNormalizer.Normalize("CREATE TABLE wh_a (id uuid);\r\nSELECT 1;")).IsEqualTo(BASE);
    await Assert.That(MigrationSqlNormalizer.Normalize("CREATE TABLE wh_a (id uuid);\rSELECT 1;")).IsEqualTo(BASE);
  }

  /// <summary>Comment markers and whitespace inside each literal kind are copied verbatim.</summary>
  [Test]
  [Arguments("SELECT 'a -- b  /* c */ ';")]
  [Arguments("SELECT 'it''s -- not a comment';")]
  [Arguments("SELECT E'a\\' -- still inside';")]
  [Arguments("SELECT e'tab\\t  /* inside */';")]
  [Arguments("SELECT E'doubled '' -- inside';")]
  [Arguments("SELECT \"col -- name\" FROM \"t /* x */\";")]
  [Arguments("SELECT \"a\"\"b -- c\";")]
  [Arguments("SELECT $$ -- body /* x */  $$;")]
  [Arguments("SELECT $fn$ body $$ -- still inside $fn$;")]
  [Arguments("SELECT $a_1$ x $b$ y $a_1$;")]
  [Arguments("SELECT $_t$ -- x $_t$;")]
  public async Task Literals_KeepCommentMarkersAndWhitespaceVerbatimAsync(string sql) {
    await Assert.That(MigrationSqlNormalizer.Normalize(sql)).IsEqualTo(sql);
  }

  /// <summary>A literal that spans lines keeps its line-edge whitespace and its blank lines.</summary>
  [Test]
  public async Task Literals_SpanningLines_KeepEveryLineAsItIsAsync() {
    var body = "CREATE FUNCTION f() RETURNS int AS $$\n  BEGIN\n\n    -- inside the body\n    RETURN 1;   \n  END;\n$$ LANGUAGE plpgsql;";

    await Assert.That(MigrationSqlNormalizer.Normalize(body)).IsEqualTo(body);
  }

  /// <summary>Text after a literal closes is normalized again.</summary>
  [Test]
  public async Task AfterALiteral_NormalizationResumesAsync() {
    await Assert.That(MigrationSqlNormalizer.Normalize("SELECT 'x'   -- note\n\n  , $$y$$  /* z */ ;"))
      .IsEqualTo("SELECT 'x'\n, $$y$$ ;");
  }

  /// <summary>Unterminated literals run to the end of the text, comment markers and all.</summary>
  [Test]
  [Arguments("SELECT 'never closed -- x  ")]
  [Arguments("SELECT E'never closed \\' -- x  ")]
  [Arguments("SELECT E'ends on a backslash \\")]
  [Arguments("SELECT \"never closed -- x  ")]
  [Arguments("SELECT $q$ never closed -- x  ")]
  public async Task Literals_Unterminated_RunToTheEndAsync(string sql) {
    await Assert.That(MigrationSqlNormalizer.Normalize(sql)).IsEqualTo(sql);
  }

  /// <summary>
  /// A dollar sign that cannot open a dollar quote (a positional parameter, a dollar inside an identifier, a tag that
  /// is not an identifier) is ordinary text, and a comment after it is still removed.
  /// </summary>
  [Test]
  [Arguments("SELECT $1; -- note", "SELECT $1;")]
  [Arguments("SELECT a$b$ FROM t; -- note", "SELECT a$b$ FROM t;")]
  [Arguments("SELECT $1x$ -- note", "SELECT $1x$")]
  [Arguments("SELECT $a b$ -- note", "SELECT $a b$")]
  [Arguments("SELECT 1 $", "SELECT 1 $")]
  [Arguments("SELECT 1 $ab", "SELECT 1 $ab")]
  [Arguments("SELECT $1$$ -- note", "SELECT $1$$")]
  [Arguments("SELECT a_$$ -- note", "SELECT a_$$")]
  public async Task DollarSigns_ThatOpenNoQuote_AreOrdinaryTextAsync(string sql, string expected) {
    await Assert.That(MigrationSqlNormalizer.Normalize(sql)).IsEqualTo(expected);
  }

  /// <summary>A dollar quote at the very start of the text opens a body.</summary>
  [Test]
  public async Task DollarQuote_AtTheStart_OpensABodyAsync() {
    await Assert.That(MigrationSqlNormalizer.Normalize("$$ -- x $$")).IsEqualTo("$$ -- x $$");
  }

  /// <summary>An E directly after an identifier character is part of that identifier, not a string prefix.</summary>
  [Test]
  public async Task EPrefix_AfterAnIdentifierCharacter_IsNotAnEscapeStringAsync() {
    // As a standard string, \' does not escape: the literal is 'a\' and the rest is a comment.
    await Assert.That(MigrationSqlNormalizer.Normalize("SELECT xE'a\\' -- gone")).IsEqualTo("SELECT xE'a\\'");
  }

  /// <summary>An E at the very start of the text opens an escape string.</summary>
  [Test]
  public async Task EPrefix_AtTheStart_IsAnEscapeStringAsync() {
    await Assert.That(MigrationSqlNormalizer.Normalize("E'a\\' -- inside'")).IsEqualTo("E'a\\' -- inside'");
  }

  /// <summary>A quote at the very start of the text, with nothing before it, is a standard string.</summary>
  [Test]
  public async Task Quote_AtTheStart_IsAStandardStringAsync() {
    await Assert.That(MigrationSqlNormalizer.Normalize("'a\\' -- gone")).IsEqualTo("'a\\'");
  }

  /// <summary>A minus or slash that starts no comment is ordinary text.</summary>
  [Test]
  public async Task MinusAndSlash_ThatStartNoComment_AreOrdinaryTextAsync() {
    await Assert.That(MigrationSqlNormalizer.Normalize("SELECT 4 - 2 / 1;")).IsEqualTo("SELECT 4 - 2 / 1;");
    await Assert.That(MigrationSqlNormalizer.Normalize("SELECT 1 -")).IsEqualTo("SELECT 1 -");
    await Assert.That(MigrationSqlNormalizer.Normalize("SELECT 1 /")).IsEqualTo("SELECT 1 /");
  }

  /// <summary>Text with nothing but comments and whitespace normalizes to nothing.</summary>
  [Test]
  public async Task OnlyCommentsAndWhitespace_NormalizeToNothingAsync() {
    await Assert.That(MigrationSqlNormalizer.Normalize(" -- a\n/* b */\n\r\n\t")).IsEqualTo(string.Empty);
  }

  /// <summary>The hash ignores what normalization removes.</summary>
  [Test]
  public async Task ContentHash_OfACommentOnlyEdit_IsUnchangedAsync() {
    // REUSE-IgnoreStart: a header written into a fixture, not this file's license
    var edited = "-- SPDX-License-Identifier: MIT\r\n" + BASE.Replace("\n", "  -- why\r\n", StringComparison.Ordinal) + "\n\n";
    // REUSE-IgnoreEnd

    await Assert.That(MigrationSqlNormalizer.ContentHash(edited)).IsEqualTo(MigrationSqlNormalizer.ContentHash(BASE));
  }

  /// <summary>A change inside a literal changes the hash, even when it only touches comment-like text.</summary>
  [Test]
  [Arguments("SELECT 'a -- b';", "SELECT 'a -- c';")]
  [Arguments("SELECT E'a\\'b';", "SELECT E'a\\'c';")]
  [Arguments("SELECT \"a -- b\";", "SELECT \"a -- c\";")]
  [Arguments("SELECT $$ -- b $$;", "SELECT $$ -- c $$;")]
  [Arguments("SELECT $$ x $$;", "SELECT $$ x  $$;")]
  [Arguments("SELECT 1;", "SELECT 2;")]
  public async Task ContentHash_OfAnExecutableEdit_ChangesAsync(string before, string after) {
    await Assert.That(MigrationSqlNormalizer.ContentHash(after)).IsNotEqualTo(MigrationSqlNormalizer.ContentHash(before));
  }

  /// <summary>The hash is the lowercase hex SHA-256 of the normalized UTF-8 text, the form the ledger stores.</summary>
  [Test]
  public async Task ContentHash_IsLowercaseHexSha256OfTheNormalizedTextAsync() {
    var sql = "  SELECT 'é';  -- note\n";
    var expected = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("SELECT 'é';")));

    await Assert.That(MigrationSqlNormalizer.ContentHash(sql)).IsEqualTo(expected);
  }

  /// <summary>The legacy hash is the SHA-256 of the raw text, which is what earlier releases stored.</summary>
  [Test]
  public async Task LegacyContentHash_IsTheSha256OfTheRawTextAsync() {
    var sql = "  SELECT 1;  -- note\n";
    var expected = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sql)));

    await Assert.That(MigrationSqlNormalizer.LegacyContentHash(sql)).IsEqualTo(expected);
    await Assert.That(MigrationSqlNormalizer.LegacyContentHash(sql)).IsNotEqualTo(MigrationSqlNormalizer.ContentHash(sql));
  }

  /// <summary>Every shipped migration normalizes without loss of a statement: no shipped file normalizes to nothing.</summary>
  [Test]
  public async Task EveryShippedMigration_KeepsItsStatementsAsync() {
    foreach (var migration in new PostgresMigrationProvider().GetMigrations()) {
      await Assert.That(MigrationSqlNormalizer.Normalize(migration.Sql)).IsNotEmpty()
        .Because($"{migration.Name} carries statements");
    }
  }

  /// <summary>A missing text is a caller error.</summary>
  [Test]
  public async Task NullText_IsRefusedAsync() {
    await Assert.That(() => MigrationSqlNormalizer.Normalize(null!)).Throws<ArgumentNullException>();
    await Assert.That(() => MigrationSqlNormalizer.ContentHash(null!)).Throws<ArgumentNullException>();
    await Assert.That(() => MigrationSqlNormalizer.LegacyContentHash(null!)).Throws<ArgumentNullException>();
  }
}
