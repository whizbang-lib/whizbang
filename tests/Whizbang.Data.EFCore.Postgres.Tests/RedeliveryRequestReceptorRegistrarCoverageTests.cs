using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Coverage for <see cref="RedeliveryRequestReceptorRegistrar.StopAsync"/> — no existing test in
/// this project drives the registrar's <c>IHostedService.StopAsync</c> path. Mirrors the sibling
/// <c>IntegrityCheckpointReceptorRegistrarCoverageTests.StopAsync_ReturnsAnAlreadyCompletedTaskAsync</c>
/// pattern for the same registrar shape.
/// </summary>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/RedeliveryRequestReceptorRegistrar.cs</code-under-test>
[Category("Shard1")]
public class RedeliveryRequestReceptorRegistrarCoverageTests {

  // IHostedService.StopAsync runs for EVERY registered hosted service during host shutdown. This
  // registrar carries no unregister logic, so it must be a genuine no-op that actually completes —
  // if it ever threw or hung, it would disrupt the orderly shutdown of every OTHER hosted service
  // the host is stopping alongside it.
  [Test]
  public async Task StopAsync_ReturnsAnAlreadyCompletedTaskAsync() {
    var services = new ServiceCollection();
    await using var sp = services.BuildServiceProvider();
    var registrar = new RedeliveryRequestReceptorRegistrar(
      sp, sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<RedeliveryRequestReceptor>.Instance);

    var stopTask = registrar.StopAsync(CancellationToken.None);

    await Assert.That(stopTask.IsCompleted).IsTrue()
      .Because("StopAsync has no async work to do — it must return an already-completed task, not "
             + "leave the host waiting during shutdown");
    await stopTask;
  }
}
