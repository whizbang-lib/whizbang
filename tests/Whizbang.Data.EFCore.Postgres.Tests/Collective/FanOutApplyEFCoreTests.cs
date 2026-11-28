// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

#pragma warning disable CA1859 // handlers return the spec interface as a generated invoker expects

using System.Data.Common;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Serialization;
using Whizbang.Data.EFCore.Postgres.Functions;

namespace Whizbang.Data.EFCore.Postgres.Tests.Collective;

/// <summary>
/// #2620 on the EF Core driver: a foreign event a perspective fans out updates every row whose key holds the event's
/// stream id, in the event's tenant, in the collective machinery's one keyset-batched statement per batch, with its
/// progress record (#1969), so a redelivered event applies once and an interrupted apply resumes. A foreign delete marks
/// the rows and removes none. The row's <c>version</c>, which every applied batch bumps, counts how often a row received
/// the event.
/// </summary>
/// <tests>src/Whizbang.Core/Perspectives/FanOutScopeResolver.cs</tests>
/// <tests>src/Whizbang.Core/Perspectives/PerspectiveFanOutEntry.cs</tests>
/// <tests>src/Whizbang.Data.EFCore.Postgres/Collective/CollectiveEventApplier.cs</tests>
/// <docs>fundamentals/perspectives/multi-stream#fan-out-design-choices</docs>
[Category("Integration")]
[Category("CollectiveEvents")]
[Category("Shard3")]
public class FanOutApplyEFCoreTests : EFCoreTestBase {
  private const string TABLE = "wh_per_fan_out_order";
  private const string TENANT = "tenant-orders";
  private const string OTHER_TENANT = "tenant-other";

  private static readonly CollectiveApplyOptions _batchesOfTwo = new() { BatchSize = 2 };

  static FanOutApplyEFCoreTests() {
    // What the perspective runner's [ModuleInitializer] registers for the model's [PhysicalField] key.
    PerspectivePhysicalFieldRegistry.Register(typeof(OrderSummary), nameof(OrderSummary.CustomerId), "customer_id", FieldStorageMode.Extracted);
  }

  private NpgsqlDataSource? _jsonSource;
  private readonly List<string> _capturedSql = [];

  [Test]
  public async Task ARename_UpdatesTheCustomersOrders_AndLeavesAnotherCustomersAloneAsync() {
    // The issue's example: o-1 and o-2 belong to c-1, o-3 to c-2.
    var (c1, c2) = (Guid.NewGuid(), Guid.NewGuid());
    var o1 = await _orderAsync(c1, TENANT, "Old");
    var o2 = await _orderAsync(c1, TENANT, "Old");
    var o3 = await _orderAsync(c2, TENANT, "Other");

    var result = await _dispatchAsync(new CustomerRenamed("Ada"), c1, TENANT, Guid.NewGuid());

    await Assert.That(result.AffectedRowCount).IsEqualTo(2);
    await Assert.That(await _namesAsync()).IsEquivalentTo(new Dictionary<Guid, string> { [o1] = "Ada", [o2] = "Ada", [o3] = "Other" });
    await Assert.That(_capturedSql.Any(sql => sql.Contains("\"customer_id\" =", StringComparison.Ordinal))).IsTrue()
      .Because("the rows are found by the key's column, which its index serves, never by a scan of the documents");
  }

  [Test]
  public async Task ARename_InOneTenant_LeavesTheSameCustomerIdInAnotherTenantAloneAsync() {
    var customer = Guid.NewGuid();
    var mine = await _orderAsync(customer, TENANT, "Old");
    var theirs = await _orderAsync(customer, OTHER_TENANT, "Old");

    await _dispatchAsync(new CustomerRenamed("Ada"), customer, TENANT, Guid.NewGuid());

    var names = await _namesAsync();
    await Assert.That(names[mine]).IsEqualTo("Ada");
    await Assert.That(names[theirs]).IsEqualTo("Old").Because("a fan-out targets the rows of the event's own tenant only");
  }

  [Test]
  public async Task ARedeliveredRename_AppliesOnceAsync() {
    var customer = Guid.NewGuid();
    await _orderAsync(customer, TENANT, "Old");
    await _orderAsync(customer, TENANT, "Old");
    var eventId = Guid.NewGuid();

    await _dispatchAsync(new CustomerRenamed("Ada"), customer, TENANT, eventId);
    var again = await _dispatchAsync(new CustomerRenamed("Ada"), customer, TENANT, eventId);

    await Assert.That(again.AffectedRowCount).IsEqualTo(0).Because("the apply's progress record says it finished");
    await Assert.That(await _versionsAsync()).IsEquivalentTo([2, 2]).Because("each row received the rename once");
  }

