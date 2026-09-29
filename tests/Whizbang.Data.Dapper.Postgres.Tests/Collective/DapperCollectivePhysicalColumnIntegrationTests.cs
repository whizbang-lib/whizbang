#pragma warning disable CA1707

using System.Linq.Expressions;
using Dapper;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.Messaging;
using Whizbang.Core.Perspectives;
using Whizbang.Data.Dapper.Postgres.Collective;
using Whizbang.Data.Postgres.Collective;

namespace Whizbang.Data.Dapper.Postgres.Tests.Collective;

/// <summary>
/// The Dapper twin of <c>CollectivePhysicalColumnIntegrationTests</c>, against a real Postgres: a collective setter
/// on a <c>[PhysicalField]</c> property writes the column in the same UPDATE, a spec that touches only physical-only
/// fields leaves <c>data</c> byte-identical, the document path is written too when the field is kept in both places,
/// a condition on a physical property filters on the column, and the in-memory replay yields the same row.
/// </summary>
/// <docs>fundamentals/messaging/collective-events</docs>
/// <tests>src/Whizbang.Data.Dapper.Postgres/Collective/DapperCollectiveSpecCompiler.cs</tests>
[NotInParallel("PostgreSQL")]
public class DapperCollectivePhysicalColumnIntegrationTests : PostgresTestBase {
  private const string SPLIT_TABLE = "wh_per_dapper_physical_ticket";
  private const string EXTRACTED_TABLE = "wh_per_dapper_physical_order";
  private static readonly IReadOnlyDictionary<Type, string> _noSiblings = new Dictionary<Type, string>();

  static DapperCollectivePhysicalColumnIntegrationTests() {
    // What the perspective runner's [ModuleInitializer] registers for these models.
    PerspectivePhysicalFieldRegistry.Register(typeof(TicketModel), nameof(TicketModel.Lane), "lane", FieldStorageMode.Split);
    PerspectivePhysicalFieldRegistry.Register(typeof(TicketModel), nameof(TicketModel.Priority), "prio", FieldStorageMode.Split);
    PerspectivePhysicalFieldRegistry.Register(typeof(TicketModel), nameof(TicketModel.Kind), "kind", FieldStorageMode.Split, scalarType: typeof(int));
    PerspectivePhysicalFieldRegistry.Register(typeof(TicketModel), nameof(TicketModel.Embedding), "embedding", FieldStorageMode.Split, isVector: true);
    PerspectivePhysicalFieldRegistry.Register(typeof(TicketModel), nameof(TicketModel.Tags), "tags", FieldStorageMode.Split, columnType: "jsonb");
    PerspectivePhysicalFieldRegistry.Register(typeof(OrderModel), nameof(OrderModel.Priority), "priority", FieldStorageMode.Extracted);
  }

  private static readonly System.Text.Json.JsonSerializerOptions _storeJson = new() {
    TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver(),
  };

  [Test]
  public async Task Apply_PhysicalOnlySetter_UpdatesTheColumn_AndLeavesDataByteIdenticalAsync() {
    await _createTablesAsync();
    var id = Guid.NewGuid();
    var other = Guid.NewGuid();
    await _writeTicketAsync(id, "t-A", "cold", 1, "First");
    await _writeTicketAsync(other, "t-B", "cold", 1, "Other");
    var before = await _documentAsync(SPLIT_TABLE, id);

    var affected = await _applyAsync(new TicketSpec(s => s.SetProperty(t => t.Lane, "hot").SetProperty(t => t.Priority, 9)), "t-A");

    await Assert.That(affected).IsEqualTo(1);
    await Assert.That(await _ticketColumnsAsync(id)).IsEqualTo(("hot", 9));
    var after = await _documentAsync(SPLIT_TABLE, id);
    await Assert.That(after).IsEqualTo(before)
      .Because("A Split field lives only in its column: text, stored size and hash of the document must not change.");
    await Assert.That(await _ticketColumnsAsync(other)).IsEqualTo(("cold", 1))
      .Because("The tenant scope still binds.");
  }

  [Test]
  public async Task Apply_SetterKeptInBothPlaces_UpdatesTheColumnAndTheDocumentAsync() {
    await _createTablesAsync();
    var id = Guid.NewGuid();
    await _writeOrderAsync(id, "t-A", 1, "Open");

    await _applyAsync(new OrderSpec(s => s.SetProperty(o => o.Priority, 5)), "t-A");

    using var conn = await ConnectionFactory.CreateConnectionAsync();
    var (column, document) = await conn.QuerySingleAsync<(int, string)>(
      $"SELECT priority, data->>'Priority' FROM {EXTRACTED_TABLE} WHERE id = @id", new { id });
    await Assert.That(column).IsEqualTo(5);
    await Assert.That(document).IsEqualTo("5");
  }

