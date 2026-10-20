// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.RegularExpressions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.Dapper.Postgres.Tests;

/// <summary>
/// The EF Core driver qualifies the framework names a migration writes bare, and only where SQL names
/// an object: never inside a string literal, a quoted identifier or a comment, where the same letters
/// are a value. A guard comparing <c>table_name = 'wh_inbox'</c> turned into
/// <c>table_name = '"svc".wh_inbox'</c> matches no catalog row, so it read "not there yet" on every
/// re-run, and a label or a stored <c>source_table</c> value changed meaning the same way.
/// </summary>
/// <docs>contributors/data-engines/writing-migrations</docs>
/// <code-under-test>src/Whizbang.Data.Postgres/MigrationSchemaQualifier.cs</code-under-test>
[Category("Unit")]
public class MigrationSchemaQualifierTests {
  private const string SCHEMA = "\"svc\"";

  [Test]
  public async Task ABareTableReference_IsQualifiedAsync() {
    await Assert.That(MigrationSchemaQualifier.Qualify("DELETE FROM wh_inbox WHERE id = 1;", SCHEMA))
      .IsEqualTo("DELETE FROM \"svc\".wh_inbox WHERE id = 1;");
  }

  [Test]
  public async Task AQualifiedReference_AndALongerName_AreLeftAloneAsync() {
    const string sql = "SELECT wh_inbox_id FROM \"svc\".wh_inbox;";

    await Assert.That(MigrationSchemaQualifier.Qualify(sql, SCHEMA)).IsEqualTo(sql);
  }

  [Test]
  public async Task ATableNameInsideAStringLiteral_IsAValueAndIsLeftAloneAsync() {
    const string sql = """
      IF NOT EXISTS (SELECT 1 FROM information_schema.columns
        WHERE table_name = 'wh_inbox' AND column_name = 'source_service_id') THEN
        ALTER TABLE wh_inbox ADD COLUMN source_service_id UUID;
      END IF;
      """;

    await Assert.That(MigrationSchemaQualifier.Qualify(sql, SCHEMA)).IsEqualTo("""
      IF NOT EXISTS (SELECT 1 FROM information_schema.columns
        WHERE table_name = 'wh_inbox' AND column_name = 'source_service_id') THEN
        ALTER TABLE "svc".wh_inbox ADD COLUMN source_service_id UUID;
      END IF;
      """);
  }

  [Test]
  public async Task AFunctionBody_IsCodeAndIsQualified_ButItsLiteralsAreNotAsync() {
    const string sql = """
      CREATE OR REPLACE FUNCTION f(p_source TEXT) RETURNS VOID AS $$
      BEGIN
        IF p_source = 'wh_outbox' THEN
          DELETE FROM wh_outbox;
        END IF;
      END;
      $$ LANGUAGE plpgsql;
      """;

    await Assert.That(MigrationSchemaQualifier.Qualify(sql, SCHEMA)).IsEqualTo("""
      CREATE OR REPLACE FUNCTION f(p_source TEXT) RETURNS VOID AS $$
      BEGIN
        IF p_source = 'wh_outbox' THEN
          DELETE FROM "svc".wh_outbox;
        END IF;
      END;
      $$ LANGUAGE plpgsql;
      """);
  }

  [Test]
  public async Task ATaggedDollarQuote_InsideABody_IsAlsoCodeAsync() {
    const string sql = "DO $do$ BEGIN EXECUTE $q$DELETE FROM wh_inbox WHERE note = 'wh_inbox'$q$; END $do$;";

    await Assert.That(MigrationSchemaQualifier.Qualify(sql, SCHEMA))
      .IsEqualTo("DO $do$ BEGIN EXECUTE $q$DELETE FROM \"svc\".wh_inbox WHERE note = 'wh_inbox'$q$; END $do$;");
  }

  [Test]
  public async Task AnApostropheInsideADollarQuote_StaysInsideItAsync() {
    // The tagged string holds an unbalanced quote; it must not swallow the code after the string ends.
    const string sql = "SELECT $t$it's$t$; DELETE FROM wh_inbox;";

    await Assert.That(MigrationSchemaQualifier.Qualify(sql, SCHEMA))
      .IsEqualTo("SELECT $t$it's$t$; DELETE FROM \"svc\".wh_inbox;");
  }

  [Test]
  public async Task AnUnclosedDollarQuote_IsCodeToTheEndAsync() {
    await Assert.That(MigrationSchemaQualifier.Qualify("DO $$ BEGIN DELETE FROM wh_inbox;", SCHEMA))
      .IsEqualTo("DO $$ BEGIN DELETE FROM \"svc\".wh_inbox;");
  }

