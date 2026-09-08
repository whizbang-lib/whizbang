using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Tests.Migrations;

/// <summary>
/// The body extractor behind the stale-definition sweep. The ledger hashes describe the files; the sweep
/// compares the database. A database reverted to an earlier definition by a replay that predates the
/// redefinition closure reports "hash unchanged" forever while the function stays generations old (the
/// observed shape: the ledger-aware registry reconcile replaced by its pre-ledger predecessor, so every
/// recorded former name read as unacknowledged drift on every boot).
/// </summary>
/// <code-under-test>src/Whizbang.Data.Postgres/MigrationFunctionBodies.cs</code-under-test>
[Category("Migrations")]
[Category("Shard3")]
public class MigrationFunctionBodiesTests {
  private static readonly string[] _onlySecond = ["064_second.sql"];
  private static readonly string[] _onlyFifty = ["050_only.sql"];
  private static readonly string[] _onlyBoth = ["070_both.sql"];
  private static readonly string[] _xAndY = ["x", "y"];


  [Test]
  public async Task Extract_CreateOrReplaceFunction_YieldsNameAndNormalizedBodyAsync() {
    var bodies = MigrationFunctionBodies.Extract("""
      CREATE OR REPLACE FUNCTION __SCHEMA__.Reconcile_Thing(p_entries JSONB)
      RETURNS TABLE(o_action VARCHAR) AS $$
      DECLARE
        v_x INT;
      BEGIN
        RETURN;
      END;
      $$ LANGUAGE plpgsql;
      """);

    await Assert.That(bodies.Count).IsEqualTo(1);
    await Assert.That(bodies[0].Name).IsEqualTo("reconcile_thing");
    await Assert.That(bodies[0].NormalizedBody).IsEqualTo("DECLARE v_x INT; BEGIN RETURN; END;");
  }

  [Test]
  public async Task Extract_TwoRenderingsOfTheSameBody_NormalizeEqualAsync() {
    // The embedded runner indents the file and quotes the schema; the file on disk does neither.
    var fromFile = MigrationFunctionBodies.Extract(
      "CREATE OR REPLACE FUNCTION f() RETURNS VOID AS $$\nBEGIN\n  PERFORM 1;\nEND;\n$$ LANGUAGE plpgsql;");
    var fromEmbedding = MigrationFunctionBodies.Extract(
      "      CREATE OR REPLACE FUNCTION f() RETURNS VOID AS $$\r\n      BEGIN\r\n        PERFORM 1;\r\n      END;\r\n      $$ LANGUAGE plpgsql;");

    await Assert.That(fromFile[0].NormalizedBody).IsEqualTo(fromEmbedding[0].NormalizedBody);
  }

  [Test]
  public async Task Extract_TaggedDollarQuote_UsesTheMatchingCloserAsync() {
    var bodies = MigrationFunctionBodies.Extract("""
      CREATE FUNCTION g() RETURNS TEXT AS $fn$
      BEGIN RETURN 'a $$ inside'; END;
      $fn$ LANGUAGE plpgsql;
      """);

    await Assert.That(bodies.Count).IsEqualTo(1);
    await Assert.That(bodies[0].NormalizedBody).IsEqualTo("BEGIN RETURN 'a $$ inside'; END;");
  }

  [Test]
  public async Task Extract_SeveralFunctionsInOneFile_YieldsEachInOrderAsync() {
    var bodies = MigrationFunctionBodies.Extract("""
      CREATE OR REPLACE FUNCTION a() RETURNS VOID AS $$ BEGIN END; $$ LANGUAGE plpgsql;
      CREATE TABLE IF NOT EXISTS wh_thing (id INT);
      CREATE OR REPLACE FUNCTION b(p INT) RETURNS INT AS $$ SELECT p; $$ LANGUAGE sql;
      """);

    await Assert.That(bodies.Count).IsEqualTo(2);
    await Assert.That(bodies[0].Name).IsEqualTo("a");
    await Assert.That(bodies[1].Name).IsEqualTo("b");
    await Assert.That(bodies[1].NormalizedBody).IsEqualTo("SELECT p;");
  }

  [Test]
  public async Task Extract_DynamicSqlTemplate_IsSkippedAsync() {
    // A CREATE inside format() carries %I placeholders; the database never holds that text verbatim,
    // so treating it as a definition would flag the file as stale on every boot.
    var bodies = MigrationFunctionBodies.Extract("""
      DO $do$
      BEGIN
        EXECUTE format('CREATE OR REPLACE FUNCTION %I.dyn() RETURNS VOID AS $b$ BEGIN END; $b$ LANGUAGE plpgsql', current_schema());
      END
      $do$;
      """);

    await Assert.That(bodies).IsEmpty();
  }

