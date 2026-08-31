using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.DependencyInjection;
using CoreManifest = Whizbang.Core.DependencyInjection.WhizbangServiceRequirements;

namespace Whizbang.Core.Tests.DependencyInjection;

/// <summary>
/// End-to-end checks on the generated requirements manifest.
/// </summary>
/// <remarks>
/// The generator tests prove it emits the right thing for a synthetic compilation. These prove the
/// manifest it emits for the framework itself is non-empty and usable, which is the part that would
/// silently regress: a generator that quietly stopped matching any registration would still compile,
/// still emit a valid empty array, and validate every composition successfully forever.
/// </remarks>
/// <docs>operations/dependency-injection/registration-validation</docs>
[Category("DependencyInjection")]
public class GeneratedManifestTests {

  [Test]
  public async Task TheFrameworkManifestIsNotEmptyAsync() {
    // An empty manifest validates everything successfully, which is indistinguishable from a
    // healthy composition and is exactly how this guard would fail silently.
    await Assert.That(CoreManifest.All).IsNotEmpty()
      .Because("a manifest that matched no registration would pass every composition forever "
             + "while checking nothing");
  }

  [Test]
  public async Task EveryRequirementNamesAtLeastOneDependencyAsync() {
    var empty = CoreManifest.All.Where(r => r.Dependencies.Count == 0).ToList();

    await Assert.That(empty).IsEmpty()
      .Because("a requirement with no dependencies imposes no obligation and only adds noise");
  }

  [Test]
  public async Task NoRequirementIsListedTwiceAsync() {
    var duplicates = CoreManifest.All
      .GroupBy(r => r.ImplementationType)
      .Where(g => g.Count() > 1)
      .Select(g => g.Key.Name)
      .ToList();

    // A type registered twice contributes the same requirement twice, which would report one gap
    // as two and overstate how much is broken.
    await Assert.That(duplicates).IsEmpty();
  }

  [Test]
  public async Task ValidatingTheManifestAgainstAnEmptyCollectionReportsGapsAsync() {
    var services = new ServiceCollection();

    var ex = Assert.Throws<WhizbangRegistrationException>(
      () => services.ValidateWhizbangRegistrations(CoreManifest.All));

    // Proves the manifest is actually exercised rather than merely present: against a collection
    // registering nothing, every dependency it names must be reported missing.
    await Assert.That(ex!.Missing).IsNotEmpty();
  }
}
