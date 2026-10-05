// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Data.EFCore.Postgres.Dispatch;
using Whizbang.Testing.Containers;

namespace Whizbang.Data.EFCore.Postgres.Tests.Perspectives;

/// <summary>
/// Issue #1021: promoting fields of every kind on a Split model (a string, a number, an enumeration, a native
/// array, a jsonb collection, an instant) fills their columns from the documents the previous release wrote,
/// and keeps the column and the document in agreement for every write either release makes during the deploy.
/// </summary>
/// <remarks>
/// Each test first puts the table in the shape the previous release left it: the fields only in the document,
/// no columns. The perspective's schema hash is then forgotten so the next start applies the promoting DDL,
/// which is what a release that promotes the fields does. A write by the previous release names no column; a
/// write by the new one names every column and strips the fields from the document, as the Split runner does.
/// </remarks>
/// <docs>fundamentals/perspectives/physical-fields#storage-moves</docs>
[Category("Integration")]
[NotInParallel("EFCorePostgresTests")]
[Category("Shard1")]
public class SplitPromotionTests {
  private const string TABLE = PhysicalMoves.TABLE;
  private static readonly Guid _watcher = Guid.Parse("8a1f3c4e-0000-4000-8000-000000000001");
  private static readonly Guid _row = Guid.Parse("8a1f3c4e-0000-4000-8000-0000000000aa");

  private string _databaseName = null!;
  private string _connectionString = null!;

  [Before(Test)]
  public async Task SetupAsync() {
    await SharedPostgresContainer.InitializeAsync();
    var database = await PerTestDatabaseFactory.CreateAsync("split_promote");
    _databaseName = database.Name;
    _connectionString = database.ConnectionString;
    await PhysicalMoves.StartAsync(_connectionString);
    await _promoteAsync();
  }

  [After(Test)]
  public async Task TeardownAsync() {
    if (_databaseName is not null) {
      await PerTestDatabaseFactory.DropAsync(_databaseName);
    }
  }

  private Task<string> _scalarAsync(string sql) => PhysicalMoves.ScalarAsync(_connectionString, sql);

  private Task _execAsync(string sql) => PhysicalMoves.ExecAsync(_connectionString, sql);

  /// <summary>The document as the previous release, which kept every field in it, wrote it.</summary>
  private static string _document(string title, int points, int lane, int score = 0) =>
    $$"""{"Id": "{{_row}}", "Title": "{{title}}", "Points": {{points}}, "Lane": {{lane}}, "Watchers": ["{{_watcher}}"], "Tags": [{"Label": "a", "Weight": 2}], "DueAt": 1772600767123456, "Score": {{score}}, "Notes": null, "Labels": null, "SeenAt": null, "Spot": null}""";

  /// <summary>
  /// The table as the release before the promotion left it, 2,000 rows and one known row with every field only
  /// in the document; then the promoting start.
  /// </summary>
  private async Task _promoteAsync() {
    await _execAsync($$"""
      ALTER TABLE {{TABLE}} DROP COLUMN title, DROP COLUMN points, DROP COLUMN lane, DROP COLUMN watchers,
        DROP COLUMN tags, DROP COLUMN due_at;
      INSERT INTO {{TABLE}} (id, data, metadata, scope, created_at, updated_at, sys_created_at, sys_updated_at, version)
      SELECT gen_random_uuid(), jsonb_build_object('Title', 'item-' || g, 'Points', g, 'Lane', g % 3,
               'Watchers', jsonb_build_array('{{_watcher}}'), 'Tags', jsonb_build_array(jsonb_build_object('Label', 'l' || g, 'Weight', g)),
               'DueAt', 1772600767123456 + g, 'Score', 0),
             jsonb_build_object(), jsonb_build_object(), now(), now(), now(), now(), 1
      FROM generate_series(1, 2000) AS g;
      INSERT INTO {{TABLE}} (id, data, metadata, scope, created_at, updated_at, sys_created_at, sys_updated_at, version)
      VALUES ('{{_row}}', '{{_document("known", 7, 2)}}'::jsonb, '{}', '{}', now(), now(), now(), now(), 1);
      """);
    await PhysicalMoves.ForgetSchemaAsync(_connectionString);
    await PhysicalMoves.StartAsync(_connectionString);
  }

  private Task<string> _columnsAsync() => _scalarAsync(
    $"SELECT concat_ws('|', title, points, lane, watchers::text, tags::text, (EXTRACT(EPOCH FROM due_at) * 1000000)::bigint) FROM {TABLE} WHERE id = '{_row}'");

