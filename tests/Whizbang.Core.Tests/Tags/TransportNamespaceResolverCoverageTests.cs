using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Tags;

namespace Whizbang.Core.Tests.Tags;

/// <summary>
/// Covers <see cref="TransportNamespaceResolver.ResolveConsumeNamespaceKeys"/>'s cheap early-out
/// when NO routing bindings exist at all. Every existing test in
/// <c>TransportNamespaceResolverTests</c> that exercises this method registers at least one
/// binding first, so <c>HasBindings == false</c> is never reached there.
/// </summary>
[Category("Core")]
[Category("Tags")]
public class TransportNamespaceResolverCoverageTests {

  /// <summary>
  /// If this guard were dropped, EVERY host with the tag-routing feature entirely unused would
  /// still pay the per-message registration-source walk (and enumerate whatever caller-supplied
  /// type names it was given) on every consume-namespace projection — exactly the per-message
  /// cost the guard exists to make disappear for the common case.
  /// </summary>
  [Test]
  public async Task ResolveConsumeNamespaceKeys_NoBindingsAtAll_ReturnsEmptyWithoutConsultingTheRegistryAsync() {
    var options = new TagOptions(); // no RouteNamespace bindings registered at all
    var resolver = new TransportNamespaceResolver(options, () =>
      throw new InvalidOperationException("the registration source must not be consulted when no "
                                         + "routing bindings exist at all"));

    var keys = resolver.ResolveConsumeNamespaceKeys(["Some.Fake.TypeName"]);

    await Assert.That(keys.Count).IsEqualTo(0)
      .Because("with no routing bindings configured, every type resolves to the default namespace "
             + "and the consume-side projection is empty by construction");
  }
}
