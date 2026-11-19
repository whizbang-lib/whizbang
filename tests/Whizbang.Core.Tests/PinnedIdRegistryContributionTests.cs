// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.SystemEvents;

namespace Whizbang.Core.Tests;

/// <summary>
/// Every assembly's pinned types reach the one <see cref="IPinnedIdRegistry"/> the container serves (#2198).
/// </summary>
/// <remarks>
/// Whizbang.Core pins types of its own, so a host always has two assemblies' registries: Whizbang.Core's and its
/// own. Each was registered as a plain singleton from a startup slot the next assembly overwrote, so only one of
/// them was ever known.
/// </remarks>
/// <code-under-test>src/Whizbang.Core/CompositePinnedIdRegistry.cs</code-under-test>
public class PinnedIdRegistryContributionTests {
  private sealed record HostEvent;
  private sealed record SharedEvent;

  private abstract class AssemblyRegistry(params (Type Type, string PinnedId)[] pairs) : IPinnedIdRegistry {
    public string? GetPinnedId(Type type) => pairs.FirstOrDefault(p => p.Type == type).PinnedId;
    public IReadOnlyList<(Type Type, string PinnedId)> GetAll() => pairs;
  }

  private sealed class HostRegistry() : AssemblyRegistry(
    (typeof(HostEvent), "11111111-0000-0000-0000-000000000001"), (typeof(SharedEvent), "11111111-0000-0000-0000-00000000000a"));

  private sealed class OtherRegistry() : AssemblyRegistry((typeof(SharedEvent), "22222222-0000-0000-0000-00000000000a"));

  [Test]
  public async Task TwoAssemblies_EachAssemblysPinnedTypeIsKnownAsync() {
    var services = new ServiceCollection();
    Whizbang.Core.Generated.PinnedIdRegistryExtensions.AddPinnedIdRegistry(services);
    services.AddSingleton<HostRegistry>();
    services.AddPinnedIdRegistryContribution<HostRegistry>();
    await using var provider = services.BuildServiceProvider();

    var registry = provider.GetRequiredService<IPinnedIdRegistry>();

    await Assert.That(registry.GetPinnedId(typeof(EventAudited))).IsEqualTo("a917ce3a-52ce-4c20-92de-99ab649c2ebe")
      .Because("Whizbang.Core's own pinned types stay known when the host registers its own registry after it");
    await Assert.That(registry.GetPinnedId(typeof(HostEvent))).IsEqualTo("11111111-0000-0000-0000-000000000001");
    await Assert.That(registry.GetAll().Any(p => p.Type == typeof(EventAudited))).IsTrue();
    await Assert.That(registry.GetAll().Any(p => p.Type == typeof(HostEvent))).IsTrue();
  }

  [Test]
  public async Task ATypeTwoAssembliesPin_AnswersFromTheLatest_AndIsListedOnceAsync() {
    var registry = new CompositePinnedIdRegistry([new OtherRegistry(), new HostRegistry()]);

    await Assert.That(registry.GetPinnedId(typeof(SharedEvent))).IsEqualTo("22222222-0000-0000-0000-00000000000a")
      .Because("the first registry given, the latest registered, answers");
    await Assert.That(registry.GetAll().Count(p => p.Type == typeof(SharedEvent))).IsEqualTo(1);
    await Assert.That(registry.GetPinnedId(typeof(string))).IsNull().Because("a type no registry pins has no pinned id");
  }

  [Test]
  public async Task ThePinnedIdRegistry_ReportsTypesTwoAssembliesPinAsync() {
    var services = new ServiceCollection();
    services.AddSingleton<HostRegistry>();
    services.AddPinnedIdRegistryContribution<HostRegistry>();
    services.AddSingleton<OtherRegistry>();
    services.AddPinnedIdRegistryContribution<OtherRegistry>();
    await using var provider = services.BuildServiceProvider();

    var sources = provider.GetServices<IRegistrationCollisionSource>().ToArray();

    await Assert.That(sources.Length).IsEqualTo(1);
    await Assert.That(sources[0].GetCollisions()).IsEmpty()
      .Because("both stand-in registries are declared in this one test assembly, which cannot hide its own pin");
  }

  [Test]
  public async Task Guards_ThrowAsync() {
    await Assert.That(() => PinnedIdRegistryServiceCollectionExtensions.AddPinnedIdRegistryContribution<HostRegistry>(null!))
      .Throws<ArgumentNullException>();
    await Assert.That(() => new CompositePinnedIdRegistry(null!)).Throws<ArgumentNullException>();
  }
}
