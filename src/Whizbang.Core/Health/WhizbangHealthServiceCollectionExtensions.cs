using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Whizbang.Core.RunControl;

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
    // The managed health sources judge their state against the lifecycle phase, so ensure the
    // lifecycle plane (IWhizbangLifecycleState) is registered. Idempotent (TryAdd throughout).
    services.AddWhizbangRunControl();
    var options = new WhizbangHealthOptions();
    configure?.Invoke(options);
    services.TryAddSingleton(options);
    services.TryAddSingleton(static sp => new WhizbangHealthAggregator(
      sp.GetServices<IWhizbangHealthSource>(), sp.GetRequiredService<WhizbangHealthOptions>()));
    return services;
  }

  /// <summary>
  /// Registers one managed-resource health source. Every registered source is aggregated.
  /// Idempotent per source type: a consumer with more than one worker pipeline (e.g. one per
  /// DbContext) calls the pipeline registration N times, and stacking N identical sources would
  /// evaluate the same gates N times per probe and emit duplicate component names downstream.
  /// </summary>
  public static IServiceCollection AddWhizbangHealthSource<
      [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TSource>(
      this IServiceCollection services)
      where TSource : class, IWhizbangHealthSource {
    ArgumentNullException.ThrowIfNull(services);
    services.TryAddEnumerable(ServiceDescriptor.Singleton<IWhizbangHealthSource, TSource>());
    return services;
  }
}
