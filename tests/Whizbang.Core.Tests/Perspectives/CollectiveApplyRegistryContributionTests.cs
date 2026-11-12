// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Perspectives;

namespace Whizbang.Core.Tests.Perspectives;

/// <summary>
/// Every assembly's collective apply handlers reach the one <see cref="ICollectiveApplyRegistry"/> the container
/// serves (#2238).
/// </summary>
/// <remarks>
/// The handler table is generated in every assembly that declares <c>[CollectiveApplyFor]</c> handlers. It used to be
/// handed to the collective registration by hand, so a host whose handlers live in several assemblies gave the
/// dispatcher one assembly's table, and the others' collective events were never applied. Each assembly now
/// contributes its table, and the served registry lists every contribution's handlers.
/// </remarks>
/// <code-under-test>src/Whizbang.Core/Perspectives/CompositeCollectiveApplyRegistry.cs</code-under-test>
public class CollectiveApplyRegistryContributionTests {
  private sealed record OrdersModel;
  private sealed record BillingModel;
  private sealed record OrdersCollective : ICollectiveEvent { public CollectiveScope Scope { get; init; } = new TenantCollectiveScope("t"); }
  private sealed record BillingCollective : ICollectiveEvent { public CollectiveScope Scope { get; init; } = new TenantCollectiveScope("t"); }
  private sealed class OrdersHandler;
  private sealed class BillingHandler;

  private static CollectiveApplyEntry _entry(Type model, Type evt, Type handler, string method = "Apply") =>
    new(model, evt, handler, method, CollectiveScopeHandling.Framework, CollectiveSpecKind.Linq,
      Invoker: static (_, _, _) => new object());

  private static readonly CollectiveApplyEntry _orders = _entry(typeof(OrdersModel), typeof(OrdersCollective), typeof(OrdersHandler));
  private static readonly CollectiveApplyEntry _billing = _entry(typeof(BillingModel), typeof(BillingCollective), typeof(BillingHandler));

  private sealed class OrdersRegistry : ICollectiveApplyRegistry {
    public IReadOnlyList<CollectiveApplyEntry> Entries { get; } = [_orders];
  }

  private sealed class BillingRegistry : ICollectiveApplyRegistry {
    public IReadOnlyList<CollectiveApplyEntry> Entries { get; } = [_billing];
  }

  /// <summary>Lists the orders handler as a second assembly would if the same source were compiled into it.</summary>
  private sealed class OrdersAgainRegistry : ICollectiveApplyRegistry {
    public IReadOnlyList<CollectiveApplyEntry> Entries { get; } = [_entry(typeof(OrdersModel), typeof(OrdersCollective), typeof(OrdersHandler))];
  }

  private static ServiceCollection _withContributions(bool ordersFirst) {
    var services = new ServiceCollection();
    services.AddSingleton<OrdersRegistry>();
    services.AddSingleton<BillingRegistry>();
    if (ordersFirst) {
      services.AddCollectiveApplyRegistryContribution<OrdersRegistry>();
      services.AddCollectiveApplyRegistryContribution<BillingRegistry>();
    } else {
      services.AddCollectiveApplyRegistryContribution<BillingRegistry>();
      services.AddCollectiveApplyRegistryContribution<OrdersRegistry>();
    }
    return services;
  }

  [Test]
  [Arguments(true)]
  [Arguments(false)]
  public async Task TwoAssemblies_TheServedRegistry_ListsBothAssemblysHandlersAsync(bool ordersFirst) {
    await using var provider = _withContributions(ordersFirst).BuildServiceProvider();

    var served = provider.GetRequiredService<ICollectiveApplyRegistry>();

    await Assert.That(served).IsTypeOf<CompositeCollectiveApplyRegistry>();
    await Assert.That(served.Entries).Contains(_orders).Because("the orders assembly's handler, whichever registered last");
    await Assert.That(served.Entries).Contains(_billing).Because("the billing assembly's handler, whichever registered last");
    await Assert.That(served.Entries.Count).IsEqualTo(2);
  }

  [Test]
  public async Task TheServedRegistry_ListsTheLatestRegisteredContributionFirstAsync() {
    await using var provider = _withContributions(ordersFirst: true).BuildServiceProvider();

    var served = provider.GetRequiredService<ICollectiveApplyRegistry>().Entries;

    await Assert.That(served[0]).IsEqualTo(_billing).Because("the latest registration answers first, as every contribution does");
    await Assert.That(served[1]).IsEqualTo(_orders);
  }

  [Test]
  public async Task AHandlerTwoContributionsList_IsListedOnce_AtTheLatestAsync() {
    var latest = new OrdersAgainRegistry();
    var registry = new CompositeCollectiveApplyRegistry([latest, new OrdersRegistry(), new BillingRegistry()]);

    await Assert.That(registry.Entries.Count).IsEqualTo(2)
      .Because("one handler method applying one event to one model is one handler, however many tables list it");
    await Assert.That(registry.Entries[0]).IsSameReferenceAs(latest.Entries[0])
      .Because("the first contribution given, the latest registered, keeps the handler");
    await Assert.That(registry.Entries[1]).IsEqualTo(_billing);
  }

