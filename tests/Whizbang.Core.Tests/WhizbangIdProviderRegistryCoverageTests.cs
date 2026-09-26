using Microsoft.Extensions.DependencyInjection;
using Whizbang.Core;

namespace Whizbang.Core.Tests;

/// <summary>
/// Coverage for <see cref="WhizbangIdProviderRegistry.CreateProvider{TId}"/>'s "no factory
/// registered" failure path.
/// </summary>
/// <remarks>
/// <see cref="WhizbangIdProviderRegistry"/> holds process-global static state (a static
/// <c>Dictionary</c> of factories, guarded by a <c>Lock</c>) that every ModuleInitializer in the
/// process writes into. The test below never registers anything, though: it queries with a struct
/// type that no generated ModuleInitializer in this assembly (or any other loaded one) ever passes
/// to <c>RegisterFactory</c>, so there is no shared mutation for a concurrently running test to
/// race against and no <c>[NotInParallel]</c> is needed here.
///
/// <para>
/// <c>InvokeDICallbacks</c>'s <c>_diRegistrations.Count == 0</c> guard needs the shared
/// registration list to be empty, which it never is in this process: this assembly's generated
/// <c>WhizbangIdProviderRegistration.g.cs</c> module initializer calls <c>RegisterDICallback</c>
/// before any test runs. The test below empties the list through the registry's own
/// take/restore seam and puts it back in a <c>finally</c>; it and everything else that touches
/// these registrations share the <c>WhizbangIdProviderRegistry</c> parallel key.
/// </para>
/// </remarks>
[NotInParallel("WhizbangIdProviderRegistry")]
public class WhizbangIdProviderRegistryCoverageTests {
  // A plain, un-attributed struct: it satisfies CreateProvider<TId>'s `where TId : struct`
  // constraint but carries no [WhizbangId] attribute, so no generated ModuleInitializer ever
  // registers a factory for it.
  private readonly struct UnregisteredCoverageId;

  // If this threw the wrong exception (or returned a mismatched/null provider) instead of failing
  // loudly with a message naming the unresolved type, a consumer whose source generator didn't run
  // for a [WhizbangId] struct would hit a confusing NullReferenceException or InvalidCastException
  // deep inside dispatch instead of a clear, actionable message at the call site.
  [Test]
  public async Task CreateProvider_WithNoFactoryRegisteredForType_ThrowsInvalidOperationExceptionNamingTheTypeAsync() {
    var baseProvider = new Uuid7IdProvider();

    await Assert.That(() => WhizbangIdProviderRegistry.CreateProvider<UnregisteredCoverageId>(baseProvider))
      .Throws<InvalidOperationException>()
      .WithMessageContaining(nameof(UnregisteredCoverageId));
  }

  // AddWhizbang calls this on every startup. A host that declares no [WhizbangId] struct anywhere
  // has no registrations, and must come up with no id provider registered at all — the guard is
  // what keeps the call from adding a Uuid7 base provider that nothing asked for and that would
  // then win over one the host registers itself later.
  [Test]
  public async Task InvokeDICallbacks_WithNoRegistrationsAtAll_RegistersNothingAsync() {
    var taken = WhizbangIdProviderRegistry.TakeDICallbacksForTests();
    try {
      var services = new ServiceCollection();

      WhizbangIdProviderRegistry.InvokeDICallbacks(services);

      await Assert.That(services.Count).IsEqualTo(0)
        .Because("with nothing registered there is no base provider to choose and no typed "
          + "provider to build, so the collection must be left exactly as it was found");
    } finally {
      WhizbangIdProviderRegistry.RestoreDICallbacksForTests(taken);
    }
  }

  // The control: with a registration present the same call does add the base provider and run the
  // callback, so the empty result above is the guard's answer rather than the method doing
  // nothing in general.
  [Test]
  public async Task InvokeDICallbacks_WithARegistration_AddsTheBaseProviderAndRunsItAsync() {
    var taken = WhizbangIdProviderRegistry.TakeDICallbacksForTests();
    try {
      var ran = 0;
      WhizbangIdProviderRegistry.RegisterDICallback((_, _) => ran++);
      var services = new ServiceCollection();

      WhizbangIdProviderRegistry.InvokeDICallbacks(services);

      await Assert.That(ran).IsEqualTo(1)
        .Because("the registered callback is what wires the typed providers");
      await Assert.That(services.Any(d => d.ServiceType == typeof(IWhizbangIdProvider))).IsTrue()
        .Because("the typed providers are built on a base provider, so one has to be registered");
    } finally {
      WhizbangIdProviderRegistry.RestoreDICallbacksForTests(taken);
    }
  }
}
