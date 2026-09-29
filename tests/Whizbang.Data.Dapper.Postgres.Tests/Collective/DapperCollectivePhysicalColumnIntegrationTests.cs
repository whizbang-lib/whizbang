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
    PerspectivePhysicalFieldRegistry.Register(typeof(OrderModel), nameof(OrderModel.Priority), "priority", FieldStorageMode.Extracted);
  }

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

  // ── Fixtures ──────────────────────────────────────────────────────────────────────────────────

  [PerspectiveStorage(FieldStorageMode.Split)]
  internal sealed class TicketModel {
    [PhysicalField] public string? Lane { get; set; }
    [PhysicalField(ColumnName = "prio")] public int Priority { get; set; }
    public string Title { get; set; } = "";
    public bool IsHot { get; set; }
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
    await conn.ExecuteAsync($"""
      CREATE TABLE {SPLIT_TABLE} (
        id uuid PRIMARY KEY, data jsonb NOT NULL, metadata jsonb, scope jsonb NOT NULL,
        created_at timestamptz NOT NULL DEFAULT now(), updated_at timestamptz NOT NULL DEFAULT now(),
        version bigint NOT NULL DEFAULT 1, lane text, prio integer NOT NULL DEFAULT 0);
      CREATE TABLE {EXTRACTED_TABLE} (
        id uuid PRIMARY KEY, data jsonb NOT NULL, metadata jsonb, scope jsonb NOT NULL,
        created_at timestamptz NOT NULL DEFAULT now(), updated_at timestamptz NOT NULL DEFAULT now(),
        version bigint NOT NULL DEFAULT 1, priority integer NOT NULL DEFAULT 0);
      """);
  }

  // Written the way the generated runner writes a Split model: values to the columns, defaults in the document.
  private async Task _writeTicketAsync(Guid id, string tenant, string? lane, int priority, string title, bool isHot = false) {
    using var conn = await ConnectionFactory.CreateConnectionAsync();
    var data = System.Text.Json.JsonSerializer.Serialize(new TicketModel { Title = title, IsHot = isHot });
    await conn.ExecuteAsync($"""
      INSERT INTO {SPLIT_TABLE} (id, data, scope, lane, prio) VALUES (@id, @data::jsonb, @scope::jsonb, @lane, @priority)
      ON CONFLICT (id) DO UPDATE SET data = EXCLUDED.data, lane = EXCLUDED.lane, prio = EXCLUDED.prio
      """, new { id, data, scope = $"{{\"t\": \"{tenant}\"}}", lane, priority });
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
