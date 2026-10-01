using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Perspectives;
using Whizbang.Data.Postgres;
using Whizbang.Testing.Containers;

namespace Whizbang.Data.EFCore.Postgres.Tests.Migrations;

/// <summary>
/// App-declared stored-form migrations, run through the stored-format rewrite phase against Postgres: each
/// generated case converts the values still in the old form and leaves the rest, a second run changes nothing, a
/// value that cannot be converted stops startup naming the table, the path and the values, and the journal makes a
/// named migration run once and say so.
/// </summary>
/// <remarks>
/// The journal comes from its migration's embedded text, schema-substituted the way the runtime applies it, against
/// a scratch database. The table is a plain table with a <c>data</c> jsonb column: the rewrite is what is under test,
/// not the initializer.
/// </remarks>
/// <docs>fundamentals/perspectives/stored-form-migrations</docs>
/// <tests>src/Whizbang.Data.Postgres/StoredFormMigrationSql.cs</tests>
/// <tests>src/Whizbang.Data.Postgres/StoredFormStep.cs</tests>
/// <tests>src/Whizbang.Data.Postgres/StoredFormMigrationJournal.cs</tests>
[Category("Integration")]
[NotInParallel("EFCorePostgresTests")]
[Category("Shard1")]
public class StoredFormMigrationTests : IAsyncDisposable {
  private const string MIGRATION = "176_StoredFormMigrations";
  private const long LOCK_ID = 987654986;
  private const int TIMEOUT_SECONDS = 30;
  private const string TABLE = "wh_per_thing";

  private string _databaseName = null!;
  private string _connectionString = null!;

  [Before(Test)]
  public async Task SetupAsync() {
    await SharedPostgresContainer.InitializeAsync();
    var database = await PerTestDatabaseFactory.CreateAsync("storedform");
    _databaseName = database.Name;
    _connectionString = database.ConnectionString;

    var journal = new PostgresMigrationProvider(typeof(PostgresMigrationProvider).Assembly, "public").GetMigration(MIGRATION);
    await Assert.That(journal).IsNotNull().Because($"migration {MIGRATION} must be embedded");
    await _executeAsync(journal!.Sql);
    await _executeAsync($"CREATE TABLE {TABLE} (id int PRIMARY KEY, data jsonb)");
  }

  [After(Test)]
  public async ValueTask DisposeAsync() {
    if (_databaseName is not null) {
      await PerTestDatabaseFactory.DropAsync(_databaseName);
    }
    GC.SuppressFinalize(this);
  }

  // ─── Type changes ────────────────────────────────────────────────────────────────────────────────────────

  [Test]
  public async Task NumberToString_ConvertsNumbersAndBooleans_LeavesStringsNullsAndMissingAsync() {
    await _seedAsync("""{"Status": 123}""", """{"Status": "Open"}""", """{"Status": null}""", "{}", """{"Status": 1.50}""", """{"Status": true}""");

    var log = await _applyAsync(_generated("wh_per_thing.Status:Int32->String", StoredFormStep.ToText("Status")));

    await Assert.That(await _documentsAsync()).IsEqualTo(
      """{"Status": "123"}|{"Status": "Open"}|{"Status": null}|{}|{"Status": "1.50"}|{"Status": "true"}""");
    await Assert.That(log).Contains("wh_per_thing: stored-form migration wh_per_thing.Status:Int32->String: converted, 3 row update(s)");
  }

  [Test]
  public async Task EnumNumberToName_UsesTheMemberName_OrTheNumbersTextAsync() {
    await _seedAsync("""{"Stage": 0}""", """{"Stage": 2}""", """{"Stage": 9}""", """{"Stage": "Open"}""");

    await _applyAsync(_generated("wh_per_thing.Stage:Stage->String",
      StoredFormStep.EnumNumberToName("Stage", [("Draft", "0"), ("Open", "1"), ("Closed", "2")])));

    await Assert.That(await _documentsAsync()).IsEqualTo(
      """{"Stage": "Draft"}|{"Stage": "Closed"}|{"Stage": "9"}|{"Stage": "Open"}""")
      .Because("A number no single member has keeps its text; a string is already in the new form.");
  }

