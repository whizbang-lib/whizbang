using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Tests.Migrations;

/// <summary>
/// The extractor + closure pair that makes partial ledger replays safe: when a hash-driven replay
/// re-runs an earlier migration that redefines a SQL object, every later same-object migration
/// must re-run after it or the database is silently left on a stale definition (the observed
/// production shape: store procedures reverted to a pre-flags version, persisting flags=0 for
/// every row with no error anywhere).
/// </summary>
/// <code-under-test>src/Whizbang.Data.Postgres/MigrationObjectExtractor.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/MigrationRedefinitionClosure.cs</code-under-test>
[Category("Migrations")]
[Category("Shard3")]
public class MigrationRedefinitionClosureTests {

  // ── extractor: parsed DDL forms ─────────────────────────────────────────

  [Test]
  public async Task Extract_CreateOrReplaceFunction_YieldsBareLowercasedNameAsync() {
    var set = MigrationObjectExtractor.Extract(
      "CREATE OR REPLACE FUNCTION __SCHEMA__.Store_Inbox_Messages(p_json JSONB) RETURNS VOID AS $$ BEGIN END $$ LANGUAGE plpgsql;");
    await Assert.That(set.Objects).Contains("store_inbox_messages");
    await Assert.That(set.ExplicitOverride).IsFalse();
  }

  [Test]
  public async Task Extract_CreateTableIfNotExists_YieldsNameAsync() {
    var set = MigrationObjectExtractor.Extract(
      "CREATE TABLE IF NOT EXISTS __SCHEMA__.wh_outbox (id UUID PRIMARY KEY);");
    await Assert.That(set.Objects).Contains("wh_outbox");
  }

  [Test]
  public async Task Extract_DropFunction_CountsAsDefiningTheObjectAsync() {
    var set = MigrationObjectExtractor.Extract(
      "DROP FUNCTION IF EXISTS __SCHEMA__.wh_backfill_event_bodies();");
    await Assert.That(set.Objects).Contains("wh_backfill_event_bodies");
  }

  [Test]
  public async Task Extract_AlterTable_YieldsTableNameAsync() {
    // ALTER TABLE is a structural redefinition — order-sensitive with every other file touching
    // the same table, so it must participate in the closure like a CREATE.
    var set = MigrationObjectExtractor.Extract(
      "ALTER TABLE __SCHEMA__.wh_event_destruction_hold ADD COLUMN IF NOT EXISTS failure_count INTEGER NOT NULL DEFAULT 0;");
    await Assert.That(set.Objects).Contains("wh_event_destruction_hold");
  }

  [Test]
  public async Task Extract_IndexViewTrigger_AllRecognizedAsync() {
    var set = MigrationObjectExtractor.Extract("""
      CREATE INDEX IF NOT EXISTS idx_outbox_created ON __SCHEMA__.wh_outbox (created_at);
      CREATE OR REPLACE VIEW __SCHEMA__.wh_active_view AS SELECT 1;
      CREATE TRIGGER trg_audit AFTER INSERT ON __SCHEMA__.wh_outbox EXECUTE FUNCTION __SCHEMA__.audit_fn();
      """);
    await Assert.That(set.Objects).Contains("idx_outbox_created");
    await Assert.That(set.Objects).Contains("wh_active_view");
    await Assert.That(set.Objects).Contains("trg_audit");
  }

  [Test]
  public async Task Extract_MultipleStatements_DistinctSetAsync() {
    var set = MigrationObjectExtractor.Extract("""
      CREATE OR REPLACE FUNCTION __SCHEMA__.fn_a() RETURNS VOID AS $$ BEGIN END $$ LANGUAGE plpgsql;
      CREATE OR REPLACE FUNCTION __SCHEMA__.fn_b() RETURNS VOID AS $$ BEGIN END $$ LANGUAGE plpgsql;
      CREATE OR REPLACE FUNCTION __SCHEMA__.fn_a() RETURNS VOID AS $$ BEGIN END $$ LANGUAGE plpgsql;
      """);
    await Assert.That(set.Objects.Count).IsEqualTo(2);
  }

  // ── extractor: explicit override ────────────────────────────────────────