  [Test]
  public async Task ARenameInterruptedAfterOneBatch_ResumesAfterIt_SoEveryRowHoldsItOnceAsync() {
    var customer = Guid.NewGuid();
    for (var i = 0; i < 5; i++) {
      await _orderAsync(customer, TENANT, "Old");
    }
    var eventId = Guid.NewGuid();

    await Assert.That(async () => await _dispatchAsync(new CustomerRenamed("Ada"), customer, TENANT, eventId,
        onBatchApplied: _ => throw new InvalidOperationException("the host went down after the first batch")))
      .Throws<InvalidOperationException>();
    var resumed = await _dispatchAsync(new CustomerRenamed("Ada"), customer, TENANT, eventId);

    await Assert.That(resumed.AffectedRowCount).IsEqualTo(3).Because("the retry applies the rows the first batch did not reach");
    await Assert.That(await _versionsAsync()).IsEquivalentTo([2, 2, 2, 2, 2]);
    await Assert.That((await _namesAsync()).Values.Distinct()).IsEquivalentTo(["Ada"]);
  }

  [Test]
  public async Task ADelete_MarksTheCustomersOrders_AndRemovesNoneAsync() {
    var customer = Guid.NewGuid();
    await _orderAsync(customer, TENANT, "Old");
    await _orderAsync(customer, TENANT, "Old");
    await _orderAsync(Guid.NewGuid(), TENANT, "Other");

    await _dispatchAsync(new CustomerDeleted(), customer, TENANT, Guid.NewGuid());

    await Assert.That(await _deletedFlagsAsync()).IsEquivalentTo([true, true, false])
      .Because("a foreign delete is a setter: the rows stay, marked, and a fan-out has no purge");
  }

  // ---------- the perspective, as a generated entry describes it ----------

  internal sealed class OrderSummary {
    public Guid CustomerId { get; set; }
    public string CustomerName { get; set; } = string.Empty;
    public bool CustomerDeleted { get; set; }
  }

  internal sealed record CustomerRenamed(string Name) : IEvent;

  internal sealed record CustomerDeleted : IEvent;

