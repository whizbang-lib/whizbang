// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.Postgres;

namespace Whizbang.Core.Tests.Schema;

/// <summary>
/// Idempotent DDL rewritten so it asks the catalog first (#1636): <c>ALTER TABLE ... ADD COLUMN IF NOT EXISTS</c> and
/// <c>CREATE INDEX IF NOT EXISTS</c> take their table lock before they find nothing to do, so a re-run of a migration
/// queued behind every worker using the table, and with a second lock held in the same step it deadlocked them.
/// </summary>
/// <remarks>
/// What these pin is the rewrite: which statements are guarded, how the names reach the catalog query, and that
/// nothing else in a script moves. What the server does with the result (no lock taken when the column is there, the
/// column added when it is not) is asserted against a real server in <c>CatalogCheckedDdlPostgresTests</c>.
/// </remarks>
/// <code-under-test>src/Whizbang.Data.Postgres/CatalogCheckedDdl.cs</code-under-test>
[Category("Schema")]
public class CatalogCheckedDdlTests {
  private const string TAG = "$whizbang_catalog_checked$";

  // --- what is guarded ----------------------------------------------------------------------------

  [Test]
  public async Task AnAddColumnIfNotExists_RunsOnlyWhenTheCatalogLacksTheColumnAsync() {
    var guarded = CatalogCheckedDdl.Guard("ALTER TABLE public.wh_instance_evictions ADD COLUMN IF NOT EXISTS evicted_by UUID;");

    await Assert.That(guarded).IsEqualTo(
      "DO $whizbang_catalog_checked$\n"
      + "BEGIN\n"
      + "  IF EXISTS (SELECT 1 FROM unnest(ARRAY['evicted_by']::text[]) AS wanted(name) WHERE NOT EXISTS ("
      + "SELECT 1 FROM pg_catalog.pg_attribute AS a WHERE a.attrelid = to_regclass('public.wh_instance_evictions') "
      + "AND a.attname = wanted.name AND a.attnum > 0 AND NOT a.attisdropped)) THEN\n"
      + "    ALTER TABLE public.wh_instance_evictions ADD COLUMN IF NOT EXISTS evicted_by UUID;\n"
      + "  END IF;\n"
      + "END\n"
      + "$whizbang_catalog_checked$;");
  }

  [Test]
  public async Task EveryColumnOfAMultiColumnAdd_IsAskedFor_WithOrWithoutTheColumnKeywordAsync() {
    var guarded = CatalogCheckedDdl.Guard("""
      ALTER TABLE wh_role_assignments
        ADD COLUMN IF NOT EXISTS holder_version_key INTEGER[],
        ADD IF NOT EXISTS drain_requested_at TIMESTAMPTZ,
        ADD COLUMN IF NOT EXISTS last_vacated_fleet_wide BOOLEAN NOT NULL DEFAULT FALSE;
      """);

    await Assert.That(guarded).Contains(
      "unnest(ARRAY['holder_version_key', 'drain_requested_at', 'last_vacated_fleet_wide']::text[])");
    await Assert.That(guarded).Contains("to_regclass('wh_role_assignments')");
    await Assert.That(guarded).StartsWith("DO " + TAG);
  }

  [Test]
  public async Task AQuotedName_KeepsItsCase_AndAnUnquotedOneIsFolded_AsTheServerReadsThemAsync() {
    var guarded = CatalogCheckedDdl.Guard(
      "ALTER TABLE \"Inventory\".\"Wh_Items\" ADD COLUMN IF NOT EXISTS \"Odd\"\"Name\" text, ADD COLUMN IF NOT EXISTS MixedCase int;");

    await Assert.That(guarded).Contains("ARRAY['Odd\"Name', 'mixedcase']::text[]")
      .Because("a quoted name is compared as written, with its doubled quote undone; an unquoted one is folded to lower case");
    await Assert.That(guarded).Contains("to_regclass('\"Inventory\".\"Wh_Items\"')")
      .Because("the relation reaches to_regclass as written, which reads quotes and case exactly as the ALTER does");
  }

  [Test]
  public async Task AQuoteInAName_IsDoubledInsideTheLiteralAsync() {
    var guarded = CatalogCheckedDdl.Guard("ALTER TABLE \"it's\" ADD COLUMN IF NOT EXISTS \"o'k\" int;");

    await Assert.That(guarded).Contains("ARRAY['o''k']::text[]");
    await Assert.That(guarded).Contains("to_regclass('\"it''s\"')");
  }

  [Test]
  public async Task IfExistsAndOnly_AreAllowedBeforeTheTableAsync() {
    var guarded = CatalogCheckedDdl.Guard("ALTER TABLE IF EXISTS ONLY wh_t ADD COLUMN IF NOT EXISTS c int;");

    await Assert.That(guarded).Contains("to_regclass('wh_t')");
    await Assert.That(guarded).Contains("    ALTER TABLE IF EXISTS ONLY wh_t ADD COLUMN IF NOT EXISTS c int;\n");
  }

