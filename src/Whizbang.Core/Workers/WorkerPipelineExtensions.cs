using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Whizbang.Core.Messaging;
using Whizbang.Core.Notifications;
using Whizbang.Core.Routing;

namespace Whizbang.Core.Workers;

/// <summary>
/// DI registration helpers for the Phase C worker pipeline. Invoked automatically by
/// <see cref="ServiceCollectionExtensions.AddWhizbang(IServiceCollection)"/>; consumers
/// don't need to call this directly. Exposed publicly so advanced scenarios can register
/// just the worker pipeline (e.g., to host workers in a separate process).
/// </summary>
/// <docs>fundamentals/work-coordinator/configuration-reference</docs>
public static class WorkerPipelineExtensions {
  /// <summary>
  /// Registers the new work-pump worker pipeline (HeartbeatWorker, ClaimWorker, InboxHandlerWorker,
  /// and the four batched-flush workers + their channel interfaces). Idempotent — calling
  /// multiple times has no additional effect.
  /// </summary>
  /// <param name="services">DI service collection.</param>
  /// <returns>The same <see cref="IServiceCollection"/> for chaining.</returns>
  public static IServiceCollection AddWhizbangWorkers(this IServiceCollection services) {
    ArgumentNullException.ThrowIfNull(services);

    // Schema-ready gate — workers await this before issuing any SQL. The driver's
    // initializer (e.g., WhizbangDatabaseInitializerService) calls MarkReady after migrations
    // complete. Singleton because all workers + the initializer must observe the same instance.
    services.TryAddSingleton<ISchemaReadyGate, SchemaReadyGate>();

    // Register each worker type as a singleton so the channel-surface registrations
    // can resolve the SAME instance the hosted-service collection runs.
    // This avoids a circular DI deadlock: if we resolved the channel via
    // sp.GetServices<IHostedService>() and any other hosted service depended on
    // a channel surface, IHostedService resolution would recurse on itself.
    services.TryAddSingleton<HeartbeatWorker>();
    services.TryAddSingleton<ClaimWorker>();
    services.TryAddSingleton<OutboxCompletionFlushWorker>();
    services.TryAddSingleton<PerspectiveCompletionFlushWorker>();
    services.TryAddSingleton<FailureFlushWorker>();
    services.TryAddSingleton<LeaseRenewalWorker>();
    services.TryAddSingleton<InboxHandlerWorker>();
    services.TryAddSingleton<MaintenanceWorker>();
    services.TryAddSingleton<OutboxPublishWorker>();
    services.TryAddSingleton<InboxDispatchWorker>();
    services.TryAddSingleton<OutboxDrainWorker>();
    services.TryAddSingleton<InboxDrainWorker>();

    // Phase H step 7 slice 7: cooldown cache for the perspective drainer's short-circuit gate.
    // Singleton — PerspectiveWorker reads/writes it; the sweep worker periodically evicts.
    services.TryAddSingleton(sp => {
      var opts = sp.GetRequiredService<IOptions<RecentlyProcessedEventCacheOptions>>().Value;
      var time = sp.GetRequiredService<ITimeProvider>();
      return new RecentlyProcessedEventCache(
        timeProvider: time,
        ttl: TimeSpan.FromMinutes(Math.Max(1, opts.TtlMinutes)),
        maxEntries: Math.Max(1, opts.MaxEntries));
    });
    services.TryAddSingleton<RecentlyProcessedEventCacheSweepWorker>();

    // Slice 15 of pump-then-process.md: per-instance bounded LRU cache for deserialized inbox
    // payloads. Holds the parsed object across the four lifecycle stages of one dispatch AND
    // across transport redelivery / lease re-claim within the configured TTL. Singleton —
    // InboxDispatchWorker reads/writes; cache is a passive structure.
    services.TryAddSingleton(sp => {
      var opts = sp.GetRequiredService<IOptions<InboxDeserializeCacheOptions>>().Value;
      var time = sp.GetRequiredService<ITimeProvider>();
      return new InboxDeserializeCache(
        timeProvider: time,
        ttl: TimeSpan.FromMinutes(Math.Max(1, opts.TtlMinutes)),
        maxEntries: Math.Max(1, opts.MaxEntries));
    });

    // Phase H step 9 slice 7: lease-tied cancellation infrastructure. LeaseRegistry is the
    // singleton handle store; dispatch workers Register at claim, LeaseRenewalWorker looks up
    // by (category, work_id) when extending DB leases so the in-process CT deadline tracks the
    // SQL lease until the MaxRenewalsPerWork cap is hit.
    services.TryAddSingleton<LeaseRegistry>();
    services.TryAddSingleton(TimeProvider.System);

    // Slice 4 of pump-then-process.md: source-generated receptor registry adapter. The
    // InboxDispatchWorker uses this to skip lifecycle deserialize for cross-service events
    // that the local service has no receptor for. Registered as a singleton — adapter is
    // stateless and just forwards to the static generated lookup.
    services.TryAddSingleton<IReceptorRegistryQuery, WhizbangReceptorRegistryQueryAdapter>();

    // Message-discard policy: shared "should this message be skipped?" decision used by
    // the transport-receive, inbox-dispatch, and outbox-publish gates. Owns the structured
    // log level + OTel counter so all three gates emit consistent telemetry. The Meter is
    // dedicated to this concern so dashboards can scrape it without picking up unrelated
    // transport metrics. See <see cref="Whizbang.Core.Routing.MessageDiscardPolicy"/>.
#pragma warning disable CA2000 // The Meter is held by the singleton policy for the host's lifetime.
    services.TryAddSingleton<IMessageDiscardPolicy>(sp => new MessageDiscardPolicy(
      sp.GetRequiredService<IReceptorRegistryQuery>(),
      sp.GetRequiredService<ILogger<MessageDiscardPolicy>>(),
      new System.Diagnostics.Metrics.Meter(MessageDiscardPolicy.METER_NAME)));
#pragma warning restore CA2000

    // Hosted services — delegate to the singleton instance so DI hands the same one
    // to both the hosted-service collection and the channel-surface registrations.
    //
    // WhizbangVersionLogger registers FIRST so the assembly version lands in the log
    // before any worker-startup chatter — gives operators a deterministic anchor for
    // "which build is running in this pod" via `kubectl logs | grep "Whizbang loaded"`.
    services.AddHostedService<WhizbangVersionLogger>();
    services.AddHostedService(sp => sp.GetRequiredService<HeartbeatWorker>());
    services.AddHostedService(sp => sp.GetRequiredService<ClaimWorker>());
    services.AddHostedService(sp => sp.GetRequiredService<OutboxCompletionFlushWorker>());
    services.AddHostedService(sp => sp.GetRequiredService<PerspectiveCompletionFlushWorker>());
    services.AddHostedService(sp => sp.GetRequiredService<FailureFlushWorker>());
    services.AddHostedService(sp => sp.GetRequiredService<LeaseRenewalWorker>());
    services.AddHostedService(sp => sp.GetRequiredService<InboxHandlerWorker>());
    services.AddHostedService(sp => sp.GetRequiredService<MaintenanceWorker>());
    services.AddHostedService(sp => sp.GetRequiredService<OutboxPublishWorker>());
    services.AddHostedService(sp => sp.GetRequiredService<InboxDispatchWorker>());
    services.AddHostedService(sp => sp.GetRequiredService<OutboxDrainWorker>());
    services.AddHostedService(sp => sp.GetRequiredService<InboxDrainWorker>());
    services.AddHostedService(sp => sp.GetRequiredService<RecentlyProcessedEventCacheSweepWorker>());

    // Channel interfaces — singletons that delegate to the singleton worker.
    services.TryAddSingleton<IOutboxCompletionChannel>(sp => sp.GetRequiredService<OutboxCompletionFlushWorker>());
    services.TryAddSingleton<IPerspectiveCompletionChannel>(sp => sp.GetRequiredService<PerspectiveCompletionFlushWorker>());
    services.TryAddSingleton<IFailureChannel>(sp => sp.GetRequiredService<FailureFlushWorker>());
    services.TryAddSingleton<ILeaseRenewalChannel>(sp => sp.GetRequiredService<LeaseRenewalWorker>());
    services.TryAddSingleton<IInboxHandlerCommitChannel>(sp => sp.GetRequiredService<InboxHandlerWorker>());

    // Perspective work-distribution channel: ClaimWorker writes here, PerspectiveWorker reads.
    // Singleton so producer + consumer share the same Channel<T>.
    services.TryAddSingleton<Whizbang.Core.Messaging.IPerspectiveChannelWriter, Whizbang.Core.Messaging.PerspectiveChannelWriter>();

    // Drain-mode channel: stream IDs that claim_work flagged for batched (RunWithEventsAsync) processing.
    services.TryAddSingleton<Whizbang.Core.Messaging.IPerspectiveDrainChannel, Whizbang.Core.Messaging.PerspectiveDrainChannel>();

    // Per-stream-id outbox/inbox drain channels: ClaimWorker writes stream_ids; OutboxDrainWorker /
    // InboxDrainWorker read them and call FetchOutboxBatchAsync / FetchInboxBatchAsync to pull
    // payloads on demand. Restores archive design where the poller does not carry full bodies.
    services.TryAddSingleton<Whizbang.Core.Messaging.IOutboxDrainChannel, Whizbang.Core.Messaging.OutboxDrainChannel>();
    services.TryAddSingleton<Whizbang.Core.Messaging.IInboxDrainChannel, Whizbang.Core.Messaging.InboxDrainChannel>();

    // NoOp notification listener by default — driver-specific extensions
    // (e.g., AddWhizbangPostgresNotifications) replace it with the real listener.
    services.TryAddSingleton<IWorkNotificationListener, NoOpWorkNotificationListener>();

    // Defense-in-depth concurrency cap on IWorkCoordinator calls. Default 50 (matches
    // recommended Npgsql Maximum Pool Size). Users can register their own gate before
    // calling AddWhizbang to override.
    services.TryAddSingleton(_ => new WorkCoordinatorGate(maxConcurrent: 50));

    // AddOptions<T>() is idempotent (uses TryAdd internally for IOptions<T>).
    services.AddOptions<HeartbeatWorkerOptions>();
    services.AddOptions<ClaimWorkerOptions>();
    services.AddOptions<OutboxCompletionFlushWorkerOptions>();
    services.AddOptions<PerspectiveCompletionFlushWorkerOptions>();
    services.AddOptions<FailureFlushWorkerOptions>();
    services.AddOptions<LeaseRenewalWorkerOptions>();
    services.AddOptions<InboxHandlerWorkerOptions>();
    services.AddOptions<OutboxPublishWorkerOptions>();
    services.AddOptions<InboxDispatchWorkerOptions>();
    services.AddOptions<MaintenanceWorkerOptions>();
    services.AddOptions<OutboxDrainWorkerOptions>();
    services.AddOptions<InboxDrainWorkerOptions>();
    services.AddOptions<RecentlyProcessedEventCacheOptions>();
    services.AddOptions<InboxDeserializeCacheOptions>();
    services.AddOptions<LeaseHandleOptions>();
    services.AddOptions<SlidingWindowOutboxOptions>();
    services.AddOptions<SlidingWindowInboxOptions>();

    // Producer-side stream-affinity batcher — half B of pump-then-process. Singleton; the
    // flush callback resolves IWorkCoordinator from a fresh DI scope per batch so the
    // strategy itself can outlive any one request scope. Default is the sliding-window
    // batcher; override via the AddWhizbangOutboxStrategy generic extension — see
    // ImmediateOutboxBatchStrategy for the no-batching alternative.
    services.TryAddSingleton<OutboxBulkFlushCallback>(_buildOutboxFlushCallback);
    services.TryAddSingleton<SlidingWindowOutboxBatchStrategy>(sp => new SlidingWindowOutboxBatchStrategy(
      flush: sp.GetRequiredService<OutboxBulkFlushCallback>(),
      options: sp.GetRequiredService<IOptions<SlidingWindowOutboxOptions>>().Value,
      timeProvider: sp.GetService<TimeProvider>(),
      logger: sp.GetService<ILogger<SlidingWindowOutboxBatchStrategy>>()));
    services.TryAddSingleton<ImmediateOutboxBatchStrategy>(sp => new ImmediateOutboxBatchStrategy(
      flush: sp.GetRequiredService<OutboxBulkFlushCallback>()));
    services.TryAddSingleton<IOutboxBatchStrategy>(sp => sp.GetRequiredService<SlidingWindowOutboxBatchStrategy>());

    // Receive-boundary inbox batcher — half A of pump-then-process. Mirror of the outbox
    // registration above. Flush callback resolves IWorkCoordinator from a fresh DI scope
    // per batch and calls StoreInboxMessagesAsync. Default is the sliding-window batcher;
    // override via the AddWhizbangInboxStrategy generic extension for the immediate
    // passthrough or a custom implementation.
    services.TryAddSingleton<InboxBulkFlushCallback>(_buildInboxFlushCallback);
    services.TryAddSingleton<SlidingWindowInboxBatchStrategy>(sp => new SlidingWindowInboxBatchStrategy(
      flush: sp.GetRequiredService<InboxBulkFlushCallback>(),
      options: sp.GetRequiredService<IOptions<SlidingWindowInboxOptions>>().Value,
      timeProvider: sp.GetService<TimeProvider>(),
      logger: sp.GetService<ILogger<SlidingWindowInboxBatchStrategy>>()));
    services.TryAddSingleton<ImmediateInboxBatchStrategy>(sp => new ImmediateInboxBatchStrategy(
      flush: sp.GetRequiredService<InboxBulkFlushCallback>()));
    services.TryAddSingleton<IInboxBatchStrategy>(sp => sp.GetRequiredService<SlidingWindowInboxBatchStrategy>());

    return services;
  }