  private Task<string> _documentFieldsAsync() => _scalarAsync(
    $"SELECT concat_ws('|', data ->> 'Title', data ->> 'Points', data ->> 'Lane', data -> 'Watchers', data -> 'Tags', data ->> 'DueAt') FROM {TABLE} WHERE id = '{_row}'");

  /// <summary>Every promoted column of every row holds what the document the previous release wrote holds.</summary>
  [Test]
  public async Task ASplitPromotion_FillsTheColumnFromTheDocumentAsync() {
    await Assert.That(await _columnsAsync())
      .IsEqualTo($"known|7|2|{{{_watcher}}}|[{{\"Label\": \"a\", \"Weight\": 2}}]|1772600767123456");
    await Assert.That(await _scalarAsync($"""
      SELECT count(*) FROM {TABLE}
      WHERE title IS NULL OR points IS NULL OR lane IS NULL OR watchers IS NULL OR tags IS NULL OR due_at IS NULL
      """)).IsEqualTo("0");
    await Assert.That(await _scalarAsync($"SELECT count(*) FROM {TABLE} WHERE lane = 2 AND points % 3 = 2")).IsEqualTo("667");
  }

  /// <summary>Each promoted column is armed as a Split move, with its two sync triggers on the table.</summary>
  [Test]
  public async Task EachPromotedColumn_IsArmedWithItsWritesSyncedAsync() {
    await Assert.That(await _scalarAsync("""
      SELECT string_agg(column_name || ':' || direction || ':' || sync_writes, ',' ORDER BY column_name)
      FROM wh_physical_column_fills WHERE direction <> 'recorded'
      """)).IsEqualTo("due_at:to_column:true,lane:to_column:true,points:to_column:true,tags:to_column:true,title:to_column:true,watchers:to_column:true");
    await Assert.That(await PhysicalMoves.SyncTriggersAsync(_connectionString)).IsEqualTo("12");
  }

  /// <summary>
  /// A row the previous release writes, naming no column, has its columns follow the document at once; a row
  /// the new release writes, naming the columns and stripping the document, has the document follow the columns,
  /// so the previous release still reads it right.
  /// </summary>
  [Test]
  public async Task AWriteFromEitherRelease_KeepsTheColumnAndTheDocumentInAgreementAsync() {
    var late = Guid.NewGuid();
    await _execAsync($$"""
      INSERT INTO {{TABLE}} (id, data, metadata, scope, created_at, updated_at, sys_created_at, sys_updated_at, version)
      VALUES ('{{late}}', '{{_document("late", 11, 1)}}'::jsonb, '{}', '{}', now(), now(), now(), now(), 1);
      UPDATE {{TABLE}} SET data = jsonb_set(data, '{Points}', '8') WHERE id = '{{_row}}';
      """);

    await Assert.That(await _scalarAsync($"SELECT concat_ws('|', title, points, lane, watchers::text) FROM {TABLE} WHERE id = '{late}'"))
      .IsEqualTo($"late|11|1|{{{_watcher}}}")
      .Because("the previous release wrote the row knowing no column");
    await Assert.That(await _scalarAsync($"SELECT points FROM {TABLE} WHERE id = '{_row}'")).IsEqualTo("8");

    // The new release: every column named, the fields stripped to their defaults in the document.
    await _execAsync($$"""
      UPDATE {{TABLE}} SET
        data = data || '{"Title": null, "Points": 0, "Lane": 0, "Watchers": [], "Tags": null, "DueAt": null}'::jsonb,
        title = 'renamed', points = 9, lane = 1, watchers = ARRAY['{{_watcher}}'::uuid, '{{_watcher}}'::uuid],
        tags = '[]'::jsonb, due_at = TIMESTAMPTZ '2026-03-04 05:06:07.123457+00'
      WHERE id = '{{_row}}';
      """);

    await Assert.That(await _documentFieldsAsync())
      .IsEqualTo($"renamed|9|1|[\"{_watcher}\", \"{_watcher}\"]|[]|1772600767123457")
      .Because("the previous release reads these fields from the document");
    await Assert.That(await _columnsAsync()).IsEqualTo($"renamed|9|1|{{{_watcher},{_watcher}}}|[]|1772600767123457");
  }

  /// <summary>A document value the column cannot take leaves the column as it was rather than failing the write.</summary>
  [Test]
  public async Task ADocumentValueTheColumnCannotTake_DoesNotFailTheWriteAsync() {
    await _execAsync($"UPDATE {TABLE} SET data = jsonb_set(data, '{{Points}}', '\"many\"') WHERE id = '{_row}'");

    await Assert.That(await _scalarAsync($"SELECT concat_ws('|', points, data ->> 'Points') FROM {TABLE} WHERE id = '{_row}'"))
      .IsEqualTo("7|many");
  }

