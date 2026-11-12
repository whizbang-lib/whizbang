// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Configuration;
using Whizbang.Core.Perspectives;

namespace Whizbang.Core.Tests.ServiceScope;

/// <summary>
/// A host replays the generated registrations of its own service only (#2243): one process that builds a host per
/// service gives each host its own, and a service split over several assemblies keeps all of them.
/// </summary>
/// <remarks>
/// Each test replaces the process-wide callbacks and facts with a described process (two services that share a
/// contracts assembly) and puts the real ones back afterwards. The callbacks register marker services named after
/// their assembly, so what a host holds says whose registrations it replayed.
/// </remarks>
/// <code-under-test>src/Whizbang.Core/ServiceRegistrationCallbacks.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/WhizbangServiceScopeExtensions.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Perspectives/PerspectiveRunnerCallbackRegistry.cs</code-under-test>
[NotInParallel("WhizbangBackgroundServiceTests")]
public class ServiceScopedRegistrationTests {
  private const string THIS_ASSEMBLY = "Whizbang.Core.Tests";

  private sealed record Registered(string Assembly, string Slot);

  private interface IShared;

  private sealed class OrdersShared : IShared;

  private sealed class BillingShared : IShared;

  private static readonly ServiceAssemblyFacts[] _twoServices = [
    new("Shared.Contracts", false, []),
    new("Orders", true, ["Shared.Contracts"]),
    new("Billing", true, ["Shared.Contracts"]),
  ];

  private static Action<IServiceCollection> _marks(string assembly, string slot) =>
    services => services.AddSingleton(new Registered(assembly, slot));

  /// <summary>Replaces the callbacks and facts with a process of two services; disposing puts the real ones back.</summary>
  private static Restore _process(params ServiceAssemblyFacts[] facts) {
    var callbacks = ServiceRegistrationCallbacks.ReplaceForTesting();
    var runners = PerspectiveRunnerCallbackRegistry.TakeCallbacksForTests();
    var recorded = WhizbangServiceAssemblies.ReplaceForTesting(facts.Length == 0 ? _twoServices : facts);
    return new Restore(() => {
      recorded.Dispose();
      PerspectiveRunnerCallbackRegistry.RestoreCallbacksForTests(runners);
      callbacks.Dispose();
    });
  }

  private sealed class Restore(Action restore) : IDisposable {
    public void Dispose() => restore();
  }

  private static void _registerDispatchers(params string[] assemblies) {
    foreach (var assembly in assemblies) {
      ServiceRegistrationCallbacks.RegisterDispatcher(assembly, _marks(assembly, "Dispatcher"));
    }
  }

  private static string[] _registered(IServiceProvider provider) =>
    [.. provider.GetServices<Registered>().Select(r => $"{r.Assembly}:{r.Slot}").Order(StringComparer.Ordinal)];

  private static string[] _registered(IServiceCollection services) =>
    [.. services.Where(d => d.ServiceType == typeof(Registered)).Select(d => (Registered)d.ImplementationInstance!)
      .Select(r => $"{r.Assembly}:{r.Slot}").Order(StringComparer.Ordinal)];

  [Test]
  public async Task TwoHostsInOneProcess_EachGetsOnlyItsOwnServicesRegistrationsAsync() {
    // The first service is this test assembly, so the host can anchor on one of its types.
    using var process = _process(
      new("Shared.Contracts", false, []),
      new(THIS_ASSEMBLY, true, ["Shared.Contracts"]),
      new("Billing", true, ["Shared.Contracts"]));
    _registerDispatchers("Shared.Contracts", THIS_ASSEMBLY, "Billing");

    var orders = new ServiceCollection();
    orders.AddWhizbang().ForServiceOf<ServiceScopedRegistrationTests>();
    var billing = new ServiceCollection();
    billing.AddWhizbang();
    billing.AddWhizbangServiceAnchor("Billing");

    await using var ordersHost = orders.BuildServiceProvider();
    await using var billingHost = billing.BuildServiceProvider();

    await Assert.That(_registered(ordersHost)).IsEquivalentTo([$"{THIS_ASSEMBLY}:Dispatcher", "Shared.Contracts:Dispatcher"]);
    await Assert.That(_registered(billingHost)).IsEquivalentTo(["Billing:Dispatcher", "Shared.Contracts:Dispatcher"]);
    await Assert.That(ordersHost.GetRequiredService<ServiceAssemblyScope>().Excluded).IsEquivalentTo(["Billing"]);
    await Assert.That(billingHost.GetRequiredService<ServiceAssemblyScope>().Excluded).IsEquivalentTo([THIS_ASSEMBLY]);
  }

