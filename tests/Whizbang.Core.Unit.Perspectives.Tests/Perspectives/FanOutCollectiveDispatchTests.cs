// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Linq.Expressions;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.Messaging;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Security;

namespace Whizbang.Core.Tests.Perspectives;

/// <summary>
/// #2620: a foreign event a perspective fans out is applied by the collective machinery as it stands. The sink wraps it
/// in a <see cref="FanOutCollectiveEvent"/> whose scope is the event's stream and tenant; each fan-out becomes a
/// <see cref="CollectiveApplyEntry"/> that applies only to that wrapper; and <see cref="FanOutScopeResolver"/> turns
/// the scope into the filter <c>key = stream AND tenant = tenant</c>.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Perspectives/FanOutCollectiveEvent.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Perspectives/FanOutScopeResolver.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Perspectives/PerspectiveFanOutEntry.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Perspectives/CollectiveApplyEntry.cs</code-under-test>
/// <docs>fundamentals/perspectives/multi-stream#fan-out-design-choices</docs>
[Category("Unit")]
[Category("CollectiveEvents")]
public class FanOutCollectiveDispatchTests {
  private const string TENANT = "tenant-1";

  [Test]
  public async Task ToCollectiveApplyEntry_KeepsTheFanOutsIdentity_SoItsProgressIsItsOwnAsync() {
    var fanOut = _renamedFanOut();

    var entry = fanOut.ToCollectiveApplyEntry();

    await Assert.That(entry.EntryKey).IsEqualTo(fanOut.EntryKey)
      .Because("the apply's progress record (#1969) is kept under the entry key, one per perspective, event and model");
    await Assert.That(entry.ModelType).IsEqualTo(typeof(OrderSummary));
    await Assert.That(entry.EventType).IsEqualTo(typeof(CustomerRenamed));
    await Assert.That(entry.HandlerType).IsEqualTo(typeof(OrderSummaryProjection));
    await Assert.That(entry.ScopeHandling).IsEqualTo(CollectiveScopeHandling.Framework)
      .Because("the scope filter, key and tenant, always binds; a fan-out Apply's own Where can only narrow it");
    await Assert.That(entry.FanOut).IsSameReferenceAs(fanOut);
  }

  [Test]
  public async Task ToCollectiveApplyEntry_TheInvoker_CallsApplyWithTheForeignEventAsync() {
    var entry = _renamedFanOut().ToCollectiveApplyEntry();

    var spec = entry.Invoker(new OrderSummaryProjection(), _wrap(new CustomerRenamed("Ada"), Guid.NewGuid()), null!);

    await Assert.That(spec).IsTypeOf<NameSpec>();
    await Assert.That(((NameSpec)spec).Name).IsEqualTo("Ada");
  }

  [Test]
  public async Task AppliesTo_AFanOutEntry_AppliesOnlyToTheWrapperOfItsOwnEventTypeAsync() {
    var entry = _renamedFanOut().ToCollectiveApplyEntry();

    await Assert.That(entry.AppliesTo(_wrap(new CustomerRenamed("Ada"), Guid.NewGuid()))).IsTrue();
    await Assert.That(entry.AppliesTo(_wrap(new CustomerMoved(), Guid.NewGuid()))).IsFalse();
    await Assert.That(entry.AppliesTo(new RenameCollective(new TenantScope(TENANT)))).IsFalse()
      .Because("a fan-out never applies to a collective, even one whose type it shares");
  }

  [Test]
  public async Task AppliesTo_ACollectiveEntry_NeverAppliesToAFanOutWrapperAsync() {
    var collective = new CollectiveApplyEntry(typeof(OrderSummary), typeof(RenameCollective), typeof(OrderSummaryProjection),
      "Apply", CollectiveScopeHandling.Framework, CollectiveSpecKind.Linq, static (_, _, _) => new object());

    await Assert.That(collective.AppliesTo(new RenameCollective(new TenantScope(TENANT)))).IsTrue();
    await Assert.That(collective.AppliesTo(new FanOutCollectiveEvent(new RenameCollective(new TenantScope(TENANT)), Guid.NewGuid(), TENANT)))
      .IsFalse().Because("the wrapper carries a fan-out; the collective's own handler runs from the collective's own row");
  }