  /// <summary>
  /// Replace the registered <see cref="IOutboxBatchStrategy"/> with the given type. Used for
  /// low-throughput tenants who opt to <see cref="ImmediateOutboxBatchStrategy"/> for
  /// strict-ordering / no-batching semantics, or for users plugging in a custom strategy.
  /// </summary>
  /// <typeparam name="TStrategy">Strategy type. Must be DI-resolvable as a singleton.</typeparam>
  /// <param name="services">DI service collection.</param>
  /// <returns>The same <see cref="IServiceCollection"/> for chaining.</returns>
  public static IServiceCollection AddWhizbangOutboxStrategy<TStrategy>(this IServiceCollection services)
      where TStrategy : class, IOutboxBatchStrategy {
    ArgumentNullException.ThrowIfNull(services);
    var existing = services.FirstOrDefault(d => d.ServiceType == typeof(IOutboxBatchStrategy));
    if (existing is not null) {
      services.Remove(existing);
    }
    services.AddSingleton<IOutboxBatchStrategy>(sp => sp.GetRequiredService<TStrategy>());
    return services;
  }

  /// <summary>
  /// Replace the registered <see cref="IInboxBatchStrategy"/> with the given type. Mirror of
  /// <see cref="AddWhizbangOutboxStrategy{TStrategy}"/> — used to opt into
  /// <see cref="ImmediateInboxBatchStrategy"/> or a custom implementation.
  /// </summary>
  /// <typeparam name="TStrategy">Strategy type. Must be DI-resolvable as a singleton.</typeparam>
  /// <param name="services">DI service collection.</param>
  /// <returns>The same <see cref="IServiceCollection"/> for chaining.</returns>
  public static IServiceCollection AddWhizbangInboxStrategy<TStrategy>(this IServiceCollection services)
      where TStrategy : class, IInboxBatchStrategy {
    ArgumentNullException.ThrowIfNull(services);
    var existing = services.FirstOrDefault(d => d.ServiceType == typeof(IInboxBatchStrategy));
    if (existing is not null) {
      services.Remove(existing);
    }
    services.AddSingleton<IInboxBatchStrategy>(sp => sp.GetRequiredService<TStrategy>());
    return services;
  }

