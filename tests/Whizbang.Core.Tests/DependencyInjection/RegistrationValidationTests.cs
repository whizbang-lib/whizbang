using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.DependencyInjection;

namespace Whizbang.Core.Tests.DependencyInjection;

/// <summary>
/// Tests for reflection-free validation of a composed service collection.
/// </summary>
/// <remarks>
/// <para>
/// The defect class this guards against is a dependency that is declared but never registered. It
/// produces no error: the container hands back null, the feature is silently absent, and the
/// behavior is indistinguishable from behavior nobody asked for. It has shipped repeatedly.
/// </para>
/// <para>
/// Validation reads <see cref="ServiceDescriptor.ServiceType"/> and compares type handles. It never
/// activates a service, so it introduces no reflection and cannot be defeated by a registration
/// whose factory has side effects, and it runs before anything is constructed.
/// </para>
/// </remarks>
/// <docs>operations/dependency-injection/registration-validation</docs>
[Category("DependencyInjection")]
public class RegistrationValidationTests {

  [Test]
  public async Task MissingDependencyThrowsNamingBothTheServiceAndItsDependentAsync() {
    var services = new ServiceCollection();
    services.AddSingleton<IPresent, Present>();
    var requirements = new[] { new ServiceRequirement(typeof(Consumer), [typeof(IPresent), typeof(IAbsent)]) };

    var ex = Assert.Throws<WhizbangRegistrationException>(
      () => services.ValidateWhizbangRegistrations(requirements));

    // Naming only the missing service leaves an engineer grepping for who wanted it. The pair is
    // what makes the message actionable.
    await Assert.That(ex!.Missing).Count().IsEqualTo(1);
    await Assert.That(ex.Missing[0].MissingService).IsEqualTo(typeof(IAbsent));
    await Assert.That(ex.Missing[0].NeededBy).IsEqualTo(typeof(Consumer));
    await Assert.That(ex.Message).Contains(nameof(IAbsent));
    await Assert.That(ex.Message).Contains(nameof(Consumer));
  }

  [Test]
  public async Task AllDependenciesRegisteredPassesAsync() {
    var services = new ServiceCollection();
    services.AddSingleton<IPresent, Present>();
    var requirements = new[] { new ServiceRequirement(typeof(Consumer), [typeof(IPresent)]) };

    var returned = services.ValidateWhizbangRegistrations(requirements);

    await Assert.That(returned).IsSameReferenceAs(services)
      .Because("validation is a chainable step in a registration pipeline");
  }

  [Test]
  public async Task EveryMissingDependencyIsReportedNotJustTheFirstAsync() {
    var services = new ServiceCollection();
    var requirements = new[] {
      new ServiceRequirement(typeof(Consumer), [typeof(IAbsent), typeof(IAlsoAbsent)]),
      new ServiceRequirement(typeof(OtherConsumer), [typeof(IAbsent)]),
    };

    var ex = Assert.Throws<WhizbangRegistrationException>(
      () => services.ValidateWhizbangRegistrations(requirements));

    // Failing on the first forces one fix-and-rerun cycle per missing service. A composition with
    // five gaps should report five.
    await Assert.That(ex!.Missing).Count().IsEqualTo(3);
  }

  [Test]
  public async Task ValidationNeverActivatesAServiceAsync() {
    var services = new ServiceCollection();
    var activated = false;
    services.AddSingleton<IPresent>(_ => { activated = true; return new Present(); });
    var requirements = new[] { new ServiceRequirement(typeof(Consumer), [typeof(IPresent)]) };

    services.ValidateWhizbangRegistrations(requirements);

    // Resolving to test presence would construct the graph, which for type-based registrations goes
    // through the container's reflective activator and defeats the whole point.
    await Assert.That(activated).IsFalse()
      .Because("validation must inspect descriptors, not build them, or it introduces both "
             + "reflection and side effects into startup");
  }

  [Test]
  public async Task ADependencySatisfiedByAFactoryRegistrationCountsAsPresentAsync() {
    var services = new ServiceCollection();
    services.AddSingleton<IPresent>(_ => new Present());
    var requirements = new[] { new ServiceRequirement(typeof(Consumer), [typeof(IPresent)]) };

    var returned = services.ValidateWhizbangRegistrations(requirements);

    await Assert.That(returned).IsSameReferenceAs(services);
  }

