// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Perspectives;

namespace Whizbang.Core.Tests.Perspectives;

/// <summary>
/// Every assembly's fan-out declarations reach the one <see cref="IPerspectiveFanOutRegistry"/> the container serves
/// (#2618), the way every assembly's collective handlers reach the collective apply registry (#2238).
/// </summary>
/// <code-under-test>src/Whizbang.Core/Perspectives/CompositePerspectiveFanOutRegistry.cs</code-under-test>
public class PerspectiveFanOutRegistryTests {
  private sealed record OrderModel;
  private sealed record InvoiceModel;
  private sealed record CustomerRenamed : IEvent;
  private sealed record RegionRenamed : IEvent;
  private sealed class OrderProjection;
  private sealed class InvoiceProjection;

  private static PerspectiveFanOutEntry _entry(Type perspective, Type model, Type evt, string key = "CustomerId") =>
    new(perspective, model, evt, key, "customer_id", Invoker: static (_, _) => null!, KeySelector: _anyKey);

  private static readonly System.Linq.Expressions.Expression<Func<object, object?>> _anyKey = row => row;

  private static readonly PerspectiveFanOutEntry _orders = _entry(typeof(OrderProjection), typeof(OrderModel), typeof(CustomerRenamed));
  private static readonly PerspectiveFanOutEntry _invoices = _entry(typeof(InvoiceProjection), typeof(InvoiceModel), typeof(CustomerRenamed));

  private sealed class OrdersRegistry : IPerspectiveFanOutRegistry {
    public IReadOnlyList<PerspectiveFanOutEntry> Entries { get; } = [_orders];
  }

  private sealed class InvoicesRegistry : IPerspectiveFanOutRegistry {
    public IReadOnlyList<PerspectiveFanOutEntry> Entries { get; } = [_invoices];
  }

  private sealed class ListedRegistry(IReadOnlyList<PerspectiveFanOutEntry> entries) : IPerspectiveFanOutRegistry {
    public IReadOnlyList<PerspectiveFanOutEntry> Entries => entries;
  }

  private static ServiceCollection _withContributions() {
    var services = new ServiceCollection();
    services.AddSingleton<OrdersRegistry>();
    services.AddSingleton<InvoicesRegistry>();
    services.AddPerspectiveFanOutRegistryContribution<OrdersRegistry>();
    services.AddPerspectiveFanOutRegistryContribution<InvoicesRegistry>();
    return services;
  }

  [Test]
  public async Task TwoAssemblies_TheServedRegistry_ListsBothAssemblysFanOuts_LatestFirstAsync() {
    await using var provider = _withContributions().BuildServiceProvider();

    var served = provider.GetRequiredService<IPerspectiveFanOutRegistry>();

    await Assert.That(served).IsTypeOf<CompositePerspectiveFanOutRegistry>();
    await Assert.That(served.Entries.SequenceEqual([_invoices, _orders], ReferenceEqualityComparer.Instance)).IsTrue()
      .Because("every contribution is listed, the latest registered first, as every contribution is");
  }

  [Test]
  public async Task AFanOutTwoContributionsList_IsListedOnce_AtTheFirstAsync() {
    var again = _entry(typeof(OrderProjection), typeof(OrderModel), typeof(CustomerRenamed));

    var registry = new CompositePerspectiveFanOutRegistry([new ListedRegistry([again]), new OrdersRegistry()]);

    await Assert.That(registry.Entries.Count).IsEqualTo(1);
    await Assert.That(registry.Entries[0]).IsSameReferenceAs(again);
  }

  [Test]
  public async Task OnePerspectiveFanningOutTwoEvents_IsTwoFanOutsAsync() {
    var region = _entry(typeof(OrderProjection), typeof(OrderModel), typeof(RegionRenamed), key: "RegionId");

    var registry = new CompositePerspectiveFanOutRegistry([new ListedRegistry([_orders, region])]);

    await Assert.That(registry.Entries.SequenceEqual([_orders, region], ReferenceEqualityComparer.Instance)).IsTrue()
      .Because("each foreign event a perspective fans out is its own entry, with its own key");
  }

  [Test]
  public async Task TheRegistry_AddsACollisionSourceAsync() {
    await using var provider = _withContributions().BuildServiceProvider();

    var sources = provider.GetServices<IRegistrationCollisionSource>().ToArray();

    await Assert.That(sources.Length).IsEqualTo(1);
    await Assert.That(sources[0].GetCollisions()).IsEmpty();
  }

  [Test]
  public async Task CollisionItem_NamesTheEventAndTheEntryKeyAsync() {
    var item = PerspectiveFanOutRegistryServiceCollectionExtensions.CollisionItem(_orders);

    await Assert.That(item).Contains(nameof(CustomerRenamed));
    await Assert.That(item).Contains(_orders.EntryKey);
  }

  [Test]
  public async Task GetPerspectiveFanOutEntries_WithNoRegistry_IsEmptyAsync() {
    await using var provider = new ServiceCollection().BuildServiceProvider();

    await Assert.That(provider.GetPerspectiveFanOutEntries()).IsEmpty();
  }

  [Test]
  public async Task GetPerspectiveFanOutEntries_IsTheServedRegistrysEntriesAsync() {
    await using var provider = _withContributions().BuildServiceProvider();

    await Assert.That(provider.GetPerspectiveFanOutEntries().SequenceEqual([_invoices, _orders], ReferenceEqualityComparer.Instance)).IsTrue();
  }

  [Test]
  public async Task Guards_ThrowAsync() {
    await Assert.That(() => PerspectiveFanOutRegistryServiceCollectionExtensions.AddPerspectiveFanOutRegistryContribution<OrdersRegistry>(null!))
      .Throws<ArgumentNullException>();
    await Assert.That(() => PerspectiveFanOutRegistryServiceCollectionExtensions.GetPerspectiveFanOutEntries(null!))
      .Throws<ArgumentNullException>();
    await Assert.That(() => PerspectiveFanOutRegistryServiceCollectionExtensions.CollisionItem(null!))
      .Throws<ArgumentNullException>();
    await Assert.That(() => new CompositePerspectiveFanOutRegistry(null!)).Throws<ArgumentNullException>();
  }
}
