// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

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
/// <para>
/// A requirement applies only when its implementation type is registered in the collection being
/// validated. The manifest is derived from every registration call in the compiled source, and a
/// composition makes only some of them: a host that never adds the transport consumer never
/// registers its worker, and must not be failed for the transport that worker would need.
/// </para>
/// </remarks>
/// <docs>operations/dependency-injection/registration-validation</docs>
/// <tests>tests/Whizbang.Core.Tests/DependencyInjection/RegistrationValidationTests.cs</tests>
public static class RegistrationValidation {

  /// <summary>
  /// Throws if any requirement whose implementation is registered names a service type that no
  /// descriptor provides.
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
      if (!_isRegistered(services, requirement)) {
        continue;
      }
      var dependencies = requirement.Dependencies;

      for (var d = 0; d < dependencies.Count; d++) {
        var dependency = dependencies[d];
        if (!_isSatisfied(services, dependency)) {
          missing ??= [];
          missing.Add(new MissingRegistration(requirement.ImplementationType, dependency));
        }
      }
    }

    if (missing is not null) {
      throw new WhizbangRegistrationException(missing);
    }

    return services;
  }

  /// <summary>
  /// True when some descriptor in the collection will construct the requirement's implementation.
  /// </summary>
  /// <remarks>
  /// <para>
  /// A type-based or instance descriptor names what it builds, so it is matched directly. A factory
  /// descriptor names only its service type, which is enough when the requirement is named after that
  /// service type (the generator names a factory's requirement that way). When the requirement is a
  /// concrete type that some registration builds through a factory under a different service type,
  /// the generator records that service type in <see cref="ServiceRequirement.FactoryServiceTypes"/>,
  /// and a factory descriptor under it counts as registering the implementation. Erring towards
  /// checking keeps a factory-built type from silently dropping out of validation.
  /// </para>
  /// <para>
  /// A keyed descriptor throws if its non-keyed implementation members are read, so keyed and
  /// non-keyed descriptors are read through their own members.
  /// </para>
  /// </remarks>
  private static bool _isRegistered(IServiceCollection services, ServiceRequirement requirement) {
    var implementation = requirement.ImplementationType;
    var factoryServiceTypes = requirement.FactoryServiceTypes;

    for (var i = 0; i < services.Count; i++) {
      var descriptor = services[i];
      if (descriptor.ServiceType == implementation) {
        return true;
      }

      Type? implementationType;
      object? instance;
      bool hasFactory;
      if (descriptor.IsKeyedService) {
        implementationType = descriptor.KeyedImplementationType;
        instance = descriptor.KeyedImplementationInstance;
        hasFactory = descriptor.KeyedImplementationFactory is not null;
      } else {
        implementationType = descriptor.ImplementationType;
        instance = descriptor.ImplementationInstance;
        hasFactory = descriptor.ImplementationFactory is not null;
      }

      if (implementationType == implementation || instance?.GetType() == implementation) {
        return true;
      }
      if (hasFactory && _contains(factoryServiceTypes, descriptor.ServiceType)) {
        return true;
      }
    }

    return false;
  }

  private static bool _contains(IReadOnlyList<Type> types, Type type) {
    for (var i = 0; i < types.Count; i++) {
      if (types[i] == type) {
        return true;
      }
    }
    return false;
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