  [Test]
  public async Task Apply_MixedSetters_UpdateTheColumnAndTheDocument_InOneStatementAsync() {
    await _createTablesAsync();
    var id = Guid.NewGuid();
    await _writeTicketAsync(id, "t-A", "cold", 1, "First");

    await _applyAsync(new TicketSpec(s => s.SetProperty(t => t.Lane, "hot").SetProperty(t => t.Title, "Moved")), "t-A");

    using var conn = await ConnectionFactory.CreateConnectionAsync();
    var (lane, title) = await conn.QuerySingleAsync<(string, string)>(
      $"SELECT lane, data->>'Title' FROM {SPLIT_TABLE} WHERE id = @id", new { id });
    await Assert.That(lane).IsEqualTo("hot");
    await Assert.That(title).IsEqualTo("Moved");
  }

  [Test]
  public async Task Apply_WhereOnAPhysicalField_FiltersOnTheColumnAsync() {
    await _createTablesAsync();
    var hot = Guid.NewGuid();
    var cold = Guid.NewGuid();
    await _writeTicketAsync(hot, "t-A", "hot", 1, "a");
    await _writeTicketAsync(cold, "t-A", "cold", 1, "b");

    var affected = await _applyAsync(new TicketSpec(s => s.SetProperty(t => t.Title, "Escalated"), r => r.Data.Lane == "hot"), "t-A");

    await Assert.That(affected).IsEqualTo(1);
    using var conn = await ConnectionFactory.CreateConnectionAsync();
    await Assert.That(await conn.QuerySingleAsync<string>($"SELECT data->>'Title' FROM {SPLIT_TABLE} WHERE id = @id", new { id = cold }))
      .IsEqualTo("b");
  }

  [Test]
  public async Task Replay_MatchesLive_ForColumnAndDocumentAsync() {
    await _createTablesAsync();
    var live = Guid.NewGuid();
    var replayed = Guid.NewGuid();
    await _writeTicketAsync(live, "t-A", "hot", 2, "First");
    await _writeTicketAsync(replayed, "t-B", "hot", 2, "First");
    var spec = new TicketSpec(
      s => s.SetProperty(t => t.Priority, 8).SetProperty(t => t.Title, "Moved").SetProperty(t => t.IsHot, t => t.Lane == "hot"),
      r => r.Data.Lane == "hot");

    await _applyAsync(spec, "t-A");
    var model = (TicketModel)new CollectiveInMemoryExecutor<TicketModel>().ApplyToRow(
      spec, new TicketModel { Lane = "hot", Priority = 2, Title = "First" }, replayed);
    await _writeTicketAsync(replayed, "t-B", model.Lane, model.Priority, model.Title, model.IsHot);

    await Assert.That(await _ticketColumnsAsync(replayed)).IsEqualTo(await _ticketColumnsAsync(live));
    using var conn = await ConnectionFactory.CreateConnectionAsync();
    var same = await conn.QuerySingleAsync<bool>(
      $"SELECT (SELECT data FROM {SPLIT_TABLE} WHERE id = @live) = (SELECT data FROM {SPLIT_TABLE} WHERE id = @replayed)",
      new { live, replayed });
    await Assert.That(same).IsTrue();
  }

  [Test]
  public async Task Apply_EnumSetterAndPredicate_UseTheUnderlyingNumberAsync() {
    await _createTablesAsync();
    var bug = Guid.NewGuid();
    var task = Guid.NewGuid();
    await _runnerWriteAsync(bug, "t-A", new TicketModel { Kind = TicketKind.Bug, Title = "a" });
    await _runnerWriteAsync(task, "t-A", new TicketModel { Kind = TicketKind.Task, Title = "b" });

    var affected = await _applyAsync(
      new TicketSpec(s => s.SetProperty(t => t.Kind, TicketKind.Task).SetProperty(t => t.Title, "Demoted"), r => r.Data.Kind == TicketKind.Bug), "t-A");

    await Assert.That(affected).IsEqualTo(1);
    using var conn = await ConnectionFactory.CreateConnectionAsync();
    var (kind, title) = await conn.QuerySingleAsync<(int, string)>($"SELECT kind, data->>'Title' FROM {SPLIT_TABLE} WHERE id = @id", new { id = bug });
    await Assert.That(kind).IsEqualTo(0);
    await Assert.That(title).IsEqualTo("Demoted");
  }

  [Test]
  public async Task Store_EnumPhysicalField_IsWrittenAsItsNumberAsync() {
    await _createTablesAsync();
    var id = Guid.NewGuid();

    await _runnerWriteAsync(id, "t-A", new TicketModel { Kind = TicketKind.Bug });

    using var conn = await ConnectionFactory.CreateConnectionAsync();
    await Assert.That(await conn.QuerySingleAsync<int>($"SELECT kind FROM {SPLIT_TABLE} WHERE id = @id", new { id })).IsEqualTo(1)
      .Because("The per-event write stores an enum column as the same number the collective binds.");
  }

