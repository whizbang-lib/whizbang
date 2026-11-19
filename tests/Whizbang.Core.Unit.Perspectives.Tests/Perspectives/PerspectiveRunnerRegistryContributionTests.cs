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
/// Every assembly's perspective runners are found through the one <see cref="IPerspectiveRunnerRegistry"/> the
/// container serves (#2191).
/// </summary>
/// <remarks>
/// Each assembly that declares perspectives generates a registry and registers it through
/// <see cref="PerspectiveRunnerRegistryServiceCollectionExtensions.AddPerspectiveRunnerRegistryContribution{TRegistry}"/>.
/// Registered as a plain singleton, the last assembly's registry displaced the others, so the perspective worker, the
/// rebuilder, the maintenance worker and the stream disposition step found no runner for any perspective another
/// assembly declared. These pin the lookup across assemblies, the listing, which contribution answers when two declare
/// the same perspective (the latest, #2201), and that a host's own registry and the event-type union each keep their meaning.
/// </remarks>
/// <code-under-test>src/Whizbang.Core/Perspectives/PerspectiveRunnerRegistryServiceCollectionExtensions.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Perspectives/CompositePerspectiveRunnerRegistry.cs</code-under-test>
public class PerspectiveRunnerRegistryContributionTests {
  private const string ORDERS_PERSPECTIVE = "Orders.OrderSummaryPerspective";
  private const string BILLING_PERSPECTIVE = "Billing.InvoicePerspective";

  private sealed record OrderPlaced;
  private sealed record InvoiceIssued;
  private sealed record TenantRenamedCollective;

  /// <summary>A runner that says which assembly's registry handed it out.</summary>
  private sealed class OwnedRunner(string owner) : IPerspectiveRunner {
    public string Owner { get; } = owner;
    public Type PerspectiveType => typeof(object);

    public Task<PerspectiveCursorCompletion> RunAsync(Guid streamId, string perspectiveName, Guid? lastProcessedEventId, CancellationToken cancellationToken = default) =>
      throw new NotSupportedException();

    public Task<PerspectiveCursorCompletion> RewindAndRunAsync(Guid streamId, string perspectiveName, Guid triggeringEventId, CancellationToken cancellationToken = default) =>
      throw new NotSupportedException();

    public Task BootstrapSnapshotAsync(Guid streamId, string perspectiveName, Guid lastProcessedEventId, CancellationToken cancellationToken = default) =>
      throw new NotSupportedException();
  }

  /// <summary>Stands in for one assembly's generated registry: a switch over the perspectives it declares.</summary>
  private abstract class AssemblyRegistry(
      string owner, string[] perspectives, Type[] eventTypes, LifecycleStage[] stages) : IPerspectiveRunnerRegistry {
    public IPerspectiveRunner? GetRunner(string perspectiveName, IServiceProvider serviceProvider) =>
      perspectives.Contains(perspectiveName) ? new OwnedRunner(owner) : null;

    public IReadOnlyList<PerspectiveRegistrationInfo> GetRegisteredPerspectives() =>
      [.. perspectives.Select(p => new PerspectiveRegistrationInfo(p, $"global::{p}", $"global::{owner}.Model", []))];

    public IReadOnlyList<Type> GetEventTypes() => eventTypes;

    public IReadOnlySet<LifecycleStage> LifecycleStagesWithReceptors { get; } = new HashSet<LifecycleStage>(stages);
  }

  private sealed class OrdersAssemblyRegistry() : AssemblyRegistry(
    "Orders", [ORDERS_PERSPECTIVE], [typeof(OrderPlaced)], [LifecycleStage.PostPerspectiveDetached]);

  private sealed class BillingAssemblyRegistry() : AssemblyRegistry(
    "Billing", [BILLING_PERSPECTIVE], [typeof(InvoiceIssued), typeof(OrderPlaced), typeof(TenantRenamedCollective)],
    [LifecycleStage.PostPerspectiveInline]);

  /// <summary>A third assembly that declares a perspective under the same CLR name as the orders assembly.</summary>
  private sealed class ShadowOrdersAssemblyRegistry() : AssemblyRegistry("ShadowOrders", [ORDERS_PERSPECTIVE], [], []);

  private sealed class HostsOwnRegistry() : AssemblyRegistry("Host", [ORDERS_PERSPECTIVE], [], []);

  private static void _add<TRegistry>(IServiceCollection services)
      where TRegistry : class, IPerspectiveRunnerRegistry {
    services.AddSingleton<TRegistry>();
    services.AddPerspectiveRunnerRegistryContribution<TRegistry>();
  }

