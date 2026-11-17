// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// The DbContext a host registers names its service, and the host registers only that service's EF Core models
/// (#2243).
/// </summary>
/// <remarks>
/// The facts recorded here use names no real assembly has, so they stay inert for every other test in the process:
/// an assembly that is not loaded registers nothing, whatever its facts say.
/// </remarks>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/ModelRegistrationRegistry.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/EFCoreDriverSelector.cs</code-under-test>
[NotInParallel] // Resets the process-wide model registry, as ModelRegistrationRegistryTests does.
[Category("Shard2")]
public class ServiceScopedModelRegistrationTests {
  private const string ORDERS = "ServiceScopeTests.Orders";
  private const string BILLING = "ServiceScopeTests.Billing";

  private IDisposable? _restore;

  [Before(Test)]
  public void ResetStaticState() {
    _restore = ModelRegistrationRegistry.ResetForTesting();
    WhizbangServiceAssemblies.Register("ServiceScopeTests.Contracts", false, []);
    WhizbangServiceAssemblies.Register(ORDERS, true, ["ServiceScopeTests.Contracts"]);
    WhizbangServiceAssemblies.Register(BILLING, true, ["ServiceScopeTests.Contracts"]);
  }

  [After(Test)]
  public void RestoreStaticState() => _restore?.Dispose();

  [Test]
  public async Task WithEFCore_AnchorsTheHostOnTheDbContextsAssemblyAsync() {
    var services = new ServiceCollection();

    _ = services.AddWhizbang().WithEFCore<RegistryTestDbContext>();

    await Assert.That(ServiceAssemblyScope.For(services).Anchors)
      .IsEquivalentTo([typeof(RegistryTestDbContext).Assembly.GetName().Name!]);
  }

  [Test]
  public async Task InvokeRegistration_RegistersOnlyTheHostsOwnServicesModels_AndKeepsTheOthersForALaterAnchorAsync() {
    var ran = new List<string>();
    ModelRegistrationRegistry.RegisterModels(ORDERS, [], (_, _, _) => ran.Add(ORDERS));
    ModelRegistrationRegistry.RegisterModels(BILLING, [], (_, _, _) => ran.Add(BILLING));
    var services = new ServiceCollection();
    var strategy = new InMemoryUpsertStrategy();

    services.AddWhizbangServiceAnchor(ORDERS);
    ModelRegistrationRegistry.InvokeRegistration(services, typeof(RegistryTestDbContext), strategy);
    await Assert.That(ran).IsEquivalentTo([ORDERS]);

    services.AddWhizbangServiceAnchor(BILLING);
    ModelRegistrationRegistry.InvokeRegistration(services, typeof(RegistryTestDbContext), strategy);
    await Assert.That(ran).IsEquivalentTo([ORDERS, BILLING])
      .Because("another service's models are skipped, not spent, so anchoring on it later registers them once");
  }
}
