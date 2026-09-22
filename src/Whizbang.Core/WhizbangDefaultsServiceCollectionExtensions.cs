using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Whizbang.Core;

/// <summary>
/// The framework's defaults for every interface its own types take by constructor. Every entry point
/// that can stand alone calls this first, so a type registered by any of them is always constructible
/// and a host's own registration still wins.
/// </summary>
/// <docs>extending/extensibility/replaceable-services</docs>
public static class WhizbangDefaultsServiceCollectionExtensions {
  /// <summary>
  /// Registers the framework's default for every replaceable service, all with TryAdd. <c>AddWhizbang</c>
  /// calls this; so does every extension that can be composed on its own (the worker pipeline, the
  /// routing builder, the transports, the notification stack), so a type registered by any of them is
  /// constructible and a host's own registration still wins. Idempotent.
  /// </summary>
  public static IServiceCollection TryAddWhizbangDefaults(this IServiceCollection services) {
    ArgumentNullException.ThrowIfNull(services);
    // ILogger<T> and IMeterFactory are framework-provided the same way: AddLogging and AddMetrics are
    // TryAdd-based, so a host's own pipelines are untouched and a bare service collection still gets both.
    services.AddLogging();
    // ---- Turnkey defaults for injected extensibility points -------------------------------------
    // Every interface a framework type takes by constructor is registered here with TryAdd, so a
    // construction site can no longer receive null for it and a host that registers its own
    // implementation first still wins. Where no real default exists the fallback is a null object
    // that reports itself unavailable, which is the behavior a null dependency produced before,
    // now spelled out. WHIZ501 enforces the "required parameter" half of this contract.
    services.TryAddSingleton<Messaging.IWorkChannelWriter, Messaging.WorkChannelWriter>();
    services.TryAddSingleton<Messaging.IInboxChannelWriter, Messaging.InboxChannelWriter>();
    services.TryAddSingleton<Messaging.IDeferredOutboxChannel, Messaging.DeferredOutboxChannel>();
    services.TryAddSingleton<Observability.IEnvelopeRegistry, Observability.EnvelopeRegistry>();
    services.TryAddSingleton<Messaging.IEnvelopeSerializer>(sp =>
      new Messaging.EnvelopeSerializer(sp.GetService<System.Text.Json.JsonSerializerOptions>()));
    services.TryAddSingleton<Messaging.ILifecycleMessageDeserializer>(sp =>
      new Messaging.JsonLifecycleMessageDeserializer(sp.GetService<System.Text.Json.JsonSerializerOptions>()));
    // The perspective worker's completion default is the batched strategy sized from its retry
    // options, not the instant one: the instant strategy exists for tests and is never the default.
    services.TryAddSingleton<Workers.IPerspectiveCompletionStrategy>(sp => {
      var retry = sp.GetRequiredService<IOptions<Workers.PerspectiveWorkerOptions>>().Value.RetryOptions;
      return new Workers.BatchedCompletionStrategy(
        retryTimeout: TimeSpan.FromSeconds(retry.RetryTimeoutSeconds),
        backoffMultiplier: retry.EnableExponentialBackoff ? retry.BackoffMultiplier : 1.0,
        maxTimeout: TimeSpan.FromSeconds(retry.MaxBackoffSeconds));
    });
    services.TryAddSingleton<Workers.IProcessedEventCacheObserver, Workers.NullProcessedEventCacheObserver>();
    services.TryAddSingleton<Messaging.IReceptorRegistry>(Messaging.NullReceptorRegistry.Instance);
    services.TryAddSingleton<Messaging.IReceptorRegistryQuery>(sp =>
      new Messaging.WhizbangReceptorRegistryQueryAdapter(sp.GetRequiredService<Messaging.IReceptorRegistry>()));
    services.TryAddScoped<Messaging.ILifecycleContextAccessor, Messaging.AsyncLocalLifecycleContextAccessor>();
    services.TryAddSingleton<IStreamIdExtractor>(_ => Registry.StreamIdExtractorRegistry.GetComposite());
    services.TryAddSingleton<IMessageTypeCatalog>(NullMessageTypeCatalog.Instance);
    services.TryAddSingleton<Notifications.INotifySignalingGate>(Notifications.NullNotifySignalingGate.Instance);
    services.TryAddSingleton<Routing.IEventNamespaceRegistry, Routing.StaticEventNamespaceRegistry>();
    // IOptions<RoutingOptions> is deliberately NOT registered here: the options pipeline (AddOptions,
    // pulled in by AddLogging above) yields a default instance and still honors a host's
    // Configure<RoutingOptions>, which a closed TryAddSingleton<IOptions<RoutingOptions>> would shadow.
    // The routing builder's own closed registration replaces the pipeline's when it runs.
    // IOutboxRoutingStrategy and IInboxRoutingStrategy are deliberately NOT defaulted either. A host
    // registers them by configuring routing (WithRouting), and every consumer treats their presence as
    // that configuration having happened: the dispatcher routes events through the outbox strategy when
    // one is registered and falls back to the topic strategy and registry conventions when none is,
    // and the transports derive their inbox topic the same way. A default here would silently move
    // every host that never configured routing onto namespace topics nobody subscribed to.
    // The address resolver is derived from the outbox strategy a host registered, exactly as the
    // transports used to derive it by casting; with no strategy, or one that does not resolve
    // addresses, the null resolver's default address is the shared inbox.
    services.TryAddSingleton<Routing.ICommandInboxAddressResolver>(sp =>
      sp.GetService<Routing.IOutboxRoutingStrategy>() as Routing.ICommandInboxAddressResolver
        ?? Routing.NullCommandInboxAddressResolver.Instance);
    services.TryAddSingleton<Workers.IMessagePublishStrategy>(Workers.NullMessagePublishStrategy.Instance);
    services.TryAddSingleton<Messaging.IEventTypeProvider>(Messaging.NullEventTypeProvider.Instance);
    // Concurrency governors are keyed per worker: each worker sizes its own from its own options,
    // and a host that wants a different strategy for one worker registers it under that key.
    services.TryAddKeyedSingleton<Execution.IConcurrencyGovernor>(Workers.OutboxDrainWorker.GOVERNOR_KEY, (sp, _) =>
      Workers.OutboxDrainWorker.CreateDefaultGovernor(sp.GetRequiredService<IOptions<Workers.OutboxDrainWorkerOptions>>().Value));
    services.TryAddKeyedSingleton<Execution.IConcurrencyGovernor>(Workers.PerspectiveWorker.GOVERNOR_KEY, (sp, _) =>
      Workers.PerspectiveWorker.CreateDefaultGovernor(sp.GetRequiredService<IOptions<Workers.PerspectiveWorkerOptions>>().Value));
    // Storage- and transport-backed seams. Each null default reports IsConfigured false; the subsystem
    // that supplies the real one registers it with TryAddSingletonOverNullDefault, so it wins whether
    // it is added before or after this call while a host's own registration is still left alone.
    services.TryAddSingleton<Messaging.IDeadLetterStore>(Messaging.NullDeadLetterStore.Instance);
    services.TryAddSingleton<Perspectives.IPerspectiveSnapshotStore>(Perspectives.NullPerspectiveSnapshotStore.Instance);
    services.TryAddSingleton<Perspectives.IPerspectiveStreamLocker>(Perspectives.NullPerspectiveStreamLocker.Instance);
    services.TryAddSingleton<Startup.IStartupAssessor>(Startup.NullStartupAssessor.Instance);
    services.TryAddSingleton<Startup.IDutyElector>(Startup.NullDutyElector.Instance);
    services.TryAddSingleton<Signals.ISignalBus>(Signals.NullSignalBus.Instance);
    services.TryAddSingleton<Workers.IPinnedConnectionPool>(Workers.NoOpPinnedConnectionPool.Instance);
    // The event mint: the composite splitter every publish path groups through. Turnkey, and a host
    // may substitute its own families first.
    services.TryAddSingleton<Minting.ICompositeFactory, Minting.CompositeFactory>();
    services.TryAddSingleton<Workers.IInstanceAliveLockSource>(Workers.NullInstanceAliveLockSource.Instance);
    services.TryAddSingleton<Workers.IOccurrencePublishGate, Workers.NoOpOccurrencePublishGate>();
    // Both resolvers read the message-type catalog; over the null catalog they resolve nothing, which is
    // what "no resolver" meant before. Generated registrations supply the same types over a real catalog.
    services.TryAddSingleton<IEventMarkerResolver, EventMarkerResolver>();
    services.TryAddSingleton<IEphemeralModeResolver, EphemeralModeResolver>();
    services.AddMetrics();
    services.TryAddEmptyConfiguration();
    return services;
  }
}
