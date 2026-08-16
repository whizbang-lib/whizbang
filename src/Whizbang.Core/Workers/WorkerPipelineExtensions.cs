using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Whizbang.Core.Health;
using Whizbang.Core.Messaging;
using Whizbang.Core.Notifications;
using Whizbang.Core.Routing;
using Whizbang.Core.RunControl;

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

    // Establish the thread-pool reserve BEFORE registering the workers that will compete for it.
    // These workers run on the host's pool, so their burst of async database completions is what
    // starves the host's own request pipeline — including a liveness endpoint that does no I/O,
    // whose probe timeout then kills a pod that is merely busy, discarding the progress that
    // would have ended the burst. Raises only; never lowers.
    WorkerThreadPoolFloor.Apply();

    // Schema-ready gate — workers await this before issuing any SQL. The driver's
    // initializer (e.g., WhizbangDatabaseInitializerService) calls MarkReady after migrations
    // complete. Singleton because all workers + the initializer must observe the same instance.
    services.TryAddSingleton<ISchemaReadyGate, SchemaReadyGate>();

    // Managed-resource health: register the aggregator + the "schema" source over the gate. When a
    // consumer wires AddWhizbangManagedHealthChecks() the schema reports "migrating" (ready under the
    // default Lenient policy) during a startup migration instead of failing readiness.
    services.AddWhizbangManagedHealth();
    services.AddWhizbangHealthSource<Health.SchemaHealthSource>();
    services.AddWhizbangHealthSource<Health.WorkerHealthSource>();

    // Transport managed-resource health: a REAL probe when a transport is registered — the driver's
    // ITransport.CheckConnectivityAsync (RabbitMQ IConnection.IsOpen / Service Bus !IsClosed) detects a
    // broker connection that dropped after init; RequiredWhenRunning, so a disconnected transport during a
    // migration is by-design. When there is no transport (single-service apps) it reports assumed-healthy.
    // One source either way — no duplication.
    services.AddSingleton<Health.IWhizbangHealthSource>(sp => {
      var lifecycle = sp.GetRequiredService<IWhizbangLifecycleState>();
      var transport = sp.GetService<Whizbang.Core.Transports.ITransport>();
      return transport is null
        ? Health.ConnectivityHealthSource.AssumedHealthy("transport", lifecycle)
        : Health.ConnectivityHealthSource.RequiredWhenRunning(
            "transport", transport.CheckConnectivityAsync, lifecycle, "transport broker unreachable");
    });

    // Offload managed-resource health: a REAL probe when an offload store is registered — the store's
    // IMessageBodyStore.CheckConnectivityAsync (a blob service round-trip; in-memory is always reachable);
    // assumed-healthy when no offload is configured. RequiredWhenRunning, one source either way.
    services.AddSingleton<Health.IWhizbangHealthSource>(sp => {
      var lifecycle = sp.GetRequiredService<IWhizbangLifecycleState>();
      var store = sp.GetService<Whizbang.Core.Offloads.IMessageBodyStore>();
      return store is null
        ? Health.ConnectivityHealthSource.AssumedHealthy("offload", lifecycle)
        : Health.ConnectivityHealthSource.RequiredWhenRunning(
            "offload", store.CheckConnectivityAsync, lifecycle, "offload store unreachable");
    });

    // signal-bus: ASSUMED-HEALTHY placeholder. Its real dependency is the same Postgres the event-store/DB
    // source already probes (wh_signals + LISTEN/NOTIFY), so a DB outage that breaks the signal bus already
    // surfaces there. TODO: a dedicated notify-connection liveness probe if finer signal is wanted.
    services.AddSingleton<Health.IWhizbangHealthSource>(sp =>
      Health.ConnectivityHealthSource.AssumedHealthy(
        "signal-bus", sp.GetRequiredService<IWhizbangLifecycleState>()));

    // Run-control (killswitch) plane + the driver that advances the lifecycle phase from the schema
    // gate (Migrating at startup, Ready once migrations complete), so any registered run-control
    // adapter is paused/resumed automatically. Inert when no adapters are registered.
    services.AddWhizbangRunControl();
    services.AddHostedService<LifecyclePhaseWorker>();
    // Each lifecycle transition is recorded on this instance's own row so peers and the status
    // surface can observe it — the standby handshake turns on states a peer can actually see.
    services.AddSingleton<IWhizbangRunControl, InstanceStateRunControl>();

    // Startup pipeline (increment 3 of the startup-pipeline proposal): declared steps, an order
    // resolved from their dependencies, and per-step outcome/duration/reason. The state is
    // registered as BOTH the queryable surface (IStartupPipelineState) and an observer — it
    // derives its answers from the same notifications every other observer gets, never
    // privileged. Migrate is the first framework behaviour to become a declared step: the
    // schema-ready gate is demoted from THE global barrier to that one step's completion
    // signal, and workers adopt WaitForAsync("Migrate") one declared dependency at a time.
    services.TryAddSingleton<Whizbang.Core.Startup.StartupPipelineState>();
    services.TryAddSingleton<Whizbang.Core.Startup.IStartupPipelineState>(
      sp => sp.GetRequiredService<Whizbang.Core.Startup.StartupPipelineState>());
    services.AddSingleton<Whizbang.Core.Startup.IStartupStepObserver>(
      sp => sp.GetRequiredService<Whizbang.Core.Startup.StartupPipelineState>());
    services.TryAddSingleton<Whizbang.Core.Observability.StartupPipelineMetrics>();
    services.AddSingleton<Whizbang.Core.Startup.IStartupStepObserver>(sp =>
      new Whizbang.Core.Startup.LoggingStartupStepObserver(
        (Microsoft.Extensions.Logging.ILogger?)sp.GetService<ILoggerFactory>()?.CreateLogger("Whizbang.Core.Startup.Pipeline")
          ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance));
    services.AddSingleton<Whizbang.Core.Startup.IStartupStepObserver>(sp =>
      new Whizbang.Core.Startup.MetricsStartupStepObserver(
        sp.GetRequiredService<Whizbang.Core.Observability.StartupPipelineMetrics>()));
    // Assess (increment 9): where this instance stands — Migrate/Serve/StandDown — decided on
    // every instance before the migration barrier. StandDown reports as a failed blocking step:
    // fail-closed readiness IS not-ready-while-alive.
    services.AddSingleton<Whizbang.Core.Startup.IStartupStep>(sp =>
      new Whizbang.Core.Startup.AssessStartupStep(
        sp.GetService<Whizbang.Core.Startup.IStartupAssessor>(),
        sp.GetService<ILoggerFactory>()?.CreateLogger<Whizbang.Core.Startup.AssessStartupStep>()));
    services.AddSingleton<Whizbang.Core.Startup.IStartupStep, Whizbang.Core.Startup.MigrateStartupStep>();
    // The post-ready table-rewrite step (increment 8): fleet-exclusive under the maintainer duty,
    // non-blocking with respect to Ready, deliberately unbounded. The runtime maintenance cycle
    // now only detects and records; this is where recorded rewrites actually run.
    services.AddSingleton<Whizbang.Core.Startup.IStartupStep, Whizbang.Core.Startup.TableRewriteStartupStep>();
    services.TryAddSingleton(sp => new Whizbang.Core.Startup.StartupPipelineRunner(
      [.. sp.GetServices<Whizbang.Core.Startup.IStartupStep>()],
      [.. sp.GetServices<Whizbang.Core.Startup.IStartupStepObserver>()],
      // Optional: the storage driver supplies the elector. Without one, a duty degrades to a
      // shared capability — survivable only because the framework's exclusive steps are
      // individually idempotent and separately guarded.
      sp.GetService<Whizbang.Core.Startup.IDutyElector>()));
    services.TryAddSingleton<Whizbang.Core.Startup.StartupPipelineWorker>();
    services.AddHostedService(sp => sp.GetRequiredService<Whizbang.Core.Startup.StartupPipelineWorker>());

    // Ready as a composite (increment 4): the terminal signal, on the one seam that means
    // "after everything" — IHostedLifecycleService.StartedAsync runs once every StartAsync has
    // returned. It waits for the blocking steps to drain, then for every registered readiness
    // contributor (transport subscription readiness among them), and only then marks the signal.
    // Fail-closed: a failed blocking step keeps the pipeline's readiness pending forever, so the
    // signal never fires and the instance never reports itself fully up.
    services.TryAddSingleton<Whizbang.Core.Startup.StartupReadySignal>();
    services.TryAddSingleton<Whizbang.Core.Startup.IStartupReadySignal>(
      sp => sp.GetRequiredService<Whizbang.Core.Startup.StartupReadySignal>());
    // The "startup" health component: probes report the current step and its progress, so "why is
    // this pod not ready" is answerable from the health surface without reading logs.
    services.AddSingleton<Health.IWhizbangHealthSource>(sp => new Health.StartupPipelineHealthSource(
      sp.GetRequiredService<Whizbang.Core.Startup.IStartupPipelineState>(),
      sp.GetService<Whizbang.Core.Startup.IStartupReadySignal>()));
    services.TryAddSingleton(sp => new Whizbang.Core.Startup.StartupReadyService(
      sp.GetRequiredService<Whizbang.Core.Startup.IStartupPipelineState>(),
      sp.GetRequiredService<Whizbang.Core.Startup.StartupReadySignal>(),
      [.. sp.GetServices<Whizbang.Core.Startup.IStartupReadinessContributor>()],
      sp.GetService<ILoggerFactory>()?.CreateLogger<Whizbang.Core.Startup.StartupReadyService>()));
    services.AddHostedService(sp => sp.GetRequiredService<Whizbang.Core.Startup.StartupReadyService>());

    // The standby handshake (increment 9): the watcher is the peer side — it drains and holds on
    // a newer instance's request, posts StandingBy for the migrator to observe, and carries the
    // runtime verdict (an instance becomes obsolete the moment a newer peer migrates underneath
    // it). StandbyHandshake is the migrator side, consumed when a breaking migration runs.
    services.TryAddSingleton<Whizbang.Core.Startup.StandbyWatcherOptions>();
    services.AddHostedService(sp => new Whizbang.Core.Startup.StandbyWatcher(
      sp.GetRequiredService<IServiceScopeFactory>(),
      sp.GetRequiredService<IWhizbangLifecycleState>(),
      sp.GetRequiredService<Microsoft.Extensions.Hosting.IHostApplicationLifetime>(),
      sp.GetService<Whizbang.Core.Observability.IServiceInstanceProvider>(),
      sp.GetService<Whizbang.Core.Observability.ILibraryVersionProvider>(),
      sp.GetService<Whizbang.Core.Startup.IStartupAssessor>(),
      sp.GetService<Whizbang.Core.Startup.StartupPipelineRunner>(),
      sp.GetService<ISchemaReadyGate>(),
      sp.GetService<Whizbang.Core.Startup.StandbyWatcherOptions>(),
      sp.GetService<ILoggerFactory>()?.CreateLogger<Whizbang.Core.Startup.StandbyWatcher>()));

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
    // WhizbangMetrics normally rides AddWhizbang; the TryAdd keeps a standalone pipeline
    // registration constructable (the F2-era lesson: extensions must be self-contained).
    services.TryAddSingleton<Whizbang.Core.Observability.WhizbangMetrics>();
    services.TryAddSingleton<Whizbang.Core.Observability.StreamIntegrityMetrics>();
    services.TryAddSingleton<Whizbang.Core.Observability.MaintenanceMetrics>();
    services.TryAddSingleton<IntegrityCheckpointWorker>();
    services.TryAddSingleton<SubscriptionExpansionWorker>();
    services.TryAddSingleton<IntegrityAuditWorker>();
    services.TryAddSingleton<RepairDrainWorker>();
    // #80-D: the audit worker doubles as the sweep runner (the scheduled occurrence's receptor
    // resolves it); the state object is how the driver's cron scheduler stands the counter down.
    services.TryAddSingleton<IIntegritySweepRunner>(sp => sp.GetRequiredService<IntegrityAuditWorker>());
    services.TryAddSingleton<IntegritySweepScheduleState>();
    services.TryAddSingleton<Whizbang.Core.Messaging.IntegrityGapTracker>();
    services.TryAddSingleton<Whizbang.Core.Messaging.IntegrityRepairLedger>();
    services.TryAddSingleton<OutboxPublishWorker>();
    services.TryAddSingleton<InboxDispatchWorker>();
    services.TryAddSingleton<OutboxDrainWorker>();
    services.TryAddSingleton<InboxDrainWorker>();
    services.TryAddSingleton<DeadLetterRecoveryWorker>();
    services.TryAddSingleton<TransportDeadLetterDrainWorker>();
    // Type-definition fingerprint reconciler (F-4): detect-by-default, act-by-opt-in. Inert without a
    // catalog (GetService returns null) or without the fingerprint tables (coordinator defaults no-op).
    services.AddOptions<Whizbang.Core.Configuration.EphemeralOptions>();
    // Perspective row retention (operator rung of the override ladder): the options are applied
    // to the TTL registry at startup so a TTL can be retuned — or retention switched off — per
    // environment without a redeploy. Registered as a hosted configurator; inert on defaults.
    services.AddOptions<Whizbang.Core.Configuration.PerspectiveRowRetentionOptions>();
    services.TryAddSingleton<Whizbang.Core.Perspectives.PerspectiveRowRetentionConfigurator>();
    services.AddSingleton<Microsoft.Extensions.Hosting.IHostedService>(
      sp => sp.GetRequiredService<Whizbang.Core.Perspectives.PerspectiveRowRetentionConfigurator>());
    // Schema-init behavior (blocking by default; opt-in non-blocking + optional migration timeout).
    services.AddOptions<SchemaInitializationOptions>();
    services.TryAddSingleton(TimeProvider.System);
    services.TryAddSingleton(sp => new Whizbang.Core.Fingerprint.TypeDefinitionReconciler(
      sp.GetRequiredService<Microsoft.Extensions.DependencyInjection.IServiceScopeFactory>(),
      sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<Whizbang.Core.Configuration.EphemeralOptions>>(),
      sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<Whizbang.Core.Fingerprint.TypeDefinitionReconciler>>(),
      sp.GetService<Whizbang.Core.IMessageTypeCatalog>()));
    services.TryAddSingleton<Whizbang.Core.Fingerprint.TypeDefinitionReconcilerHostedService>();
    // A1 "close the books" (StreamCloser): fires the E2 destruction hook around a Sourced-stream close.
    // Factory-resolved so the IDestructionHook is optional (null = a thin pass-through to the gated truncate).
    services.TryAddSingleton<Whizbang.Core.Lifecycle.IStreamCloser>(sp => new Whizbang.Core.Lifecycle.StreamCloser(
      sp.GetRequiredService<Whizbang.Core.Messaging.IWorkCoordinator>(),
      sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<Whizbang.Core.Lifecycle.StreamCloser>>(),
      sp.GetService<Whizbang.Core.Lifecycle.IDestructionHook>()));
    // E3 Tier-2 compaction (StreamCompactor): folds a state-based stream to a permanent Compacted origin,
    // reusing the snapshot store + event store + the A1 closer. On-demand, like IStreamCloser.
    services.TryAddSingleton<Whizbang.Core.Perspectives.IStreamCompactor>(sp => new Whizbang.Core.Perspectives.StreamCompactor(
      sp.GetRequiredService<Whizbang.Core.Perspectives.IPerspectiveSnapshotStore>(),
      sp.GetRequiredService<Whizbang.Core.Messaging.IWorkCoordinator>(),
      sp.GetRequiredService<Whizbang.Core.Messaging.IEventStore>(),
      sp.GetRequiredService<Whizbang.Core.Lifecycle.IStreamCloser>(),
      sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<Whizbang.Core.Perspectives.StreamCompactor>>()));
    services.TryAddSingleton<IGenerationProvider, DefaultGenerationProvider>();
    services.TryAddSingleton<IDeadLetterRecoveryPolicy, DefaultDeadLetterRecoveryPolicy>();
    services.TryAddSingleton<Whizbang.Core.Observability.DeadLetterMetrics>();
    // Process-wide TaskScheduler.UnobservedTaskException + (optional)
    // AppDomain.FirstChanceException subscription. Without this, exceptions raised on
    // fire-and-forget Tasks after their registered catch handler completes silently vanish
    // at GC time — see UnobservedExceptionDiagnostics for the production forensic investigation that drove this.
    services.TryAddSingleton<Whizbang.Core.Observability.UnobservedExceptionDiagnostics>();
    services.AddOptions<Whizbang.Core.Observability.UnobservedExceptionDiagnosticsOptions>();

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

    // Slice 4 of zero-idle-polling: single in-process idle-activity tracker +
    // registry-driven backup-tick coordinator. The tracker is shared across
    // every event source that proves the pod is doing real work; the
    // coordinator reads it on each loop iteration to decide between ASLEEP
    // (no DB calls) and POLLING (registered backstop ticks).
    services.AddOptions<BackupTickCoordinatorOptions>();
    services.TryAddSingleton<IIdleActivityTracker, IdleActivityTracker>();
    services.TryAddSingleton<IBackupTickRegistry, BackupTickRegistry>();
    services.TryAddSingleton<BackupTickCoordinator>();

    // Slice 4 of pump-then-process.md: source-generated receptor registry adapter. The
    // InboxDispatchWorker uses this to skip lifecycle deserialize for cross-service events
    // that the local service has no receptor for. Registered as a singleton — adapter is
    // stateless and just forwards to the static generated lookup.
    services.TryAddSingleton<IReceptorRegistryQuery>(sp =>
      new WhizbangReceptorRegistryQueryAdapter(sp.GetService<IReceptorRegistry>()));

    // Message-discard policy: shared "should this message be skipped?" decision used by
    // the transport-receive, inbox-dispatch, and outbox-publish gates. Owns the structured
    // log level + OTel counter so all three gates emit consistent telemetry. The Meter is
    // dedicated to this concern so dashboards can scrape it without picking up unrelated
    // transport metrics. See <see cref="Whizbang.Core.Routing.MessageDiscardPolicy"/>.