  [Test]
  public async Task StringToInteger_ConvertsNumericStrings_AndWholeNumbersWrittenWithAFractionAsync() {
    await _seedAsync("""{"Qty": "42"}""", """{"Qty": 7}""", """{"Qty": "-3"}""", """{"Qty": 5.0}""", """{"Qty": null}""", """{"Qty": "1e2"}""");

    await _applyAsync(_generated("wh_per_thing.Qty:String->Int32", StoredFormStep.ToNumber("Qty", StoredNumber.Int32)));

    await Assert.That(await _documentsAsync()).IsEqualTo(
      """{"Qty": 42}|{"Qty": 7}|{"Qty": -3}|{"Qty": 5}|{"Qty": null}|{"Qty": 100}""");
  }

  [Test]
  public async Task StringToDecimal_ConvertsNumericStrings_LeavesNumbersAsync() {
    await _seedAsync("""{"Price": "12.50"}""", """{"Price": 3.25}""");

    await _applyAsync(_generated("wh_per_thing.Price:String->Decimal", StoredFormStep.ToNumber("Price", StoredNumber.Decimal)));

    await Assert.That(await _documentsAsync()).IsEqualTo("""{"Price": 12.50}|{"Price": 3.25}""");
  }

  [Test]
  [Arguments("""{"Qty": "n/a"}""", "\"n/a\"")]
  [Arguments("""{"Qty": "2.5"}""", "\"2.5\"")]
  [Arguments("""{"Qty": 3000000000}""", "3000000000")]
  [Arguments("""{"Qty": ""}""", "\"\"")]
  public async Task StringToInteger_AValueItCannotConvert_BlocksNamingTheTablePathAndValue_ChangingNothingAsync(
      string unconvertible, string reported) {
    await _seedAsync("""{"Qty": "42"}""", unconvertible);

    var failure = await Assert.That(() => _applyOnlyAsync(_generated("wh_per_thing.Qty:String->Int32",
      StoredFormStep.ToNumber("Qty", StoredNumber.Int32)))).Throws<StoredFormConversionBlockedException>();

    await Assert.That(failure!.Message).Contains("wh_per_thing.Qty:String->Int32");
    await Assert.That(failure.Message).Contains("public.wh_per_thing");
    await Assert.That(failure.Message).Contains("$.Qty");
    await Assert.That(failure.Message).Contains(reported);
    await Assert.That(failure.Message).Contains("Int32");
    await Assert.That(await _documentsAsync()).IsEqualTo($$"""{"Qty": "42"}|{{unconvertible}}""")
      .Because("A blocked migration is rolled back to its savepoint; nothing changes.");
    await Assert.That(await _journalAsync("wh_per_thing.Qty:String->Int32")).IsNull()
      .Because("Nothing is recorded for a migration that did not apply.");
  }

  [Test]
  public async Task NarrowingNumbers_BlockOutOfRange_AndWideningNeedsNoChangeAsync() {
    await _seedAsync("""{"Level": 300}""", """{"Level": 4}""");

    var failure = await Assert.That(() => _applyOnlyAsync(_generated("wh_per_thing.Level:Int32->Byte",
      StoredFormStep.ToNumber("Level", StoredNumber.Byte)))).Throws<StoredFormConversionBlockedException>();
    await Assert.That(failure!.Message).Contains("300");

    var log = await _applyAsync(_generated("wh_per_thing.Level:Int32->Int64", StoredFormStep.ToNumber("Level", StoredNumber.Int64)));
    await Assert.That(log).Contains("nothing left to convert, settled")
      .Because("A JSON number already reads as the wider type; the pass only checks the range.");
  }

