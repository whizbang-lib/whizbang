#pragma warning disable CA1707

using System.Data.Common;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.Messaging;
using Whizbang.Core.Perspectives;
using Whizbang.Data.EFCore.Postgres.Collective;
using Whizbang.Data.Postgres.Collective;
using Whizbang.Testing.Containers;

namespace Whizbang.Data.EFCore.Postgres.Tests.Collective;

/// <summary>
/// A collective setter on a <c>[PhysicalField]</c> property writes the column as a typed parameter, in the same
/// UPDATE as every other setter, and writes the document path too only when the storage mode keeps the field in
/// both places. A collective that touches only physical-only fields must not rewrite <c>data</c>: Postgres copies
/// the whole jsonb value, its TOAST and its index entries on any change, which is the cost this avoids. A
/// condition on a physical property filters on the column, and the in-memory replay produces the same row.
/// </summary>
/// <remarks>
/// Rows are seeded the way the generated perspective runner writes them: the physical values go to their columns,
/// and on a <c>Split</c> model the document holds the property's default in their place. The models are registered
/// in <see cref="PerspectivePhysicalFieldRegistry"/> exactly as the runner's <c>[ModuleInitializer]</c> does.
/// </remarks>
/// <docs>fundamentals/messaging/collective-events</docs>
/// <tests>src/Whizbang.Data.EFCore.Postgres/Collective/EFCoreCollectiveAdapter.cs</tests>
/// <tests>src/Whizbang.Data.Postgres/Collective/CollectivePhysicalColumns.cs</tests>
[Category("Integration")]
[Category("CollectiveEvents")]
[NotInParallel("EFCorePostgresTests")]
[Category("Shard3")]
public class CollectivePhysicalColumnIntegrationTests : IAsyncDisposable {
  private const string SPLIT_TABLE = "wh_per_physical_ticket";
  private const string EXTRACTED_TABLE = "wh_per_physical_order";

  static CollectivePhysicalColumnIntegrationTests() {
    AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", false);
    // What the perspective runner's [ModuleInitializer] registers for these models.
    PerspectivePhysicalFieldRegistry.Register(typeof(TicketModel), nameof(TicketModel.Lane), "lane", FieldStorageMode.Split);
    PerspectivePhysicalFieldRegistry.Register(typeof(TicketModel), nameof(TicketModel.Priority), "prio", FieldStorageMode.Split);
    PerspectivePhysicalFieldRegistry.Register(typeof(TicketModel), nameof(TicketModel.Urgent), "urgent", FieldStorageMode.Split);
    PerspectivePhysicalFieldRegistry.Register(typeof(TicketModel), nameof(TicketModel.Tags), "tags", FieldStorageMode.Split);
    PerspectivePhysicalFieldRegistry.Register(typeof(OrderModel), nameof(OrderModel.Priority), "priority", FieldStorageMode.Extracted);
    PerspectivePhysicalFieldRegistry.Register(typeof(OrderModel), nameof(OrderModel.Flagged), "flagged", FieldStorageMode.Extracted);
  }

  private string? _databaseName;
  private string _connectionString = null!;
  private NpgsqlDataSource? _dataSource;
  private readonly List<string> _capturedSql = [];

  // ── Physical-only setters: the column changes, the document does not ──────────────────────────

  [Test]
  public async Task Apply_PhysicalOnlySetter_UpdatesTheColumn_AndLeavesDataByteIdenticalAsync() {
    var inScope = Guid.NewGuid();
    var otherTenant = Guid.NewGuid();
    await _runnerWriteTicketAsync(inScope, "t-A", new TicketModel { Lane = "cold", Priority = 1, Title = "First" });
    await _runnerWriteTicketAsync(otherTenant, "t-B", new TicketModel { Lane = "cold", Priority = 1, Title = "Other" });
    var before = await _documentAsync(SPLIT_TABLE, inScope);

    var affected = await _applyTicketAsync(
      new TicketSpec(s => s.SetProperty(t => t.Lane, "hot").SetProperty(t => t.Priority, 9)), "t-A");

    await Assert.That(affected).IsEqualTo(1);
    var (lane, priority) = await _ticketColumnsAsync(inScope);
    await Assert.That(lane).IsEqualTo("hot");
    await Assert.That(priority).IsEqualTo(9);
    var after = await _documentAsync(SPLIT_TABLE, inScope);
    await Assert.That(after.Text).IsEqualTo(before.Text)
      .Because("A Split field lives only in its column, so the document must come out exactly as it went in.");
    await Assert.That(after.Size).IsEqualTo(before.Size);
    await Assert.That(after.Hash).IsEqualTo(before.Hash);
    await Assert.That(_updateStatements().Single()).DoesNotContain("data =")
      .Because("Assigning data at all makes Postgres write a new copy of the document and its TOAST.");
    await Assert.That((await _ticketColumnsAsync(otherTenant)).Lane).IsEqualTo("cold")
      .Because("The tenant scope still binds.");
  }