  [Test]
  public async Task Extract_FunctionWithoutADollarQuotedBody_IsSkippedAsync() {
    var bodies = MigrationFunctionBodies.Extract(
      "CREATE OR REPLACE FUNCTION h() RETURNS INT AS 'SELECT 1' LANGUAGE sql;");

    await Assert.That(bodies).IsEmpty();
  }

  [Test]
  public async Task LastWord_LaterFileWins_EarlierFileKeepsItsOtherFunctionsAsync() {
    var lastWord = MigrationFunctionBodies.LastWord(new[] {
      ("040_first.sql", "CREATE OR REPLACE FUNCTION rec(p JSONB) RETURNS VOID AS $$ BEGIN RETURN; END; $$ LANGUAGE plpgsql; CREATE OR REPLACE FUNCTION other() RETURNS VOID AS $$ BEGIN END; $$ LANGUAGE plpgsql;"),
      ("064_second.sql", "CREATE OR REPLACE FUNCTION rec(p JSONB) RETURNS VOID AS $$ BEGIN PERFORM 2; END; $$ LANGUAGE plpgsql;"),
    });

    await Assert.That(lastWord["rec"].FileName).IsEqualTo("064_second.sql");
    await Assert.That(lastWord["rec"].NormalizedBody).IsEqualTo("BEGIN PERFORM 2; END;");
    await Assert.That(lastWord["other"].FileName).IsEqualTo("040_first.sql");
  }

  [Test]
  public async Task LastWord_FunctionDroppedByALaterFile_IsRetiredAsync() {
    // A migration that retires a function (DROP without a redefinition) is that function's real last
    // word. Keeping the earlier CREATE as the last word would report the function "missing" on every
    // boot and replay its old file forever.
    var last = MigrationFunctionBodies.LastWord([
      ("077_define.sql", "CREATE OR REPLACE FUNCTION __SCHEMA__.wh_backfill() RETURNS VOID AS $$ BEGIN END; $$ LANGUAGE plpgsql;"),
      ("078_retire.sql", "DROP FUNCTION IF EXISTS __SCHEMA__.wh_backfill();"),
    ]);

    await Assert.That(last.ContainsKey("wh_backfill")).IsFalse()
      .Because("a function a later migration dropped is retired, not stale");
  }

  [Test]
  public async Task LastWord_DropAllOverloadsThenRecreateInTheSameFile_KeepsTheRecreationAsync() {
    // The standard signature-change pattern: drop every overload, then define the one that stays.
    var last = MigrationFunctionBodies.LastWord([
      ("106_old.sql", "CREATE OR REPLACE FUNCTION __SCHEMA__.beat(a INT) RETURNS BOOLEAN AS $$ BEGIN RETURN TRUE; END; $$ LANGUAGE plpgsql;"),
      ("147_new.sql", "SELECT __SCHEMA__.drop_all_overloads('beat');\nCREATE OR REPLACE FUNCTION __SCHEMA__.beat(a INT, b INT) RETURNS BOOLEAN AS $$ BEGIN RETURN FALSE; END; $$ LANGUAGE plpgsql;"),
    ]);

    await Assert.That(last.ContainsKey("beat")).IsTrue();
    await Assert.That(last["beat"].FileName).IsEqualTo("147_new.sql")
      .Because("the drop precedes the recreation in the same file; the recreation is the last word");
    await Assert.That(last["beat"].NormalizedBody).Contains("RETURN FALSE");
  }

  [Test]
  public async Task LastWord_DropAllOverloadsWithoutARecreation_RetiresTheFunctionAsync() {
    var last = MigrationFunctionBodies.LastWord([
      ("050_define.sql", "CREATE OR REPLACE FUNCTION __SCHEMA__.legacy() RETURNS VOID AS $$ BEGIN END; $$ LANGUAGE plpgsql;"),
      ("060_retire.sql", "SELECT __SCHEMA__.drop_all_overloads('legacy');"),
    ]);

    await Assert.That(last.ContainsKey("legacy")).IsFalse();
  }

