using Microsoft.Extensions.DependencyInjection;

namespace Whizbang.Core.DependencyInjection;

/// <summary>
/// Verifies that a composed service collection can satisfy every registered type's constructor.
/// </summary>
/// <remarks>
/// <para>
/// This catches the two failure modes no static analysis can see, because in both of them the
/// source is correct: a registration guarded by a condition that is not live in this composition,
/// and a registration call that never runs at all because an assembly was stripped or replaced.
/// Both present identically at run time, as a dependency that is simply absent.
/// </para>
/// <para>
/// It inspects <see cref="ServiceDescriptor.ServiceType"/> and compares type handles. It never
/// resolves anything. Testing presence by resolution would construct the graph, which for
/// type-based registrations runs the container's reflective activator, and would also fire any side
/// effects a factory has; validation would then be both a reflection dependency and a behavior
/// change. Reading descriptors is a list scan over data.
/// </para>
/// <para>
/// Because it runs against <see cref="IServiceCollection"/> rather than a built provider, it fails
/// before a single service is constructed.
/// </para>
/// </remarks>
/// <docs>operations/dependency-injection/registration-validation</docs>
/// <tests>tests/Whizbang.Core.Tests/DependencyInjection/RegistrationValidationTests.cs</tests>
public static class RegistrationValidation {

  /// <summary>
  /// Throws if any requirement names a service type that no descriptor provides.
  /// </summary>
  /// <param name="services">The composed service collection.</param>
  /// <param name="requirements">
  /// The types to check and their constructor dependencies, normally the generated manifest.
  /// </param>
  /// <returns>The same collection, so validation can be chained into a registration pipeline.</returns>
  /// <exception cref="WhizbangRegistrationException">
  /// One or more dependencies are declared but not registered. Every gap is reported, not just the
  /// first.
  /// </exception>
  public static IServiceCollection ValidateWhizbangRegistrations(
      this IServiceCollection services,
      IReadOnlyList<ServiceRequirement> requirements) {
    ArgumentNullException.ThrowIfNull(services);
    ArgumentNullException.ThrowIfNull(requirements);

    List<MissingRegistration>? missing = null;

    for (var r = 0; r < requirements.Count; r++) {
      var requirement = requirements[r];
      var dependencies = requirement.Dependencies;

      for (var d = 0; d < dependencies.Count; d++) {
        var dependency = dependencies[d];
        if (!_isSatisfied(services, dependency)) {
          (missing ??= []).Add(new MissingRegistration(requirement.ImplementationType, dependency));
        }
      }
    }

    if (missing is not null) {
      throw new WhizbangRegistrationException(missing);
    }

    return services;
  }

  private static bool _isSatisfied(IServiceCollection services, Type dependency) {
    // The container supplies these itself; they never appear as descriptors, so requiring a
    // registration would report gaps that cannot be closed. In a real composition that was two
    // dozen impossible entries burying the handful of genuine ones.
    if (dependency == typeof(IServiceProvider) || dependency == typeof(IServiceScopeFactory)) {
      return true;
    }

    // IEnumerable<T> resolves to an empty sequence when nothing is registered, so a gap cannot
    // exist. The same is true of the read-only collection interfaces the container composes.
    if (dependency.IsConstructedGenericType) {
      var definition = dependency.GetGenericTypeDefinition();
      if (definition == typeof(IEnumerable<>)
          || definition == typeof(IReadOnlyCollection<>)
          || definition == typeof(IReadOnlyList<>)
          || definition == typeof(IReadOnlyDictionary<,>)) {
        return true;
      }
    }

    // A closed generic dependency is satisfied by an open generic registration: the container
    // constructs IGeneric<string> from an IGeneric<> descriptor. Comparing handles alone would
    // report it missing and make the validator cry wolf on a correct composition, which is the
    // fastest way to get a guard suppressed.
    Type? openGeneric = dependency.IsConstructedGenericType
      ? dependency.GetGenericTypeDefinition()
      : null;

    for (var i = 0; i < services.Count; i++) {
      var serviceType = services[i].ServiceType;
      if (serviceType == dependency || (openGeneric is not null && serviceType == openGeneric)) {
        return true;
      }
    }

    return false;
  }
}
