// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lenses;
using Whizbang.Data.EFCore.Postgres.QueryTranslation;
using Whizbang.Data.EFCore.Postgres.Tests.Generated;
using Whizbang.Testing.Containers;

namespace Whizbang.Data.EFCore.Postgres.Tests.QueryTranslation;

/// <summary>
/// A perspective keeping its filter values in a second, small, GIN-indexed jsonb column, end to end against a
/// real database: the generated schema, the generated model configuration, both write paths, the read-back,
/// and filters compiled into containment that return the rows the same filter returns in memory and are
/// answered from the column's index.
/// </summary>
/// <docs>fundamentals/perspectives/physical-fields#jsonb-columns</docs>
[Category("Integration")]
[NotInParallel("EFCorePostgresTests")]
[Category("Shard1")]
[SuppressMessage("Readability", "RCS1118:Mark local variable as const",
  Justification = "Captured on purpose: a const local is inlined as a literal and the parameterized path would go untested.")]
public class PhysicalJsonbContainmentIntegrationTests {
  private const string EXTRACTED_TABLE = "wh_per_jsonb_extracted_item";
  private const string SPLIT_TABLE = "wh_per_jsonb_split_item";
  private static readonly string[] _northEast = ["north", "east"];
  private static readonly string[] _west = ["west"];

  private string _databaseName = null!;
  private string _connectionString = null!;

  [Before(Test)]
  public async Task SetupAsync() {
    await SharedPostgresContainer.InitializeAsync();
    var database = await PerTestDatabaseFactory.CreateAsync("jsonb_cols");
    _databaseName = database.Name;
    _connectionString = database.ConnectionString;
    await using var context = Context(_connectionString);
    await context.EnsureWhizbangDatabaseInitializedAsync();
  }

  [After(Test)]
  public async Task TeardownAsync() {
    if (_databaseName is not null) {
      await PerTestDatabaseFactory.DropAsync(_databaseName);
    }
  }

  // A context over a data source the test built: the shape of a consumer that registers its own.
  private static JsonbColumnsDbContext Context(NpgsqlDataSource dataSource) {
    ModelRegistrationRegistry.InvokeRegistration(new ServiceCollection(), typeof(JsonbColumnsDbContext), new PostgresUpsertStrategy());
    return new JsonbColumnsDbContext(new DbContextOptionsBuilder<JsonbColumnsDbContext>()
      .UseNpgsql(dataSource)
      .UseWhizbangPhysicalFields()
      .ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
      .Options);
  }

  internal static JsonbColumnsDbContext Context(string connectionString, DbCommandInterceptor? interceptor = null) {
    // The generated registration an application's AddWhizbang runs: it fills the query and hydrator
    // registries for this context's models. Once per collection, so calling it per context is harmless.
    ModelRegistrationRegistry.InvokeRegistration(new ServiceCollection(), typeof(JsonbColumnsDbContext), new PostgresUpsertStrategy());
    var builder = new DbContextOptionsBuilder<JsonbColumnsDbContext>()
      .UseNpgsql(connectionString)
      .UseWhizbangPhysicalFields()
      .ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
    if (interceptor is not null) {
      builder.AddInterceptors(interceptor);
    }

    return new JsonbColumnsDbContext(builder.Options);
  }

  // ------------------------------------------------------------------
  // Writing and reading back
  // ------------------------------------------------------------------

  /// <summary>
  /// What the generated runner writes for an Extracted model reads back whole, on both write paths: the
  /// atomic upsert binds the jsonb columns through the column converter, and the change-tracker path writes
  /// them through the model, including a UTC DateTime into its timestamptz column.
  /// </summary>
  [Test]
  [Arguments(true)]
  [Arguments(false)]
  public async Task ExtractedModel_RoundTripsThroughEitherWritePathAsync(bool atomic) {
    var id = Guid.NewGuid();
    var model = _extracted(id, "north", "red");

    await _writeAsync(id, model, atomic);

    await using var context = Context(_connectionString);
    var store = new EFCorePostgresPerspectiveStore<JsonbExtractedItem.Model>(context, EXTRACTED_TABLE);
    var read = await store.GetByStreamIdAsync(id);

    await Assert.That(read).IsNotNull();
    await Assert.That(read!.GridFilter["region"]).IsEquivalentTo(_northEast);
    await Assert.That(read.Labels).IsEquivalentTo(model.Labels);
    await Assert.That(read.Tags).IsEquivalentTo(model.Tags);
    await Assert.That(read.Place!.City).IsEqualTo("Springfield");
    await Assert.That(read.Stamp).IsEqualTo(model.Stamp);

    var copy = await _scalarAsync($"SELECT (grid_filter = data -> 'GridFilter')::text FROM {EXTRACTED_TABLE} WHERE id = '{id}'");
    await Assert.That(copy).IsEqualTo("true")
      .Because("both write paths leave the document's copy of a jsonb column, identical to the column.");
  }