  [Test]
  [Arguments(StoredNumber.SByte, "-129")]
  [Arguments(StoredNumber.UInt16, "65536")]
  [Arguments(StoredNumber.Int16, "40000")]
  [Arguments(StoredNumber.UInt32, "-1")]
  [Arguments(StoredNumber.UInt64, "18446744073709551616")]
  [Arguments(StoredNumber.Int64, "9223372036854775808")]
  [Arguments(StoredNumber.Single, "1e39")]
  [Arguments(StoredNumber.Double, "1e309")]
  [Arguments(StoredNumber.Decimal, "1e29")]
  public async Task EveryNumberKind_BlocksAValueOutsideItsRangeAsync(StoredNumber kind, string value) {
    await _seedAsync($$"""{"N": "{{value}}"}""");

    await Assert.That(() => _applyOnlyAsync(_generated("n", StoredFormStep.ToNumber("N", kind))))
      .Throws<StoredFormConversionBlockedException>();
  }

  [Test]
  public async Task StringToEnum_NamesAndNumericStringsBecomeNumbers_UnknownNamesBlockAsync() {
    await _seedAsync("""{"Stage": "Open"}""", """{"Stage": "2"}""", """{"Stage": 0}""", """{"Stage": null}""");
    (string, string)[] members = [("Draft", "0"), ("Open", "1"), ("Closed", "2")];

    await _applyAsync(_generated("wh_per_thing.Stage:String->Stage", StoredFormStep.ToEnumNumber("Stage", "Stage", StoredNumber.Int32, members, flags: false)));
    await Assert.That(await _documentsAsync()).IsEqualTo("""{"Stage": 1}|{"Stage": 2}|{"Stage": 0}|{"Stage": null}""");

    await _seedAsync("""{"Stage": "open"}""", """{"Stage": "Archived"}""");
    var failure = await Assert.That(() => _applyOnlyAsync(_generated("wh_per_thing.Stage:String->Stage#2",
      StoredFormStep.ToEnumNumber("Stage", "Stage", StoredNumber.Int32, members, flags: false)))).Throws<StoredFormConversionBlockedException>();
    await Assert.That(failure!.Message).Contains("\"Archived\"");
    await Assert.That(failure.Message).Contains("\"open\"").Because("Names match exactly.");
    await Assert.That(failure.Message).Contains("Stage");
  }

  [Test]
  public async Task StringToFlagsEnum_CombinedNamesBecomeTheirBitwiseOr_AnUnknownComponentBlocksAsync() {
    await _seedAsync("""{"Access": "Read, Write"}""", """{"Access": "Write"}""", """{"Access": "5"}""");
    (string, string)[] members = [("None", "0"), ("Read", "1"), ("Write", "2"), ("Admin", "4")];

    await _applyAsync(_generated("wh_per_thing.Access:String->Access", StoredFormStep.ToEnumNumber("Access", "Access", StoredNumber.Int32, members, flags: true)));
    await Assert.That(await _documentsAsync()).IsEqualTo("""{"Access": 3}|{"Access": 2}|{"Access": 5}""");

    await _seedAsync("""{"Access": "Read, Delete"}""", """{"Access": ""}""");
    var failure = await Assert.That(() => _applyOnlyAsync(_generated("wh_per_thing.Access:String->Access#2",
      StoredFormStep.ToEnumNumber("Access", "Access", StoredNumber.Int32, members, flags: true)))).Throws<StoredFormConversionBlockedException>();
    await Assert.That(failure!.Message).Contains("\"Read, Delete\"");
    await Assert.That(failure.Message).Contains("\"\"").Because("An empty string names no member.");
  }

  [Test]
  public async Task StringToEnum_ANumericStringOutsideTheUnderlyingRange_BlocksAsync() {
    await _seedAsync("""{"Size": "300"}""");

    await Assert.That(() => _applyOnlyAsync(_generated("n", StoredFormStep.ToEnumNumber("Size", "Size", StoredNumber.Byte, [("A", "0")], flags: false))))
      .Throws<StoredFormConversionBlockedException>();
  }

  // ─── Paths ───────────────────────────────────────────────────────────────────────────────────────────────

