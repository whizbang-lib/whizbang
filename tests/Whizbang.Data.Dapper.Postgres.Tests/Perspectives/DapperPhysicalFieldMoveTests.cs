using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Workers;

namespace Whizbang.Data.Dapper.Postgres.Tests.Perspectives;

/// <summary>
/// Issues #1010, #1021 and #1022 on the Dapper driver: a promotion that the column-copy swap applies is armed and
/// filled, rows an instance still on the previous release writes are filled by the Dapper maintenance step, a
/// Split promotion syncs writes, and a demoted column is copied into the document.
/// </summary>
/// <remarks>
/// Each test applies the generated perspective schema the way the driver does, puts a table in the shape the
/// previous release left it, forgets its schema hash, and applies the schema again: what a release that moves
/// the fields does.
/// </remarks>
/// <docs>fundamentals/perspectives/physical-fields#storage-moves</docs>
[NotInParallel("PostgreSQL")]
public class DapperPhysicalFieldMoveTests : PostgresTestBase {
  private const string PROMOTED = "wh_per_dapper_promoted_perspective";
  private const string SPLIT = "wh_per_dapper_split_moved_perspective";
  private static readonly Guid _watcher = Guid.Parse("4d2a7b10-0000-4000-8000-000000000001");
  private static readonly Guid _row = Guid.Parse("4d2a7b10-0000-4000-8000-0000000000cc");
  private static readonly DateTimeOffset _now = new(2026, 10, 1, 12, 3, 0, TimeSpan.Zero);

  private static KeyValuePair<string, string>[] _entries() => [..
    Whizbang.Generated.PerspectiveSchemas.Entries.Where(e => e.Key is "DapperPromotedPerspective" or "DapperSplitMovedPerspective")];

  private Task _startAsync() => new PostgresSchemaInitializer(ConnectionString, _entries()).InitializeSchemaAsync();

  private async Task _execAsync(string sql) {
    await using var db = new NpgsqlConnection(ConnectionString);
    await db.OpenAsync();
    await using var command = new NpgsqlCommand(sql, db);
    await command.ExecuteNonQueryAsync();
  }

  private async Task<string> _scalarAsync(string sql) {
    await using var db = new NpgsqlConnection(ConnectionString);
    await db.OpenAsync();
    await using var command = new NpgsqlCommand(sql, db);
    return (await command.ExecuteScalarAsync())?.ToString() ?? "<null>";
  }

  private Task _forgetAsync(string perspective) =>
    _execAsync($"DELETE FROM wh_schema_migrations WHERE file_name = 'perspective:{perspective}'");

  /// <summary>The Extracted table as the release before the promotion left it, the fields only in the document; then the promoting start.</summary>
  private async Task _promoteExtractedAsync() {
    await _startAsync();
    await _execAsync($$"""
      ALTER TABLE {{PROMOTED}} DROP COLUMN name, DROP COLUMN rank, DROP COLUMN lane, DROP COLUMN tags;
      INSERT INTO {{PROMOTED}} (id, data, metadata, scope, created_at, updated_at, version)
      SELECT gen_random_uuid(), jsonb_build_object('Name', 'item-' || g, 'Rank', g, 'Lane', g % 3,
               'Tags', jsonb_build_array(jsonb_build_object('Label', 't' || g, 'Weight', g))),
             '{}', '{}', now(), now(), 1
      FROM generate_series(1, 1200) AS g;
      """);
    await _forgetAsync("DapperPromotedPerspective");
    await _startAsync();
  }

  private Task _writeAsThePreviousReleaseAsync(int count) => _execAsync($$"""
    INSERT INTO {{PROMOTED}} (id, data, metadata, scope, created_at, updated_at, version)
    SELECT gen_random_uuid(), jsonb_build_object('Name', 'late-' || g, 'Rank', 100000 + g, 'Lane', 1, 'Tags', '[]'::jsonb),
           '{}', '{}', now(), now(), 1
    FROM generate_series(1, {{count}}) AS g;
    """);

  private static ServiceProvider _services(IClaimedEmissionStore? claims = null) {
    var services = new ServiceCollection();
    if (claims is not null) {
      services.AddSingleton(claims);
    }
    return services.BuildServiceProvider();
  }

  private DapperPhysicalColumnFillMaintenanceStep _step(TimeSpan? settle = null) =>
    new(ConnectionString, null, new FixedTime(_now), settle: settle);

  // ── #1010: the column-copy swap arms and fills; the step fills the rollout's rows ─────────────────

