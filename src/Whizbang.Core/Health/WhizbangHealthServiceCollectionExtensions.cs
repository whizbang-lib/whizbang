using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Whizbang.Core.Health;

/// <summary>
/// DI wiring for the managed-resource health model: registers the <see cref="WhizbangHealthAggregator"/>
/// (over every registered <see cref="IWhizbangHealthSource"/>) and the <see cref="WhizbangHealthOptions"/>
/// policy. Each managed subsystem contributes its own source via <see cref="AddWhizbangHealthSource{TSource}"/>.
/// </summary>
/// <docs>resilience/managed-resource-health</docs>
public static class WhizbangHealthServiceCollectionExtensions {
  /// <summary>
  /// Registers the health aggregator + policy options (idempotent). <paramref name="configure"/>
  /// customizes the policy — the default is <see cref="HealthPolicy.Lenient"/> for every component.
  /// </summary>
  public static IServiceCollection AddWhizbangManagedHealth(
      this IServiceCollection services, Action<WhizbangHealthOptions>? configure = null) {
    ArgumentNullException.ThrowIfNull(services);
    var options = new WhizbangHealthOptions();
    configure?.Invoke(options);
    services.TryAddSingleton(options);
    services.TryAddSingleton(static sp => new WhizbangHealthAggregator(
      sp.GetServices<IWhizbangHealthSource>(), sp.GetRequiredService<WhizbangHealthOptions>()));
    return services;
  }

  /// <summary>Registers one managed-resource health source. Every registered source is aggregated.</summary>
  public static IServiceCollection AddWhizbangHealthSource<
      [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TSource>(
      this IServiceCollection services)
      where TSource : class, IWhizbangHealthSource {
    ArgumentNullException.ThrowIfNull(services);
    services.AddSingleton<IWhizbangHealthSource, TSource>();
    return services;
  }
}