  // ── Kept in both places ───────────────────────────────────────────────────────────────────────

  [Test]
  public async Task Apply_SetterKeptInBothPlaces_UpdatesTheColumnAndTheDocumentAsync() {
    var id = Guid.NewGuid();
    await _runnerWriteOrderAsync(id, "t-A", new OrderModel { Priority = 1, Status = "Open" });

    await _applyOrderAsync(new OrderSpec(s => s.SetProperty(o => o.Priority, 5)), "t-A");

    await using var conn = await _openAsync();
    var column = await conn.QuerySingleAsync<int>($"SELECT priority FROM {EXTRACTED_TABLE} WHERE id = @id", new { id });
    var document = await conn.QuerySingleAsync<string>($"SELECT data->>'Priority' FROM {EXTRACTED_TABLE} WHERE id = @id", new { id });
    await Assert.That(column).IsEqualTo(5);
    await Assert.That(document).IsEqualTo("5")
      .Because("Extracted keeps the full model in the document, so the copy there must follow the column.");
    await Assert.That(_updateStatements()).Count().IsEqualTo(1);
  }

  // ── Mixed setters: one statement ──────────────────────────────────────────────────────────────

  [Test]
  public async Task Apply_MixedSetters_UpdateTheColumnAndTheDocument_InOneStatementAsync() {
    var id = Guid.NewGuid();
    await _runnerWriteTicketAsync(id, "t-A", new TicketModel { Lane = "cold", Priority = 1, Title = "First" });

    await _applyTicketAsync(new TicketSpec(s => s.SetProperty(t => t.Lane, "hot").SetProperty(t => t.Title, "Moved")), "t-A");

    await Assert.That((await _ticketColumnsAsync(id)).Lane).IsEqualTo("hot");
    await using var conn = await _openAsync();
    var title = await conn.QuerySingleAsync<string>($"SELECT data->>'Title' FROM {SPLIT_TABLE} WHERE id = @id", new { id });
    await Assert.That(title).IsEqualTo("Moved");
    var update = _updateStatements().Single();
    await Assert.That(update).Contains("\"lane\" =");
    await Assert.That(update).Contains("jsonb_set(")
      .Because("The column and the document path are written by the same UPDATE, not two.");
  }

  // ── WHERE on a physical field ─────────────────────────────────────────────────────────────────

  [Test]
  public async Task Apply_WhereOnAPhysicalField_FiltersOnTheColumnAsync() {
    var hot = Guid.NewGuid();
    var cold = Guid.NewGuid();
    await _runnerWriteTicketAsync(hot, "t-A", new TicketModel { Lane = "hot", Priority = 1, Title = "a" });
    await _runnerWriteTicketAsync(cold, "t-A", new TicketModel { Lane = "cold", Priority = 1, Title = "b" });

    var affected = await _applyTicketAsync(
      new TicketSpec(s => s.SetProperty(t => t.Title, "Escalated"), r => r.Data.Lane == "hot"), "t-A");

    await Assert.That(affected).IsEqualTo(1)
      .Because("The document holds no lane on a Split model, so only a condition on the column can match the hot row.");
    await using var conn = await _openAsync();
    await Assert.That(await conn.QuerySingleAsync<string>($"SELECT data->>'Title' FROM {SPLIT_TABLE} WHERE id = @id", new { id = hot }))
      .IsEqualTo("Escalated");
    await Assert.That(await conn.QuerySingleAsync<string>($"SELECT data->>'Title' FROM {SPLIT_TABLE} WHERE id = @id", new { id = cold }))
      .IsEqualTo("b");
  }