  /// <summary>The swap that adds the columns arms each one first, and every row is filled from its document.</summary>
  [Test]
  public async Task AColumnCopyPromotion_ArmsAndFillsEachColumnAsync() {
    await _promoteExtractedAsync();

    await Assert.That(await _scalarAsync("""
      SELECT string_agg(column_name || ':' || direction || ':' || sync_writes, ',' ORDER BY column_name) FROM wh_physical_column_fills
      WHERE direction <> 'recorded'
      """)).IsEqualTo("lane:to_column:false,name:to_column:false,rank:to_column:false,tags:to_column:false");
    await Assert.That(await _scalarAsync($"""
      SELECT count(*) FROM {PROMOTED}
      WHERE name IS DISTINCT FROM data ->> 'Name' OR rank IS DISTINCT FROM (data ->> 'Rank')::int
         OR lane IS DISTINCT FROM (data ->> 'Lane')::int OR tags IS DISTINCT FROM data -> 'Tags'
      """)).IsEqualTo("0");
    await Assert.That(await _scalarAsync($"SELECT count(*) FROM {PROMOTED}")).IsEqualTo("1200");
  }

  /// <summary>A row the previous release writes after the swap is filled by the Dapper step, and found by a column filter.</summary>
  [Test]
  public async Task ARowThePreviousReleaseWrote_IsFilledByTheDapperStepAsync() {
    await _promoteExtractedAsync();
    await _writeAsThePreviousReleaseAsync(3);
    await Assert.That(await _scalarAsync($"SELECT count(*) FROM {PROMOTED} WHERE rank = 100002")).IsEqualTo("0");

    await using var services = _services();
    await _step().RunAsync(services, CancellationToken.None);

    await Assert.That(await _scalarAsync($"SELECT count(*) FROM {PROMOTED} WHERE rank = 100002 AND name = 'late-2' AND lane = 1 AND tags = '[]'"))
      .IsEqualTo("1");
    await Assert.That(await _scalarAsync("SELECT count(*) FROM wh_physical_column_fills WHERE direction <> 'recorded'")).IsEqualTo("4")
      .Because("a promotion stays armed until the settle window has passed");
  }

  /// <summary>Only the instance that takes the window's claim fills; one that loses it changes nothing.</summary>
  [Test]
  public async Task TheStep_RunsOnlyOnTheInstanceThatTakesTheClaimAsync() {
    await _promoteExtractedAsync();
    await _writeAsThePreviousReleaseAsync(2);

    await using (var losing = _services(new FixedClaims(false))) {
      await _step().RunAsync(losing, CancellationToken.None);
    }
    await Assert.That(await _scalarAsync($"SELECT count(*) FROM {PROMOTED} WHERE rank IS NULL")).IsEqualTo("2");

    await using (var winning = _services(new FixedClaims(true))) {
      await _step().RunAsync(winning, CancellationToken.None);
    }
    await Assert.That(await _scalarAsync($"SELECT count(*) FROM {PROMOTED} WHERE rank IS NULL")).IsEqualTo("0");

    // With no claim store registered, the step takes the same claim directly: once per window.
    await _writeAsThePreviousReleaseAsync(1);
    await using (var direct = _services()) {
      await _step().RunAsync(direct, CancellationToken.None);
    }
    await Assert.That(await _scalarAsync($"SELECT count(*) FROM {PROMOTED} WHERE rank IS NULL")).IsEqualTo("0");

    await _writeAsThePreviousReleaseAsync(1);
    await using (var again = _services()) {
      await _step().RunAsync(again, CancellationToken.None);
    }
    await Assert.That(await _scalarAsync($"SELECT count(*) FROM {PROMOTED} WHERE rank IS NULL")).IsEqualTo("1")
      .Because("this window's claim is taken, so the next window's run fills the row");
  }

  /// <summary>With nothing armed the step claims nothing; once settled it disarms every promotion.</summary>
  [Test]
  public async Task TheStep_IsIdleWithNothingArmed_AndDisarmsOnceSettledAsync() {
    await _startAsync();
    await using (var services = _services()) {
      await _step().RunAsync(services, CancellationToken.None);
    }
    await Assert.That(await _scalarAsync("SELECT count(*) FROM wh_unique_emission_claims")).IsEqualTo("0");

    await _promoteExtractedAsync();
    await using (var services = _services()) {
      await _step(settle: TimeSpan.Zero).RunAsync(services, CancellationToken.None);
    }
    await Assert.That(await _scalarAsync("SELECT count(*) FROM wh_physical_column_fills WHERE direction <> 'recorded'")).IsEqualTo("0");
  }