  [Test]
  public async Task Rename_MovesTheValue_NestedToo_AndTheNewKeyWinsWhenBothExistAsync() {
    await _seedAsync(
      """{"Name": "a", "Shipping": {"Street": "s1"}}""",
      """{"DisplayName": "b", "Shipping": {"Line1": "s2"}}""",
      """{"Name": "old", "DisplayName": "new"}""",
      """{"Name": null}""");

    await _applyAsync(
      _generated("wh_per_thing.DisplayName:renamed-from:Name", StoredFormStep.Rename("DisplayName", "Name")),
      _generated("wh_per_thing.Shipping.Line1:renamed-from:Street", StoredFormStep.Rename("Shipping.Line1", "Street")));

    await Assert.That(await _documentsAsync()).IsEqualTo(
      """{"Shipping": {"Line1": "s1"}, "DisplayName": "a"}|{"Shipping": {"Line1": "s2"}, "DisplayName": "b"}|{"DisplayName": "new"}|{"DisplayName": null}""");
  }

  [Test]
  public async Task Remove_DropsTheKeyWhereverItIs_AndNeverBlocksAsync() {
    await _seedAsync("""{"Legacy": "x", "Keep": 1}""", """{"Keep": 2, "Shipping": {"Note": "n"}}""");

    await _applyAsync(
      _generated("wh_per_thing.Legacy:removed", StoredFormStep.Remove("Legacy")),
      _generated("wh_per_thing.Shipping.Note:removed", StoredFormStep.Remove("Shipping.Note")));

    await Assert.That(await _documentsAsync()).IsEqualTo("""{"Keep": 1}|{"Keep": 2, "Shipping": {}}""");
  }

  [Test]
  public async Task DefaultWhenMissing_FillsOnlyAMissingKey_UnderAnExistingParentAsync() {
    await _seedAsync("{}", """{"Tier": 5}""", """{"Tier": null}""", """{"Shipping": {}}""", """{"Shipping": null}""");

    await _applyAsync(
      _generated("wh_per_thing.Tier:default", StoredFormStep.DefaultWhenMissing("Tier", "1")),
      _generated("wh_per_thing.Shipping.Country:default", StoredFormStep.DefaultWhenMissing("Shipping.Country", "\"US\"")));

    await Assert.That(await _documentsAsync()).IsEqualTo(
      """{"Tier": 1}|{"Tier": 5}|{"Tier": null}|{"Tier": 1, "Shipping": {"Country": "US"}}|{"Tier": 1, "Shipping": null}""")
      .Because("A null may be a real value, and a missing parent has nowhere to put the key.");
  }

  // ─── Physical columns ────────────────────────────────────────────────────────────────────────────────────

  [Test]
  public async Task RetypeColumn_ToText_AndToANumber_BlockingWhatCannotConvertAsync() {
    await _executeAsync("ALTER TABLE wh_per_thing ADD COLUMN status integer, ADD COLUMN qty text, ADD COLUMN stage integer");
    await _executeAsync("INSERT INTO wh_per_thing (id, data, status, qty, stage) VALUES (1, '{}', 7, '12', 0), (2, '{}', NULL, NULL, 2), (3, '{}', 8, '4.0', 9)");

    var log = await _applyAsync(
      _generated("wh_per_thing.Status:Int32->String", StoredFormStep.RetypeColumn("status", "TEXT", number: null)),
      _generated("wh_per_thing.Qty:String->Int64", StoredFormStep.RetypeColumn("qty", "BIGINT", StoredNumber.Int64)),
      _generated("wh_per_thing.Stage:Stage->String", StoredFormStep.RetypeColumn("stage", "TEXT", number: null, [("Draft", "0"), ("Closed", "2")])));

    await Assert.That(await _scalarAsync("SELECT string_agg(format_type(atttypid, atttypmod), ',' ORDER BY attname) FROM pg_attribute WHERE attrelid = 'wh_per_thing'::regclass AND attname IN ('status', 'qty', 'stage')"))
      .IsEqualTo("bigint,text,text");
    await Assert.That(await _scalarAsync("SELECT string_agg(coalesce(status, '-') || '/' || coalesce(qty::text, '-') || '/' || coalesce(stage, '-'), ',' ORDER BY id) FROM wh_per_thing"))
      .IsEqualTo("7/12/Draft,-/-/Closed,8/4/9");
    await Assert.That(log).Contains("converted, 2 row update(s)").Because("One per non-null value retyped.");

    // Retyped already: a second pass changes nothing and settles.
    var again = await _applyAsync(_generated("wh_per_thing.Status:Int32->String", StoredFormStep.RetypeColumn("status", "TEXT", number: null)));
    await Assert.That(again).Contains("nothing left to convert, settled");
  }