  [Test]
  public async Task APositionalParameter_IsNotADollarQuoteAsync() {
    await Assert.That(MigrationSchemaQualifier.Qualify("EXECUTE 'x' USING $1; DELETE FROM wh_inbox $2;", SCHEMA))
      .IsEqualTo("EXECUTE 'x' USING $1; DELETE FROM \"svc\".wh_inbox $2;");
  }

  [Test]
  public async Task ADollarSignInsideAnIdentifier_IsNotADollarQuoteAsync() {
    await Assert.That(MigrationSchemaQualifier.Qualify("SELECT a$b$ FROM wh_inbox; SELECT $b$ wh_outbox $b$;", SCHEMA))
      .IsEqualTo("SELECT a$b$ FROM \"svc\".wh_inbox; SELECT $b$ \"svc\".wh_outbox $b$;");
  }

  [Test]
  public async Task DoubledQuotes_AndEscapeStrings_StayInsideTheirLiteralAsync() {
    const string sql = "SELECT 'it''s wh_inbox', E'it\\'s wh_inbox', e'\\\\' FROM wh_inbox;";

    await Assert.That(MigrationSchemaQualifier.Qualify(sql, SCHEMA))
      .IsEqualTo("SELECT 'it''s wh_inbox', E'it\\'s wh_inbox', e'\\\\' FROM \"svc\".wh_inbox;");
  }

  [Test]
  public async Task AnIdentifierEndingInE_DoesNotMakeTheNextLiteralAnEscapeStringAsync() {
    // In "type'\'" the E belongs to the identifier, so the backslash is an ordinary character and the
    // literal ends at the second quote.
    const string sql = "SELECT type'\\' FROM wh_inbox;";

    await Assert.That(MigrationSchemaQualifier.Qualify(sql, SCHEMA))
      .IsEqualTo("SELECT type'\\' FROM \"svc\".wh_inbox;");
  }

  [Test]
  public async Task AnUnclosedLiteral_IsAValueToTheEndAsync() {
    const string sql = "SELECT 'wh_inbox";

    await Assert.That(MigrationSchemaQualifier.Qualify(sql, SCHEMA)).IsEqualTo(sql);
  }

  [Test]
  public async Task AQuotedIdentifier_IsLeftAsWrittenAsync() {
    const string sql = "SELECT \"wh_inbox\", \"a\"\"wh_outbox\" FROM wh_inbox;";

    await Assert.That(MigrationSchemaQualifier.Qualify(sql, SCHEMA))
      .IsEqualTo("SELECT \"wh_inbox\", \"a\"\"wh_outbox\" FROM \"svc\".wh_inbox;");
  }

  [Test]
  public async Task Comments_AreProse_AndAnApostropheInOneOpensNoLiteralAsync() {
    const string sql = """
      -- don't qualify wh_inbox here
      /* nor wh_outbox /* nested wh_inbox */ here */
      DELETE FROM wh_inbox;
      """;

    await Assert.That(MigrationSchemaQualifier.Qualify(sql, SCHEMA)).IsEqualTo("""
      -- don't qualify wh_inbox here
      /* nor wh_outbox /* nested wh_inbox */ here */
      DELETE FROM "svc".wh_inbox;
      """);
  }

  [Test]
  public async Task ALineCommentAtTheEndOfTheText_IsProseToTheEndAsync() {
    await Assert.That(MigrationSchemaQualifier.Qualify("DELETE FROM wh_inbox; -- then wh_outbox", SCHEMA))
      .IsEqualTo("DELETE FROM \"svc\".wh_inbox; -- then wh_outbox");
  }

  [Test]
  public async Task AnEscapeStringAtTheStartOfTheText_StaysInsideItsLiteralAsync() {
    // E at index 0 has no character before it to belong to; the escaped quote must not end the literal.
    const string sql = "E'it\\'s wh_inbox' || wh_inbox";

    await Assert.That(MigrationSchemaQualifier.Qualify(sql, SCHEMA))
      .IsEqualTo("E'it\\'s wh_inbox' || \"svc\".wh_inbox");
  }

  [Test]
  public async Task ALiteralAtTheStartOfTheText_IsAValueAsync() {
    await Assert.That(MigrationSchemaQualifier.Qualify("'wh_inbox' || wh_inbox", SCHEMA))
      .IsEqualTo("'wh_inbox' || \"svc\".wh_inbox");
  }

  [Test]
  public async Task ADollarSignThatOpensNoTagBeforeTheEnd_IsCodeAsync() {
    // A lone "$" or an unterminated "$tag" at the end of the text opens no dollar quote.
    await Assert.That(MigrationSchemaQualifier.Qualify("DELETE FROM wh_inbox $", SCHEMA))
      .IsEqualTo("DELETE FROM \"svc\".wh_inbox $");
    await Assert.That(MigrationSchemaQualifier.Qualify("DELETE FROM wh_inbox $tag", SCHEMA))
      .IsEqualTo("DELETE FROM \"svc\".wh_inbox $tag");
  }

