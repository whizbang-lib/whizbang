#nullable disable
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Perspectives;

namespace Whizbang.Data.EFCore.Postgres.Tests;

#pragma warning disable CA1707
#pragma warning disable IDE1006

/// <summary>
/// Locks the turnkey EFCore Postgres path's DLQ wiring. v0.502 added
/// <see cref="IDeadLetterStore"/> + <see cref="IDeadLetterRecoveryService"/> as
/// optional collaborators on the dispatch/recovery workers but the original
/// commit forgot to register the EFCore implementations — production symptom
/// observed by a consumer: <c>wh_dead_letters</c> empty even with WRN logs firing.
/// </summary>
public class PostgresDriverExtensions_DeadLetterRegistrationTests {

  [Test]
  public async Task Postgres_RegistersDeadLetterStoreAsync() {
    // After the turnkey property runs, IDeadLetterStore must resolve to a non-null
    // implementation. The dispatch worker is a singleton that injects
    // IDeadLetterStore? — registration must satisfy a singleton consumer.
    var services = new ServiceCollection();
    services.AddDbContext<PostgresTestDbContext>(options =>
        options.UseInMemoryDatabase("DlqRegistrationDb"));

    var builder = new WhizbangPerspectiveBuilder(services);
    var selector = builder.WithEFCore<PostgresTestDbContext>();
    _ = selector.WithDriver.Postgres;

    using var sp = services.BuildServiceProvider();
    var store = sp.GetService<IDeadLetterStore>();

    await Assert.That(store).IsNotNull()
      .Because("InboxDispatchWorker depends on IDeadLetterStore to move failed rows into " +
               "wh_dead_letters. Without registration, _deadLetterStore is null and the " +
               "DLQ branch silently falls back to mark-Published — wh_dead_letters stays empty.");
  }

  [Test]
  public async Task Postgres_RegistersDeadLetterRecoveryServiceAsync() {
    // IDeadLetterRecoveryService is resolved by DeadLetterRecoveryWorker via a
    // fresh scope per scan (the worker explicitly uses GetService — null = no-op).
    // Register as Scoped so it composes with the consumer's scoped DbContext.
    var services = new ServiceCollection();
    services.AddDbContext<PostgresTestDbContext>(options =>
        options.UseInMemoryDatabase("DlqRegistrationDb"));

    var builder = new WhizbangPerspectiveBuilder(services);
    var selector = builder.WithEFCore<PostgresTestDbContext>();
    _ = selector.WithDriver.Postgres;

    using var sp = services.BuildServiceProvider();
    using var scope = sp.CreateScope();
    var recovery = scope.ServiceProvider.GetService<IDeadLetterRecoveryService>();

    await Assert.That(recovery).IsNotNull()
      .Because("DeadLetterRecoveryWorker scans wh_dead_letters via IDeadLetterRecoveryService. " +
               "Without registration, the recovery worker degrades to a no-op and dead-lettered " +
               "rows never get retried.");
  }
}