  [Test]
  public async Task RetypeColumn_AValueThatIsNotANumber_BlocksNamingTheColumnAsync() {
    await _executeAsync("ALTER TABLE wh_per_thing ADD COLUMN qty text");
    await _executeAsync("INSERT INTO wh_per_thing (id, data, qty) VALUES (1, '{}', '12'), (2, '{}', 'lots'), (3, '{}', '2.5')");

    var failure = await Assert.That(() => _applyOnlyAsync(_generated("wh_per_thing.Qty:String->Int32",
      StoredFormStep.RetypeColumn("qty", "INTEGER", StoredNumber.Int32)))).Throws<StoredFormConversionBlockedException>();

    await Assert.That(failure!.Message).Contains("column qty");
    await Assert.That(failure.Message).Contains("lots");
    await Assert.That(failure.Message).Contains("2.5");
    await Assert.That(await _scalarAsync("SELECT format_type(atttypid, atttypmod) FROM pg_attribute WHERE attrelid = 'wh_per_thing'::regclass AND attname = 'qty'"))
      .IsEqualTo("text");
  }

  [Test]
  public async Task RetypeColumn_ToADecimal_KeepsTheFractionAsync() {
    await _executeAsync("ALTER TABLE wh_per_thing ADD COLUMN price text");
    await _executeAsync("INSERT INTO wh_per_thing (id, data, price) VALUES (1, '{}', '2.50')");

    await _applyAsync(_generated("wh_per_thing.Price:String->Decimal", StoredFormStep.RetypeColumn("price", "NUMERIC", StoredNumber.Decimal)));

    await Assert.That(await _scalarAsync("SELECT price::text FROM wh_per_thing")).IsEqualTo("2.50");
  }

  [Test]
  public async Task RenameColumn_RenamesOnlyWhenTheOldExistsAndTheNewDoesNotAsync() {
    await _executeAsync("ALTER TABLE wh_per_thing ADD COLUMN name text");
    await _executeAsync("INSERT INTO wh_per_thing (id, data, name) VALUES (1, '{}', 'a')");

    var log = await _applyAsync(_generated("wh_per_thing.DisplayName:renamed-from:Name", StoredFormStep.RenameColumn("name", "display_name")));

    await Assert.That(await _scalarAsync("SELECT display_name FROM wh_per_thing")).IsEqualTo("a");
    await Assert.That(log).Contains("converted, 0 row update(s)")
      .Because("A column rename changes no row, but the pass did change something, so it does not settle yet.");
    var again = await _applyAsync(_generated("wh_per_thing.DisplayName:renamed-from:Name", StoredFormStep.RenameColumn("name", "display_name")));
    await Assert.That(again).Contains("nothing left to convert, settled");
  }

  [Test]
  public async Task ColumnSteps_OnAMissingColumn_AreNoOpsAsync() {
    var log = await _applyAsync(_generated("m",
      StoredFormStep.RetypeColumn("absent", "TEXT", number: null),
      StoredFormStep.RenameColumn("absent", "other")));

    await Assert.That(log).Contains("nothing left to convert, settled");
  }

  // ─── Idempotence and the journal ─────────────────────────────────────────────────────────────────────────

