using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Coverage for two <see cref="IntegrityCheckpointReceptorRegistrar"/> branches the sibling
/// <c>IntegrityCheckpointReceptorTests.Registrar_RegistersReceptorAtThreeDefaultStagesAsync</c>
/// test never exercises: the no-registry early return, and <c>StopAsync</c> — that test only ever
/// registers <see cref="IReceptorRegistry"/> and only ever calls <c>StartAsync</c>.
/// </summary>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/IntegrityCheckpointReceptorRegistrar.cs</code-under-test>
[Category("Shard1")]
public class IntegrityCheckpointReceptorRegistrarCoverageTests {

  // Per the class's own doc comment, this guard is what lets "schema-only / diagnostic hosts still
  // boot" — a host that never wires a full IReceptorRegistry (e.g. a migration/CLI tool that only
  // needs the DbContext). If this regressed to calling GetRequiredService instead of the
  // null-tolerant lookup, every such host would crash on startup trying to register a receptor into
  // infrastructure it deliberately never wired.
  [Test]
  public async Task StartAsync_NoReceptorRegistryRegistered_CompletesWithoutRegisteringAnythingAsync() {
    var services = new ServiceCollection();
    // Deliberately NOT registered: IReceptorRegistry.
    await using var sp = services.BuildServiceProvider();
    var registrar = new IntegrityCheckpointReceptorRegistrar(
      sp, sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<IntegrityCheckpointReceptor>.Instance);

    // There is nothing else to observe here — a registry-less host must see zero side effects from
    // this hosted service — so the assertion IS the completion itself, not left as a bare await that
    // a bad refactor could silently drop without failing the test.
    await Assert.That(async () => await registrar.StartAsync(CancellationToken.None)).ThrowsNothing()
      .Because("GetService (not GetRequiredService) must be used for the registry lookup — an "
             + "unregistered IReceptorRegistry must short-circuit, not throw");
  }

  // IHostedService.StopAsync is called on EVERY registered hosted service during host shutdown,
  // including this one. It carries no unregister logic (LifecycleReceptorRegistry has no
  // Unregister-on-shutdown story), so it must be a genuine no-op — but a no-op that actually
  // completes. If it ever threw or hung, it would disrupt orderly shutdown of every OTHER hosted
  // service the host is also stopping alongside it.
  [Test]
  public async Task StopAsync_ReturnsAnAlreadyCompletedTaskAsync() {
    var services = new ServiceCollection();
    await using var sp = services.BuildServiceProvider();
    var registrar = new IntegrityCheckpointReceptorRegistrar(
      sp, sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<IntegrityCheckpointReceptor>.Instance);

    var stopTask = registrar.StopAsync(CancellationToken.None);

    await Assert.That(stopTask.IsCompleted).IsTrue()
      .Because("StopAsync has no async work to do — it must return an already-completed task, not "
             + "leave the host waiting during shutdown");
    await stopTask;
  }
}