  /// <summary>A second start arms nothing, fills nothing and changes no value.</summary>
  [Test]
  public async Task ASecondStart_ChangesNothingAsync() {
    await _execAsync("UPDATE wh_physical_column_fills SET armed_at = TIMESTAMPTZ '2026-01-01 00:00:00+00'");
    await PhysicalMoves.ForgetSchemaAsync(_connectionString);

    await PhysicalMoves.StartAsync(_connectionString);

    await Assert.That(await _scalarAsync("SELECT count(*) FROM wh_physical_column_fills WHERE armed_at > TIMESTAMPTZ '2026-01-01 00:00:00+00'"))
      .IsEqualTo("0").Because("only the start that adds a column arms it");
    await Assert.That(await PhysicalMoves.SyncTriggersAsync(_connectionString)).IsEqualTo("12");
    await Assert.That(await _columnsAsync())
      .IsEqualTo($"known|7|2|{{{_watcher}}}|[{{\"Label\": \"a\", \"Weight\": 2}}]|1772600767123456");
  }

  /// <summary>
  /// Once the settle window has passed the maintenance step disarms each promotion and drops its triggers; a
  /// write after that is the new release's alone and leaves the document stripped.
  /// </summary>
  [Test]
  public async Task TheSyncTriggers_AreDroppedWhenTheMoveSettlesAsync() {
    await using (var context = PhysicalMoves.Context(_connectionString)) {
      var services = new ServiceCollection();
      services.AddSingleton(context);
      services.AddSingleton<IClaimedEmissionStore>(new EFCoreClaimedEmissionStore(context));
      await using var provider = services.BuildServiceProvider();
      var step = new PhysicalColumnFillMaintenanceStep(
        typeof(PhysicalMovesDbContext), null, new FakeTimeProvider(new DateTimeOffset(2026, 10, 1, 12, 3, 0, TimeSpan.Zero)), settle: TimeSpan.Zero);
      await step.RunAsync(provider, CancellationToken.None);
    }

    await Assert.That(await PhysicalMoves.SyncTriggersAsync(_connectionString)).IsEqualTo("0");
    await Assert.That(await _scalarAsync("SELECT count(*) FROM wh_physical_column_fills WHERE direction <> 'recorded'")).IsEqualTo("0");
    await Assert.That(await _scalarAsync("SELECT count(*) FROM pg_proc WHERE proname LIKE 'wh_mv_%'")).IsEqualTo("0");

    await _execAsync($"UPDATE {TABLE} SET data = jsonb_set(data, '{{Points}}', '0'), points = 12 WHERE id = '{_row}'");
    await Assert.That(await _scalarAsync($"SELECT concat_ws('|', points, data ->> 'Points') FROM {TABLE} WHERE id = '{_row}'"))
      .IsEqualTo("12|0");
  }

  /// <summary>The new release reads a row the previous release wrote during the deploy with its values, through the store.</summary>
  [Test]
  public async Task ARowThePreviousReleaseWrote_IsReadWithItsValuesThroughTheStoreAsync() {
    var late = Guid.NewGuid();
    await _execAsync($$"""
      INSERT INTO {{TABLE}} (id, data, metadata, scope, created_at, updated_at, sys_created_at, sys_updated_at, version)
      VALUES ('{{late}}', '{{_document("late", 11, 1).Replace(_row.ToString(), late.ToString(), StringComparison.Ordinal)}}'::jsonb,
        '{}', '{}', now(), now(), now(), now(), 1);
      """);

    // A collection in a jsonb column is read by Npgsql's dynamic JSON, which an application opts into.
    var builder = new NpgsqlDataSourceBuilder(_connectionString);
    builder.EnableDynamicJson();
    await using var dataSource = builder.Build();
    await using var context = new PhysicalMovesDbContext(
      new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<PhysicalMovesDbContext>()
        .UseNpgsql(dataSource)
        .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.CoreEventId.ManyServiceProvidersCreatedWarning))
        .Options);
    var store = new EFCorePostgresPerspectiveStore<MovedTicket.Model>(context, TABLE);
    var model = await store.GetByStreamIdAsync(late);

    await Assert.That(model).IsNotNull();
    await Assert.That(model!.Title).IsEqualTo("late");
    await Assert.That(model.Points).IsEqualTo(11);
    await Assert.That(model.Lane).IsEqualTo(MovedTicket.Stage.Doing);
    await Assert.That(model.Watchers).IsEquivalentTo([_watcher]);
    await Assert.That(model.Tags!.Single().Weight).IsEqualTo(2);
    await Assert.That(model.DueAt).IsEqualTo(DateTimeOffset.UnixEpoch.AddTicks(1772600767123456 * 10));
  }
}
