using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;

namespace Whizbang.Data.Dapper.Postgres.Tests;

#pragma warning disable CA1707
#pragma warning disable IDE1006

/// <summary>
/// Locks the Dapper turnkey path's DLQ wiring. v0.502 added
/// <see cref="IDeadLetterStore"/> as an optional collaborator on the dispatch
/// worker but the original commit forgot to register the Dapper implementation —
/// services calling <c>AddWhizbangPostgres</c> got a null store and the DLQ
/// branch silently fell back to mark-Published.
/// </summary>
public class ServiceCollectionExtensions_DeadLetterRegistrationTests {

  [Test]
  public async Task AddDeadLetterStore_RegistersDeadLetterStoreAsSingletonAsync() {
    // The internal helper is the registration seam called from both
    // AddWhizbangPostgres overloads. Direct test of the helper keeps this a
    // fast unit test (no Postgres / Docker required) — the DapperDeadLetterStore
    // constructor only stashes the connection string; no I/O at registration.
    var services = new ServiceCollection();
    ServiceCollectionExtensions._addDeadLetterStore(
      services,
      "Host=localhost;Database=test;Username=u;Password=p");

    using var sp = services.BuildServiceProvider();
    var store = sp.GetService<IDeadLetterStore>();

    await Assert.That(store).IsNotNull()
      .Because("Dapper turnkey path must register IDeadLetterStore so InboxDispatchWorker " +
               "can move failed rows into wh_dead_letters. Without registration, the worker " +
               "field is null and dead-lettered rows are silently marked Published instead.");
  }

  [Test]
  public async Task AddDeadLetterStore_RegistersAsDapperDeadLetterStoreAsync() {
    var services = new ServiceCollection();
    ServiceCollectionExtensions._addDeadLetterStore(
      services,
      "Host=localhost;Database=test;Username=u;Password=p");

    using var sp = services.BuildServiceProvider();
    var store = sp.GetService<IDeadLetterStore>();

    await Assert.That(store).IsTypeOf<DapperDeadLetterStore>();
  }
}
