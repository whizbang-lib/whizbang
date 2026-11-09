// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Every assembly's event types reach the one <see cref="IEventTypeProvider"/> the container serves (#2146).
/// </summary>
/// <remarks>
/// Each assembly that declares perspectives generates a provider and registers it through
/// <see cref="EventTypeProviderServiceCollectionExtensions.AddEventTypeProviderContribution{TProvider}"/>. Registered as
/// a plain singleton, the last assembly's provider displaced the others, so the drain's type list, the polymorphic
/// deserializer and the inbox pre-filter were blind to every event (collective ones included) the other assemblies
/// knew. These pin the union, its de-duplication, and that a host's own provider and the null default each keep their
/// meaning.
/// </remarks>
/// <code-under-test>src/Whizbang.Core/Messaging/EventTypeProviderServiceCollectionExtensions.cs</code-under-test>
public class EventTypeProviderContributionTests {
  private sealed record OrderPlaced;
  private sealed record OrderShipped;
  private sealed record InvoiceIssued;
  private sealed record TenantRenamedCollective;

  /// <summary>Stands in for the first assembly's generated registry.</summary>
  private sealed class OrdersAssemblyRegistry : IEventTypeProvider {
    public IReadOnlyList<Type> GetEventTypes() => [typeof(OrderPlaced), typeof(OrderShipped)];
  }

  /// <summary>Stands in for a second assembly's generated registry, which also knows a collective event.</summary>
  private sealed class BillingAssemblyRegistry : IEventTypeProvider {
    public IReadOnlyList<Type> GetEventTypes() => [typeof(InvoiceIssued), typeof(OrderPlaced), typeof(TenantRenamedCollective)];
  }

  private sealed class HostsOwnProvider : IEventTypeProvider {
    public IReadOnlyList<Type> GetEventTypes() => [typeof(OrderShipped)];
  }

  private sealed class UnavailableProvider : IEventTypeProvider {
    public bool IsAvailable => false;
    public IReadOnlyList<Type> GetEventTypes() => [];
  }

  private static IServiceCollection _twoAssemblies() {
    IServiceCollection services = new ServiceCollection();
    services.AddSingleton<OrdersAssemblyRegistry>();
    services.AddEventTypeProviderContribution<OrdersAssemblyRegistry>();
    services.AddSingleton<BillingAssemblyRegistry>();
    services.AddEventTypeProviderContribution<BillingAssemblyRegistry>();
    return services;
  }

  [Test]
  public async Task TwoAssemblies_TheServedProvider_ListsBothAssembliesTypes_CollectiveIncludedAsync() {
    await using var provider = _twoAssemblies().BuildServiceProvider();

    var types = provider.GetRequiredService<IEventTypeProvider>().GetEventTypes();

    await Assert.That(types).IsEquivalentTo(
      [typeof(OrderPlaced), typeof(OrderShipped), typeof(InvoiceIssued), typeof(TenantRenamedCollective)])
      .Because("the first assembly's events must not vanish because a second assembly registered after it");
  }

  [Test]
  public async Task TwoAssemblies_AfterTheDefaults_TheServedProviderIsTheUnionAndAvailableAsync() {
    IServiceCollection services = new ServiceCollection();
    services.TryAddWhizbangDefaults();
    foreach (var descriptor in _twoAssemblies()) {
      services.Add(descriptor);
    }
    await using var provider = services.BuildServiceProvider();

    var served = provider.GetRequiredService<IEventTypeProvider>();

    await Assert.That(served.IsAvailable).IsTrue()
      .Because("the null default steps aside for the assemblies' contributions");
    await Assert.That(served.GetEventTypes()).Contains(typeof(OrderShipped));
    await Assert.That(served.GetEventTypes()).Contains(typeof(InvoiceIssued));
  }

  [Test]
  public async Task TheDefaults_AfterTheContributions_DoNotDisplaceThemAsync() {
    var services = _twoAssemblies();
    services.TryAddWhizbangDefaults();
    await using var provider = services.BuildServiceProvider();

    var served = provider.GetRequiredService<IEventTypeProvider>();

    await Assert.That(served.IsAvailable).IsTrue();
    await Assert.That(served.GetEventTypes().Count).IsEqualTo(4);
  }

  [Test]
  public async Task AnAssemblyRegisteredTwice_ContributesItsTypesOnceAsync() {
    var services = _twoAssemblies();
    services.AddEventTypeProviderContribution<OrdersAssemblyRegistry>();
    await using var provider = services.BuildServiceProvider();

    var types = provider.GetRequiredService<IEventTypeProvider>().GetEventTypes();

    await Assert.That(types.Count(t => t == typeof(OrderShipped))).IsEqualTo(1);
    await Assert.That(types.Count(t => t == typeof(OrderPlaced))).IsEqualTo(1)
      .Because("a type two assemblies both know is listed once");
  }

  [Test]
  public async Task AHostsOwnProvider_RegisteredFirst_IsTheOneServedAsync() {
    IServiceCollection services = new ServiceCollection();
    services.AddSingleton<IEventTypeProvider>(new HostsOwnProvider());
    foreach (var descriptor in _twoAssemblies()) {
      services.Add(descriptor);
    }
    await using var provider = services.BuildServiceProvider();

    await Assert.That(provider.GetRequiredService<IEventTypeProvider>()).IsTypeOf<HostsOwnProvider>()
      .Because("a provider the host registers itself is a decision, and the contributions do not override it");
  }

  [Test]
  public async Task OnlyUnavailableContributions_TheServedProviderReportsUnavailableAsync() {
    IServiceCollection services = new ServiceCollection();
    services.AddSingleton<UnavailableProvider>();
    services.AddEventTypeProviderContribution<UnavailableProvider>();
    await using var provider = services.BuildServiceProvider();

    await Assert.That(provider.GetRequiredService<IEventTypeProvider>().IsAvailable).IsFalse()
      .Because("the union is available when one of its contributions is, and only then");
  }

  [Test]
  public async Task AddEventTypeProviderContribution_NullServices_ThrowsAsync() {
    await Assert.That(() => EventTypeProviderServiceCollectionExtensions.AddEventTypeProviderContribution<OrdersAssemblyRegistry>(null!))
      .Throws<ArgumentNullException>();
  }

  [Test]
  public async Task CompositeEventTypeProvider_NullProviders_ThrowsAsync() {
    await Assert.That(() => new CompositeEventTypeProvider(null!)).Throws<ArgumentNullException>();
  }
}