  [Test]
  public async Task ASecondRun_ChangesNothing_AndSettles_AndAThirdSkipsWithoutAScanAsync() {
    await _seedAsync("""{"Status": 1}""", """{"Status": "b"}""");
    var migration = _generated("wh_per_thing.Status:Int32->String", StoredFormStep.ToText("Status"));

    await _applyAsync(migration);
    var afterFirst = await _documentsAsync();
    var first = await _journalAsync(migration.Name);
    await Assert.That(first!.Value.Rows).IsEqualTo(1L);
    await Assert.That(first.Value.Settled).IsFalse()
      .Because("A pass that converted rows runs again, to catch rows an older release wrote meanwhile.");

    var second = await _applyAsync(migration);
    await Assert.That(await _documentsAsync()).IsEqualTo(afterFirst);
    await Assert.That(second).Contains("nothing left to convert, settled");
    var settled = await _journalAsync(migration.Name);
    await Assert.That(settled!.Value.Rows).IsEqualTo(1L);
    await Assert.That(settled.Value.Settled).IsTrue();

    // Once settled, a value in the old form written later is not touched: the migration no longer runs.
    await _seedAsync("""{"Status": 9}""");
    var third = await _applyAsync(migration);
    await Assert.That(third).Contains("settled, skipped");
    await Assert.That(await _documentsAsync()).Contains("""{"Status": 9}""");
  }

  [Test]
  public async Task ACustomMigration_RunsOnce_EvenThoughItsSqlIsNotIdempotentAsync() {
    await _seedAsync("""{"Count": 1}""", """{"Count": 5}""");
    var migration = StoredFormMigrationSql.Custom("public", TABLE, new IncrementCount());

    var first = await _applyAsync(migration);
    await _applyAsync(migration);

    await Assert.That(await _documentsAsync()).IsEqualTo("""{"Count": 2}|{"Count": 6}""")
      .Because("The journal records the named migration as settled on its first run; the second is skipped.");
    await Assert.That(first).Contains("stored-form migration 2026-10-increment-count: applied, 2 row update(s)");
    var journal = await _journalAsync("2026-10-increment-count");
    await Assert.That(journal!.Value.Settled).IsTrue();
    await Assert.That(journal.Value.Kind).IsEqualTo("custom");
  }

  [Test]
  public async Task ACustomMigration_CanBlockStartup_WithTheStoredFormStateAsync() {
    var migration = StoredFormMigrationSql.Custom("public", TABLE, new Refuses());

    var failure = await Assert.That(() => _applyOnlyAsync(migration)).Throws<StoredFormConversionBlockedException>();

    await Assert.That(failure!.Message).Contains("custom refusal");
    await Assert.That(await _journalAsync("2026-10-refuses")).IsNull();
  }

  [Test]
  public async Task ACustomMigrationWhoseSqlHoldsTheDelimiter_StillRunsAsync() {
    await _seedAsync("""{"Count": 1}""");
    var migration = StoredFormMigrationSql.Custom("public", TABLE, new HoldsTheDelimiter());

    await _applyAsync(migration);

    await Assert.That(await _documentsAsync()).IsEqualTo("""{"Tag": "$wh_sfm_sql$ and $wh_sfm$", "Count": 1}""");
  }

  [Test]
  public async Task AMissingTable_Waits_AndIsNotJournaledAsApplied_ThenRunsOnceItExistsAsync() {
    var migration = StoredFormMigrationSql.Generated("public", "wh_per_later", "wh_per_later.Code:default", StoredFormStep.DefaultWhenMissing("Code", "0"));

    var log = await _applyAsync(migration);
    await Assert.That(log).Contains("wh_per_later: stored-form migration wh_per_later.Code:default: table absent, waits for the table");
    var pending = await _statusAsync([migration]);
    await Assert.That(pending.Single().State).IsEqualTo(StoredFormMigrationState.Pending)
      .Because("Every migration is declared before the phase runs them, so a waiting one shows as pending.");

    await _executeAsync("CREATE TABLE wh_per_later (id int PRIMARY KEY, data jsonb); INSERT INTO wh_per_later VALUES (1, '{}')");
    await _applyAsync(migration);
    await Assert.That(await _scalarAsync("SELECT data::text FROM wh_per_later")).IsEqualTo("""{"Code": 0}""");
  }

