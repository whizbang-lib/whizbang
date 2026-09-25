using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Whizbang.Core.Observability;
using Whizbang.Sagas.Observability;
using Whizbang.Sagas.Services;

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
    services.AddScoped<ISagaEventEmitter, DispatcherSagaEventEmitter>();

    // Every saga started through BaseSagaService arms a completion watchdog tick. A saga declared
    // with [Saga] gets a generated receiver for it; a hand-written one relies on this router, which
    // routes each tick by saga name to the services registered with AddSagaService.
    services.AddHostedService<SagaWatchdogTickRouterRegistrar>();

    return services;
  }

  /// <summary>
  /// Registers a hand-written saga service so its completion watchdog ticks reach it.
  /// </summary>
  /// <remarks>
  /// <para>
  /// Registers <typeparamref name="TService"/> scoped, and exposes the same scoped instance as an
  /// <see cref="ISagaWatchdogParticipant"/> for the framework's tick router. Without the second
  /// registration a hand-written saga still arms its watchdog, but the tick is delivered to nothing
  /// and discarded — a saga stranded on a lost item then has no safety net at all.
  /// </para>
  /// <para>
  /// Use this for a saga service that subclasses <c>BaseSagaService</c> directly. A saga declared
  /// with <c>[Saga]</c> already has a generated receiver and must not also be registered here, or
  /// every tick would be handled — and re-armed — twice.
  /// </para>
  /// </remarks>
  /// <typeparam name="TService">The saga service.</typeparam>
  /// <param name="services">The service collection.</param>
  /// <returns>The same service collection.</returns>
  /// <docs>fundamentals/sagas/completion-orchestration#hand-written-sagas</docs>
  /// <tests>tests/Whizbang.Sagas.Tests/Services/SagaWatchdogTickRoutingTests.cs:AddSagaService_RegistersTheServiceAndItsWatchdogParticipationAsOneInstanceAsync</tests>
  // DynamicallyAccessedMembers keeps TService's public constructors through trimming so the container
  // can build it under native AOT — the same annotation AddScoped<TService> itself declares.
  public static IServiceCollection AddSagaService<
      [System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(
        System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicConstructors)] TService>(
      this IServiceCollection services)
      where TService : class, ISagaWatchdogParticipant {
    ArgumentNullException.ThrowIfNull(services);
    services.AddScoped<TService>();
    services.AddScoped<ISagaWatchdogParticipant>(sp => sp.GetRequiredService<TService>());
    return services;
  }
}
