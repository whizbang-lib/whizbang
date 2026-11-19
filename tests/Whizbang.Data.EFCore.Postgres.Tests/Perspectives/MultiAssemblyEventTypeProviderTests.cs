// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Messaging;

namespace Whizbang.Data.EFCore.Postgres.Tests.Perspectives;

/// <summary>
/// A host with two perspective assemblies serves one <see cref="IEventTypeProvider"/> that knows both assemblies'
/// events (#2146).
/// </summary>
/// <remarks>
/// This assembly's registration is the real generated <c>AddPerspectiveRunners</c>; the second assembly is a provider
/// registered through the same call the generator emits. The perspective worker, its drain and the inbox pre-filter
/// all resolve the single provider, so what it lists here is what they read.
/// </remarks>
/// <code-under-test>src/Whizbang.Core/Messaging/EventTypeProviderServiceCollectionExtensions.cs</code-under-test>
[Category("Shard3")]
public class MultiAssemblyEventTypeProviderTests {
  private sealed record SecondAssemblyEvent;
  private sealed record SecondAssemblyCollectiveEvent;

  private sealed class SecondAssemblyRegistry : IEventTypeProvider {
    public IReadOnlyList<Type> GetEventTypes() => [typeof(SecondAssemblyEvent), typeof(SecondAssemblyCollectiveEvent)];
  }

  [Test]
  [Arguments(true)]
  [Arguments(false)]
  public async Task TwoPerspectiveAssemblies_TheServedProvider_ListsBothAsync(bool generatedAssemblyFirst) {
    var services = new ServiceCollection();
    services.TryAddWhizbangDefaults();
    if (generatedAssemblyFirst) {
      Generated.PerspectiveRunnerRegistryExtensions.AddPerspectiveRunners(services);
    }
    services.AddSingleton<SecondAssemblyRegistry>();
    services.AddEventTypeProviderContribution<SecondAssemblyRegistry>();
    if (!generatedAssemblyFirst) {
      Generated.PerspectiveRunnerRegistryExtensions.AddPerspectiveRunners(services);
    }
    await using var provider = services.BuildServiceProvider();
    var thisAssemblysTypes = new Generated.PerspectiveRunnerRegistry().GetEventTypes();

    var served = provider.GetRequiredService<IEventTypeProvider>().GetEventTypes();

    await Assert.That(thisAssemblysTypes).IsNotEmpty().Because("precondition: this assembly declares perspectives");
    foreach (var type in thisAssemblysTypes) {
      await Assert.That(served).Contains(type)
        .Because($"{type.Name} is folded by this assembly's perspectives, whichever assembly registered last");
    }
    await Assert.That(served).Contains(typeof(SecondAssemblyEvent));
    await Assert.That(served).Contains(typeof(SecondAssemblyCollectiveEvent))
      .Because("collective events the other assembly knows reach the drain too");
  }
}