  internal sealed class OrderSummaryProjection {
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Minor Code Smell", "S2325:Methods and properties that don't access instance data should be static", Justification = "Invoked through the instance invoker the generator emits.")]
    public ICollectiveSpec<OrderSummary> Apply(CustomerRenamed e) => new Spec(s => s.SetProperty(o => o.CustomerName, e.Name));

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Sonar", "S1172:Unused method parameters should be removed", Justification = "A fan-out Apply takes its event.")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Minor Code Smell", "S2325:Methods and properties that don't access instance data should be static", Justification = "Invoked through the instance invoker the generator emits.")]
    public ICollectiveSpec<OrderSummary> Apply(CustomerDeleted e) => new Spec(s => s.SetProperty(o => o.CustomerDeleted, true));
  }

  private sealed record Spec(Expression<Action<ICollectiveSetters<OrderSummary>>> Setters) : ICollectiveSpec<OrderSummary>;

  private static readonly Expression<Func<OrderSummary, object?>> _byCustomer = o => o.CustomerId;

  private static readonly PerspectiveFanOutEntry _renamed = new(
    typeof(OrderSummaryProjection), typeof(OrderSummary), typeof(CustomerRenamed), nameof(OrderSummary.CustomerId), "customer_id",
    static (p, e) => ((OrderSummaryProjection)p).Apply((CustomerRenamed)e), _byCustomer, TABLE,
    static factory => factory.Create<OrderSummary>(TABLE));

  private static readonly PerspectiveFanOutEntry _deleted = new(
    typeof(OrderSummaryProjection), typeof(OrderSummary), typeof(CustomerDeleted), nameof(OrderSummary.CustomerId), "customer_id",
    static (p, e) => ((OrderSummaryProjection)p).Apply((CustomerDeleted)e), _byCustomer, TABLE,
    static factory => factory.Create<OrderSummary>(TABLE));

  // ---------- helpers ----------

  // The dispatcher as the EF Core collective registration builds it, with no executor registered by hand: the
  // registration makes the fan-out model's executor from the entry (#2620).
  private async Task<CollectiveDispatchResult> _dispatchAsync(
      IEvent evt, Guid customer, string tenant, Guid eventId, Func<CancellationToken, ValueTask>? onBatchApplied = null) {
    var services = new ServiceCollection()
      .AddSingleton(new OrderSummaryProjection())
      .AddSingleton(_batchesOfTwo)
      .AddSingleton<IPerspectiveFanOutRegistry>(new Listed([_renamed, _deleted]));
    services.AddCollectiveEventsEFCore<OrderDbContext>();
    await using var provider = services.BuildServiceProvider();
    var dispatcher = provider.GetRequiredService<ICollectiveDispatcher>();
    await using var ctx = _context();
    _capturedSql.Clear();
    return await dispatcher.DispatchAsync(PerspectiveFanOutEntry.CollectiveEventFor(evt, customer, tenant), eventId, ctx, onBatchApplied);
  }

  private sealed class Listed(IReadOnlyList<PerspectiveFanOutEntry> entries) : IPerspectiveFanOutRegistry {
    public IReadOnlyList<PerspectiveFanOutEntry> Entries => entries;
  }

  private async Task<Guid> _orderAsync(Guid customer, string tenant, string name) {
    await _ensureTableAsync();
    var id = Guid.NewGuid();
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    await using var insert = conn.CreateCommand();
    insert.CommandText = $"""
      INSERT INTO {TABLE} (id, data, metadata, scope, customer_id)
      VALUES (@id, jsonb_build_object('CustomerId', @c::text, 'CustomerName', @n, 'CustomerDeleted', false), jsonb_build_object(), @scope::jsonb, @c)
      """;
    insert.Parameters.AddWithValue("id", id);
    insert.Parameters.AddWithValue("c", customer);
    insert.Parameters.AddWithValue("n", name);
    insert.Parameters.AddWithValue("scope", $"{{\"t\": \"{tenant}\"}}");
    await insert.ExecuteNonQueryAsync();
    return id;
  }

  private bool _tableCreated;

  private async Task _ensureTableAsync() {
    if (_tableCreated) {
      return;
    }
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    await using var create = conn.CreateCommand();
    create.CommandText = $"""
      CREATE TABLE {TABLE} (
        id UUID PRIMARY KEY, data JSONB NOT NULL, metadata JSONB NOT NULL, scope JSONB NOT NULL,
        created_at TIMESTAMPTZ NOT NULL DEFAULT now(), updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
        version INTEGER NOT NULL DEFAULT 1, customer_id UUID);
      CREATE INDEX ix_{TABLE}_customer_id ON {TABLE} (customer_id);
      """;
    await create.ExecuteNonQueryAsync();
    _tableCreated = true;
  }

  private async Task<Dictionary<Guid, string>> _namesAsync() {
    var names = new Dictionary<Guid, string>();
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = $"SELECT id, data->>'CustomerName' FROM {TABLE}";
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync()) {
      names[reader.GetGuid(0)] = reader.GetString(1);
    }
    return names;
  }

  private async Task<int[]> _versionsAsync() {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = $"SELECT version FROM {TABLE} ORDER BY id";
    var versions = new List<int>();
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync()) {
      versions.Add(reader.GetInt32(0));
    }
    return [.. versions];
  }

  private async Task<bool[]> _deletedFlagsAsync() {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = $"SELECT (data->>'CustomerDeleted')::boolean FROM {TABLE} ORDER BY (data->>'CustomerDeleted')::boolean DESC";
    var flags = new List<bool>();
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync()) {
      flags.Add(reader.GetBoolean(0));
    }
    return [.. flags];
  }

  [After(Test)]
  public async Task DisposeJsonSourceAsync() {
    if (_jsonSource is not null) {
      await _jsonSource.DisposeAsync();
      _jsonSource = null;
    }
  }

  private OrderDbContext _context() {
    if (_jsonSource is null) {
      var builder = new NpgsqlDataSourceBuilder(ConnectionString);
      builder.ConfigureJsonOptions(JsonContextRegistry.CreateCombinedOptions());
      builder.EnableDynamicJson();
      _jsonSource = builder.Build();
    }
    var options = new DbContextOptionsBuilder<OrderDbContext>()
      .UseNpgsql(_jsonSource, npg => npg.UseWhizbangFunctions())
      .AddInterceptors(new SqlCapture(_capturedSql))
      .ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
      .Options;
    return new OrderDbContext(options);
  }

  private sealed class OrderDbContext(DbContextOptions<OrderDbContext> options) : DbContext(options) {
    protected override void OnModelCreating(ModelBuilder modelBuilder) {
      base.OnModelCreating(modelBuilder);
      modelBuilder.Entity<PerspectiveRow<OrderSummary>>(e => {
        e.ToTable(TABLE);
        e.HasKey(x => x.Id);
        e.Property(x => x.Id).HasColumnName("id");
        e.ComplexProperty(x => x.Data, d => d.ToJson("data"));
        e.Property(x => x.Metadata).HasColumnName("metadata").HasColumnType("jsonb");
        e.Property(x => x.Scope).HasColumnName("scope").HasColumnType("jsonb");
        e.Property(x => x.CreatedAt).HasColumnName("created_at");
        e.Property(x => x.UpdatedAt).HasColumnName("updated_at");
        e.Property(x => x.Version).HasColumnName("version");
        e.Property<Guid?>("customer_id").HasColumnName("customer_id");
      });
    }
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
