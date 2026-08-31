using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Whizbang.Core.Observability;

/// <summary>
/// Registers this process's service instance identity.
/// </summary>
/// <remarks>
/// <para>
/// Every registration extension that composes a type requiring <see cref="IServiceInstanceProvider"/>
/// calls this, so each extension stands alone. A pipeline composed without the full framework
/// registration used to leave the identity silently null, and the records that identity stamps were
/// simply unattributable: telemetry, audit records, and per-instance state all pointed at no
/// instance at all, with nothing reporting a problem.
/// </para>
/// <para>
/// <see cref="ServiceCollectionDescriptorExtensions.TryAddSingleton{TService}(IServiceCollection, Func{IServiceProvider, TService})"/>
/// keeps this idempotent and lets an application register its own identity first.
/// </para>
/// </remarks>
/// <docs>operations/dependency-injection/injectable-services</docs>
/// <tests>tests/Whizbang.Core.Tests/DependencyInjection/InstanceProviderWiringTests.cs</tests>
public static class InstanceIdentityRegistration {

  /// <summary>
  /// Ensures an <see cref="IServiceInstanceProvider"/> is registered.
  /// </summary>
  /// <param name="services">The service collection.</param>
  /// <returns>The same collection, for chaining.</returns>
  public static IServiceCollection AddWhizbangInstanceIdentity(this IServiceCollection services) {
    ArgumentNullException.ThrowIfNull(services);

    services.TryAddSingleton<IServiceInstanceProvider>(sp =>
      new ServiceInstanceProvider(sp.GetService<IConfiguration>()));

    return services;
  }
}
