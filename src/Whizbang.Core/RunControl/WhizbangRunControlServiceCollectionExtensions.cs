using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Whizbang.Core.RunControl;

/// <summary>
/// DI wiring for the run-control (killswitch) plane: registers the <see cref="WhizbangRunController"/>
/// over every registered <see cref="IWhizbangRunControl"/> and the <see cref="WhizbangRunControlOptions"/>
/// (defaulting to <see cref="WhizbangRunControlOptions.Default"/> — pause processing + writes during a
/// migration). Each managed subsystem registers its adapter via <see cref="AddWhizbangRunControlAdapter{TControl}"/>.
/// </summary>
/// <docs>resilience/managed-resource-run-control</docs>
public static class WhizbangRunControlServiceCollectionExtensions {
  /// <summary>Registers the run-controller + options (idempotent). <paramref name="configure"/> tweaks the phase table / overrides.</summary>
  public static IServiceCollection AddWhizbangRunControl(
      this IServiceCollection services, Action<WhizbangRunControlOptions>? configure = null) {
    ArgumentNullException.ThrowIfNull(services);
    var options = WhizbangRunControlOptions.Default();
    configure?.Invoke(options);
    services.TryAddSingleton(options);
    services.TryAddSingleton(static sp => new WhizbangRunController(
      sp.GetServices<IWhizbangRunControl>(), sp.GetRequiredService<WhizbangRunControlOptions>()));
    services.TryAddSingleton<IWhizbangLifecycleState>(static sp =>
      new WhizbangLifecycleState(sp.GetRequiredService<WhizbangRunController>()));
    return services;
  }

  /// <summary>Registers one managed-resource run-control adapter. Every registered adapter is driven by the controller.</summary>
  public static IServiceCollection AddWhizbangRunControlAdapter<
      [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TControl>(
      this IServiceCollection services)
      where TControl : class, IWhizbangRunControl {
    ArgumentNullException.ThrowIfNull(services);
    services.AddSingleton<IWhizbangRunControl, TControl>();
    return services;
  }
}