  [Test]
  public async Task Status_MergesTheDeclaredMigrationsWithTheJournal_AndFormatsAReportAsync() {
    await _seedAsync("""{"Status": 1}""", """{"Legacy": 1}""");
    var converted = _generated("wh_per_thing.Status:Int32->String", StoredFormStep.ToText("Status"));
    var settled = _generated("wh_per_thing.Legacy:removed", StoredFormStep.Remove("Legacy"));
    var custom = StoredFormMigrationSql.Custom("public", TABLE, new IncrementCount());
    await _applyAsync(converted, settled);
    await _applyAsync(settled);

    var status = await _statusAsync([converted, settled, custom]);

    (string, StoredFormMigrationState)[] expected = [
      ("wh_per_thing.Status:Int32->String", StoredFormMigrationState.Applied),
      ("wh_per_thing.Legacy:removed", StoredFormMigrationState.Settled),
      ("2026-10-increment-count", StoredFormMigrationState.Pending),
    ];
    await Assert.That(status.Select(s => (s.Name, s.State)).ToList()).IsEquivalentTo(expected);
    var applied = status.Single(s => s.State == StoredFormMigrationState.Applied);
    await Assert.That(applied.RowsConverted).IsEqualTo(1L);
    await Assert.That(applied.Table).IsEqualTo(TABLE);
    await Assert.That(applied.Kind).IsEqualTo("generated");
    await Assert.That(applied.FirstAppliedAt).IsNotNull();
    await Assert.That(applied.LastAppliedAt).IsNotNull();
    await Assert.That(applied.DeclaredAt).IsNotNull();
    await Assert.That(applied.SettledAt).IsNull();
    var neverRun = status.Single(s => s.State == StoredFormMigrationState.Pending);
    await Assert.That(neverRun.DeclaredAt).IsNull().Because("A migration no pass has declared has no journal row.");
    await Assert.That(neverRun.Kind).IsEqualTo("custom");

    // The CLI's view: the journal alone, with no declarations to merge.
    var journalOnly = await _statusAsync(null);
    await Assert.That(string.Join(",", journalOnly.Select(s => s.Name)))
      .IsEqualTo("wh_per_thing.Legacy:removed,wh_per_thing.Status:Int32->String")
      .Because("The journal alone is ordered by table and name.");

    var report = StoredFormMigrationJournal.Format(status);
    await Assert.That(report).Contains("State");
    await Assert.That(report).Contains("Settled   wh_per_thing.Legacy:removed");
    await Assert.That(report).Contains("Pending   2026-10-increment-count");
    await Assert.That(StoredFormMigrationJournal.Format([])).IsEqualTo("No stored-form migrations are declared or recorded.");
  }

  [Test]
  public async Task Status_WithoutAJournalTable_ReportsEveryDeclaredMigrationAsPendingAsync() {
    await _executeAsync("DROP TABLE wh_stored_form_migrations");
    var migration = _generated("wh_per_thing.Status:Int32->String", StoredFormStep.ToText("Status"));

    var status = await _statusAsync([migration]);
    var journalOnly = await _statusAsync(null);

    await Assert.That(status.Single().State).IsEqualTo(StoredFormMigrationState.Pending);
    await Assert.That(journalOnly).IsEmpty();
  }

  [Test]
  public async Task WithoutAJournalTable_AMigrationWaits_ChangingNothingAsync() {
    await _executeAsync("DROP TABLE wh_stored_form_migrations");
    await _seedAsync("""{"Status": 1}""");

    var log = await _applyAsync(_generated("wh_per_thing.Status:Int32->String", StoredFormStep.ToText("Status")));

    await Assert.That(await _documentsAsync()).IsEqualTo("""{"Status": 1}""");
    await Assert.That(log).Contains("table absent, waits for the table");
  }

  // ─── Helpers ─────────────────────────────────────────────────────────────────────────────────────────────

  private sealed class IncrementCount : IStoredFormMigration {
    public string Name => "2026-10-increment-count";

    public string BuildSql(StoredFormMigrationTarget target) =>
      $"UPDATE {target.QualifiedTable} SET data = jsonb_set(data, '{{Count}}', to_jsonb((data ->> 'Count')::int + 1));";
  }

  private sealed class Refuses : IStoredFormMigration {
    public string Name => "2026-10-refuses";

