using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
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

    return services;
  }

}