  [Test]
  public async Task Apply_ComputedComparisonOnAPhysicalField_ReadsTheColumnAsync() {
    var hot = Guid.NewGuid();
    var cold = Guid.NewGuid();
    await _runnerWriteTicketAsync(hot, "t-A", new TicketModel { Lane = "hot", Priority = 1 });
    await _runnerWriteTicketAsync(cold, "t-A", new TicketModel { Lane = "cold", Priority = 1 });

    await _applyTicketAsync(new TicketSpec(s => s.SetProperty(t => t.IsHot, t => t.Lane == "hot")), "t-A");

    await using var conn = await _openAsync();
    await Assert.That(await conn.QuerySingleAsync<bool>($"SELECT (data->>'IsHot')::boolean FROM {SPLIT_TABLE} WHERE id = @id", new { id = hot }))
      .IsTrue();
    await Assert.That(await conn.QuerySingleAsync<bool>($"SELECT (data->>'IsHot')::boolean FROM {SPLIT_TABLE} WHERE id = @id", new { id = cold }))
      .IsFalse();
  }

  [Test]
  public async Task Apply_ComputedIntoAPhysicalField_AssignsTheBooleanToTheColumnAsync() {
    var match = Guid.NewGuid();
    var miss = Guid.NewGuid();
    await _runnerWriteTicketAsync(match, "t-A", new TicketModel { Lane = "cold", Title = "x" });
    await _runnerWriteTicketAsync(miss, "t-A", new TicketModel { Lane = "cold", Title = "y" });
    var before = await _documentAsync(SPLIT_TABLE, match);

    await _applyTicketAsync(new TicketSpec(s => s.SetProperty(t => t.Urgent, t => t.Title == "x")), "t-A");

    await using var conn = await _openAsync();
    await Assert.That(await conn.QuerySingleAsync<bool>($"SELECT urgent FROM {SPLIT_TABLE} WHERE id = @id", new { id = match })).IsTrue();
    await Assert.That(await conn.QuerySingleAsync<bool>($"SELECT urgent FROM {SPLIT_TABLE} WHERE id = @id", new { id = miss })).IsFalse();
    await Assert.That((await _documentAsync(SPLIT_TABLE, match)).Text).IsEqualTo(before.Text)
      .Because("The target is physical-only, so the comparison's result goes to the column and the document is untouched.");
  }

  [Test]
  public async Task Apply_ComputedOverAPhysicalField_KeptInBothPlaces_WritesTheColumnAndTheDocumentAsync() {
    var id = Guid.NewGuid();
    await _runnerWriteOrderAsync(id, "t-A", new OrderModel { Priority = 1, Status = "Open" });

    await _applyOrderAsync(new OrderSpec(s => s.SetProperty(o => o.Flagged, o => o.Priority == 1)), "t-A");

    await using var conn = await _openAsync();
    var (column, document) = await conn.QuerySingleAsync<(bool, string)>(
      $"SELECT flagged, data->>'Flagged' FROM {EXTRACTED_TABLE} WHERE id = @id", new { id });
    await Assert.That(column).IsTrue();
    await Assert.That(document).IsEqualTo("true");
  }

  [Test]
  public async Task Apply_UpsertElementOnAPhysicalField_ThrowsNotSupportedAsync() {
    var tag = new TicketTag { Key = "k" };
    var spec = new TicketSpec(s => s.UpsertElement(t => t.Tags, x => x.Key, tag));

    await Assert.That(async () => await _applyTicketAsync(spec, "t-A")).Throws<NotSupportedException>()
      .WithMessageContaining("targets a physical column")
      .Because("A keyed array lives in the document; a physical column holding one cannot be upserted element-wise.");
  }

  // ── Replay equals live ────────────────────────────────────────────────────────────────────────

