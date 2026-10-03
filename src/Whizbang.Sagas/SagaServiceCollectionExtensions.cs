using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Observability;
using Whizbang.Core.Routing;
using Whizbang.Sagas.Observability;
using Whizbang.Sagas.Services;

namespace Whizbang.Sagas;

/// <summary>DI registration for Whizbang.Sagas.</summary>
public static class SagaServiceCollectionExtensions {

  /// <summary>
  /// Registers Whizbang.Sagas runtime services. Call exactly once during
  /// container setup before any saga operation runs.
  /// </summary>
  /// <remarks>
  /// The dispatcher-backed <see cref="ISagaEventEmitter"/> is a default: an emitter the consumer
  /// registered before this call is the one saga services get.
  /// </remarks>
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
  /// <remarks>
  /// Registers the framework's watchdog tick router and declares the tick as an event this host
  /// consumes, so the transport consumer subscribes to the tick's topic even when no receptor the
  /// source generator can see handles it. A hand-written saga registered with
  /// <see cref="AddSagaService{TService}"/> therefore needs nothing else to receive its ticks.
  /// </remarks>
  /// <docs>fundamentals/sagas/completion-orchestration#hand-written-sagas</docs>
  /// <tests>tests/Whizbang.Sagas.Tests/Services/SagaWatchdogTickRoutingTests.cs:AddWhizbangSagas_RegistersTheRouterRegistrarAsync</tests>
  /// <tests>tests/Whizbang.Sagas.Tests/Services/SagaWatchdogTickRoutingTests.cs:AddWhizbangSagas_DeclaresTheTickAsConsumed_OnceHoweverOftenItIsCalledAsync</tests>
  /// <tests>tests/Whizbang.Sagas.Tests/SagaWatchdogTickSubscriptionIntegrationTests.cs:AddSagaServiceOnly_SubscribesToTheTicksTopic_AndAPublishedTickReachesTheSagaAsync</tests>
  /// <tests>tests/Whizbang.Sagas.Tests/SagaWatchdogTickSubscriptionIntegrationTests.cs:HostWithItsOwnTickReceptor_SubscribesOnce_AndEachTickIsRecoveredOnceAsync</tests>
  public static IServiceCollection AddWhizbangSagas(this IServiceCollection services, Action<SagaOptions>? configure = null) {
    ArgumentNullException.ThrowIfNull(services);

    var opts = new SagaOptions();
    configure?.Invoke(opts);

    // Apply the namespace before any saga service can resolve. SagaItemStreams.Of's
    // no-override path reads AppDefaultNamespace at call time, so every subsequent
    // derivation in this process uses the configured value.
    SagaItemStreams.AppDefaultNamespace = opts.PerItemStreamNamespace;

    // Whizbang:Sagas binds over the code values when the options first resolve, so the watchdog and
    // the stranded-saga sweep can be tuned at deploy time (#1014). A key that is absent leaves the
    // code value alone.
    services.AddSingleton(sp => _bindFromConfiguration(sp.GetService<IConfiguration>(), opts));
    services.AddSingleton<SagaMetrics>(sp => new SagaMetrics(sp.GetRequiredService<WhizbangMetrics>()));
    // The default: a consumer that registered its own emitter first keeps it.
    services.TryAddScoped<ISagaEventEmitter, DispatcherSagaEventEmitter>();

    // Every saga started through BaseSagaService arms a completion watchdog tick. A saga declared
    // with [Saga] gets a generated receiver for it; a hand-written one relies on this router, which
    // routes each tick by saga name to the services registered with AddSagaService.
    services.AddHostedService<SagaWatchdogTickRouterRegistrar>();
    // The router is registered at startup, so the compile-time subscription discovery that finds
    // generated receptors cannot see it. Declare the tick as consumed so the transport subscribes to
    // its topic as it would for a generated receiver; a host that also has one subscribes once.
    services.AddRuntimeEventSubscription<SagaCompletionWatchdogTickEvent>();

    // A lost tick ends a saga's watchdog chain for good. The maintenance cycle re-arms sagas whose
    // chain has ended; TryAddEnumerable so calling this twice does not sweep twice.
    services.TryAddEnumerable(ServiceDescriptor.Scoped<Whizbang.Core.Workers.IMaintenanceStep, StrandedSagaSweepStep>());
    // The claims a saga spends as it runs are pruned once past their retention; the abandonment
    // claim, a record, is kept.
    services.TryAddEnumerable(ServiceDescriptor.Scoped<Whizbang.Core.Workers.IMaintenanceStep, SagaClaimPruneStep>());
    // The general expiry prune leaves the saga claims to that step, so a completion claim lives out its
    // retention and an abandonment claim is never pruned (#999).
    foreach (var prefix in SagaClaimPruneStep.OwnedClaimPrefixes) {
      services.AddRetainedClaimKeyPrefix(prefix);
    }

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
  /// Together with <see cref="AddWhizbangSagas"/>, which registers the router and subscribes the host
  /// to the tick's topic, this is all a hand-written saga needs. It needs no tick receptor of its own,
  /// and one that forwards ticks to a service registered here would recover each tick twice.
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
  /// <tests>tests/Whizbang.Sagas.Tests/SagaWatchdogTickSubscriptionIntegrationTests.cs:AddSagaServiceOnly_SubscribesToTheTicksTopic_AndAPublishedTickReachesTheSagaAsync</tests>
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

  /// <summary>
  /// Applies <c>Whizbang:Sagas</c> to <paramref name="options"/>. The keys bind into a section of their
  /// own and are copied across only when present, so an absent key leaves the code value alone. The
  /// per-item stream namespace has no key: it is stream identity, applied process-wide at registration,
  /// and a key that changed it here would disagree with every stream derived before.
  /// </summary>
  private static SagaOptions _bindFromConfiguration(IConfiguration? configuration, SagaOptions options) {
    if (configuration is not null) {
      var section = new SagaConfigurationSection();
#pragma warning disable IL2026, IL3050 // intercepted: the binder source generator compiles this call to typed assignments (BindingExtensions.g.cs); format's analyzer pass does not see the generator's suppressor
      configuration.GetSection("Whizbang:Sagas").Bind(section);
#pragma warning restore IL2026, IL3050
      section.ApplyTo(options);
    }
    return options;
  }
}

/// <summary>
/// The keys of <c>Whizbang:Sagas</c>. Each is nullable so an absent key leaves the value code set alone.
/// </summary>
internal sealed class SagaConfigurationSection {
  public TimeSpan? MinWatchdogDelay { get; set; }
  public TimeSpan? MaxWatchdogDelay { get; set; }
  public TimeSpan? WatchdogSafetyMargin { get; set; }
  public int? MaxConsecutiveStalls { get; set; }
  public double? StallBackoffMultiplier { get; set; }
  public TimeSpan? StrandedSagaIdleGuard { get; set; }
  public TimeSpan? StrandedSagaRearmInterval { get; set; }
  public TimeSpan? ClaimRetention { get; set; }

  public void ApplyTo(SagaOptions options) {
    options.MinWatchdogDelay = MinWatchdogDelay ?? options.MinWatchdogDelay;
    options.MaxWatchdogDelay = MaxWatchdogDelay ?? options.MaxWatchdogDelay;
    options.WatchdogSafetyMargin = WatchdogSafetyMargin ?? options.WatchdogSafetyMargin;
    options.MaxConsecutiveStalls = MaxConsecutiveStalls ?? options.MaxConsecutiveStalls;
    options.StallBackoffMultiplier = StallBackoffMultiplier ?? options.StallBackoffMultiplier;
    options.StrandedSagaIdleGuard = StrandedSagaIdleGuard ?? options.StrandedSagaIdleGuard;
    options.StrandedSagaRearmInterval = StrandedSagaRearmInterval ?? options.StrandedSagaRearmInterval;
    options.ClaimRetention = ClaimRetention ?? options.ClaimRetention;
  }
}