  [Test]
  public async Task ADollarTagWithUnderscoresAndDigits_IsADollarQuoteAsync() {
    const string sql = "DO $_t$ BEGIN DELETE FROM wh_inbox; END $_t$; DO $a_1$ BEGIN DELETE FROM wh_outbox; END $a_1$;";

    await Assert.That(MigrationSchemaQualifier.Qualify(sql, SCHEMA)).IsEqualTo(
      "DO $_t$ BEGIN DELETE FROM \"svc\".wh_inbox; END $_t$; DO $a_1$ BEGIN DELETE FROM \"svc\".wh_outbox; END $a_1$;");
  }

  [Test]
  public async Task AnUnclosedBlockComment_IsProseToTheEndAsync() {
    const string sql = "SELECT 1; /* wh_inbox";

    await Assert.That(MigrationSchemaQualifier.Qualify(sql, SCHEMA)).IsEqualTo(sql);
  }

  [Test]
  public async Task ARenameTarget_StaysBareAsync() {
    await Assert.That(MigrationSchemaQualifier.Qualify("ALTER TABLE wh_events RENAME TO wh_event_store;", SCHEMA))
      .IsEqualTo("ALTER TABLE \"svc\".wh_events RENAME TO wh_event_store;");
  }

  [Test]
  public async Task AFrameworkFunction_IsQualifiedWhereItIsDefinedDroppedGrantedOrCommentedAsync() {
    const string sql = """
      DROP FUNCTION IF EXISTS perform_maintenance;
      CREATE OR REPLACE FUNCTION perform_maintenance() RETURNS VOID AS $$ BEGIN END $$ LANGUAGE plpgsql;
      GRANT EXECUTE ON FUNCTION perform_maintenance TO PUBLIC;
      COMMENT ON FUNCTION perform_maintenance IS 'Runs perform_maintenance work';
      """;

    await Assert.That(MigrationSchemaQualifier.Qualify(sql, SCHEMA)).IsEqualTo("""
      DROP FUNCTION IF EXISTS "svc".perform_maintenance;
      CREATE OR REPLACE FUNCTION "svc".perform_maintenance() RETURNS VOID AS $$ BEGIN END $$ LANGUAGE plpgsql;
      GRANT EXECUTE ON FUNCTION "svc".perform_maintenance TO PUBLIC;
      COMMENT ON FUNCTION "svc".perform_maintenance IS 'Runs perform_maintenance work';
      """);
  }

  [Test]
  public async Task EveryListedFunction_IsOneAMigrationDefinesAsync() {
    // The list once named four functions no migration had defined for a long time (#1463), so it
    // read as a description of the current function set while qualifying nothing. Each entry has to
    // be defined by some migration, written qualified or bare; line comments are dropped first so a
    // name mentioned in prose does not count as a definition.
    var assembly = typeof(PostgresMigrationProvider).Assembly;
    const string prefix = "Whizbang.Data.Postgres.Migrations.";
    var definitions = new List<string>();
    foreach (var resource in assembly.GetManifestResourceNames()
               .Where(n => n.StartsWith(prefix, StringComparison.Ordinal) && n.EndsWith(".sql", StringComparison.Ordinal))) {
      using var stream = assembly.GetManifestResourceStream(resource)!;
      using var reader = new StreamReader(stream);
      definitions.Add(Regex.Replace(await reader.ReadToEndAsync(), "--[^\n]*", string.Empty, RegexOptions.None, TimeSpan.FromSeconds(5)));
    }

    var undefined = MigrationSchemaQualifier.FunctionNames
      .Where(name => !definitions.Any(sql => Regex.IsMatch(
        sql,
        $@"CREATE\s+(?:OR\s+REPLACE\s+)?FUNCTION\s+(?:__SCHEMA__\.)?{Regex.Escape(name)}\s*\(",
        RegexOptions.IgnoreCase,
        TimeSpan.FromSeconds(5))))
      .ToList();

    await Assert.That(definitions).IsNotEmpty().Because("the migrations are embedded in Whizbang.Data.Postgres");
    await Assert.That(undefined).IsEmpty()
      .Because($"every qualified function name must be one a migration defines; undefined: {string.Join(", ", undefined)}");
  }

  [Test]
  public async Task TextHoldingTheMaskingCharacters_IsRefusedAsync() {
    await Assert.That(() => MigrationSchemaQualifier.Qualify("SELECT ''", SCHEMA)).Throws<ArgumentException>();
  }

  [Test]
  public async Task MissingArguments_AreRefusedAsync() {
    await Assert.That(() => MigrationSchemaQualifier.Qualify(null!, SCHEMA)).Throws<ArgumentNullException>();
    await Assert.That(() => MigrationSchemaQualifier.Qualify("SELECT 1", "")).Throws<ArgumentException>();
  }
}
