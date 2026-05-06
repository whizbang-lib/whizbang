using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Whizbang.Core.Messaging;
using Whizbang.Core.Notifications;

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

    // Hosted services — delegate to the singleton instance so DI hands the same one
    // to both the hosted-service collection and the channel-surface registrations.
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
    services.AddOptions<LeaseHandleOptions>();
    services.AddOptions<SlidingWindowOutboxOptions>();

    // Producer-side stream-affinity batcher (Half B of pump-then-process). Singleton; the
    // flush callback resolves IWorkCoordinator from a fresh DI scope per batch so the
    // strategy itself can outlive any one request scope. Default = sliding-window batcher;
    // override via AddWhizbangOutboxStrategy<TStrategy>() — see ImmediateOutboxBatchStrategy
    // for the no-batching alternative.
    services.TryAddSingleton<OutboxBulkFlushCallback>(_buildOutboxFlushCallback);
    services.TryAddSingleton<SlidingWindowOutboxBatchStrategy>(sp => new SlidingWindowOutboxBatchStrategy(
      flush: sp.GetRequiredService<OutboxBulkFlushCallback>(),
      options: sp.GetRequiredService<IOptions<SlidingWindowOutboxOptions>>().Value,
      timeProvider: sp.GetService<TimeProvider>(),
      logger: sp.GetService<ILogger<SlidingWindowOutboxBatchStrategy>>()));
    services.TryAddSingleton<ImmediateOutboxBatchStrategy>(sp => new ImmediateOutboxBatchStrategy(
      flush: sp.GetRequiredService<OutboxBulkFlushCallback>()));
    services.TryAddSingleton<IOutboxBatchStrategy>(sp => sp.GetRequiredService<SlidingWindowOutboxBatchStrategy>());

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

  private static OutboxBulkFlushCallback _buildOutboxFlushCallback(IServiceProvider sp) {
    var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();
    var schemaReadyGate = sp.GetRequiredService<ISchemaReadyGate>();
    var coordinatorOptions = sp.GetRequiredService<IOptions<WorkCoordinatorOptions>>();
    return async (messages, ct) => {
      await schemaReadyGate.WaitForReadyAsync(ct).ConfigureAwait(false);
      using var scope = scopeFactory.CreateScope();
      var coordinator = scope.ServiceProvider.GetRequiredService<IWorkCoordinator>();
      await coordinator.StoreOutboxMessagesAsync(messages, coordinatorOptions.Value.PartitionCount, ct).ConfigureAwait(false);
    };
  }
}