  [Test]
  public async Task Extract_ObjectsHeader_ReplacesParsingAsync() {
    var set = MigrationObjectExtractor.Extract("""
      -- Objects: fn_dynamic, wh_dynamic_table
      DO $$ BEGIN EXECUTE 'CREATE TABLE something_invisible (id INT)'; END $$;
      """);
    await Assert.That(set.ExplicitOverride).IsTrue();
    await Assert.That(set.Objects).Contains("fn_dynamic");
    await Assert.That(set.Objects).Contains("wh_dynamic_table");
    await Assert.That(set.Objects.Count).IsEqualTo(2);
  }

  [Test]
  public async Task Extract_ObjectsNone_EmptyExplicitSetAsync() {
    var set = MigrationObjectExtractor.Extract("""
      -- Objects: none
      UPDATE __SCHEMA__.wh_settings SET value = '1' WHERE key = 'x';
      """);
    await Assert.That(set.ExplicitOverride).IsTrue();
    await Assert.That(set.Objects.Count).IsEqualTo(0);
  }

  // ── the lint: every real embedded migration is covered ──────────────────

  [Test]
  public async Task EveryEmbeddedMigration_YieldsObjectsOrDeclaresNoneAsync() {
    // Silence is not an option: a migration the extractor cannot see AND that declares nothing
    // would be invisible to the closure — the exact hole this feature closes. Every real file
    // must parse to at least one object or carry an explicit "-- Objects:" header.
    var provider = new PostgresMigrationProvider();
    var uncovered = new List<string>();
    foreach (var migration in provider.GetMigrations()) {
      var set = MigrationObjectExtractor.Extract(migration.Sql);
      if (set.Objects.Count == 0 && !set.ExplicitOverride) {
        uncovered.Add(migration.Name);
      }
    }
    await Assert.That(uncovered).IsEmpty()
      .Because("every migration must either parse to at least one object or declare '-- Objects:' " +
               $"explicitly. Uncovered: {string.Join(", ", uncovered)}");
  }

  // ── closure expansion ───────────────────────────────────────────────────

  private static readonly IReadOnlyList<(string Name, IReadOnlyCollection<string> Objects)> _chain = [
    ("021_StoreInbox", new[] { "store_inbox_messages" }),
    ("029_Monolith", new[] { "store_inbox_messages", "store_outbox_messages", "claim_work" }),
    ("032_Maintenance", new[] { "perform_maintenance" }),
    ("046_CommitSeq", new[] { "store_inbox_messages" }),
    ("062_Flags", new[] { "store_inbox_messages", "store_outbox_messages" }),
  ];

  [Test]
  public async Task Expand_EarlierRedefiner_PullsInEveryLaterSameObjectMigrationAsync() {
    // The incident shape: 046 re-runs alone → 062 (the flags-aware last word on the store
    // procedures) must be pulled in, or the database keeps the 046-era definition forever.
    var closure = MigrationRedefinitionClosure.Expand(_chain, ["046_CommitSeq"]);
    await Assert.That(closure).Contains("046_CommitSeq");
    await Assert.That(closure).Contains("062_Flags");
    await Assert.That(closure.Contains("032_Maintenance")).IsFalse()
      .Because("closure only reaches later migrations sharing an OBJECT — unrelated files stay skipped.");
  }

  [Test]
  public async Task Expand_TransitiveObjects_ReachFixedPointAsync() {
    // 021 re-runs → 029 (same object) joins; 029 brings claim_work; a later claim_work-only file
    // must then join too — the fixed point crosses objects introduced by pulled-in files.
    IReadOnlyList<(string, IReadOnlyCollection<string>)> chain = [
      ("010_A", new[] { "fn_x" }),
      ("020_B", new[] { "fn_x", "fn_y" }),
      ("030_C", new[] { "fn_y" }),
      ("040_D", new[] { "fn_z" }),
    ];
    var closure = MigrationRedefinitionClosure.Expand(chain, ["010_A"]);
    await Assert.That(closure).Contains("020_B");
    await Assert.That(closure).Contains("030_C");
    await Assert.That(closure.Contains("040_D")).IsFalse();
  }

  [Test]
  public async Task Expand_LastWordItself_PullsNothingExtraAsync() {
    var closure = MigrationRedefinitionClosure.Expand(_chain, ["062_Flags"]);
    await Assert.That(closure.Count).IsEqualTo(1)
      .Because("re-running the last word needs no closure — nothing later redefines its objects.");
  }

  [Test]
  public async Task Expand_EmptyToRun_EmptyClosureAsync() {
    var closure = MigrationRedefinitionClosure.Expand(_chain, []);
    await Assert.That(closure.Count).IsEqualTo(0);
  }
}