  // ── #1021: a Split promotion syncs writes on the swapped-in table ────────────────────────────────

  /// <summary>
  /// A Split promotion fills the columns, and the triggers on the swapped-in table keep the column and the
  /// document in agreement for a write from either release; the Dapper store then reads the row's values.
  /// </summary>
  [Test]
  public async Task ASplitPromotion_FillsAndSyncsOnTheSwappedInTableAsync() {
    await _startAsync();
    await _execAsync($$"""
      ALTER TABLE {{SPLIT}} DROP COLUMN title, DROP COLUMN points, DROP COLUMN watchers;
      INSERT INTO {{SPLIT}} (id, data, metadata, scope, created_at, updated_at, version)
      VALUES ('{{_row}}', '{"Id": "{{_row}}", "Title": "known", "Points": 7, "Watchers": ["{{_watcher}}"], "Score": 0, "Labels": null}',
              '{}', '{}', now(), now(), 1);
      """);
    await _forgetAsync("DapperSplitMovedPerspective");
    await _startAsync();

    await Assert.That(await _scalarAsync($"SELECT concat_ws('|', title, points, watchers::text) FROM {SPLIT} WHERE id = '{_row}'"))
      .IsEqualTo($"known|7|{{{_watcher}}}");
    await Assert.That(await _scalarAsync($"SELECT count(*) FROM pg_trigger WHERE tgrelid = '{SPLIT}'::regclass AND tgname LIKE 'wh_mv_%'"))
      .IsEqualTo("6");

    // The previous release names no column.
    await _execAsync($"UPDATE {SPLIT} SET data = jsonb_set(data, '{{Points}}', '8') WHERE id = '{_row}'");
    await Assert.That(await _scalarAsync($"SELECT points FROM {SPLIT} WHERE id = '{_row}'")).IsEqualTo("8");

    // The new release, through the Dapper store: columns written, the document stripped by the runner.
    var store = new DapperPostgresPerspectiveStore<DapperSplitMovedModel>(ConnectionString, SPLIT, new JsonSerializerOptions {
      TypeInfoResolver = JsonTypeInfoResolver.Combine(DapperMoveJsonContext.Default, global::Whizbang.Core.Generated.InfrastructureJsonContext.Default),
    });
    await store.UpsertWithPhysicalFieldsAsync(_row, new DapperSplitMovedModel { Id = _row },
      new Dictionary<string, object?> { ["title"] = "renamed", ["points"] = 9, ["watchers"] = new[] { _watcher, _watcher } });

    await Assert.That(await _scalarAsync($"SELECT concat_ws('|', data ->> 'Title', data ->> 'Points', data -> 'Watchers') FROM {SPLIT} WHERE id = '{_row}'"))
      .IsEqualTo($"renamed|9|[\"{_watcher}\", \"{_watcher}\"]")
      .Because("the previous release reads these fields from the document");
    var model = await store.GetByStreamIdAsync(_row);
    await Assert.That(model!.Title).IsEqualTo("renamed");
    await Assert.That(model.Points).IsEqualTo(9);
  }

  // ── #1022: a demoted column is copied into the document, once ────────────────────────────────────

  /// <summary>The first start records each promoted column as the framework's, under its field, on a table it creates.</summary>
  [Test]
  public async Task TheFirstStart_RecordsEachPromotedColumnUnderItsFieldAsync() {
    await _startAsync();

    await Assert.That(await _scalarAsync($"""
      SELECT string_agg(column_name || ':' || json_key, ',' ORDER BY column_name) FROM wh_physical_column_fills
      WHERE table_name = 'public.{SPLIT}' AND direction = 'recorded'
      """)).IsEqualTo("points:Points,title:Title,watchers:Watchers");
  }