  private static OutboxBulkFlushCallback _buildOutboxFlushCallback(IServiceProvider sp) {
    var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();
    var schemaReadyGate = sp.GetRequiredService<ISchemaReadyGate>();
    var coordinatorOptions = sp.GetRequiredService<IOptions<WorkCoordinatorOptions>>();
    var lifecycleDeserializer = sp.GetService<ILifecycleMessageDeserializer>();
    var lifecycleMetrics = sp.GetService<Whizbang.Core.Observability.LifecycleMetrics>();
    var tracingOptions = sp.GetService<IOptionsMonitor<Whizbang.Core.Tracing.TracingOptions>>();
    var loggerFactory = sp.GetService<ILoggerFactory>();
    var lifecycleLogger = loggerFactory?.CreateLogger("Whizbang.Core.Workers.OutboxBatchFlush");
    return async (messages, ct) => {
      await schemaReadyGate.WaitForReadyAsync(ct).ConfigureAwait(false);
      using var scope = scopeFactory.CreateScope();
      var coordinator = scope.ServiceProvider.GetRequiredService<IWorkCoordinator>();

      // Stream-affinity batch path bypasses the scoped strategy's queue, so the
      // dispatcher's strategy.FlushAsync sees an empty queue and skips lifecycle
      // invocation. Fire Pre/Distribute lifecycle BEFORE store, Post after — same shape
      // as WorkCoordinatorFlushHelper — so receptors registered at Distribute lifecycle
      // stages still fire for outbox messages routed through the batcher.
      //
      // Lifecycle invocation is wrapped in try/catch so a misbehaving receptor (deserialize
      // failure, handler throw) cannot block the storage path. The storage call MUST
      // happen; without it the message is permanently lost.
      var enableLifecycleTracing = tracingOptions?.CurrentValue.IsEnabled(Whizbang.Core.Tracing.TraceComponents.Lifecycle) ?? false;
      var distributeContext = new DistributeLifecycleContext(
        OutboxMessages: messages,
        InboxMessages: Array.Empty<InboxMessage>(),
        ScopeFactory: scopeFactory,
        LifecycleMessageDeserializer: lifecycleDeserializer,
        Logger: lifecycleLogger,
        EnableLifecycleTracing: enableLifecycleTracing,
        Metrics: lifecycleMetrics);

      try {
        await LifecycleInvocationHelper.InvokeDistributeLifecycleStagesAsync(
          LifecycleStage.PreDistributeDetached,
          LifecycleStage.PreDistributeInline,
          distributeContext,
          ct).ConfigureAwait(false);

        LifecycleInvocationHelper.InvokeAsyncOnlyLifecycleStage(
          LifecycleStage.DistributeDetached,
          distributeContext,
          ct);
      } catch (Exception ex) when (ex is not OperationCanceledException) {
#pragma warning disable CA1848 // LoggerMessage not applicable for exception handlers in background tasks
        lifecycleLogger?.LogError(ex, "Outbox batch flush: Pre/Distribute lifecycle invocation failed; proceeding to store {Count} message(s).", messages.Length);
#pragma warning restore CA1848
      }

      await coordinator.StoreOutboxMessagesAsync(messages, coordinatorOptions.Value.PartitionCount, ct).ConfigureAwait(false);

      try {
        await LifecycleInvocationHelper.InvokeDistributeLifecycleStagesAsync(
          LifecycleStage.PostDistributeDetached,
          LifecycleStage.PostDistributeInline,
          distributeContext,
          ct).ConfigureAwait(false);
      } catch (Exception ex) when (ex is not OperationCanceledException) {
#pragma warning disable CA1848
        lifecycleLogger?.LogError(ex, "Outbox batch flush: Post-Distribute lifecycle invocation failed after store for {Count} message(s).", messages.Length);
#pragma warning restore CA1848
      }
    };
  }

  private static InboxBulkFlushCallback _buildInboxFlushCallback(IServiceProvider sp) {
    var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();
    var schemaReadyGate = sp.GetRequiredService<ISchemaReadyGate>();
    var coordinatorOptions = sp.GetRequiredService<IOptions<WorkCoordinatorOptions>>();
    return async (messages, ct) => {
      await schemaReadyGate.WaitForReadyAsync(ct).ConfigureAwait(false);
      using var scope = scopeFactory.CreateScope();
      var coordinator = scope.ServiceProvider.GetRequiredService<IWorkCoordinator>();
      await coordinator.StoreInboxMessagesAsync(messages, coordinatorOptions.Value.PartitionCount, ct).ConfigureAwait(false);
    };
  }
}
