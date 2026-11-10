// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Perspectives;

namespace Whizbang.Data.EFCore.Postgres.Tests.Perspectives;

/// <summary>
/// A host with two perspective assemblies serves one <see cref="IPerspectiveRunnerRegistry"/> that finds both
/// assemblies' runners (#2191).
/// </summary>
/// <remarks>
/// This assembly's registration is the real generated <c>AddPerspectiveRunners</c>; the second assembly is a registry
/// registered through the same call the generator emits. The perspective worker, the rebuilder, the maintenance worker
/// and the stream disposition step all resolve the single registry, so what it finds here is what they find.
/// </remarks>
/// <code-under-test>src/Whizbang.Core/Perspectives/PerspectiveRunnerRegistryServiceCollectionExtensions.cs</code-under-test>
[Category("Shard3")]
public class MultiAssemblyPerspectiveRunnerRegistryTests {
  private const string SECOND_ASSEMBLY_PERSPECTIVE = "SecondAssembly.Perspectives.LedgerPerspective";

  private sealed class SecondAssemblyRunner : IPerspectiveRunner {
    public Type PerspectiveType => typeof(object);

    public Task<PerspectiveCursorCompletion> RunAsync(Guid streamId, string perspectiveName, Guid? lastProcessedEventId, CancellationToken cancellationToken = default) =>
      throw new NotSupportedException();

    public Task<PerspectiveCursorCompletion> RewindAndRunAsync(Guid streamId, string perspectiveName, Guid triggeringEventId, CancellationToken cancellationToken = default) =>
      throw new NotSupportedException();

    public Task BootstrapSnapshotAsync(Guid streamId, string perspectiveName, Guid lastProcessedEventId, CancellationToken cancellationToken = default) =>
      throw new NotSupportedException();
  }

  private sealed class SecondAssemblyRegistry : IPerspectiveRunnerRegistry {
    public IPerspectiveRunner? GetRunner(string perspectiveName, IServiceProvider serviceProvider) =>
      perspectiveName == SECOND_ASSEMBLY_PERSPECTIVE ? new SecondAssemblyRunner() : null;

    public IReadOnlyList<PerspectiveRegistrationInfo> GetRegisteredPerspectives() =>
      [new PerspectiveRegistrationInfo(SECOND_ASSEMBLY_PERSPECTIVE, $"global::{SECOND_ASSEMBLY_PERSPECTIVE}", "global::SecondAssembly.Ledger", [])];

    public IReadOnlyList<Type> GetEventTypes() => [];

    public IReadOnlySet<LifecycleStage> LifecycleStagesWithReceptors { get; } = new HashSet<LifecycleStage>();
  }

  /// <summary>
  /// Records the services a lookup asks for. A generated registry resolves the runner it found from the provider it
  /// is given, so the runner type requested is the proof the name reached the assembly that declares it, without
  /// building every dependency a real runner takes.
  /// </summary>
  private sealed class RecordingServiceProvider : IServiceProvider {
    public List<Type> Requested { get; } = [];

    public object? GetService(Type serviceType) {
      Requested.Add(serviceType);
      return null;
    }
  }

  [Test]
  [Arguments(true)]
  [Arguments(false)]
  public async Task TwoPerspectiveAssemblies_TheServedRegistry_FindsBothAssembliesRunnersAsync(bool generatedAssemblyFirst) {
    var services = new ServiceCollection();
    services.TryAddWhizbangDefaults();
    if (generatedAssemblyFirst) {
      Generated.PerspectiveRunnerRegistryExtensions.AddPerspectiveRunners(services);
    }
    services.AddSingleton<SecondAssemblyRegistry>();
    services.AddPerspectiveRunnerRegistryContribution<SecondAssemblyRegistry>();
    if (!generatedAssemblyFirst) {
      Generated.PerspectiveRunnerRegistryExtensions.AddPerspectiveRunners(services);
    }
    await using var provider = services.BuildServiceProvider();
    var thisAssemblysPerspectives = new Generated.PerspectiveRunnerRegistry().GetRegisteredPerspectives();
    var served = provider.GetRequiredService<IPerspectiveRunnerRegistry>();

    var listed = served.GetRegisteredPerspectives().Select(p => p.ClrTypeName).ToHashSet();

    await Assert.That(thisAssemblysPerspectives).IsNotEmpty().Because("precondition: this assembly declares perspectives");
    foreach (var perspectiveName in thisAssemblysPerspectives.Select(p => p.ClrTypeName)) {
      await Assert.That(listed).Contains(perspectiveName)
        .Because($"{perspectiveName} is declared by this assembly, whichever assembly registered last");
      var recording = new RecordingServiceProvider();
      await Assert.That(() => served.GetRunner(perspectiveName, recording)).Throws<InvalidOperationException>()
        .Because("the generated registry resolves the runner it found; the recording provider has none to give");
      await Assert.That(recording.Requested.Any(t => t.Name.EndsWith("Runner", StringComparison.Ordinal))).IsTrue()
        .Because($"the lookup for {perspectiveName} must reach this assembly's registry and ask for its runner");
    }
    await Assert.That(listed).Contains(SECOND_ASSEMBLY_PERSPECTIVE);
    await Assert.That(served.GetRunner(SECOND_ASSEMBLY_PERSPECTIVE, provider)).IsTypeOf<SecondAssemblyRunner>()
      .Because("the other assembly's runner is found too, whichever assembly registered last");
  }
}