  [Test]
  public async Task Replay_MatchesLive_ForColumnAndDocumentAsync() {
    // Same pre-state on two rows: one takes the live UPDATE, the other is replayed in memory and written the way
    // the runner writes a model after any event. The two rows must come out identical, column and document.
    var live = Guid.NewGuid();
    var replayed = Guid.NewGuid();
    TicketModel PreState() => new() { Lane = "hot", Priority = 2, Title = "First" };
    await _runnerWriteTicketAsync(live, "t-A", PreState());
    await _runnerWriteTicketAsync(replayed, "t-B", PreState());

    var spec = new TicketSpec(
      s => s.SetProperty(t => t.Priority, 8).SetProperty(t => t.Title, "Moved").SetProperty(t => t.IsHot, t => t.Lane == "hot"),
      r => r.Data.Lane == "hot");
    await _applyTicketAsync(spec, "t-A");

    var model = (TicketModel)new CollectiveInMemoryExecutor<TicketModel>().ApplyToRow(spec, PreState(), replayed);
    await _runnerWriteTicketAsync(replayed, "t-B", model);

    await Assert.That(await _ticketColumnsAsync(replayed)).IsEqualTo(await _ticketColumnsAsync(live));
    await using var conn = await _openAsync();
    var sameDocument = await conn.QuerySingleAsync<bool>(
      $"SELECT (SELECT data FROM {SPLIT_TABLE} WHERE id = @live) = (SELECT data FROM {SPLIT_TABLE} WHERE id = @replayed)",
      new { live, replayed });
    await Assert.That(sameDocument).IsTrue()
      .Because("Replay applies the same setters in memory and the runner writes the model's columns and document; the rebuilt row must equal the live one.");
  }

  [Test]
  public async Task Replay_MatchesLive_WhenTheFieldIsKeptInBothPlacesAsync() {
    var live = Guid.NewGuid();
    var replayed = Guid.NewGuid();
    OrderModel PreState() => new() { Priority = 1, Status = "Open" };
    await _runnerWriteOrderAsync(live, "t-A", PreState());
    await _runnerWriteOrderAsync(replayed, "t-B", PreState());

    var spec = new OrderSpec(s => s.SetProperty(o => o.Priority, 4).SetProperty(o => o.Status, "Queued"), r => r.Data.Priority == 1);
    await _applyOrderAsync(spec, "t-A");

    var model = (OrderModel)new CollectiveInMemoryExecutor<OrderModel>().ApplyToRow(spec, PreState(), replayed);
    await _runnerWriteOrderAsync(replayed, "t-B", model);

    await using var conn = await _openAsync();
    var same = await conn.QuerySingleAsync<bool>(
      $"SELECT (SELECT (priority, data) FROM {EXTRACTED_TABLE} WHERE id = @live) = (SELECT (priority, data) FROM {EXTRACTED_TABLE} WHERE id = @replayed)",
      new { live, replayed });
    await Assert.That(same).IsTrue();
  }

  // ── Models, specs, events ─────────────────────────────────────────────────────────────────────

  [PerspectiveStorage(FieldStorageMode.Split)]
  internal sealed class TicketModel {
    [PhysicalField] public string? Lane { get; set; }
    [PhysicalField(ColumnName = "prio")] public int Priority { get; set; }
    [PhysicalField] public bool Urgent { get; set; }
    [PhysicalField(ColumnType = "jsonb")] public List<TicketTag>? Tags { get; set; }
    public string Title { get; set; } = "";
    public bool IsHot { get; set; }
  }

  internal sealed class TicketTag {
    public string Key { get; set; } = "";
  }

  [PerspectiveStorage(FieldStorageMode.Extracted)]
  internal sealed class OrderModel {
    [PhysicalField] public int Priority { get; set; }
    [PhysicalField] public bool Flagged { get; set; }
    public string Status { get; set; } = "";
  }

  private sealed record TicketSpec(
      Expression<Action<ICollectiveSetters<TicketModel>>> Setters,
      Expression<Func<PerspectiveRow<TicketModel>, bool>>? Where = null) : ICollectiveSpec<TicketModel>;

  private sealed record OrderSpec(
      Expression<Action<ICollectiveSetters<OrderModel>>> Setters,
      Expression<Func<PerspectiveRow<OrderModel>, bool>>? Where = null) : ICollectiveSpec<OrderModel>;

  internal sealed record PhysicalCollectiveEvent : ICollectiveEvent {
    public required CollectiveScope Scope { get; init; }
  }

  private sealed class SpecHandler;

  private Task<int> _applyTicketAsync(ICollectiveSpec<TicketModel> spec, string tenant) => _applyAsync(spec, tenant);

  private Task<int> _applyOrderAsync(ICollectiveSpec<OrderModel> spec, string tenant) => _applyAsync(spec, tenant);

