using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Coverage for <see cref="ModelRegistrationRegistry.InvokeRegistration"/>'s no-registrar early
/// return. This project's own <c>GeneratedModelRegistration</c> module initializer calls
/// <see cref="ModelRegistrationRegistry.RegisterModels"/> unconditionally at assembly load (it
/// registers the models this project's own perspective fixtures declare), so
/// <c>_registrars.Count == 0</c> is otherwise unreachable for the lifetime of this test process —
/// <see cref="ModelRegistrationRegistryTests"/>'s own "no registrar" test never actually observes an
/// empty registry for the same reason (it registers a callback first). Resetting the private static
/// list via reflection — the same technique <see cref="DbContextInitializationRegistryTests"/> uses
/// for the sibling <c>DbContextInitializationRegistry</c> registry — is the only way to reach the
/// branch. <see cref="NotInParallelAttribute"/> uses the exact key
/// <see cref="ModelRegistrationRegistryTests"/> already uses to guard the same static state, so the
/// two classes serialize against each other instead of racing.
/// </summary>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/ModelRegistrationRegistry.cs</code-under-test>
[NotInParallel("ModelRegistrationRegistry tests share static state")]
[Category("Shard1")]
public class ModelRegistrationRegistryCoverageTests {
  [Before(Test)]
  public void ResetStaticState() {
    var field = typeof(ModelRegistrationRegistry)
        .GetField("_registrars", BindingFlags.Static | BindingFlags.NonPublic)!;
    var list = (System.Collections.IList)field.GetValue(null)!;
    list.Clear();
  }

  // If no assembly ever registered a model callback, InvokeRegistration must gracefully do
  // nothing rather than throw — schema-only / diagnostic hosts (a migration CLI, a health-check
  // service) that never wire perspectives must still be able to boot. A throw here would crash
  // every such host at startup.
  [Test]
  public async Task InvokeRegistration_WithNoRegistrarsRegistered_CompletesWithoutRegisteringAnythingAsync() {
    var services = new ServiceCollection();
    var upsertStrategy = new InMemoryUpsertStrategy();
    var countBefore = services.Count;

    // Act & Assert — with zero registrars set, this must be a silent no-op rather than a startup
    // crash. An uncaught exception here fails the test just as surely as an explicit assertion.
    ModelRegistrationRegistry.InvokeRegistration(services, typeof(RegistryTestDbContext), upsertStrategy);

    await Assert.That(services.Count).IsEqualTo(countBefore)
      .Because("an empty registry must add nothing to the service collection");
  }
}