  [Test]
  public async Task AHostThatNamesNoService_KeepsEveryAssemblysRegistrationsAsync() {
    using var process = _process();
    _registerDispatchers("Shared.Contracts", "Orders", "Billing");

    var services = new ServiceCollection();
    services.AddWhizbang();

    await Assert.That(_registered(services)).IsEquivalentTo(
      ["Billing:Dispatcher", "Orders:Dispatcher", "Shared.Contracts:Dispatcher"]);
  }

  [Test]
  public async Task AServiceSplitOverSeveralAssemblies_KeepsEveryOneBesideAnotherServiceAsync() {
    using var process = _process(
      new("Shared.Contracts", false, []),
      new("Shop.Data", true, ["Shared.Contracts"]),
      new("Shop.Handlers", false, ["Shared.Contracts"]),
      new("Shop.Api", true, ["Shop.Data", "Shop.Handlers", "Shared.Contracts"]),
      new("Billing", true, ["Shared.Contracts"]));
    _registerDispatchers("Shared.Contracts", "Shop.Data", "Shop.Handlers", "Shop.Api", "Billing");

    var services = new ServiceCollection();
    ServiceRegistrationCallbacks.InvokeAll(services, new ServiceRegistrationOptions());
    services.AddWhizbangServiceAnchor("Shop.Data");

    await Assert.That(_registered(services)).IsEquivalentTo(
      ["Shared.Contracts:Dispatcher", "Shop.Api:Dispatcher", "Shop.Data:Dispatcher", "Shop.Handlers:Dispatcher"]);
  }

  [Test]
  public async Task EverySlot_ReplaysOnlyTheHostsOwnServiceAsync() {
    using var process = _process();
    foreach (var assembly in (string[])["Orders", "Billing"]) {
      ServiceRegistrationCallbacks.RegisterLensServices(assembly, (s, _) => _marks(assembly, "Lens")(s));
      ServiceRegistrationCallbacks.RegisterPerspectiveServices(assembly, (s, _) => _marks(assembly, "Perspective")(s));
      ServiceRegistrationCallbacks.RegisterDispatcher(assembly, _marks(assembly, "Dispatcher"));
      ServiceRegistrationCallbacks.RegisterRawReceptors(assembly, _marks(assembly, "Raw"));
      ServiceRegistrationCallbacks.RegisterPinnedIdRegistry(assembly, _marks(assembly, "PinnedId"));
      ServiceRegistrationCallbacks.RegisterCollectiveApplyRegistry(assembly, _marks(assembly, "Collective"));
      PerspectiveRunnerCallbackRegistry.RegisterCallback(assembly, _marks(assembly, "Runners"));
    }

    var services = new ServiceCollection();
    services.AddWhizbangServiceAnchor("Orders");
    ServiceRegistrationCallbacks.InvokeAll(services, new ServiceRegistrationOptions());
    PerspectiveRunnerCallbackRegistry.InvokeRegistration(services);

    await Assert.That(_registered(services)).IsEquivalentTo([
      "Orders:Collective", "Orders:Dispatcher", "Orders:Lens", "Orders:Perspective", "Orders:PinnedId", "Orders:Raw", "Orders:Runners"]);
  }

  [Test]
  public async Task AnExcludedRunnerCallback_IsNotSpent_SoALaterAnchorOnItsServiceStillRegistersItAsync() {
    using var process = _process();
    PerspectiveRunnerCallbackRegistry.RegisterCallback("Orders", _marks("Orders", "Runners"));
    PerspectiveRunnerCallbackRegistry.RegisterCallback("Billing", _marks("Billing", "Runners"));

    var services = new ServiceCollection();
    services.AddWhizbangServiceAnchor("Orders");
    PerspectiveRunnerCallbackRegistry.InvokeRegistration(services);
    services.AddWhizbangServiceAnchor("Billing");
    PerspectiveRunnerCallbackRegistry.InvokeRegistration(services);
    PerspectiveRunnerCallbackRegistry.InvokeRegistration(services);

    await Assert.That(_registered(services)).IsEquivalentTo(["Billing:Runners", "Orders:Runners"])
      .Because("each callback runs once, when its service is first in scope");
  }