  /// <summary>
  /// A demoted column's values reach the document (one recorded under its default name, one under a name of its own),
  /// writes from either release are synced, a later start copies nothing, and a column the framework never recorded
  /// is never copied.
  /// </summary>
  [Test]
  public async Task ADemotedColumn_IsCopiedIntoTheDocumentAndSyncedAsync() {
    await _startAsync();
    await _execAsync($$"""
      ALTER TABLE {{SPLIT}} ADD COLUMN score integer, ADD COLUMN label_list text[], ADD COLUMN labels text[];
      INSERT INTO {{SPLIT}} (id, data, metadata, scope, created_at, updated_at, version, score, label_list, labels)
      VALUES ('{{_row}}', '{"Score": 0, "Labels": null}', '{}', '{}', now(), now(), 1, 42, ARRAY['a', 'b'], ARRAY['operator']);
      INSERT INTO wh_physical_column_fills (table_name, column_name, json_key, extraction, direction, armed_at, settled_at)
      VALUES ('public.{{SPLIT}}', 'score', 'Score', NULL, 'recorded', now(), now()),
             ('public.{{SPLIT}}', 'label_list', 'Labels', NULL, 'recorded', now(), now());
      """);
    await _forgetAsync("DapperSplitMovedPerspective");
    await _startAsync();

    await Assert.That(await _scalarAsync($"SELECT concat_ws('|', data ->> 'Score', data -> 'Labels', score) FROM {SPLIT} WHERE id = '{_row}'"))
      .IsEqualTo("42|[\"a\", \"b\"]|42")
      .Because("labels, an unrecorded column under the field's default name, is not the field's: label_list is");

    await _execAsync($"UPDATE {SPLIT} SET data = jsonb_set(data, '{{Score}}', '0'), score = 50, label_list = NULL WHERE id = '{_row}'");
    await Assert.That(await _scalarAsync($"SELECT concat_ws('|', data ->> 'Score', data -> 'Labels') FROM {SPLIT} WHERE id = '{_row}'"))
      .IsEqualTo("50|null");
    await _execAsync($"UPDATE {SPLIT} SET data = jsonb_set(data, '{{Score}}', '51') WHERE id = '{_row}'");
    await Assert.That(await _scalarAsync($"SELECT score FROM {SPLIT} WHERE id = '{_row}'")).IsEqualTo("51");

    await using (var services = _services()) {
      await _step(settle: TimeSpan.Zero).RunAsync(services, CancellationToken.None);
    }
    await _execAsync($"UPDATE {SPLIT} SET data = jsonb_set(data, '{{Score}}', '60') WHERE id = '{_row}'");
    await _forgetAsync("DapperSplitMovedPerspective");
    await _startAsync();

    await Assert.That(await _scalarAsync($"SELECT concat_ws('|', data ->> 'Score', score) FROM {SPLIT} WHERE id = '{_row}'"))
      .IsEqualTo("60|51").Because("a settled demotion is never copied again, and the column is left for an operator to drop");
  }

  // ── Registration ─────────────────────────────────────────────────────────────────────────────────

  /// <summary>Both registrations of the Dapper driver add the step.</summary>
  [Test]
  public async Task AddWhizbangPostgres_RegistersTheStepAsync() {
    var withEntries = new ServiceCollection().AddWhizbangPostgres(ConnectionString, new JsonSerializerOptions(), false, _entries(), null);
    var withSql = new ServiceCollection().AddWhizbangPostgres(ConnectionString, new JsonSerializerOptions(), false, (string?)null, null);

    foreach (var services in new[] { withEntries, withSql }) {
      await using var provider = services.BuildServiceProvider();
      await using var scope = provider.CreateAsyncScope();
      var steps = scope.ServiceProvider.GetServices<IMaintenanceStep>().ToList();
      await Assert.That(steps.OfType<DapperPhysicalColumnFillMaintenanceStep>().Count()).IsEqualTo(1);
      await Assert.That(steps.OfType<DapperPhysicalColumnFillMaintenanceStep>().Single().Name).IsEqualTo("physical-column-fill");
    }
  }

  /// <summary>A step needs a database to run against.</summary>
  [Test]
  public async Task TheStep_WithoutAConnectionString_ThrowsAsync() {
    await Assert.That(() => new DapperPhysicalColumnFillMaintenanceStep(" ")).Throws<ArgumentException>();
    await Assert.That(async () => await _step().RunAsync(null!, CancellationToken.None)).Throws<ArgumentNullException>();
  }

  /// <summary>A clock stopped at one instant, so every run of a test falls in the same claim window.</summary>
  private sealed class FixedTime(DateTimeOffset now) : TimeProvider {
    public override DateTimeOffset GetUtcNow() => now;
  }

  /// <summary>A claim store whose answer the test decides.</summary>
  private sealed class FixedClaims(bool granted) : IClaimedEmissionStore {
    public Task<bool> TryClaimAsync(string claimKey, Guid claimedByEventId, CancellationToken cancellationToken) =>
      Task.FromResult(granted);
  }
}