  [Test]
  public async Task FanOutCollectiveEvent_ItsScope_IsTheStreamAndTenant_IdentifiedByBothAsync() {
    var stream = Guid.NewGuid();
    var evt = _wrap(new CustomerRenamed("Ada"), stream);

    var scope = (FanOutScope)evt.Scope;

    await Assert.That(scope.ScopeKind).IsEqualTo(FanOutScope.KIND);
    await Assert.That(scope.StreamId).IsEqualTo(stream);
    await Assert.That(scope.TenantId).IsEqualTo(TENANT);
    await Assert.That(scope.EventType).IsEqualTo(typeof(CustomerRenamed));
    await Assert.That(scope.ScopeIdentity).IsNotEqualTo(_wrap(new CustomerRenamed("Ada"), Guid.NewGuid()).Scope.ScopeIdentity)
      .Because("the apply lock is per table and scope identity, so two keys never wait for each other");
    await Assert.That(scope.ScopeIdentity)
      .IsNotEqualTo(new FanOutCollectiveEvent(new CustomerRenamed("Ada"), stream, "tenant-2").Scope.ScopeIdentity);
    await Assert.That(evt.Event).IsTypeOf<CustomerRenamed>();
  }

  [Test]
  public async Task FanOutCollectiveEvent_ANullEventOrTenant_ThrowsAsync() {
    await Assert.That(() => new FanOutCollectiveEvent(null!, Guid.NewGuid(), TENANT)).Throws<ArgumentNullException>();
    await Assert.That(() => new FanOutCollectiveEvent(new CustomerRenamed("Ada"), Guid.NewGuid(), null!)).Throws<ArgumentNullException>();
  }

  [Test]
  public async Task ScopeFilter_MatchesTheRowsWhoseKeyIsTheStream_InTheEventsTenantOnlyAsync() {
    var customer = Guid.NewGuid();
    var resolver = new FanOutScopeResolver([_renamedFanOut()]);

    var filter = resolver.ScopeFilter<OrderSummary>(_wrap(new CustomerRenamed("Ada"), customer).Scope).Compile();

    await Assert.That(filter(_row(customer, TENANT))).IsTrue();
    await Assert.That(filter(_row(Guid.NewGuid(), TENANT))).IsFalse().Because("another customer's order");
    await Assert.That(filter(_row(customer, "tenant-2"))).IsFalse().Because("the same customer id in another tenant");
  }

  [Test]
  public async Task ScopeFilter_ReadsTheKeyTheFanOutForThatEventDeclaresAsync() {
    // One model, two foreign events, two keys: each event targets rows by its own.
    var region = Guid.NewGuid();
    var resolver = new FanOutScopeResolver([_renamedFanOut(), _movedFanOut()]);

    var filter = resolver.ScopeFilter<OrderSummary>(_wrap(new CustomerMoved(), region).Scope).Compile();

    await Assert.That(filter(_row(Guid.NewGuid(), TENANT, region))).IsTrue();
    await Assert.That(filter(_row(region, TENANT))).IsFalse().Because("the customer key is not the region key");
  }

  [Test]
  public async Task ScopeFilter_ForAModelAndEventNoFanOutDeclares_ThrowsAsync() {
    var resolver = new FanOutScopeResolver([_renamedFanOut()]);

    await Assert.That(() => resolver.ScopeFilter<OrderSummary>(_wrap(new CustomerMoved(), Guid.NewGuid()).Scope))
      .Throws<InvalidOperationException>();
    await Assert.That(() => resolver.ScopeFilter<OrderSummary>(new TenantScope(TENANT))).Throws<ArgumentException>();
  }

