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
/// <c>WhizbangIdProviderRegistry.InvokeDICallbacks</c>'s <c>_diRegistrations.Count == 0</c> guard
/// (the method's first few lines) is a second uncovered branch in this class, but it is left
/// untested here on purpose: this test assembly's own generated
/// <c>WhizbangIdProviderRegistration.g.cs</c> module initializer calls
/// <c>RegisterDICallback</c> unconditionally before any test runs, so the shared registration list
/// is never empty in this process. The only way to force it empty is to reach into the private
/// static list and clear it, which would race every other test in the assembly that calls
/// <c>RegisterAllWithDI</c>/<c>InvokeDICallbacks</c> without a <c>[NotInParallel]</c> guard of its
/// own (e.g. <c>WhizbangIdProviderRegistryTests.RegisterAllWithDI_CallsAllRegisteredCallbacksAsync</c>,
/// a file this effort must not touch) — an unacceptable stability cost for one line.
/// </para>
/// </remarks>
public class WhizbangIdProviderRegistryCoverageTests {
  // A plain, un-attributed struct: it satisfies CreateProvider<TId>'s `where TId : struct`
  // constraint but carries no [WhizbangId] attribute, so no generated ModuleInitializer ever
  // registers a factory for it.
  private readonly struct _unregisteredCoverageId;

  // If this threw the wrong exception (or returned a mismatched/null provider) instead of failing
  // loudly with a message naming the unresolved type, a consumer whose source generator didn't run
  // for a [WhizbangId] struct would hit a confusing NullReferenceException or InvalidCastException
  // deep inside dispatch instead of a clear, actionable message at the call site.
  [Test]
  public async Task CreateProvider_WithNoFactoryRegisteredForType_ThrowsInvalidOperationExceptionNamingTheTypeAsync() {
    var baseProvider = new Uuid7IdProvider();

    await Assert.That(() => WhizbangIdProviderRegistry.CreateProvider<_unregisteredCoverageId>(baseProvider))
      .Throws<InvalidOperationException>()
      .WithMessageContaining(nameof(_unregisteredCoverageId));
  }
}