  [Test]
  public async Task Apply_VectorSetter_WritesTheVectorColumnAsync() {
    await _createTablesAsync();
    var id = Guid.NewGuid();
    await _runnerWriteAsync(id, "t-A", new TicketModel { Embedding = [0f, 0f, 0f] });
    var embedding = new[] { 1f, 2.5f, -3f };

    await _applyAsync(new TicketSpec(s => s.SetProperty(t => t.Embedding, embedding)), "t-A");

    using var conn = await ConnectionFactory.CreateConnectionAsync();
    await Assert.That(await conn.QuerySingleAsync<string>($"SELECT embedding::text FROM {SPLIT_TABLE} WHERE id = @id", new { id }))
      .IsEqualTo("[1,2.5,-3]");
  }

  [Test]
  public async Task Apply_UpsertElementOnAPhysicalJsonbArray_UpsertsInTheColumnAsync() {
    await _createTablesAsync();
    var id = Guid.NewGuid();
    await _runnerWriteAsync(id, "t-A", new TicketModel {
      Tags = [new TicketTag { Key = "a", Label = "old" }, new TicketTag { Key = "b", Label = "keep" }],
    });
    var before = await _documentAsync(SPLIT_TABLE, id);
    var replacement = new TicketTag { Key = "a", Label = "new" };

    await _applyAsync(new TicketSpec(s => s.UpsertElement(t => t.Tags, x => x.Key, replacement)), "t-A");

    using var conn = await ConnectionFactory.CreateConnectionAsync();
    var labels = await conn.QuerySingleAsync<string>(
      $"SELECT jsonb_path_query_array(tags, '$[*].Label')::text FROM {SPLIT_TABLE} WHERE id = @id", new { id });
    await Assert.That(labels).IsEqualTo("[\"new\", \"keep\"]");
    await Assert.That(await _documentAsync(SPLIT_TABLE, id)).IsEqualTo(before);
  }

  [Test]
  public async Task Replay_MatchesLive_ForEnumVectorAndKeyedArrayAsync() {
    await _createTablesAsync();
    var live = Guid.NewGuid();
    var replayed = Guid.NewGuid();
    TicketModel PreState() => new() {
      Kind = TicketKind.Task,
      Embedding = [0f, 0f, 0f],
      Tags = [new TicketTag { Key = "a", Label = "old" }],
      Title = "t",
    };
    await _runnerWriteAsync(live, "t-A", PreState());
    await _runnerWriteAsync(replayed, "t-B", PreState());
    var embedding = new[] { 0.5f, 1f, 1.5f };
    var tag = new TicketTag { Key = "a", Label = "new" };
    var spec = new TicketSpec(
      s => s.SetProperty(t => t.Kind, TicketKind.Bug).SetProperty(t => t.Embedding, embedding)
        .UpsertElement(t => t.Tags, x => x.Key, tag),
      r => r.Data.Kind == TicketKind.Task);

    await _applyAsync(spec, "t-A");
    var model = (TicketModel)new CollectiveInMemoryExecutor<TicketModel>().ApplyToRow(spec, PreState(), replayed);
    await _runnerWriteAsync(replayed, "t-B", model);

    using var conn = await ConnectionFactory.CreateConnectionAsync();
    var same = await conn.QuerySingleAsync<bool>($"""
      SELECT (SELECT (kind, embedding::text, tags, data) FROM {SPLIT_TABLE} WHERE id = @live)
           = (SELECT (kind, embedding::text, tags, data) FROM {SPLIT_TABLE} WHERE id = @replayed)
      """, new { live, replayed });
    await Assert.That(same).IsTrue();
  }

  // ── Fixtures ──────────────────────────────────────────────────────────────────────────────────

  [PerspectiveStorage(FieldStorageMode.Split)]
  internal sealed class TicketModel {
    [PhysicalField] public string? Lane { get; set; }
    [PhysicalField(ColumnName = "prio")] public int Priority { get; set; }
    [PhysicalField] public TicketKind Kind { get; set; }
    // A [VectorField] in the real model; the attribute is left off here because it requires the pgvector EF Core
    // package this Dapper project does not reference. The registration above marks it a vector exactly as the
    // runner would.
    public float[]? Embedding { get; set; }
    [PhysicalField(ColumnType = "jsonb")] public List<TicketTag>? Tags { get; set; }
    public string Title { get; set; } = "";
    public bool IsHot { get; set; }
  }

  internal enum TicketKind { Task, Bug }