  private static IServiceCollection _twoAssemblies(bool ordersFirst = true, IServiceCollection? services = null) {
    services ??= new ServiceCollection();
    if (ordersFirst) {
      _add<OrdersAssemblyRegistry>(services);
      _add<BillingAssemblyRegistry>(services);
    } else {
      _add<BillingAssemblyRegistry>(services);
      _add<OrdersAssemblyRegistry>(services);
    }
    return services;
  }

  [Test]
  [Arguments(true)]
  [Arguments(false)]
  public async Task TwoAssemblies_EachAssemblysRunnerIsFound_WhicheverRegisteredLastAsync(bool ordersFirst) {
    await using var provider = _twoAssemblies(ordersFirst).BuildServiceProvider();
    var registry = provider.GetRequiredService<IPerspectiveRunnerRegistry>();

    var orders = registry.GetRunner(ORDERS_PERSPECTIVE, provider) as OwnedRunner;
    var billing = registry.GetRunner(BILLING_PERSPECTIVE, provider) as OwnedRunner;

    await Assert.That(orders?.Owner).IsEqualTo("Orders")
      .Because("a perspective is found in the assembly that declares it, not only in the last one registered");
    await Assert.That(billing?.Owner).IsEqualTo("Billing");
  }

  [Test]
  public async Task APerspectiveNoAssemblyDeclares_IsNotFoundAsync() {
    await using var provider = _twoAssemblies().BuildServiceProvider();

    var runner = provider.GetRequiredService<IPerspectiveRunnerRegistry>().GetRunner("Nowhere.Perspective", provider);

    await Assert.That(runner).IsNull().Because("no contribution answers, so the lookup reports not found as before");
  }

  [Test]
  public async Task TwoAssemblies_TheServedRegistry_ListsBothAssembliesPerspectives_LatestRegisteredFirstAsync() {
    await using var provider = _twoAssemblies().BuildServiceProvider();

    var listed = provider.GetRequiredService<IPerspectiveRunnerRegistry>().GetRegisteredPerspectives()
      .Select(p => p.ClrTypeName).ToArray();

    await Assert.That(listed.SequenceEqual([BILLING_PERSPECTIVE, ORDERS_PERSPECTIVE])).IsTrue()
      .Because($"every assembly's perspectives are listed, the latest registered first; listed: {string.Join(", ", listed)}");
  }

  [Test]
  [Arguments(true)]
  [Arguments(false)]
  public async Task SamePerspectiveInTwoAssemblies_TheLatestContributionAnswers_AndIsListedOnceAsync(bool ordersFirst) {
    IServiceCollection services = new ServiceCollection();
    if (ordersFirst) {
      _add<OrdersAssemblyRegistry>(services);
      _add<ShadowOrdersAssemblyRegistry>(services);
    } else {
      _add<ShadowOrdersAssemblyRegistry>(services);
      _add<OrdersAssemblyRegistry>(services);
    }
    await using var provider = services.BuildServiceProvider();
    var registry = provider.GetRequiredService<IPerspectiveRunnerRegistry>();
    var latest = ordersFirst ? "ShadowOrders" : "Orders";

    var runner = registry.GetRunner(ORDERS_PERSPECTIVE, provider) as OwnedRunner;
    var listed = registry.GetRegisteredPerspectives().Where(p => p.ClrTypeName == ORDERS_PERSPECTIVE).ToArray();

    await Assert.That(runner?.Owner).IsEqualTo(latest)
      .Because("the latest contribution that declares a perspective answers for it: the rule every per-assembly "
             + "registration follows (#2201)");
    await Assert.That(listed.Length).IsEqualTo(1).Because("a perspective two assemblies declare is listed once");
    await Assert.That(listed[0].ModelType).IsEqualTo($"global::{latest}.Model")
      .Because("the listing describes the registration whose runner the lookup returns");
  }

  [Test]
  public async Task AnAssemblyRegisteredTwice_ListsItsPerspectivesOnceAsync() {
    var services = _twoAssemblies();
    services.AddPerspectiveRunnerRegistryContribution<OrdersAssemblyRegistry>();
    await using var provider = services.BuildServiceProvider();

    var listed = provider.GetRequiredService<IPerspectiveRunnerRegistry>().GetRegisteredPerspectives();

    await Assert.That(listed.Count).IsEqualTo(2);
  }