#pragma warning disable CA2000 // The Meter is held by the singleton policy for the host's lifetime.
    services.TryAddSingleton<IMessageDiscardPolicy>(sp => new MessageDiscardPolicy(
      sp.GetRequiredService<IReceptorRegistryQuery>(),
      sp.GetRequiredService<ILogger<MessageDiscardPolicy>>(),
      new System.Diagnostics.Metrics.Meter(MessageDiscardPolicy.METER_NAME),
      sp.GetService<Microsoft.Extensions.Options.IOptions<Whizbang.Core.Routing.RoutingOptions>>()));
#pragma warning restore CA2000

    // Hosted services — delegate to the singleton instance so DI hands the same one
    // to both the hosted-service collection and the channel-surface registrations.
    // Diagnostics warm-up runs BEFORE any worker so the TaskScheduler.UnobservedTaskException
    // + FirstChanceException subscriptions are in place before any fire-and-forget Task can
    // be spawned. Singleton construction is the side-effect we want — StartAsync just
    // resolves the singleton from DI and returns.
    services.AddHostedService<Whizbang.Core.Observability.UnobservedExceptionDiagnosticsWarmUp>();
    services.AddHostedService(sp => sp.GetRequiredService<HeartbeatWorker>());
    services.AddHostedService(sp => sp.GetRequiredService<ClaimWorker>());
    services.AddHostedService(sp => sp.GetRequiredService<OutboxCompletionFlushWorker>());
    services.AddHostedService(sp => sp.GetRequiredService<PerspectiveCompletionFlushWorker>());
    services.AddHostedService(sp => sp.GetRequiredService<FailureFlushWorker>());
    services.AddHostedService(sp => sp.GetRequiredService<LeaseRenewalWorker>());
    services.AddHostedService(sp => sp.GetRequiredService<InboxHandlerWorker>());
    services.AddHostedService(sp => sp.GetRequiredService<MaintenanceWorker>());
    services.AddHostedService(sp => sp.GetRequiredService<IntegrityCheckpointWorker>());
    services.AddHostedService(sp => sp.GetRequiredService<SubscriptionExpansionWorker>());
    services.AddHostedService(sp => sp.GetRequiredService<IntegrityAuditWorker>());
    services.AddHostedService(sp => sp.GetRequiredService<RepairDrainWorker>());
    services.AddHostedService(sp => sp.GetRequiredService<OutboxPublishWorker>());
    services.AddHostedService(sp => sp.GetRequiredService<InboxDispatchWorker>());
    services.AddHostedService(sp => sp.GetRequiredService<OutboxDrainWorker>());
    services.AddHostedService(sp => sp.GetRequiredService<InboxDrainWorker>());
    services.AddHostedService(sp => sp.GetRequiredService<DeadLetterRecoveryWorker>());
    services.AddHostedService(sp => sp.GetRequiredService<TransportDeadLetterDrainWorker>());
    services.AddHostedService(sp => sp.GetRequiredService<RecentlyProcessedEventCacheSweepWorker>());
    services.AddHostedService(sp => sp.GetRequiredService<Whizbang.Core.Fingerprint.TypeDefinitionReconcilerHostedService>());

    // Slice 4 of zero-idle-polling: register the coordinator as a hosted service
    // AND its touch-hook binder so the subscriptions wire up at startup. The
    // binder runs StartAsync once to subscribe Touch on ClaimWorker /
    // HeartbeatWorker / IWorkNotificationListener — no per-request work.
    services.AddHostedService(sp => sp.GetRequiredService<BackupTickCoordinator>());
    services.AddHostedService<IdleActivityTouchHookBinder>();
    services.AddHostedService<DefaultBackupTickRegistrar>();

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
    // recommended Npgsql Maximum Pool Size). v0.654 adds a 30 s deadline on the internal
    // semaphore wait so a saturated gate logs + degrades gracefully instead of hanging
    // every caller silently. Users can register their own gate before calling
    // AddWhizbang to override either the cap or the deadline.
    services.TryAddSingleton(sp => new WorkCoordinatorGate(
      maxConcurrent: 50,
      acquireTimeoutMilliseconds: 30000,
      logger: sp.GetService<ILogger<WorkCoordinatorGate>>(),
      metrics: sp.GetService<Whizbang.Core.Observability.WorkCoordinatorMetrics>()));

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
    services.AddOptions<DeadLetterRecoveryOptions>();
    services.AddOptions<TransportDeadLetterDrainWorkerOptions>();
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