  [Test]
  public async Task ACommaInsideAColumnsType_DoesNotSplitTheActionAsync() {
    var guarded = CatalogCheckedDdl.Guard("ALTER TABLE t ADD COLUMN IF NOT EXISTS amount numeric(10, 2) DEFAULT 0;");

    await Assert.That(guarded).Contains("ARRAY['amount']::text[]");
  }

  [Test]
  public async Task ACreateIndexIfNotExists_RunsOnlyWhenNoRelationOfThatNameIsBesideTheTableAsync() {
    var guarded = CatalogCheckedDdl.Guard(
      "CREATE INDEX IF NOT EXISTS idx_instance_evictions_evicted_at\n  ON public.wh_instance_evictions (evicted_at);");

    await Assert.That(guarded).IsEqualTo(
      "DO $whizbang_catalog_checked$\n"
      + "BEGIN\n"
      + "  IF NOT EXISTS (SELECT 1 FROM pg_catalog.pg_class AS i WHERE i.relname = 'idx_instance_evictions_evicted_at' "
      + "AND i.relnamespace = (SELECT c.relnamespace FROM pg_catalog.pg_class AS c "
      + "WHERE c.oid = to_regclass('public.wh_instance_evictions'))) THEN\n"
      + "    CREATE INDEX IF NOT EXISTS idx_instance_evictions_evicted_at\n  ON public.wh_instance_evictions (evicted_at);\n"
      + "  END IF;\n"
      + "END\n"
      + "$whizbang_catalog_checked$;");
  }

  [Test]
  public async Task AUniqueIndexOnOnlyTheParent_AndAPartialOne_AreGuardedTooAsync() {
    var unique = CatalogCheckedDdl.Guard("CREATE UNIQUE INDEX IF NOT EXISTS \"Ux\" ON ONLY s.t (a) WHERE a > 0;");

    await Assert.That(unique).Contains("i.relname = 'Ux'");
    await Assert.That(unique).Contains("to_regclass('s.t')");
    await Assert.That(unique).Contains("    CREATE UNIQUE INDEX IF NOT EXISTS \"Ux\" ON ONLY s.t (a) WHERE a > 0;\n");
  }

  [Test]
  public async Task AStatementWithoutAClosingSemicolon_IsGuardedAndClosedAsync() {
    var guarded = CatalogCheckedDdl.Guard("ALTER TABLE t ADD COLUMN IF NOT EXISTS c int");

    await Assert.That(guarded).Contains("    ALTER TABLE t ADD COLUMN IF NOT EXISTS c int;\n");
    await Assert.That(guarded).EndsWith(TAG + ";");
  }

  // --- what is left alone ------------------------------------------------------------------------

  [Test]
  public async Task AScriptWithNothingToGuard_IsReturnedAsItIsAsync() {
    const string SQL = "CREATE TABLE IF NOT EXISTS t (id int);\nCOMMENT ON TABLE t IS 'a; b';\nSELECT 1;";

    await Assert.That(CatalogCheckedDdl.Guard(SQL)).IsSameReferenceAs(SQL);
  }

  [Test]
  [Arguments("ALTER TABLE t ADD COLUMN c int;")]
  [Arguments("ALTER TABLE t ADD COLUMN IF NOT EXISTS c int, ADD CONSTRAINT k CHECK (c > 0);")]
  [Arguments("ALTER TABLE t ALTER COLUMN c SET DEFAULT 0;")]
  [Arguments("ALTER TABLE t RENAME TO u;")]
  [Arguments("ALTER TABLE t ADD COLUMN IF NOT EXISTS;")]
  [Arguments("ALTER TABLE t ADD COLUMN IF NOT EXISTS c;")]
  [Arguments("ALTER TABLE t ADD COLUMN IF EXISTS c int;")]
  [Arguments("ALTER TABLE t ADD IF;")]
  [Arguments("ALTER TABLE;")]
  [Arguments("ALTER TABLE s.;")]
  [Arguments("ALTER INDEX i RENAME TO j;")]
  [Arguments("ALTER;")]
  [Arguments("CREATE INDEX i ON t (c);")]
  [Arguments("CREATE INDEX CONCURRENTLY IF NOT EXISTS i ON t (c);")]
  [Arguments("CREATE INDEX IF NOT EXISTS ON t (c);")]
  [Arguments("CREATE INDEX IF NOT EXISTS i USING t (c);")]
  [Arguments("CREATE INDEX IF NOT EXISTS i ON;")]
  [Arguments("CREATE INDEX IF NOT EXISTS i ON ONLY;")]
  [Arguments("CREATE INDEX IF NOT EXISTS i;")]
  [Arguments("CREATE UNIQUE TABLE t (c int);")]
  [Arguments("CREATE TABLE t (c int);")]
  [Arguments("CREATE;")]
  [Arguments("SELECT 1;")]
  [Arguments(";;")]
  [Arguments("")]
  public async Task AnythingElse_IsLeftAsItIsAsync(string sql) {
    await Assert.That(CatalogCheckedDdl.Guard(sql)).IsSameReferenceAs(sql);
  }