  /// <summary>
  /// The atomic upsert and the change-tracker write leave the same row: the same document, its copies of the
  /// jsonb columns included, and the same columns. A null object column is absent from both documents.
  /// </summary>
  [Test]
  [Arguments(true)]
  [Arguments(false)]
  public async Task BothWritePaths_LeaveIdenticalRowsAsync(bool withPlace) {
    var atomicId = Guid.NewGuid();
    var trackedId = Guid.NewGuid();
    var model = _extracted(atomicId, "north", "red");
    if (!withPlace) {
      model.Place = null;
    }

    await _writeAsync(atomicId, model, atomic: true);
    model.Id = trackedId;
    await _writeAsync(trackedId, model, atomic: false);
    // A second write through each path updates the row rather than inserting it.
    model.Tags = ["changed"];
    await _writeAsync(trackedId, model, atomic: false);
    model.Id = atomicId;
    await _writeAsync(atomicId, model, atomic: true);

    const string ROW = "(data - 'Id', grid_filter, labels, tags, place, stamp)";
    var same = await _scalarAsync(
      $"SELECT ((SELECT {ROW} FROM {EXTRACTED_TABLE} WHERE id = '{atomicId}') = (SELECT {ROW} FROM {EXTRACTED_TABLE} WHERE id = '{trackedId}'))::text");
    await Assert.That(same).IsEqualTo("true");
    await Assert.That(await _scalarAsync($"SELECT (data ? 'Place')::text FROM {EXTRACTED_TABLE} WHERE id = '{trackedId}'"))
      .IsEqualTo(withPlace ? "true" : "false");
  }

  /// <summary>
  /// The change-tracker write inside a transaction the caller already opened joins it: the row and the document's
  /// copy of its jsonb columns commit together with the caller's work, and roll back with it.
  /// </summary>
  [Test]
  public async Task TrackedWrite_InTheCallersTransaction_CommitsAndRollsBackWithItAsync() {
    var committedId = Guid.NewGuid();
    var rolledBackId = Guid.NewGuid();
    var model = _extracted(committedId, "north", "red");
    var physical = new Dictionary<string, object?> {
      ["grid_filter"] = model.GridFilter,
      ["labels"] = model.Labels,
      ["tags"] = model.Tags,
      ["place"] = model.Place,
      ["stamp"] = model.Stamp,
    };

    await using (var context = Context(_connectionString)) {
      var store = new EFCorePostgresPerspectiveStore<JsonbExtractedItem.Model>(context, EXTRACTED_TABLE, UpsertWritePath.Strategy(false));
      await using var transaction = await context.Database.BeginTransactionAsync();
      await store.UpsertWithPhysicalFieldsAsync(committedId, model, physical);
      await transaction.CommitAsync();
    }
    await using (var context = Context(_connectionString)) {
      var store = new EFCorePostgresPerspectiveStore<JsonbExtractedItem.Model>(context, EXTRACTED_TABLE, UpsertWritePath.Strategy(false));
      await using var transaction = await context.Database.BeginTransactionAsync();
      model.Id = rolledBackId;
      await store.UpsertWithPhysicalFieldsAsync(rolledBackId, model, physical);
      await transaction.RollbackAsync();
    }

    await Assert.That(await _scalarAsync($"SELECT (grid_filter = data -> 'GridFilter')::text FROM {EXTRACTED_TABLE} WHERE id = '{committedId}'"))
      .IsEqualTo("true").Because("the document's copy is written in the caller's transaction and commits with it");
    await Assert.That(await _scalarAsync($"SELECT count(*)::text FROM {EXTRACTED_TABLE} WHERE id = '{rolledBackId}'"))
      .IsEqualTo("0").Because("the write joined the caller's transaction, so its rollback removes the row too");
  }