  [Test]
  public async Task FanOutScopeResolver_NullEntriesOrANullScope_ThrowAsync() {
    await Assert.That(() => new FanOutScopeResolver(null!)).Throws<ArgumentNullException>();
    await Assert.That(() => new FanOutScopeResolver([]).EnterContext(null!)).Throws<ArgumentException>();
  }

  [Test]
  public async Task AcceptsPerspective_IsTrueOnlyForAModelThatDeclaresAFanOutAsync() {
    var resolver = new FanOutScopeResolver([_renamedFanOut()]);

    await Assert.That(resolver.AcceptsPerspective<OrderSummary>()).IsTrue();
    await Assert.That(resolver.AcceptsPerspective<CustomerRenamed>()).IsFalse();
    await Assert.That(resolver.ScopeKind).IsEqualTo(FanOutScope.KIND);
  }

  [Test]
  public async Task EnterContext_SetsTheEventsTenant_AndDisposeRestoresThePriorContextAsync() {
    var resolver = new FanOutScopeResolver([]);
    var prior = ScopeContextAccessor.CurrentContext;

    using (resolver.EnterContext(_wrap(new CustomerRenamed("Ada"), Guid.NewGuid()).Scope)) {
      await Assert.That(ScopeContextAccessor.CurrentContext!.Scope.TenantId).IsEqualTo(TENANT);
    }

    await Assert.That(ScopeContextAccessor.CurrentContext).IsSameReferenceAs(prior);
    await Assert.That(() => resolver.EnterContext(new TenantScope(TENANT))).Throws<ArgumentException>();
  }

  [Test]
  public async Task DispatchAsync_AFanOut_RunsOnlyTheFanOutEntriesOfItsEventAsync() {
    var executor = new CountingExecutor();
    var services = new ServiceCollection().AddSingleton(new OrderSummaryProjection()).BuildServiceProvider();
    var dispatcher = new CollectiveDispatcher(
      services,
      [
        _renamedFanOut().ToCollectiveApplyEntry(),
        _movedFanOut().ToCollectiveApplyEntry(),
        new CollectiveApplyEntry(typeof(OrderSummary), typeof(CustomerRenamed), typeof(OrderSummaryProjection), "Apply",
          CollectiveScopeHandling.Framework, CollectiveSpecKind.Linq, static (_, _, _) => throw new InvalidOperationException("not a fan-out")),
      ],
      [new FanOutScopeResolver([_renamedFanOut(), _movedFanOut()])],
      [executor]);

    var result = await dispatcher.DispatchAsync(_wrap(new CustomerRenamed("Ada"), Guid.NewGuid()), Guid.NewGuid(), new object());

    await Assert.That(result.HandlerCount).IsEqualTo(1);
    await Assert.That(executor.EntryKeys).IsEquivalentTo([_renamedFanOut().EntryKey]);
  }

  [Test]
  public async Task GetCollectiveDispatchEntries_AreTheHandlersThenEachFanOutAsync() {
    var handler = new CollectiveApplyEntry(typeof(OrderSummary), typeof(RenameCollective), typeof(OrderSummaryProjection), "Apply",
      CollectiveScopeHandling.Framework, CollectiveSpecKind.Linq, static (_, _, _) => new object());
    var services = new ServiceCollection()
      .AddSingleton<ICollectiveApplyRegistry>(new Handlers([handler]))
      .AddSingleton<IPerspectiveFanOutRegistry>(new FanOuts([_renamedFanOut()]))
      .BuildServiceProvider();

    var entries = services.GetCollectiveDispatchEntries();

    await Assert.That(entries.Select(e => e.EntryKey)).IsEquivalentTo([handler.EntryKey, _renamedFanOut().EntryKey]);
    await Assert.That(entries[1].FanOut).IsNotNull();
    await Assert.That(services.GetCollectiveApplyEntries()).Count().IsEqualTo(1)
      .Because("the replay applier walks the handlers alone; a fan-out's replay is its own (#2621)");
  }

