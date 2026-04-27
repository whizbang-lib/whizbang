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
    // OutboxPublishWorker + InboxDispatchWorker classes are registered here so opt-in
    // extensions can resolve them, but they are NOT auto-hosted yet — Phase H step 2 will
    // move the AddHostedService lines into this method once ECommerce sample fixtures
    // switch off AddWhizbangLegacyPublisher.
    services.TryAddSingleton<OutboxPublishWorker>();
    services.TryAddSingleton<InboxDispatchWorker>();

    // Hosted services — delegate to the singleton instance so DI hands the same one
    // to both the hosted-service collection and the channel-surface registrations.
    services.AddHostedService(sp => sp.GetRequiredService<HeartbeatWorker>());
    services.AddHostedService(sp => sp.GetRequiredService<ClaimWorker>());
    services.AddHostedService(sp => sp.GetRequiredService<OutboxCompletionFlushWorker>());
    services.AddHostedService(sp => sp.GetRequiredService<PerspectiveCompletionFlushWorker>());
    services.AddHostedService(sp => sp.GetRequiredService<FailureFlushWorker>());
    services.AddHostedService(sp => sp.GetRequiredService<LeaseRenewalWorker>());
    services.AddHostedService(sp => sp.GetRequiredService<InboxHandlerWorker>());

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

    return services;
  }

  /// <summary>
  /// Registers the legacy <see cref="WorkCoordinatorPublisherWorker"/> and configures the new
  /// <see cref="ClaimWorker"/> to skip its claim loop (<see cref="ClaimWorkerOptions.PerspectiveOnly"/>).
  /// Use this when a host wants the legacy publisher as the sole poller — the publisher then
  /// forwards <see cref="WorkBatch.PerspectiveStreamIds"/> onto <see cref="IPerspectiveDrainChannel"/>
  /// so PerspectiveWorker (channel-consumer mode) still receives drain hints.
  /// </summary>
  /// <remarks>
  /// Without this flag, ClaimWorker and the legacy publisher race to call <c>claim_work</c> /
  /// <c>process_work_batch</c>. ClaimWorker's <c>claim_work</c> leases orphan rows first, then
  /// the publisher's <c>process_work_batch</c> sees an empty <c>temp_orphaned_inbox</c> and
  /// Phase 4.5B's event-store auto-create chain never fires for those rows.
  /// </remarks>
  /// <docs>fundamentals/work-coordinator/legacy-publisher-coexistence</docs>
  public static IServiceCollection AddWhizbangLegacyPublisher(this IServiceCollection services) {
    ArgumentNullException.ThrowIfNull(services);
    services.AddHostedService<WorkCoordinatorPublisherWorker>();
    services.Configure<ClaimWorkerOptions>(o => o.PerspectiveOnly = true);
    return services;
  }

  /// <summary>
  /// Registers <see cref="OutboxPublishWorker"/> as a hosted service. Call this when ready to
  /// migrate off <see cref="AddWhizbangLegacyPublisher"/> — it consumes from the same
  /// <see cref="IWorkChannelWriter"/>, so the two cannot both be active (only one will receive
  /// each message). The legacy publisher should be removed in the same change.
  /// </summary>
  /// <remarks>
  /// Phase H step 1 ships this opt-in extension so the worker is available behind an explicit
  /// flag. Phase H step 2 will move the <c>AddHostedService</c> line into
  /// <see cref="AddWhizbangWorkers"/> and delete this extension and
  /// <see cref="AddWhizbangLegacyPublisher"/>.
  /// </remarks>
  /// <docs>fundamentals/work-coordinator/outbox-publish</docs>
  public static IServiceCollection AddWhizbangOutboxPublisher(this IServiceCollection services) {
    ArgumentNullException.ThrowIfNull(services);
    services.AddHostedService(sp => sp.GetRequiredService<OutboxPublishWorker>());
    return services;
  }

  /// <summary>
  /// Registers <see cref="InboxDispatchWorker"/> as a hosted service. Call this when ready to
  /// migrate off <see cref="AddWhizbangLegacyPublisher"/> — it consumes from the same
  /// <see cref="IInboxChannelWriter"/>, so the two cannot both be active (only one will
  /// receive each orphan inbox row). The legacy publisher should be removed in the same change.
  /// </summary>
  /// <remarks>
  /// Phase H step 1 ships this opt-in extension so the worker is available behind an explicit
  /// flag. Phase H step 2 will move the <c>AddHostedService</c> line into
  /// <see cref="AddWhizbangWorkers"/> and delete this extension and
  /// <see cref="AddWhizbangLegacyPublisher"/>.
  /// </remarks>
  /// <docs>fundamentals/work-coordinator/inbox-dispatch</docs>
  public static IServiceCollection AddWhizbangInboxDispatcher(this IServiceCollection services) {
    ArgumentNullException.ThrowIfNull(services);
    services.AddHostedService(sp => sp.GetRequiredService<InboxDispatchWorker>());
    return services;
  }
}