  /// <summary>
  /// A Split model's jsonb columns are the only copy, and the model the next event is applied to reads them back.
  /// </summary>
  [Test]
  [Arguments(true)]
  [Arguments(false)]
  public async Task SplitModel_RoundTripsThroughEitherWritePathAsync(bool atomic) {
    var id = Guid.NewGuid();
    var filter = new Dictionary<string, string[]> { ["region"] = ["west"] };
    List<JsonbLabel> labels = [new("k", "v")];
    var physical = new Dictionary<string, object?> { ["grid_filter"] = filter, ["labels"] = labels };

    await using (var context = Context(_connectionString)) {
      var store = new EFCorePostgresPerspectiveStore<JsonbSplitItem.Model>(context, SPLIT_TABLE, UpsertWritePath.Strategy(atomic));
      // Stripped as the generated runner strips a Split model before the write.
      await store.UpsertWithPhysicalFieldsAsync(id, new JsonbSplitItem.Model { Id = id, Title = "t", GridFilter = null! }, physical);
    }

    await using var reading = Context(_connectionString);
    var read = await new EFCorePostgresPerspectiveStore<JsonbSplitItem.Model>(reading, SPLIT_TABLE).GetByStreamIdAsync(id);

    await Assert.That(read!.GridFilter["region"]).IsEquivalentTo(_west);
    await Assert.That(read.Labels).IsEquivalentTo(labels);
    await Assert.That(await _scalarAsync($"SELECT data ? 'GridFilter' FROM {SPLIT_TABLE} WHERE id = '{id}'")).IsEqualTo("False");
  }

  /// <summary>
  /// A consumer's own data source, built without Npgsql's dynamic JSON, reads and writes a Split model's jsonb
  /// collection columns (#1037). The generated mapping converts each one to and from its JSON text under the
  /// persistence profile, so Npgsql only ever sees a string and never needs <c>EnableDynamicJson</c>.
  /// </summary>
  [Test]
  [Arguments(true)]
  [Arguments(false)]
  public async Task SplitModel_UnderADataSourceWithoutDynamicJson_RoundTripsAsync(bool atomic) {
    var id = Guid.NewGuid();
    var filter = new Dictionary<string, string[]> { ["region"] = ["west"] };
    List<JsonbLabel> labels = [new("k", "v")];
    var physical = new Dictionary<string, object?> { ["grid_filter"] = filter, ["labels"] = labels };
    await using var dataSource = new NpgsqlDataSourceBuilder(_connectionString).Build();

    await using (var context = Context(dataSource)) {
      var store = new EFCorePostgresPerspectiveStore<JsonbSplitItem.Model>(context, SPLIT_TABLE, UpsertWritePath.Strategy(atomic));
      await store.UpsertWithPhysicalFieldsAsync(id, new JsonbSplitItem.Model { Id = id, Title = "t", GridFilter = null! }, physical);
    }

    await using var reading = Context(dataSource);
    var read = await new EFCorePostgresPerspectiveStore<JsonbSplitItem.Model>(reading, SPLIT_TABLE).GetByStreamIdAsync(id);

    await Assert.That(read!.GridFilter["region"]).IsEquivalentTo(_west);
    await Assert.That(read.Labels).IsEquivalentTo(labels);
  }

  /// <summary>A null object column is written as SQL NULL and read back as null.</summary>
  [Test]
  public async Task NullObjectColumn_IsSqlNullAsync() {
    var id = Guid.NewGuid();
    var model = _extracted(id, "north", "red");
    model.Place = null;

    await _writeAsync(id, model, atomic: true);

    await Assert.That(await _scalarAsync($"SELECT place IS NULL FROM {EXTRACTED_TABLE} WHERE id = '{id}'")).IsEqualTo("True");
    await using var context = Context(_connectionString);
    var read = await new EFCorePostgresPerspectiveStore<JsonbExtractedItem.Model>(context, EXTRACTED_TABLE).GetByStreamIdAsync(id);
    await Assert.That(read!.Place).IsNull();
  }

  // ------------------------------------------------------------------
  // Filters return the rows the same filter returns in memory
  // ------------------------------------------------------------------