  private async Task<int> _applyAsync<TModel>(ICollectiveSpec<TModel> spec, string tenant) where TModel : class {
    var entry = new CollectiveApplyEntry(
      typeof(TModel), typeof(PhysicalCollectiveEvent), typeof(SpecHandler), "Apply",
      CollectiveScopeHandling.Framework, CollectiveSpecKind.Linq, (_, _, _) => spec);
    await using var ctx = _newContext();
    _capturedSql.Clear();
    return await CollectiveEventApplier<TModel>.ApplyAsync(
      entry, new SpecHandler(), new PhysicalCollectiveEvent { Scope = new TenantCollectiveScope(tenant) },
      new TenantCollectiveScopeResolver(), ctx, Guid.NewGuid(), CollectiveApplyOptions.Default);
  }

  private List<string> _updateStatements() =>
    [.. _capturedSql.Where(sql => sql.TrimStart().StartsWith("UPDATE", StringComparison.Ordinal))];

  // ── Writing a row the way the generated runner does ───────────────────────────────────────────

  private async Task _runnerWriteTicketAsync(Guid id, string tenant, TicketModel model) {
    // Generated for a Split model: the physical values go to their columns, the document holds the defaults.
    var physicalFieldValues = new Dictionary<string, object?> {
      { "lane", model.Lane }, { "prio", model.Priority }, { "urgent", model.Urgent },
    };
    var document = new TicketModel { Lane = default!, Priority = default!, Urgent = default!, Title = model.Title, IsHot = model.IsHot };
    await _upsertAsync(SPLIT_TABLE, id, tenant, document, physicalFieldValues);
  }

  private async Task _runnerWriteOrderAsync(Guid id, string tenant, OrderModel model) {
    var physicalFieldValues = new Dictionary<string, object?> { { "priority", model.Priority }, { "flagged", model.Flagged } };
    await _upsertAsync(EXTRACTED_TABLE, id, tenant, model, physicalFieldValues);
  }

  private async Task _upsertAsync<TModel>(string table, Guid id, string tenant, TModel model, Dictionary<string, object?> physical)
      where TModel : class {
    await using var ctx = _newContext();
    var metadata = new PerspectiveMetadata { EventType = "Seeded", EventId = Guid.NewGuid().ToString(), Timestamp = DateTime.UtcNow };
    await new PostgresUpsertStrategy().UpsertPerspectiveRowWithPhysicalFieldsAsync(
      ctx, table, id, model, metadata, new PerspectiveScope { TenantId = tenant }, physical);
  }

  // ── Reading ───────────────────────────────────────────────────────────────────────────────────

  private async Task<(string? Lane, int Priority)> _ticketColumnsAsync(Guid id) {
    await using var conn = await _openAsync();
    return await conn.QuerySingleAsync<(string? Lane, int Priority)>(
      $"SELECT lane, prio FROM {SPLIT_TABLE} WHERE id = @id", new { id });
  }

  private async Task<(string Text, int Size, string Hash)> _documentAsync(string table, Guid id) {
    await using var conn = await _openAsync();
    return await conn.QuerySingleAsync<(string Text, int Size, string Hash)>(
      $"SELECT data::text, pg_column_size(data), md5(data::text) FROM {table} WHERE id = @id", new { id });
  }

  private async Task<NpgsqlConnection> _openAsync() {
    var conn = new NpgsqlConnection(_connectionString);
    await conn.OpenAsync();
    return conn;
  }

  // ── Setup / teardown ──────────────────────────────────────────────────────────────────────────