    public string BuildSql(StoredFormMigrationTarget target) =>
      $"DO $$ BEGIN RAISE EXCEPTION USING ERRCODE = '{StoredFormMigrationTarget.BLOCKED_SQL_STATE}', MESSAGE = 'custom refusal'; END $$;";
  }

  private sealed class HoldsTheDelimiter : IStoredFormMigration {
    public string Name => "2026-10-holds-the-delimiter";

    public string BuildSql(StoredFormMigrationTarget target) =>
      $"UPDATE {target.QualifiedTable} SET data = data || jsonb_build_object('Tag', '$wh_sfm_sql$ and $wh_sfm$')";
  }

  private static StoredFormMigration _generated(string name, params StoredFormStep[] steps) =>
    StoredFormMigrationSql.Generated("public", TABLE, name, steps);

  private async Task<string> _applyAsync(params StoredFormMigration[] migrations) {
    var logger = new ListLogger();
    await StoredFormMigrationSql.DeclareAsync(() => new NpgsqlConnection(_connectionString), "public", migrations);
    await CanonicalTemporalRewritePhase.ApplyAsync(
      () => new NpgsqlConnection(_connectionString), LOCK_ID,
      StoredFormMigrationSql.ForPhase("public", migrations), TIMEOUT_SECONDS, logger);
    return string.Join("\n", logger.Messages);
  }

  private async Task _applyOnlyAsync(params StoredFormMigration[] migrations) => await _applyAsync(migrations);

  private async Task<IReadOnlyList<StoredFormMigrationStatus>> _statusAsync(IReadOnlyList<StoredFormMigration>? declared) {
    await using var connection = new NpgsqlConnection(_connectionString);
    await connection.OpenAsync();
    return await StoredFormMigrationJournal.ReadAsync(connection, "public", declared);
  }

  private int _nextId;

  private async Task _seedAsync(params string[] documents) {
    foreach (var document in documents) {
      await using var connection = new NpgsqlConnection(_connectionString);
      await connection.OpenAsync();
      await using var command = new NpgsqlCommand($"INSERT INTO {TABLE} (id, data) VALUES (@id, @doc::jsonb)", connection);
      command.Parameters.AddWithValue("id", ++_nextId);
      command.Parameters.AddWithValue("doc", document);
      await command.ExecuteNonQueryAsync();
    }
  }

  private async Task<string> _documentsAsync() =>
    (string?)await _scalarAsync($"SELECT string_agg(data::text, '|' ORDER BY id) FROM {TABLE}") ?? "";

  private async Task<(long Rows, bool Settled, string Kind)?> _journalAsync(string name) {
    await using var connection = new NpgsqlConnection(_connectionString);
    await connection.OpenAsync();
    await using var command = new NpgsqlCommand(
      "SELECT rows_converted, settled_at IS NOT NULL, kind FROM wh_stored_form_migrations WHERE name = @name AND first_applied_at IS NOT NULL", connection);
    command.Parameters.AddWithValue(nameof(name), name);
    await using var reader = await command.ExecuteReaderAsync();
    return await reader.ReadAsync() ? (reader.GetInt64(0), reader.GetBoolean(1), reader.GetString(2)) : null;
  }

  private async Task<object?> _scalarAsync(string sql) {
    await using var connection = new NpgsqlConnection(_connectionString);
    await connection.OpenAsync();
    await using var command = new NpgsqlCommand(sql, connection);
    var value = await command.ExecuteScalarAsync();
    return value is DBNull ? null : value;
  }

  private async Task _executeAsync(string sql) {
    await using var connection = new NpgsqlConnection(_connectionString);
    await connection.OpenAsync();
    await using var command = new NpgsqlCommand(sql, connection);
    await command.ExecuteNonQueryAsync();
  }

  private sealed class ListLogger : Microsoft.Extensions.Logging.ILogger {
    private readonly Lock _gate = new();
    private readonly List<string> _messages = [];

    public IReadOnlyList<string> Messages {
      get {
        lock (_gate) {
          return [.. _messages];
        }
      }
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

    public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state,
        Exception? exception, Func<TState, Exception?, string> formatter) {
      lock (_gate) {
        _messages.Add(formatter(state, exception));
      }
    }
  }
}