  // ---------- fixtures ----------

  private static FanOutCollectiveEvent _wrap(IEvent evt, Guid stream) =>
    (FanOutCollectiveEvent)PerspectiveFanOutEntry.CollectiveEventFor(evt, stream, TENANT);

  private static PerspectiveFanOutEntry _renamedFanOut() => new(
    typeof(OrderSummaryProjection), typeof(OrderSummary), typeof(CustomerRenamed), nameof(OrderSummary.CustomerId), "customer_id",
    static (perspective, evt) => ((OrderSummaryProjection)perspective).Apply((CustomerRenamed)evt),
    (Expression<Func<OrderSummary, object?>>)(row => row.CustomerId));

  private static PerspectiveFanOutEntry _movedFanOut() => new(
    typeof(OrderSummaryProjection), typeof(OrderSummary), typeof(CustomerMoved), nameof(OrderSummary.RegionId), "region_id",
    static (_, _) => new NameSpec("moved"),
    (Expression<Func<OrderSummary, object?>>)(row => row.RegionId));

  private static PerspectiveRow<OrderSummary> _row(Guid customer, string tenant, Guid? region = null) => new() {
    Id = Guid.NewGuid(),
    Data = new OrderSummary { CustomerId = customer, RegionId = region ?? Guid.NewGuid() },
    Metadata = new PerspectiveMetadata { EventType = "e", EventId = Guid.NewGuid().ToString(), Timestamp = DateTime.UtcNow },
    Scope = new PerspectiveScope { TenantId = tenant },
    CreatedAt = DateTime.UtcNow,
    UpdatedAt = DateTime.UtcNow,
    Version = 1,
  };

  internal sealed class OrderSummary {
    public Guid CustomerId { get; init; }
    public Guid RegionId { get; init; }
    public string CustomerName { get; init; } = "";
  }

  internal sealed record CustomerRenamed(string Name) : IEvent;

  internal sealed record CustomerMoved : IEvent;

  internal sealed record RenameCollective(CollectiveScope Scope) : ICollectiveEvent;

  internal sealed record TenantScope(string TenantId) : CollectiveScope {
    public override string ScopeKind => "tenant";
    public override string ScopeIdentity => "tenant:" + TenantId;
  }

  internal sealed record NameSpec(string Name) : ICollectiveSpec<OrderSummary> {
    public Expression<Action<ICollectiveSetters<OrderSummary>>> Setters => s => s.SetProperty(o => o.CustomerName, Name);
  }

  internal sealed class OrderSummaryProjection {
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Minor Code Smell", "S2325:Methods and properties that don't access instance data should be static", Justification = "Invoked through the instance invoker the generator emits.")]
    public ICollectiveSpec<OrderSummary> Apply(CustomerRenamed e) => new NameSpec(e.Name);
  }

  private sealed class Handlers(IReadOnlyList<CollectiveApplyEntry> entries) : ICollectiveApplyRegistry {
    public IReadOnlyList<CollectiveApplyEntry> Entries => entries;
  }

  private sealed class FanOuts(IReadOnlyList<PerspectiveFanOutEntry> entries) : IPerspectiveFanOutRegistry {
    public IReadOnlyList<PerspectiveFanOutEntry> Entries => entries;
  }

  private sealed class CountingExecutor : ICollectiveEventExecutor {
    public List<string> EntryKeys { get; } = [];

    public Type ModelType => typeof(OrderSummary);

    public Task<int> ApplyAsync(
        CollectiveApplyEntry entry, object handlerInstance, ICollectiveEvent evt, ICollectiveScopeResolver resolver,
        object dbContextOrSession, Guid collectiveEventId, Func<CancellationToken, ValueTask>? onBatchApplied = null,
        CancellationToken cancellationToken = default) {
      EntryKeys.Add(entry.EntryKey);
      _ = entry.Invoker(handlerInstance, evt, null!);
      return Task.FromResult(2);
    }
  }
}