  [Before(Test)]
  public async Task SetupAsync() {
    await SharedPostgresContainer.InitializeAsync();
    _databaseName = $"test_collective_physical_{Guid.NewGuid():N}";
    await using (var admin = new NpgsqlConnection(SharedPostgresContainer.ConnectionString)) {
      await admin.OpenAsync();
      await admin.ExecuteAsync($"CREATE DATABASE {_databaseName}");
    }
    _connectionString = new NpgsqlConnectionStringBuilder(SharedPostgresContainer.ConnectionString) {
      Database = _databaseName,
      Timezone = "UTC",
      IncludeErrorDetail = true,
    }.ConnectionString;

    var dataSourceBuilder = new NpgsqlDataSourceBuilder(_connectionString);
    // Reflection-based options: the fixture models are nested test types no generated JSON context covers.
    dataSourceBuilder.ConfigureJsonOptions(new System.Text.Json.JsonSerializerOptions());
    dataSourceBuilder.EnableDynamicJson();
    _dataSource = dataSourceBuilder.Build();

    await using var conn = await _openAsync();
    await conn.ExecuteAsync($"""
      CREATE TABLE {SPLIT_TABLE} (
        id UUID PRIMARY KEY, data JSONB NOT NULL, metadata JSONB NOT NULL, scope JSONB NOT NULL,
        created_at TIMESTAMPTZ NOT NULL, updated_at TIMESTAMPTZ NOT NULL, version INTEGER NOT NULL,
        lane TEXT, prio INTEGER NOT NULL DEFAULT 0, urgent BOOLEAN NOT NULL DEFAULT FALSE, tags JSONB);
      CREATE TABLE {EXTRACTED_TABLE} (
        id UUID PRIMARY KEY, data JSONB NOT NULL, metadata JSONB NOT NULL, scope JSONB NOT NULL,
        created_at TIMESTAMPTZ NOT NULL, updated_at TIMESTAMPTZ NOT NULL, version INTEGER NOT NULL,
        priority INTEGER NOT NULL DEFAULT 0, flagged BOOLEAN NOT NULL DEFAULT FALSE);
      """);
  }

  [After(Test)]
  public async Task TeardownAsync() {
    if (_dataSource is not null) {
      await _dataSource.DisposeAsync();
      _dataSource = null;
    }
    if (_databaseName is not null) {
      await using var admin = new NpgsqlConnection(SharedPostgresContainer.ConnectionString);
      await admin.OpenAsync();
      await admin.ExecuteAsync($"DROP DATABASE IF EXISTS {_databaseName} WITH (FORCE)");
      _databaseName = null;
    }
  }

  public async ValueTask DisposeAsync() {
    await TeardownAsync();
    GC.SuppressFinalize(this);
  }

  private PhysicalDbContext _newContext() {
    var options = new DbContextOptionsBuilder<PhysicalDbContext>()
      .UseNpgsql(_dataSource!)
      .AddInterceptors(new SqlCapture(_capturedSql))
      .ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
      .Options;
    return new PhysicalDbContext(options);
  }

  private sealed class PhysicalDbContext(DbContextOptions<PhysicalDbContext> options) : DbContext(options) {
    protected override void OnModelCreating(ModelBuilder modelBuilder) {
      base.OnModelCreating(modelBuilder);
      _mapRow<TicketModel>(modelBuilder, SPLIT_TABLE, e => {
        e.Property<string?>("lane").HasColumnName("lane");
        e.Property<int>("prio").HasColumnName("prio");
        e.Property<bool>("urgent").HasColumnName("urgent");
      });
      _mapRow<OrderModel>(modelBuilder, EXTRACTED_TABLE, e => {
        e.Property<int>("priority").HasColumnName("priority");
        e.Property<bool>("flagged").HasColumnName("flagged");
      });
    }

    private static void _mapRow<TModel>(
        ModelBuilder modelBuilder, string table,
        Action<Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<PerspectiveRow<TModel>>> physical)
        where TModel : class =>
      modelBuilder.Entity<PerspectiveRow<TModel>>(e => {
        e.ToTable(table);
        e.HasKey(x => x.Id);
        e.Property(x => x.Id).HasColumnName("id");
        e.Property(x => x.Data).HasColumnName("data").HasColumnType("jsonb");
        e.Property(x => x.Metadata).HasColumnName("metadata").HasColumnType("jsonb");
        e.Property(x => x.Scope).HasColumnName("scope").HasColumnType("jsonb");
        e.Property(x => x.CreatedAt).HasColumnName("created_at");
        e.Property(x => x.UpdatedAt).HasColumnName("updated_at");
        e.Property(x => x.Version).HasColumnName("version");
        physical(e);
      });
  }

  private sealed class SqlCapture(List<string> captured) : DbCommandInterceptor {
    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default) {
      captured.Add(command.CommandText);
      return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
        CancellationToken cancellationToken = default) {
      captured.Add(command.CommandText);
      return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
    }
  }
}