  [Test]
  public async Task ASharedRegistrationAnExcludedAssemblyMadeFirst_IsTheIncludedAssemblysAfterRescopingAsync() {
    // Both services' callbacks TryAdd the same service; the other service's ran first and won. Once the host is
    // anchored, the host's own registration must be the one served, as if the other's had never run.
    using var process = _process();
    ServiceRegistrationCallbacks.RegisterDispatcher("Billing", s => s.TryAddSingleton<IShared, BillingShared>());
    ServiceRegistrationCallbacks.RegisterDispatcher("Orders", s => s.TryAddSingleton<IShared, OrdersShared>());

    var services = new ServiceCollection();
    ServiceRegistrationCallbacks.InvokeAll(services, new ServiceRegistrationOptions());
    services.AddWhizbangServiceAnchor("Orders");
    await using var provider = services.BuildServiceProvider();

    await Assert.That(provider.GetRequiredService<IShared>()).IsTypeOf<OrdersShared>();
  }

  [Test]
  public async Task RegistrationsMadeBeforeAndAfterTheReplay_KeepTheirPlaceAsync() {
    using var process = _process();
    ServiceRegistrationCallbacks.RegisterDispatcher("Orders", s => s.TryAddSingleton<IShared, OrdersShared>());
    ServiceRegistrationCallbacks.RegisterDispatcher("Billing", _marks("Billing", "Dispatcher"));

    // Registered before the replay: the generated TryAdd defers to it.
    var before = new ServiceCollection();
    before.AddSingleton<IShared, BillingShared>();
    ServiceRegistrationCallbacks.InvokeAll(before, new ServiceRegistrationOptions());
    before.AddWhizbangServiceAnchor("Orders");

    // Registered after the replay: it stays after the replayed block, so it is the one served.
    var after = new ServiceCollection();
    ServiceRegistrationCallbacks.InvokeAll(after, new ServiceRegistrationOptions());
    after.AddSingleton<IShared, BillingShared>();
    after.AddWhizbangServiceAnchor("Orders");

    await using var beforeHost = before.BuildServiceProvider();
    await using var afterHost = after.BuildServiceProvider();
    await Assert.That(beforeHost.GetRequiredService<IShared>()).IsTypeOf<BillingShared>();
    await Assert.That(afterHost.GetRequiredService<IShared>()).IsTypeOf<BillingShared>();
    await Assert.That(_registered(after)).IsEmpty();
  }

  [Test]
  public async Task AnAnchorThatExcludesNothingNew_LeavesTheReplayedDescriptorsUntouchedAsync() {
    using var process = _process();
    _registerDispatchers("Orders", "Shared.Contracts");

    var services = new ServiceCollection();
    ServiceRegistrationCallbacks.InvokeAll(services, new ServiceRegistrationOptions());
    var replayed = services.Where(d => d.ServiceType == typeof(Registered)).ToArray();
    services.AddWhizbangServiceAnchor("Orders");
    services.AddWhizbangServiceAnchor("Orders");

    await Assert.That(services.Where(d => d.ServiceType == typeof(Registered)).SequenceEqual(replayed)).IsTrue()
      .Because("the anchor changes nothing the host replays, so the same descriptor instances stay");
    await Assert.That(services.Count(d => d.ServiceType == typeof(WhizbangServiceAnchor))).IsEqualTo(1);
  }

  [Test]
  public async Task ASecondAddWhizbang_ReplaysOnce_AndPicksUpAnAssemblyLoadedSinceAsync() {
    using var process = _process();
    _registerDispatchers("Orders");

    var services = new ServiceCollection();
    ServiceRegistrationCallbacks.InvokeAll(services, new ServiceRegistrationOptions());
    _registerDispatchers("Shared.Contracts");
    ServiceRegistrationCallbacks.InvokeAll(services, new ServiceRegistrationOptions());

    await Assert.That(_registered(services)).IsEquivalentTo(["Orders:Dispatcher", "Shared.Contracts:Dispatcher"]);
  }

  [Test]
  public async Task AnAnchorBeforeAnyReplay_OnlyRecordsTheAnchorAsync() {
    using var process = _process();
    _registerDispatchers("Orders", "Billing");

    var services = new ServiceCollection();
    services.AddWhizbangServiceAnchor("Billing");

    await Assert.That(_registered(services)).IsEmpty();
    await Assert.That(ServiceAssemblyScope.For(services).Excluded).IsEquivalentTo(["Orders"]);
  }

  [Test]
  public async Task ACallbackAssignedThroughItsProperty_HasNoAssembly_AndIsAlwaysReplayedAsync() {
    using var process = _process();
    ServiceRegistrationCallbacks.Dispatcher = _marks("Unnamed", "Dispatcher");
    _registerDispatchers("Billing");

    var services = new ServiceCollection();
    services.AddWhizbangServiceAnchor("Orders");
    ServiceRegistrationCallbacks.InvokeAll(services, new ServiceRegistrationOptions());

    await Assert.That(_registered(services)).IsEquivalentTo(["Unnamed:Dispatcher"]);
  }

