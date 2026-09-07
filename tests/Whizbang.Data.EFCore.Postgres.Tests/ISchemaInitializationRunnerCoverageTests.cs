using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Coverage for <see cref="DbContextSchemaInitializationRunner.RunAsync"/> — the production
/// <see cref="ISchemaInitializationRunner"/> that delegates to the static
/// <see cref="DbContextInitializationRegistry"/>. This project's own generated module initializer
/// registers real DbContext initializers there at assembly load (see
/// <see cref="DbContextInitializationRegistryTests"/>), so a call against the untouched static
/// state would try to resolve and migrate real DbContexts — requiring a live database. Resetting
/// the private static list via the same reflection technique
/// <see cref="DbContextInitializationRegistryTests.ResetStaticState"/> already uses lets
/// <c>RunAsync</c> delegate into an empty registry, which the registry's own contract guarantees
/// completes without touching a database (see <c>DbContextInitializationRegistryTests
/// .InitializeAllAsync_WithNoRegistrations_CompletesSuccessfullyAsync</c>). <see
/// cref="NotInParallelAttribute"/> uses the exact key <see cref="DbContextInitializationRegistryTests"/>
/// already uses to guard the same static state, so the two classes serialize against each other
/// instead of racing. No database is used in this file.
/// </summary>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/ISchemaInitializationRunner.cs</code-under-test>
[NotInParallel("DbContextInitializationRegistry")]
[Category("Shard1")]
public class ISchemaInitializationRunnerCoverageTests {
  [Before(Test)]
  public void ResetStaticState() {
    var field = typeof(DbContextInitializationRegistry)
        .GetField("_initializers", BindingFlags.Static | BindingFlags.NonPublic)!;
    var list = (System.Collections.IList)field.GetValue(null)!;
    list.Clear();
  }

  private sealed class _fakeServiceProvider : IServiceProvider {
    public object? GetService(Type serviceType) => null;
  }

  private sealed class _fakeDbContext;

  // The runner is the seam WhizbangDatabaseInitializerService calls into for its
  // blocking/non-blocking + timeout orchestration. If this delegation line regressed to call
  // something other than the shared registry (or swallowed the call entirely), schema
  // initialization would silently stop running at host startup — every consumer would boot
  // against an un-migrated database and fail on the first query instead of at startup.
  [Test]
  public async Task RunAsync_InvokesTheRegisteredDbContextInitializationCallbackAsync() {
    var called = false;
    DbContextInitializationRegistry.Register<_fakeDbContext>((_, _, _) => {
      called = true;
      return Task.CompletedTask;
    });
    var runner = new DbContextSchemaInitializationRunner(
      new _fakeServiceProvider(), NullLogger<DbContextSchemaInitializationRunner>.Instance);

    await runner.RunAsync(CancellationToken.None);

    await Assert.That(called).IsTrue()
      .Because("RunAsync must actually delegate to DbContextInitializationRegistry.InitializeAllAsync "
             + "— a different delegation target or a swallowed call would still complete without "
             + "throwing, but the registered callback would never run");
  }
}