  [Test]
  public async Task StatementsInsideBodiesStringsAndComments_AreNotStatementsAsync() {
    const string SQL = """
      CREATE OR REPLACE FUNCTION f() RETURNS void AS $$
      BEGIN
        ALTER TABLE t ADD COLUMN IF NOT EXISTS c int;
      END $$ LANGUAGE plpgsql;
      DO $body$ BEGIN CREATE INDEX IF NOT EXISTS i ON t (c); END $body$;
      SELECT 'ALTER TABLE t ADD COLUMN IF NOT EXISTS c int;';
      SELECT E'it\'s; ALTER TABLE t ADD COLUMN IF NOT EXISTS c int;';
      SELECT "ALTER TABLE t ADD COLUMN IF NOT EXISTS c int;" FROM x;
      -- ALTER TABLE t ADD COLUMN IF NOT EXISTS c int;
      /* outer /* ALTER TABLE t ADD COLUMN IF NOT EXISTS c int; */ still a comment; */
      SELECT $1, a$b, $;
      """;

    await Assert.That(CatalogCheckedDdl.Guard(SQL)).IsSameReferenceAs(SQL);
  }

  [Test]
  public async Task OnlyTheGuardedStatementChanges_AndEverythingAroundItIsKeptInPlaceAsync() {
    const string BEFORE = "-- header\nCREATE TABLE IF NOT EXISTS t (id int);\n\n/* why */ ";
    const string STATEMENT = "ALTER TABLE t ADD COLUMN IF NOT EXISTS c int;";
    const string AFTER = "\nCOMMENT ON COLUMN t.c IS 'x';\n";

    var guarded = CatalogCheckedDdl.Guard(BEFORE + STATEMENT + AFTER);

    await Assert.That(guarded).StartsWith(BEFORE + "DO " + TAG + "\n");
    await Assert.That(guarded).EndsWith(TAG + ";" + AFTER);
    await Assert.That(guarded).Contains("    " + STATEMENT + "\n");
  }

  [Test]
  public async Task EveryMatchingStatementOfAScript_IsGuardedAsync() {
    var guarded = CatalogCheckedDdl.Guard("""
      ALTER TABLE a ADD COLUMN IF NOT EXISTS x int;
      CREATE INDEX IF NOT EXISTS ix ON a (x);
      ALTER TABLE b ADD COLUMN IF NOT EXISTS y int;
      """);

    await Assert.That(guarded.Split("DO " + TAG).Length - 1).IsEqualTo(3);
  }

  [Test]
  public async Task AnEscapeStringInADefault_IsReadToItsEnd_AndTheStatementKeptWholeAsync() {
    const string STATEMENT = "ALTER TABLE t ADD COLUMN IF NOT EXISTS note text DEFAULT E'it\\'s; fine';";

    var guarded = CatalogCheckedDdl.Guard(STATEMENT);

    await Assert.That(guarded).Contains("    " + STATEMENT + "\n");
    await Assert.That(guarded).Contains("ARRAY['note']::text[]");
  }

  [Test]
  public async Task AStatementThatAlreadyCarriesTheGuardsTag_IsLeftAloneAsync() {
    const string SQL = "ALTER TABLE t ADD COLUMN IF NOT EXISTS c text DEFAULT '$whizbang_catalog_checked$';";

    await Assert.That(CatalogCheckedDdl.Guard(SQL)).IsSameReferenceAs(SQL);
  }

  [Test]
  public async Task GuardingTwice_ChangesNothingTheSecondTimeAsync() {
    var once = CatalogCheckedDdl.Guard("ALTER TABLE t ADD COLUMN IF NOT EXISTS c int; CREATE INDEX IF NOT EXISTS i ON t (c);");

    await Assert.That(CatalogCheckedDdl.Guard(once)).IsSameReferenceAs(once);
  }

  [Test]
  public async Task UnterminatedQuotesAndBodies_RunToTheEndOfTheTextAsync() {
    string[] scripts = [
      "SELECT 'never closed ALTER TABLE t ADD COLUMN IF NOT EXISTS c int;",
      "SELECT \"never closed ALTER TABLE t ADD COLUMN IF NOT EXISTS c int;",
      "SELECT $q$ never closed ALTER TABLE t ADD COLUMN IF NOT EXISTS c int;",
      "/* never closed ALTER TABLE t ADD COLUMN IF NOT EXISTS c int;",
      "SELECT E'never closed \\",
    ];

    foreach (var sql in scripts) {
      await Assert.That(CatalogCheckedDdl.Guard(sql)).IsSameReferenceAs(sql).Because(sql);
    }
  }

  [Test]
  public async Task ANullScript_IsRefusedAsync() {
    await Assert.That(() => CatalogCheckedDdl.Guard(null!)).Throws<ArgumentNullException>();
  }
}