  [Test]
  public async Task TwoAssemblies_EventTypes_AreTheUnion_OnTheRegistryAndTheServedProviderAsync() {
    await using var provider = _twoAssemblies().BuildServiceProvider();

    var fromRegistry = provider.GetRequiredService<IPerspectiveRunnerRegistry>().GetEventTypes();
    var fromProvider = provider.GetRequiredService<IEventTypeProvider>().GetEventTypes();

    Type[] expected = [typeof(InvoiceIssued), typeof(OrderPlaced), typeof(TenantRenamedCollective)];
    await Assert.That(fromRegistry.SequenceEqual(expected)).IsTrue()
      .Because($"the registry's types are every assembly's, once each; listed: {string.Join(", ", fromRegistry.Select(t => t.Name))}");
    await Assert.That(fromProvider.SequenceEqual(expected)).IsTrue()
      .Because("contributing a registry also contributes its event types to the served provider (#2146)");
  }

  [Test]
  public async Task TwoAssemblies_LifecycleStagesWithReceptors_AreTheUnionAsync() {
    await using var provider = _twoAssemblies().BuildServiceProvider();

    var stages = provider.GetRequiredService<IPerspectiveRunnerRegistry>().LifecycleStagesWithReceptors;

    await Assert.That(stages.SetEquals([LifecycleStage.PostPerspectiveDetached, LifecycleStage.PostPerspectiveInline])).IsTrue()
      .Because("a stage with a receptor in any assembly must not be skipped");
  }

  [Test]
  public async Task SamePerspectiveInTwoAssemblies_IsReportedAsACollisionAsync() {
    IServiceCollection services = new ServiceCollection();
    _add<OrdersAssemblyRegistry>(services);
    _add<ShadowOrdersAssemblyRegistry>(services);
    await using var provider = services.BuildServiceProvider();

    var sources = provider.GetServices<IRegistrationCollisionSource>().ToArray();
    var collisions = sources.SelectMany(s => s.GetCollisions()).ToArray();

    await Assert.That(sources.Length).IsEqualTo(1)
      .Because("contributing a runner registry reports the perspectives two assemblies both declare at startup");
    await Assert.That(collisions).IsEmpty()
      .Because("both stand-in registries are declared in this one test assembly, and an assembly cannot hide its own "
             + "registration; the cross-assembly report is pinned in RegistrationCollisionsTests");
  }

  [Test]
  public async Task AHostsOwnRegistry_RegisteredFirst_IsTheOneServedAsync() {
    IServiceCollection services = new ServiceCollection();
    services.AddSingleton<IPerspectiveRunnerRegistry>(new HostsOwnRegistry());
    _twoAssemblies(services: services);
    await using var provider = services.BuildServiceProvider();

    await Assert.That(provider.GetRequiredService<IPerspectiveRunnerRegistry>()).IsTypeOf<HostsOwnRegistry>()
      .Because("a registry the host registers itself is a decision, and the contributions do not override it");
  }

  [Test]
  public async Task AddPerspectiveRunnerRegistryContribution_NullServices_ThrowsAsync() {
    await Assert.That(() => PerspectiveRunnerRegistryServiceCollectionExtensions
        .AddPerspectiveRunnerRegistryContribution<OrdersAssemblyRegistry>(null!))
      .Throws<ArgumentNullException>();
  }

  [Test]
  public async Task CompositePerspectiveRunnerRegistry_NullRegistries_ThrowsAsync() {
    await Assert.That(() => new CompositePerspectiveRunnerRegistry(null!)).Throws<ArgumentNullException>();
  }

  /// <summary>
  /// Removing one assembly's registry from the collection leaves its contribution entry behind; building the served
  /// registry must skip it, not throw "No service for type" (#2243), and the other assemblies' runners still answer.
  /// </summary>
  [Test]
  public async Task AContributionWhoseRegistryWasRemoved_IsSkipped_AndTheHostStillBuildsAsync() {
    var services = new ServiceCollection();
    _add<OrdersAssemblyRegistry>(services);
    _add<BillingAssemblyRegistry>(services);
    services.Remove(services.Single(d => d.ServiceType == typeof(BillingAssemblyRegistry)));
    await using var provider = services.BuildServiceProvider();

    var registry = provider.GetRequiredService<IPerspectiveRunnerRegistry>();
    var eventTypes = provider.GetRequiredService<IEventTypeProvider>().GetEventTypes();

    await Assert.That(registry.GetRunner(ORDERS_PERSPECTIVE, provider)).IsNotNull();
    await Assert.That(registry.GetRunner(BILLING_PERSPECTIVE, provider)).IsNull()
      .Because("the removed registry contributes nothing");
    await Assert.That(eventTypes).DoesNotContain(typeof(InvoiceIssued));
  }
}
