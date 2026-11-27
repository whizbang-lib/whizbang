// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

#pragma warning disable CA1859, CA1861 // handlers return the spec interface as a generated invoker expects; each test states its expected rows inline

using System.Linq.Expressions;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Perspectives;
using Whizbang.Data.Dapper.Postgres.Collective;

namespace Whizbang.Data.Dapper.Postgres.Tests.Collective;

/// <summary>
/// #2620 on the Dapper driver: a foreign event a perspective fans out updates every row whose key holds the event's
/// stream id, in the event's tenant, through the collective machinery's keyset-batched statement and its progress
/// record (#1969), so a redelivered event applies once and an interrupted apply resumes. A foreign delete marks the rows
/// and removes none. The row's <c>version</c>, which every applied batch bumps, counts how often a row received it.
/// </summary>
/// <tests>src/Whizbang.Core/Perspectives/FanOutScopeResolver.cs</tests>
/// <tests>src/Whizbang.Data.Dapper.Postgres/Collective/DapperCollectiveEventApplier.cs</tests>
[NotInParallel("PostgreSQL")]
public class DapperFanOutApplyTests : PostgresTestBase {
  private const string TABLE = "wh_per_fan_out_dapper_order";
  private const string TENANT = "tenant-orders";
  private const string OTHER_TENANT = "tenant-other";
  private static readonly IReadOnlyDictionary<Type, string> _noSiblings = new Dictionary<Type, string>();
  private static readonly CollectiveApplyOptions _batchesOfTwo = new() { BatchSize = 2 };

  static DapperFanOutApplyTests() {
    // What the perspective runner's [ModuleInitializer] registers for the model's [PhysicalField] key.
    PerspectivePhysicalFieldRegistry.Register(typeof(OrderSummary), nameof(OrderSummary.CustomerId), "customer_id", FieldStorageMode.Extracted);
  }

  [Test]
  public async Task ARename_UpdatesTheCustomersOrders_AndLeavesAnotherCustomersAloneAsync() {
    var (c1, c2) = (Guid.NewGuid(), Guid.NewGuid());
    await _createTableAsync();
    var o1 = await _orderAsync(c1, TENANT, "Old");
    var o2 = await _orderAsync(c1, TENANT, "Old");
    var o3 = await _orderAsync(c2, TENANT, "Other");

    var result = await _dispatchAsync(new CustomerRenamed("Ada"), c1, TENANT, Guid.NewGuid());

    await Assert.That(result.AffectedRowCount).IsEqualTo(2);
    await Assert.That(await _namesAsync()).IsEquivalentTo(new Dictionary<Guid, string> { [o1] = "Ada", [o2] = "Ada", [o3] = "Other" });
  }

  [Test]
  public async Task ARename_InOneTenant_LeavesTheSameCustomerIdInAnotherTenantAloneAsync() {
    var customer = Guid.NewGuid();
    await _createTableAsync();
    var mine = await _orderAsync(customer, TENANT, "Old");
    var theirs = await _orderAsync(customer, OTHER_TENANT, "Old");

    await _dispatchAsync(new CustomerRenamed("Ada"), customer, TENANT, Guid.NewGuid());

    var names = await _namesAsync();
    await Assert.That(names[mine]).IsEqualTo("Ada");
    await Assert.That(names[theirs]).IsEqualTo("Old");
  }

  [Test]
  public async Task ARedeliveredRename_AppliesOnceAsync() {
    var customer = Guid.NewGuid();
    await _createTableAsync();
    await _orderAsync(customer, TENANT, "Old");
    await _orderAsync(customer, TENANT, "Old");
    var eventId = Guid.NewGuid();

    await _dispatchAsync(new CustomerRenamed("Ada"), customer, TENANT, eventId);
    var again = await _dispatchAsync(new CustomerRenamed("Ada"), customer, TENANT, eventId);

    await Assert.That(again.AffectedRowCount).IsEqualTo(0);
    await Assert.That(await _versionsAsync()).IsEquivalentTo(new long[] { 2, 2 });
  }

  [Test]
  public async Task ARenameInterruptedAfterOneBatch_ResumesAfterIt_SoEveryRowHoldsItOnceAsync() {
    var customer = Guid.NewGuid();
    await _createTableAsync();
    for (var i = 0; i < 5; i++) {
      await _orderAsync(customer, TENANT, "Old");
    }
    var eventId = Guid.NewGuid();

    await Assert.That(async () => await _dispatchAsync(new CustomerRenamed("Ada"), customer, TENANT, eventId,
        onBatchApplied: _ => throw new InvalidOperationException("the host went down after the first batch")))
      .Throws<InvalidOperationException>();
    var resumed = await _dispatchAsync(new CustomerRenamed("Ada"), customer, TENANT, eventId);

    await Assert.That(resumed.AffectedRowCount).IsEqualTo(3);
    await Assert.That(await _versionsAsync()).IsEquivalentTo(new long[] { 2, 2, 2, 2, 2 });
  }

