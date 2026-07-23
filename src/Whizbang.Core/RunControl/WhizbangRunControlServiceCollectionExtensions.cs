using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Whizbang.Core.RunControl;

/// <summary>
/// DI wiring for the coordinated lifecycle plane: registers the <see cref="WhizbangLifecycleCoordinator"/>
/// (over every registered <see cref="IWhizbangRunControl"/> participant), the
/// <see cref="WhizbangLifecycleOptions"/> (ack timeout + fault record window), and the shared
/// <see cref="IWhizbangLifecycleState"/>. Each managed subsystem registers its participant via
/// <see cref="AddWhizbangRunControlAdapter{TControl}"/>; both faces read the one lifecycle state.
/// </summary>
/// <docs>resilience/managed-resource-run-control</docs>
public static class WhizbangRunControlServiceCollectionExtensions {
  /// <summary>Registers the lifecycle coordinator + options + state (idempotent). <paramref name="configure"/> tunes the ack timeout / fault window.</summary>
  public static IServiceCollection AddWhizbangRunControl(
      this IServiceCollection services, Action<WhizbangLifecycleOptions>? configure = null) {
    ArgumentNullException.ThrowIfNull(services);
    var options = new WhizbangLifecycleOptions();
    configure?.Invoke(options);
    services.TryAddSingleton(options);
    services.TryAddSingleton(static sp => new WhizbangLifecycleCoordinator(
      sp.GetServices<IWhizbangRunControl>(),
      sp.GetRequiredService<WhizbangLifecycleOptions>(),
      sp.GetService<TimeProvider>()));
    services.TryAddSingleton<IWhizbangLifecycleState>(static sp => new WhizbangLifecycleState(
      sp.GetRequiredService<WhizbangLifecycleCoordinator>(),
      sp.GetRequiredService<WhizbangLifecycleOptions>(),
      sp.GetService<TimeProvider>()));
    return services;
  }

  /// <summary>Registers one managed-resource run-control participant. Every registered participant is driven by the coordinator.</summary>
  public static IServiceCollection AddWhizbangRunControlAdapter<
      [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TControl>(
      this IServiceCollection services)
      where TControl : class, IWhizbangRunControl {
    ArgumentNullException.ThrowIfNull(services);
    services.AddSingleton<IWhizbangRunControl, TControl>();
    return services;
  }
}
