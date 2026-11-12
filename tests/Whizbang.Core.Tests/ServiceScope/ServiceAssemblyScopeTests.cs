// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Core.Tests.ServiceScope;

/// <summary>
/// The rule that decides which generated assemblies make up a host's service (#2243): a host excludes another
/// service's assemblies and nothing else.
/// </summary>
/// <remarks>
/// Each test describes a process by the facts every generated assembly records at build time (its name, whether it
/// is a service root, the generated assemblies it references) and the assemblies the host anchors on (the assembly
/// of each DbContext it registers). The process layouts are the ones a host meets in practice: one service, a
/// service split over several assemblies, one test process building a host per service, and a modular monolith.
/// </remarks>
/// <code-under-test>src/Whizbang.Core/ServiceAssemblyScope.cs</code-under-test>
public class ServiceAssemblyScopeTests {
  private static Dictionary<string, ServiceAssemblyFacts> _facts(params ServiceAssemblyFacts[] facts) =>
    facts.ToDictionary(f => f.Assembly, StringComparer.Ordinal);

  private static ServiceAssemblyFacts _root(string name, params string[] references) => new(name, true, references);

  private static ServiceAssemblyFacts _library(string name, params string[] references) => new(name, false, references);

  /// <summary>Two services that share a contracts assembly, as one test process loads them.</summary>
  private static Dictionary<string, ServiceAssemblyFacts> _twoServices() => _facts(
    _library("Shared.Contracts"),
    _library("Shared.Infrastructure", "Shared.Contracts"),
    _root("Orders", "Shared.Contracts", "Shared.Infrastructure"),
    _root("Billing", "Shared.Contracts", "Shared.Infrastructure"));

  [Test]
  public async Task NoAnchor_IncludesEveryAssembly_SoAHostThatNamesNoServiceKeepsTodaysRegistrationsAsync() {
    var scope = ServiceAssemblyScope.Compute([], _twoServices());

    await Assert.That(scope.IsScoped).IsFalse();
    await Assert.That(scope.Includes("Orders")).IsTrue();
    await Assert.That(scope.Includes("Billing")).IsTrue();
    await Assert.That(scope.Excluded).IsEmpty();
    await Assert.That(scope.UnscopedReason).IsNotNull();
  }

  [Test]
  public async Task TwoServicesInOneProcess_EachHostIncludesItsOwnServiceAndTheSharedAssemblies_NotTheOtherAsync() {
    var orders = ServiceAssemblyScope.Compute(["Orders"], _twoServices());
    var billing = ServiceAssemblyScope.Compute(["Billing"], _twoServices());

    await Assert.That(orders.IsScoped).IsTrue();
    await Assert.That(orders.Included).IsEquivalentTo(["Orders", "Shared.Contracts", "Shared.Infrastructure"]);
    await Assert.That(orders.Excluded).IsEquivalentTo(["Billing"]);
    await Assert.That(billing.Included).IsEquivalentTo(["Billing", "Shared.Contracts", "Shared.Infrastructure"]);
    await Assert.That(billing.Excluded).IsEquivalentTo(["Orders"]);
  }

  [Test]
  public async Task SingleServiceProcess_ExcludesNothing_WhicheverAssemblyTheHostAnchorsOnAsync() {
    // One service whose DbContext lives in a data assembly the executable references, with receptors in a helper
    // assembly the data assembly knows nothing about. Nothing else is loaded, so nothing may be excluded.
    var facts = _facts(
      _library("Shop.Contracts"),
      _root("Shop.Data", "Shop.Contracts"),
      _library("Shop.Handlers", "Shop.Contracts"),
      _root("Shop.Api", "Shop.Data", "Shop.Handlers", "Shop.Contracts"));

    var scope = ServiceAssemblyScope.Compute(["Shop.Data"], facts);

    await Assert.That(scope.Excluded).IsEmpty();
    await Assert.That(scope.Included).IsEquivalentTo(["Shop.Api", "Shop.Contracts", "Shop.Data", "Shop.Handlers"]);
  }

  [Test]
  public async Task ServiceSplitOverSeveralAssemblies_KeepsEveryOneOfThem_BesideAnotherServiceAsync() {
    // The same split service, now in a process that also loads another service: the executable above the anchor,
    // the helper only the executable references, and the data assembly all stay; only the other service goes.
    var facts = _facts(
      _library("Shared.Contracts"),
      _root("Shop.Data", "Shared.Contracts"),
      _library("Shop.Handlers", "Shared.Contracts"),
      _root("Shop.Api", "Shop.Data", "Shop.Handlers", "Shared.Contracts"),
      _root("Billing", "Shared.Contracts"));

    var scope = ServiceAssemblyScope.Compute(["Shop.Data"], facts);

    await Assert.That(scope.Included).IsEquivalentTo(["Shared.Contracts", "Shop.Api", "Shop.Data", "Shop.Handlers"]);
    await Assert.That(scope.Excluded).IsEquivalentTo(["Billing"]);
  }

  [Test]
  public async Task AnAssemblyOnlyAnotherServiceIsBuiltOn_IsExcludedWithItAsync() {
    var facts = _facts(
      _library("Shared.Contracts"),
      _library("Billing.Handlers", "Shared.Contracts"),
      _root("Billing", "Billing.Handlers", "Shared.Contracts"),
      _root("Orders", "Shared.Contracts"));

    var scope = ServiceAssemblyScope.Compute(["Orders"], facts);

    await Assert.That(scope.Excluded).IsEquivalentTo(["Billing", "Billing.Handlers"]);
    await Assert.That(scope.Includes("Billing.Handlers")).IsFalse();
  }