  /// <summary>
  /// Every supported shape returns exactly the rows the same predicate selects from the models in memory,
  /// including its negation, a missing dictionary key, and a row whose object column is null.
  /// </summary>
  [Test]
  public async Task SupportedShapes_ReturnTheRowsTheInMemoryFilterReturnsAsync() {
    var models = new List<JsonbExtractedItem.Model> {
      _extracted(Guid.NewGuid(), "north", "red"),
      _extracted(Guid.NewGuid(), "south", "blue"),
      _extracted(Guid.NewGuid(), "north", "blue"),
    };
    var noRegion = _extracted(Guid.NewGuid(), "x", "green");
    noRegion.GridFilter = new() { ["size"] = ["xl"] };
    noRegion.Place = null;
    models.Add(noRegion);

    foreach (var model in models) {
      await _writeAsync(model.Id, model, atomic: true);
    }

    var region = "north";
    var tag = "blue";
    var city = "Springfield";
    var labelKey = "team";
    var labelValue = "a-north";

    await _assertSameRowsAsync(models, m => m.GridFilter.ContainsKey("region") && m.GridFilter["region"].Contains(region),
      r => r.Data.GridFilter["region"].Contains(region));
    await _assertSameRowsAsync(models, m => m.GridFilter.ContainsKey("region") && m.GridFilter["region"].Any(v => v == region),
      r => r.Data.GridFilter["region"].Any(v => v == region));
    await _assertSameRowsAsync(models, m => m.Tags.Contains(tag), r => r.Data.Tags.Contains(tag));
    await _assertSameRowsAsync(models, m => !m.Tags.Contains(tag), r => !r.Data.Tags.Contains(tag));
    await _assertSameRowsAsync(models, m => m.Labels.Any(l => l.Key == labelKey && l.Value == labelValue),
      r => r.Data.Labels.Any(l => l.Key == labelKey && l.Value == labelValue));
    await _assertSameRowsAsync(models, m => m.Place != null && m.Place.City == city, r => r.Data.Place!.City == city);
    await _assertSameRowsAsync(models, m => m.Place?.Zone == 3, r => r.Data.Place!.Zone == 3);
  }

  // ------------------------------------------------------------------
  // The index answers them
  // ------------------------------------------------------------------

  /// <summary>
  /// The containment filters are answered from the columns' GIN indexes. Sequential scans are disabled for
  /// the plan, so a plan that still scans is one the index cannot answer, whatever the table's size.
  /// </summary>
  [Test]
  public async Task Explain_ContainmentFilters_UseTheColumnsGinIndexesAsync() {
    await _execAsync($"""
      INSERT INTO {EXTRACTED_TABLE} (id, data, metadata, scope, created_at, updated_at, sys_created_at, sys_updated_at, version,
                                     grid_filter, labels, tags, place, stamp)
      SELECT gen_random_uuid(), jsonb_build_object('Title', 'bulk-' || g), jsonb_build_object(), jsonb_build_object(), now(), now(), now(), now(), 1,
             jsonb_build_object('region', jsonb_build_array('r-' || g)),
             jsonb_build_array(jsonb_build_object('Key', 'team', 'Value', 't-' || g)),
             jsonb_build_array('tag-' || g), NULL, now()
      FROM generate_series(1, 5000) g;
      ANALYZE {EXTRACTED_TABLE};
      """);

    var region = "r-42";
    var plan = await _planAsync(r => r.Data.GridFilter["region"].Contains(region));
    await Assert.That(plan).Contains("idx_jsonb_extracted_item_grid_filter_gin");

    var labelValue = "t-42";
    var labelsPlan = await _planAsync(r => r.Data.Labels.Any(l => l.Key == "team" && l.Value == labelValue));
    await Assert.That(labelsPlan).Contains("idx_jsonb_extracted_item_labels_gin");
  }

  /// <summary>The containment index is the one the declaration asked for: GIN over the column with jsonb_path_ops.</summary>
  [Test]
  public async Task ContainmentDeclaration_BuildsAGinJsonbPathOpsIndexAsync() {
    var definition = await _scalarAsync(
      $"SELECT indexdef FROM pg_indexes WHERE tablename = '{SPLIT_TABLE}' AND indexname = 'idx_jsonb_split_item_grid_filter_gin'");

    await Assert.That(definition).Contains("USING gin (grid_filter jsonb_path_ops)");
  }

  // ------------------------------------------------------------------
  // Helpers
  // ------------------------------------------------------------------