  [Test]
  public async Task ACallbackThatReplacesOrRemovesAnEarlierRegistration_HasItsEffectKept_WithEverythingItAddedAsync() {
    // A callback may Replace or RemoveAll a registration made before the replay (a framework default it supersedes).
    // The replay must keep that effect and every descriptor the callback added, first one included, under every
    // rescope; an index-based "what came after" lost the first added descriptor and resurrected the replaced one.
    using var process = _process();
    ServiceRegistrationCallbacks.RegisterDispatcher("Orders", s => {
      s.AddSingleton(new Registered("Orders", "First"));
      s.Replace(ServiceDescriptor.Singleton<IShared, OrdersShared>());
      s.AddSingleton(new Registered("Orders", "Second"));
    });
    ServiceRegistrationCallbacks.RegisterDispatcher("Billing", s => s.RemoveAll<Registered>());

    var services = new ServiceCollection();
    services.AddSingleton<IShared, BillingShared>();
    services.AddSingleton(new Registered("Host", "Before"));
    services.AddSingleton(new Registered("Host", "AlsoBefore"));
    ServiceRegistrationCallbacks.InvokeAll(services, new ServiceRegistrationOptions());
    var unscoped = _registered(services);
    var unscopedShared = services.Count(d => d.ServiceType == typeof(IShared));

    services.AddWhizbangServiceAnchor("Orders");
    await using var provider = services.BuildServiceProvider();

    await Assert.That(unscoped).IsEmpty().Because("the other service's RemoveAll runs last while the host is unscoped");
    await Assert.That(unscopedShared).IsEqualTo(1);
    await Assert.That(_registered(services)).IsEquivalentTo(["Host:AlsoBefore", "Host:Before", "Orders:First", "Orders:Second"])
      .Because("excluding the other service undoes its RemoveAll, and the host's own callback keeps everything it added");
    string[] inOrder = [.. services.Where(d => d.ServiceType == typeof(Registered))
      .Select(d => ((Registered)d.ImplementationInstance!).Slot)];
    await Assert.That(inOrder).IsEquivalentTo(["Before", "AlsoBefore", "First", "Second"], TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("restored registrations go back to their own places, before the replayed block");
    await Assert.That(services.Count(d => d.ServiceType == typeof(IShared))).IsEqualTo(1);
    await Assert.That(provider.GetRequiredService<IShared>()).IsTypeOf<OrdersShared>();
  }

  [Test]
  public async Task TheCatalogSlot_IsEmptyUntilAnAssemblyRegisters_ThenServesTheOneUnionRegistrationAsync() {
    using var process = _process();
    var empty = ServiceRegistrationCallbacks.MessageTypeCatalog;

    ServiceRegistrationCallbacks.RegisterMessageTypeCatalog("Orders", static _ => { });
    var first = ServiceRegistrationCallbacks.MessageTypeCatalog;
    var second = ServiceRegistrationCallbacks.MessageTypeCatalog;

    await Assert.That(empty).IsNull().Because("no assembly registered a catalog, so there is nothing to register");
    await Assert.That(first).IsNotNull();
    await Assert.That(second).IsEqualTo(first).Because("every read serves the same union registration");
  }

  [Test]
  public async Task AKeyedAnchorDescriptor_IsNotAnAnchorAsync() {
    var services = new ServiceCollection();
    services.AddKeyedSingleton("elsewhere", new WhizbangServiceAnchor("Orders"));

    await Assert.That(WhizbangServiceScopeExtensions.AnchorsIn(services)).IsEmpty()
      .Because("anchors are registered unkeyed; a keyed descriptor of the type is someone else's");
  }

  [Test]
  public async Task AnchorArguments_AreValidatedAsync() {
    await Assert.That(() => ((IServiceCollection)null!).AddWhizbangServiceAnchor("Orders")).Throws<ArgumentNullException>();
    await Assert.That(() => new ServiceCollection().AddWhizbangServiceAnchor(" ")).Throws<ArgumentException>();
    await Assert.That(() => ((WhizbangBuilder)null!).ForServiceOf<ServiceScopedRegistrationTests>()).Throws<ArgumentNullException>();
    await Assert.That(() => ServiceAssemblyScope.For(null!)).Throws<ArgumentNullException>();
    await Assert.That(() => ServiceRegistrationCallbacks.RegisterDispatcher(null!, _ => { })).Throws<ArgumentNullException>();
    await Assert.That(() => PerspectiveRunnerCallbackRegistry.RegisterCallback(null!, _ => { })).Throws<ArgumentNullException>();
  }
}