  [Test]
  public async Task ADelete_MarksTheCustomersOrders_AndRemovesNoneAsync() {
    var customer = Guid.NewGuid();
    await _createTableAsync();
    await _orderAsync(customer, TENANT, "Old");
    await _orderAsync(customer, TENANT, "Old");
    await _orderAsync(Guid.NewGuid(), TENANT, "Other");

    await _dispatchAsync(new CustomerDeleted(), customer, TENANT, Guid.NewGuid());

    using var conn = await ConnectionFactory.CreateConnectionAsync();
    var flags = (await conn.QueryAsync<bool>($"SELECT (data->>'CustomerDeleted')::boolean FROM {TABLE}")).OrderDescending().ToArray();
    await Assert.That(flags).IsEquivalentTo(new[] { true, true, false });
  }

  // ---------- the perspective, as a generated entry describes it ----------

  internal sealed class OrderSummary {
    public Guid CustomerId { get; set; }
    public string CustomerName { get; set; } = "";
    public bool CustomerDeleted { get; set; }
  }

  private sealed record CustomerRenamed(string Name) : IEvent;

  private sealed record CustomerDeleted : IEvent;

  private sealed class OrderSummaryProjection {
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
    static (p, e) => ((OrderSummaryProjection)p).Apply((CustomerRenamed)e), _byCustomer);

  private static readonly PerspectiveFanOutEntry _deleted = new(
    typeof(OrderSummaryProjection), typeof(OrderSummary), typeof(CustomerDeleted), nameof(OrderSummary.CustomerId), "customer_id",
    static (p, e) => ((OrderSummaryProjection)p).Apply((CustomerDeleted)e), _byCustomer);

  // ---------- helpers ----------

  private Task<CollectiveDispatchResult> _dispatchAsync(
      IEvent evt, Guid customer, string tenant, Guid eventId, Func<CancellationToken, ValueTask>? onBatchApplied = null) =>
    new CollectiveDispatcher(
      new ServiceCollection().AddSingleton(new OrderSummaryProjection()).BuildServiceProvider(),
      [_renamed.ToCollectiveApplyEntry(), _deleted.ToCollectiveApplyEntry()],
      [new FanOutScopeResolver([_renamed, _deleted]), new TenantCollectiveScopeResolver()],
      [new DapperCollectiveEventExecutor<OrderSummary>(TABLE, _noSiblings, _batchesOfTwo)])
      .DispatchAsync(PerspectiveFanOutEntry.CollectiveEventFor(evt, customer, tenant), eventId, ConnectionFactory, onBatchApplied);

  private async Task _createTableAsync() {
    using var conn = await ConnectionFactory.CreateConnectionAsync();
    await conn.ExecuteAsync($@"
      CREATE TABLE IF NOT EXISTS {TABLE} (
        id uuid PRIMARY KEY, data jsonb NOT NULL, metadata jsonb, scope jsonb NOT NULL,
        created_at timestamptz NOT NULL DEFAULT now(), updated_at timestamptz NOT NULL DEFAULT now(),
        version bigint NOT NULL DEFAULT 1, customer_id uuid);
      CREATE INDEX IF NOT EXISTS ix_{TABLE}_customer_id ON {TABLE} (customer_id);
      TRUNCATE {TABLE};");
  }

  private async Task<Guid> _orderAsync(Guid customer, string tenant, string name) {
    var id = Guid.NewGuid();
    using var conn = await ConnectionFactory.CreateConnectionAsync();
    await conn.ExecuteAsync(
      $"INSERT INTO {TABLE} (id, data, scope, customer_id) VALUES (@id, jsonb_build_object('CustomerId', @c::text, "
      + "'CustomerName', @name, 'CustomerDeleted', false), @scope::jsonb, @c)",
      new { id, c = customer, name, scope = $"{{\"t\": \"{tenant}\"}}" });
    return id;
  }

  private async Task<Dictionary<Guid, string>> _namesAsync() {
    using var conn = await ConnectionFactory.CreateConnectionAsync();
    return (await conn.QueryAsync<(Guid Id, string Name)>($"SELECT id, data->>'CustomerName' FROM {TABLE}"))
      .ToDictionary(r => r.Id, r => r.Name);
  }

  private async Task<long[]> _versionsAsync() {
    using var conn = await ConnectionFactory.CreateConnectionAsync();
    return [.. await conn.QueryAsync<long>($"SELECT version FROM {TABLE} ORDER BY id")];
  }
}