  internal sealed class TicketTag {
    public string Key { get; set; } = "";
    public string Label { get; set; } = "";
  }

  [PerspectiveStorage(FieldStorageMode.Extracted)]
  internal sealed class OrderModel {
    [PhysicalField] public int Priority { get; set; }
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

  private Task<int> _applyAsync<TModel>(ICollectiveSpec<TModel> spec, string tenant) where TModel : class {
    var entry = new CollectiveApplyEntry(
      typeof(TModel), typeof(PhysicalCollectiveEvent), typeof(SpecHandler), "Apply",
      CollectiveScopeHandling.Framework, CollectiveSpecKind.Linq, (_, _, _) => spec);
    var table = typeof(TModel) == typeof(TicketModel) ? SPLIT_TABLE : EXTRACTED_TABLE;
    return DapperCollectiveEventApplier<TModel>.ApplyAsync(
      entry, new SpecHandler(), new PhysicalCollectiveEvent { Scope = new TenantCollectiveScope(tenant) },
      new TenantCollectiveScopeResolver(), ConnectionFactory, table, _noSiblings, CollectiveApplyOptions.Default);
  }

  private async Task _createTablesAsync() {
    using var conn = await ConnectionFactory.CreateConnectionAsync();
    await conn.ExecuteAsync("CREATE EXTENSION IF NOT EXISTS vector");
    await conn.ExecuteAsync($"""
      CREATE TABLE {SPLIT_TABLE} (
        id uuid PRIMARY KEY, data jsonb NOT NULL, metadata jsonb, scope jsonb NOT NULL,
        created_at timestamptz NOT NULL DEFAULT now(), updated_at timestamptz NOT NULL DEFAULT now(),
        version bigint NOT NULL DEFAULT 1, lane text, prio integer NOT NULL DEFAULT 0,
        kind integer NOT NULL DEFAULT 0, embedding vector(3), tags jsonb);
      CREATE TABLE {EXTRACTED_TABLE} (
        id uuid PRIMARY KEY, data jsonb NOT NULL, metadata jsonb, scope jsonb NOT NULL,
        created_at timestamptz NOT NULL DEFAULT now(), updated_at timestamptz NOT NULL DEFAULT now(),
        version bigint NOT NULL DEFAULT 1, priority integer NOT NULL DEFAULT 0);
      """);
  }

  private Task _writeTicketAsync(Guid id, string tenant, string? lane, int priority, string title, bool isHot = false) =>
    _runnerWriteAsync(id, tenant, new TicketModel { Lane = lane, Priority = priority, Title = title, IsHot = isHot });

  // What the generated runner does for a Split model, through the real Dapper store: the physical values go to
  // their columns (the vector as the float array, which pgvector assigns to the column) and the document holds
  // the defaults in their place.
  private async Task _runnerWriteAsync(Guid id, string tenant, TicketModel model) {
    var physicalFieldValues = new Dictionary<string, object?> {
      { "lane", model.Lane }, { "prio", model.Priority }, { "kind", model.Kind }, { "embedding", model.Embedding }, { "tags", model.Tags },
    };
    var document = new TicketModel {
      Title = model.Title,
      IsHot = model.IsHot,
      Embedding = [],
    };
    var store = new DapperPostgresPerspectiveStore<TicketModel>(ConnectionString, SPLIT_TABLE, _storeJson);
    await store.UpsertWithPhysicalFieldsAsync(id, document, physicalFieldValues, new PerspectiveScope { TenantId = tenant });
  }

  private async Task _writeOrderAsync(Guid id, string tenant, int priority, string status) {
    using var conn = await ConnectionFactory.CreateConnectionAsync();
    var data = System.Text.Json.JsonSerializer.Serialize(new OrderModel { Priority = priority, Status = status });
    await conn.ExecuteAsync(
      $"INSERT INTO {EXTRACTED_TABLE} (id, data, scope, priority) VALUES (@id, @data::jsonb, @scope::jsonb, @priority)",
      new { id, data, scope = $"{{\"t\": \"{tenant}\"}}", priority });
  }

  private async Task<(string? Lane, int Priority)> _ticketColumnsAsync(Guid id) {
    using var conn = await ConnectionFactory.CreateConnectionAsync();
    return await conn.QuerySingleAsync<(string?, int)>($"SELECT lane, prio FROM {SPLIT_TABLE} WHERE id = @id", new { id });
  }

  private async Task<(string Text, int Size, string Hash)> _documentAsync(string table, Guid id) {
    using var conn = await ConnectionFactory.CreateConnectionAsync();
    return await conn.QuerySingleAsync<(string, int, string)>(
      $"SELECT data::text, pg_column_size(data), md5(data::text) FROM {table} WHERE id = @id", new { id });
  }
}
