using Microsoft.Extensions.DependencyInjection;
using Whizbang.Core.Observability;
using Whizbang.Sagas.Observability;

namespace Whizbang.Sagas;

/// <summary>DI registration for Whizbang.Sagas.</summary>
public static class SagaServiceCollectionExtensions {

  /// <summary>
  /// Registers Whizbang.Sagas runtime services. Call exactly once during
  /// container setup before any saga operation runs.
  /// </summary>
  /// <param name="services">The service collection.</param>
  /// <param name="configure">
  /// Optional configuration callback. The most common use is overriding
  /// <c>opts.PerItemStreamNamespace</c> when the consumer has
  /// pre-existing per-item streams derived from a different namespace
  /// (e.g. a system migrating from a pre-Whizbang saga implementation):
  /// <code>
  /// services.AddWhizbangSagas(opts =&gt;
  ///   opts.PerItemStreamNamespace = Guid.Parse("0b36f8d4-3884-4c3c-b92b-fc6ec74775ea"));
  /// </code>
  /// </param>
  public static IServiceCollection AddWhizbangSagas(this IServiceCollection services, Action<SagaOptions>? configure = null) {
    ArgumentNullException.ThrowIfNull(services);

    var opts = new SagaOptions();
    configure?.Invoke(opts);

    // Apply the namespace before any saga service can resolve. SagaItemStreams.Of's
    // no-override path reads AppDefaultNamespace at call time, so every subsequent
    // derivation in this process uses the configured value.
    SagaItemStreams.AppDefaultNamespace = opts.PerItemStreamNamespace;

    services.AddSingleton(opts);
    services.AddSingleton<SagaMetrics>(sp => new SagaMetrics(sp.GetRequiredService<WhizbangMetrics>()));

    return services;
  }
}