  [Test]
  public async Task ModularMonolith_AHostPerModule_GetsItsModuleAndTheCompositionRoot_NotTheOtherModuleAsync() {
    // A generated executable hosts two modules, each with its own DbContext and its own helper assembly.
    var facts = _facts(
      _library("Shared.Contracts"),
      _library("Catalog.Handlers", "Shared.Contracts"),
      _root("Catalog", "Catalog.Handlers", "Shared.Contracts"),
      _library("Shipping.Handlers", "Shared.Contracts"),
      _root("Shipping", "Shipping.Handlers", "Shared.Contracts"),
      _root("Monolith", "Catalog", "Shipping", "Catalog.Handlers", "Shipping.Handlers", "Shared.Contracts"));

    var catalog = ServiceAssemblyScope.Compute(["Catalog"], facts);

    await Assert.That(catalog.Included).IsEquivalentTo(["Catalog", "Catalog.Handlers", "Monolith", "Shared.Contracts"]);
    await Assert.That(catalog.Excluded).IsEquivalentTo(["Shipping", "Shipping.Handlers"]);
  }

  [Test]
  public async Task AHostAnchoredOnEveryModule_GetsThemAllAsync() {
    var both = ServiceAssemblyScope.Compute(["Orders", "Billing"], _twoServices());

    await Assert.That(both.Excluded).IsEmpty();
    await Assert.That(both.Included).IsEquivalentTo(["Billing", "Orders", "Shared.Contracts", "Shared.Infrastructure"]);
  }

  [Test]
  public async Task AGeneratedTestAssemblyAboveEveryService_StaysInEachHost_TheOtherServicesDoNotAsync() {
    var facts = _twoServices();
    facts["Tests"] = _root("Tests", "Orders", "Billing", "Shared.Contracts", "Shared.Infrastructure");

    var orders = ServiceAssemblyScope.Compute(["Orders"], facts);

    await Assert.That(orders.Includes("Tests")).IsTrue();
    await Assert.That(orders.Excluded).IsEquivalentTo(["Billing"]);
  }

  [Test]
  public async Task AServiceThatReferencesTheAnchoredService_IsPartOfItAsync() {
    // A service built on another service's assembly is above the anchor, so it is one of the anchor's own.
    var facts = _twoServices();
    facts["Billing"] = _root("Billing", "Orders", "Shared.Contracts");

    var orders = ServiceAssemblyScope.Compute(["Orders"], facts);

    await Assert.That(orders.Excluded).IsEmpty();
  }

  [Test]
  public async Task ReferencesAreFollowedTransitively_SoAnAssemblyTwoStepsBelowTheAnchorStaysAsync() {
    var facts = _facts(
      _library("Deep"),
      _library("Middle", "Deep", "Recorded.Nowhere"),
      _root("Orders", "Middle"),
      _root("Billing", "Deep"));

    var orders = ServiceAssemblyScope.Compute(["Orders"], facts);

    await Assert.That(orders.Includes("Deep")).IsTrue();
    await Assert.That(orders.Included).IsEquivalentTo(["Deep", "Middle", "Orders"]);
    await Assert.That(orders.Excluded).IsEquivalentTo(["Billing"]);
  }

  [Test]
  public async Task AnAnchorWithNoRecordedFacts_LeavesTheHostUnscoped_RatherThanGuessAtItsReferencesAsync() {
    // Without the anchor's references the shared assemblies cannot be told from the other service's, and excluding
    // them would break the host; every assembly stays and the reason says why.
    var scope = ServiceAssemblyScope.Compute(["Unknown.Service"], _twoServices());

    await Assert.That(scope.IsScoped).IsFalse();
    await Assert.That(scope.Includes("Billing")).IsTrue();
    await Assert.That(scope.UnscopedReason).Contains("Unknown.Service");
  }

  [Test]
  public async Task AnAssemblyWithNoIdentityOrNoFacts_IsAlwaysIncludedAsync() {
    var orders = ServiceAssemblyScope.Compute(["Orders"], _twoServices());

    await Assert.That(orders.Includes(null)).IsTrue()
      .Because("a registration made without an assembly name (a hand-written callback) cannot be another service's");
    await Assert.That(orders.Includes("Compiled.Without.Generator")).IsTrue()
      .Because("an assembly that recorded no facts is not known to be another service's");
  }

  [Test]
  public async Task ARootThatIsAlsoAnAnchor_CountsAsTheHostsOwnAsync() {
    // An anchor whose assembly is not itself flagged a root (a DbContext without the attribute) is still the host's.
    var facts = _twoServices();
    facts["Orders"] = _library("Orders", "Shared.Contracts", "Shared.Infrastructure");

    var orders = ServiceAssemblyScope.Compute(["Orders"], facts);

    await Assert.That(orders.Includes("Orders")).IsTrue();
    await Assert.That(orders.Excluded).IsEquivalentTo(["Billing"]);
  }

  [Test]
  public async Task Unscoped_IsTheSharedInstanceThatIncludesEverythingAsync() {
    await Assert.That(ServiceAssemblyScope.Unscoped.IsScoped).IsFalse();
    await Assert.That(ServiceAssemblyScope.Unscoped.Includes("Anything")).IsTrue();
    await Assert.That(ServiceAssemblyScope.Unscoped.Anchors).IsEmpty();
  }

  [Test]
  public async Task ToString_NamesTheAnchorsAndTheIncludedAndExcludedAssembliesAsync() {
    var orders = ServiceAssemblyScope.Compute(["Orders"], _twoServices());

    var text = orders.ToString();

    await Assert.That(text).Contains("Orders");
    await Assert.That(text).Contains("excluded [Billing]");
    await Assert.That(ServiceAssemblyScope.Unscoped.ToString()).Contains("unscoped");
  }
}