  [Test]
  public async Task TwoMethodsOfOneHandler_AreTwoHandlersAsync() {
    var first = _entry(typeof(OrdersModel), typeof(OrdersCollective), typeof(OrdersHandler), "First");
    var second = _entry(typeof(OrdersModel), typeof(OrdersCollective), typeof(OrdersHandler), "Second");
    var otherEvent = _entry(typeof(OrdersModel), typeof(BillingCollective), typeof(OrdersHandler), "First");

    var registry = new CompositeCollectiveApplyRegistry([new ListedRegistry([first, second, otherEvent])]);

    await Assert.That(registry.Entries.SequenceEqual([first, second, otherEvent], ReferenceEqualityComparer.Instance)).IsTrue()
      .Because("two methods, or one method of two events, are distinct handlers: none is dropped");
  }

  private sealed class ListedRegistry(IReadOnlyList<CollectiveApplyEntry> entries) : ICollectiveApplyRegistry {
    public IReadOnlyList<CollectiveApplyEntry> Entries => entries;
  }

  [Test]
  public async Task AnAssemblyRegisteredTwice_ContributesItsHandlersOnceAsync() {
    var services = _withContributions(ordersFirst: true);
    services.AddCollectiveApplyRegistryContribution<OrdersRegistry>();
    await using var provider = services.BuildServiceProvider();

    var served = provider.GetRequiredService<ICollectiveApplyRegistry>().Entries;

    await Assert.That(served.Count).IsEqualTo(2);
    await Assert.That(served[0]).IsEqualTo(_billing).Because("a re-registration moves nothing");
  }

  [Test]
  public async Task TheRegistry_AddsACollisionSource_ThatNamesEveryHandlerAsync() {
    await using var provider = _withContributions(ordersFirst: true).BuildServiceProvider();

    var sources = provider.GetServices<IRegistrationCollisionSource>().ToArray();

    await Assert.That(sources.Length).IsEqualTo(1);
    await Assert.That(sources[0].GetCollisions()).IsEmpty()
      .Because("both stand-in registries are declared in this one test assembly, which cannot hide its own handler");
  }

  [Test]
  public async Task CollisionItem_NamesTheEventAndTheHandlerMethodAndModelAsync() {
    var item = CollectiveApplyRegistryServiceCollectionExtensions.CollisionItem(_orders);

    await Assert.That(item).Contains(nameof(OrdersCollective));
    await Assert.That(item).Contains(_orders.EntryKey);
  }

  [Test]
  public async Task GetCollectiveApplyEntries_WithNoRegistry_IsEmptyAsync() {
    await using var provider = new ServiceCollection().BuildServiceProvider();

    await Assert.That(provider.GetCollectiveApplyEntries()).IsEmpty();
  }

  [Test]
  public async Task GetCollectiveApplyEntries_IsTheServedRegistrysHandlersAsync() {
    await using var provider = _withContributions(ordersFirst: true).BuildServiceProvider();

    await Assert.That(provider.GetCollectiveApplyEntries().SequenceEqual([_billing, _orders], ReferenceEqualityComparer.Instance)).IsTrue();
  }

  [Test]
  public async Task GetCollectiveApplyEntries_WithHandPassedHandlers_AddsThoseTheRegistryLacks_OnceAsync() {
    var services = new ServiceCollection();
    services.AddSingleton<OrdersRegistry>();
    services.AddCollectiveApplyRegistryContribution<OrdersRegistry>();
    await using var provider = services.BuildServiceProvider();
    var handPassedOrders = _entry(typeof(OrdersModel), typeof(OrdersCollective), typeof(OrdersHandler));

    var entries = provider.GetCollectiveApplyEntries([handPassedOrders, _billing]);

    await Assert.That(entries.SequenceEqual([_orders, _billing], ReferenceEqualityComparer.Instance)).IsTrue()
      .Because("a hand-passed table listing a contributed handler adds nothing (the registered entry stays); one "
             + "listing another handler adds it, after the registered ones");
  }

  [Test]
  public async Task Guards_ThrowAsync() {
    await Assert.That(() => CollectiveApplyRegistryServiceCollectionExtensions.AddCollectiveApplyRegistryContribution<OrdersRegistry>(null!))
      .Throws<ArgumentNullException>();
    await Assert.That(() => CollectiveApplyRegistryServiceCollectionExtensions.GetCollectiveApplyEntries(null!))
      .Throws<ArgumentNullException>();
    await Assert.That(() => CollectiveApplyRegistryServiceCollectionExtensions.CollisionItem(null!))
      .Throws<ArgumentNullException>();
    await Assert.That(() => new CompositeCollectiveApplyRegistry(null!)).Throws<ArgumentNullException>();
  }
}