  [Test]
  public async Task FilesToRerun_DeployedBodyMatchesLastWord_NothingRerunsAsync() {
    var lastWord = MigrationFunctionBodies.LastWord(new[] {
      ("064_second.sql", "CREATE OR REPLACE FUNCTION rec() RETURNS VOID AS $$\nBEGIN\n  PERFORM 2;\nEND;\n$$ LANGUAGE plpgsql;"),
    });
    var deployed = new Dictionary<string, IReadOnlyList<string>> {
      ["rec"] = new[] { "\n      BEGIN\n        PERFORM 2;\n      END;\n      " },
    };
    var stale = new Dictionary<string, List<string>>();

    var files = MigrationFunctionBodies.FilesToRerun(lastWord, deployed, stale);

    await Assert.That(files).IsEmpty();
    await Assert.That(stale).IsEmpty();
  }

  [Test]
  public async Task FilesToRerun_DeployedBodyIsAnEarlierDefinition_RerunsTheLastWordFileAsync() {
    // The production shape: 040's body deployed while 064 is the last word and hash-unchanged.
    var lastWord = MigrationFunctionBodies.LastWord(new[] {
      ("040_first.sql", "CREATE OR REPLACE FUNCTION rec() RETURNS VOID AS $$ BEGIN RETURN; END; $$ LANGUAGE plpgsql;"),
      ("064_second.sql", "CREATE OR REPLACE FUNCTION rec() RETURNS VOID AS $$ BEGIN PERFORM 2; END; $$ LANGUAGE plpgsql;"),
    });
    var deployed = new Dictionary<string, IReadOnlyList<string>> {
      ["rec"] = new[] { " BEGIN RETURN; END; " },
    };
    var stale = new Dictionary<string, List<string>>();

    var files = MigrationFunctionBodies.FilesToRerun(lastWord, deployed, stale);

    await Assert.That(files).IsEquivalentTo(_onlySecond);
    await Assert.That(stale["064_second.sql"]).Contains("rec");
  }

  [Test]
  public async Task FilesToRerun_FunctionMissingFromTheDatabase_RerunsItsLastWordFileAsync() {
    var lastWord = MigrationFunctionBodies.LastWord(new[] {
      ("050_only.sql", "CREATE OR REPLACE FUNCTION gone() RETURNS VOID AS $$ BEGIN END; $$ LANGUAGE plpgsql;"),
    });
    var stale = new Dictionary<string, List<string>>();

    var files = MigrationFunctionBodies.FilesToRerun(lastWord, new Dictionary<string, IReadOnlyList<string>>(), stale);

    await Assert.That(files).IsEquivalentTo(_onlyFifty);
    await Assert.That(stale["050_only.sql"]).Contains("gone (missing)");
  }

  [Test]
  public async Task FilesToRerun_DuplicateOverloads_AreLeftToTheOverloadSweepAsync() {
    var lastWord = MigrationFunctionBodies.LastWord(new[] {
      ("050_only.sql", "CREATE OR REPLACE FUNCTION dup() RETURNS VOID AS $$ BEGIN END; $$ LANGUAGE plpgsql;"),
    });
    var deployed = new Dictionary<string, IReadOnlyList<string>> {
      ["dup"] = new[] { " BEGIN END; ", " BEGIN RETURN; END; " },
    };
    var stale = new Dictionary<string, List<string>>();

    var files = MigrationFunctionBodies.FilesToRerun(lastWord, deployed, stale);

    await Assert.That(files).IsEmpty();
  }

  [Test]
  public async Task FilesToRerun_TwoStaleFunctionsInOneFile_ListsTheFileOnceAsync() {
    var lastWord = MigrationFunctionBodies.LastWord(new[] {
      ("070_both.sql", "CREATE OR REPLACE FUNCTION x() RETURNS VOID AS $$ BEGIN PERFORM 1; END; $$ LANGUAGE plpgsql; CREATE OR REPLACE FUNCTION y() RETURNS VOID AS $$ BEGIN PERFORM 2; END; $$ LANGUAGE plpgsql;"),
    });
    var deployed = new Dictionary<string, IReadOnlyList<string>> {
      ["x"] = new[] { " BEGIN END; " },
      ["y"] = new[] { " BEGIN END; " },
    };
    var stale = new Dictionary<string, List<string>>();

    var files = MigrationFunctionBodies.FilesToRerun(lastWord, deployed, stale);

    await Assert.That(files).IsEquivalentTo(_onlyBoth);
    await Assert.That(stale["070_both.sql"]).IsEquivalentTo(_xAndY);
  }

  [Test]
  public async Task Normalize_CollapsesWhitespaceAndTrimsAsync() {
    await Assert.That(MigrationFunctionBodies.Normalize("\n  a \t b\r\n c \n")).IsEqualTo("a b c");
  }
}