  private static JsonbExtractedItem.Model _extracted(Guid id, string region, string tag) => new() {
    Id = id,
    Title = "item-" + region,
    GridFilter = new() { ["region"] = [region, "east"], ["size"] = ["m"] },
    Labels = [new("team", "a-" + region), new("lane", "b")],
    Tags = [tag, "common"],
    Place = new JsonbPlace { City = region == "north" ? "Springfield" : "Shelbyville", Zone = region == "south" ? 3 : 1 },
    Stamp = new DateTime(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc),
  };

  private async Task _writeAsync(Guid id, JsonbExtractedItem.Model model, bool atomic) {
    // What the generated runner hands the store for an Extracted model: the model and each promoted value.
    var physical = new Dictionary<string, object?> {
      ["grid_filter"] = model.GridFilter,
      ["labels"] = model.Labels,
      ["tags"] = model.Tags,
      ["place"] = model.Place,
      ["stamp"] = model.Stamp,
    };
    await using var context = Context(_connectionString);
    var store = new EFCorePostgresPerspectiveStore<JsonbExtractedItem.Model>(context, EXTRACTED_TABLE, UpsertWritePath.Strategy(atomic));
    await store.UpsertWithPhysicalFieldsAsync(id, model, physical);
  }

  private async Task _assertSameRowsAsync(
      List<JsonbExtractedItem.Model> models,
      Func<JsonbExtractedItem.Model, bool> inMemory,
      Expression<Func<PerspectiveRow<JsonbExtractedItem.Model>, bool>> filter) {
    await using var context = Context(_connectionString);
    var found = await context.Set<PerspectiveRow<JsonbExtractedItem.Model>>().Where(filter).Select(r => r.Id).ToListAsync();
    var expected = models.Where(inMemory).Select(m => m.Id).ToList();

    await Assert.That(found).IsEquivalentTo(expected).Because(filter.ToString());
    await Assert.That(context.Set<PerspectiveRow<JsonbExtractedItem.Model>>().Where(filter).ToQueryString()).Contains(" @> ");
  }

  private async Task<string> _planAsync(Expression<Func<PerspectiveRow<JsonbExtractedItem.Model>, bool>> filter) {
    var capture = new CommandCapture();
    await using (var context = Context(_connectionString, capture)) {
      await context.Set<PerspectiveRow<JsonbExtractedItem.Model>>().Where(filter).Select(r => r.Id).ToListAsync();
    }

    return await PlanAsync(_connectionString, capture);
  }

  /// <summary>The plan of the captured command with sequential scans disabled.</summary>
  internal static async Task<string> PlanAsync(string connectionString, CommandCapture capture) {
    await using var db = new NpgsqlConnection(connectionString);
    await db.OpenAsync();
    await using (var off = new NpgsqlCommand("SET enable_seqscan = off", db)) {
      await off.ExecuteNonQueryAsync();
    }

    await using var explain = new NpgsqlCommand("EXPLAIN " + capture.Text, db);
    foreach (var (name, value) in capture.Parameters) {
      explain.Parameters.Add(new NpgsqlParameter(name, value));
    }

    var lines = new List<string>();
    await using var reader = await explain.ExecuteReaderAsync();
    while (await reader.ReadAsync()) {
      lines.Add(reader.GetString(0));
    }

    return string.Join('\n', lines);
  }

  private async Task _execAsync(string sql) {
    await using var db = new NpgsqlConnection(_connectionString);
    await db.OpenAsync();
    await using var command = new NpgsqlCommand(sql, db);
    await command.ExecuteNonQueryAsync();
  }

  private async Task<string> _scalarAsync(string sql) {
    await using var db = new NpgsqlConnection(_connectionString);
    await db.OpenAsync();
    await using var command = new NpgsqlCommand(sql, db);
    return (await command.ExecuteScalarAsync())?.ToString() ?? "<null>";
  }

  /// <summary>Records the last query command, so its plan can be asked for with the same text and values.</summary>
  internal sealed class CommandCapture : DbCommandInterceptor {
    public string Text { get; private set; } = "";

    public List<(string Name, object? Value)> Parameters { get; } = [];

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default) {
      Text = command.CommandText;
      Parameters.Clear();
      foreach (DbParameter parameter in command.Parameters) {
        Parameters.Add((parameter.ParameterName, parameter.Value));
      }

      return ValueTask.FromResult(result);
    }
  }
}