  [Test]
  public async Task ADependencySatisfiedByAnInstanceRegistrationCountsAsPresentAsync() {
    var services = new ServiceCollection();
    services.AddSingleton<IPresent>(new Present());
    var requirements = new[] { new ServiceRequirement(typeof(Consumer), [typeof(IPresent)]) };

    var returned = services.ValidateWhizbangRegistrations(requirements);

    await Assert.That(returned).IsSameReferenceAs(services);
  }

  [Test]
  public async Task AnOpenGenericRegistrationSatisfiesAClosedDependencyAsync() {
    var services = new ServiceCollection();
    services.AddSingleton(typeof(IGeneric<>), typeof(Generic<>));
    var requirements = new[] { new ServiceRequirement(typeof(Consumer), [typeof(IGeneric<string>)]) };

    var returned = services.ValidateWhizbangRegistrations(requirements);

    // The container resolves IGeneric<string> from the open registration. Comparing type handles
    // naively would report it missing and make the validator cry wolf on correct compositions.
    await Assert.That(returned).IsSameReferenceAs(services);
  }

  [Test]
  public async Task ARequirementWithNoDependenciesPassesAsync() {
    var services = new ServiceCollection();
    var requirements = new[] { new ServiceRequirement(typeof(Consumer), []) };

    var returned = services.ValidateWhizbangRegistrations(requirements);

    await Assert.That(returned).IsSameReferenceAs(services);
  }

  [Test]
  public async Task AnEmptyRequirementSetPassesOnAnEmptyCollectionAsync() {
    var services = new ServiceCollection();

    var returned = services.ValidateWhizbangRegistrations([]);

    await Assert.That(returned).IsSameReferenceAs(services);
  }

  [Test]
  public async Task TheSameMissingServiceWantedByTwoTypesIsReportedForEachAsync() {
    var services = new ServiceCollection();
    var requirements = new[] {
      new ServiceRequirement(typeof(Consumer), [typeof(IAbsent)]),
      new ServiceRequirement(typeof(OtherConsumer), [typeof(IAbsent)]),
    };

    var ex = Assert.Throws<WhizbangRegistrationException>(
      () => services.ValidateWhizbangRegistrations(requirements));

    // Collapsing to one entry would hide that fixing the registration unblocks two consumers, and
    // would understate the blast radius when triaging a deployed system.
    await Assert.That(ex!.Missing).Count().IsEqualTo(2);
    await Assert.That(ex.Missing.Select(m => m.NeededBy))
      .Contains(typeof(Consumer)).And.Contains(typeof(OtherConsumer));
  }

  [Test]
  public async Task NullServicesThrowsArgumentNullAsync() {
    IServiceCollection services = null!;
    var ex = Assert.Throws<ArgumentNullException>(() => services.ValidateWhizbangRegistrations([]));
    await Assert.That(ex!.ParamName).IsEqualTo("services");
  }

  [Test]
  public async Task NullRequirementsThrowsArgumentNullAsync() {
    var services = new ServiceCollection();
    var ex = Assert.Throws<ArgumentNullException>(() => services.ValidateWhizbangRegistrations(null!));
    await Assert.That(ex!.ParamName).IsEqualTo("requirements");
  }

  [Test]
  public async Task ContainerIntrinsicServicesAreAlwaysSatisfiedAsync() {
    var services = new ServiceCollection();
    var requirements = new[] {
      new ServiceRequirement(typeof(Consumer), [
        typeof(IServiceProvider),
        typeof(IServiceScopeFactory),
      ]),
    };

    var returned = services.ValidateWhizbangRegistrations(requirements);

    // The container supplies these itself; they never appear as descriptors in the collection.
    // Reporting them missing would put a couple of dozen impossible gaps in front of anyone
    // reading a real failure, and bury the handful that are real.
    await Assert.That(returned).IsSameReferenceAs(services);
  }

  [Test]
  public async Task GenericCollectionDependenciesAreAlwaysSatisfiedAsync() {
    var services = new ServiceCollection();
    var requirements = new[] {
      new ServiceRequirement(typeof(Consumer), [typeof(IEnumerable<IPresent>)]),
    };

    var returned = services.ValidateWhizbangRegistrations(requirements);

    // IEnumerable<T> resolves to an empty sequence when nothing is registered, so a gap cannot
    // exist. Demanding a registration would report something that is not possible to fix.
    await Assert.That(returned).IsSameReferenceAs(services);
  }

  private interface IPresent { }
  private sealed class Present : IPresent { }
  private interface IAbsent { }
  private interface IAlsoAbsent { }
  private interface IGeneric<T> { }
  private sealed class Generic<T> : IGeneric<T> { }
  private sealed class Consumer { }
  private sealed class OtherConsumer { }
}
