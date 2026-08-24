using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Whizbang.Core.Attributes;
using Whizbang.Core.AutoPopulate;
using Whizbang.Core.Lenses;
using Whizbang.Core.Lifecycle;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Resilience;
using Whizbang.Core.Routing;
using Whizbang.Core.Security;
using Whizbang.Core.Tags;
using Whizbang.Core.Transports;
using Whizbang.Core.Validation;

#pragma warning disable CA1848 // Use LoggerMessage delegates for performance (not critical for worker startup/shutdown)

namespace Whizbang.Core.Workers;

/// <summary>
/// Generic background service that consumes messages from any ITransport implementation.
/// Subscribes to configured destinations with built-in resilience (retry with exponential backoff).
/// Uses IReceptorInvoker for unified lifecycle receptor invocation (compile-time and runtime).
/// </summary>
/// <remarks>
/// <para>
/// Resilience features:
/// <list type="bullet">
/// <item>Exponential backoff retry for failed subscriptions</item>
/// <item>Per-destination state tracking</item>
/// <item>Connection recovery handling via <see cref="ITransportWithRecovery"/></item>
/// <item>Health monitoring for failed subscriptions</item>
/// </list>
/// </para>
/// </remarks>
/// <docs>messaging/transports/transport-consumer</docs>
/// <tests>tests/Whizbang.Core.Tests/Workers/TransportConsumerWorkerTests.cs</tests>
/// <tests>tests/Whizbang.Core.Tests/Workers/TransportConsumerWorkerSecurityContextTests.cs</tests>
/// <tests>tests/Whizbang.Core.Tests/Workers/TransportConsumerWorkerDropGateTests.cs:BatchHandler_CompositeWireType_NotDroppedByNoConsumerGateAsync</tests>
public partial class TransportConsumerWorker : BackgroundService, Whizbang.Core.Startup.IStartupReadinessContributor {
  private readonly ITransport _transport;
  private readonly TransportConsumerOptions _options;
  private readonly SubscriptionResilienceOptions _resilienceOptions;
  private readonly IServiceScopeFactory _scopeFactory;
  private readonly JsonSerializerOptions _jsonOptions;
  private readonly OrderedStreamProcessor _orderedProcessor;
  private readonly ILifecycleMessageDeserializer? _lifecycleMessageDeserializer;
  private readonly TransportMetrics? _metrics;
  private readonly ILogger<TransportConsumerWorker> _logger;

  private readonly ConcurrentBag<Task> _detachedTasks = [];
  private readonly HashSet<string> _ownedDomains;
  private readonly string? _serviceName;
  private readonly IReceptorRegistryQuery? _receptorRegistry;
  private readonly IReceptorRegistry? _runtimeReceptorRegistry;
  private readonly IEphemeralModeResolver? _ephemeralModeResolver;
  // Once-per-type diagnostic guard for catalog-lookup misses on the receive path (bounded).
  private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _warnedFlagMisses = new();
  private readonly ISchemaReadyGate? _schemaReadyGate;
  private readonly IEventMarkerResolver? _eventMarkerResolver;

  // Signals when SubscribeToAllDestinationsAsync has completed and the consumer
  // is ACTUALLY bound to its transport destinations. Completes regardless of
  // per-subscription success/failure — the health monitor handles retries.
  private readonly TaskCompletionSource _subscriptionsReadyTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

  /// <summary>
  /// A task that completes once the worker has finished its initial
  /// subscription pass against every configured destination. Production
  /// callers can <c>await</c> this to know the consumer is operational
  /// (for example, to gate a readiness probe or to delay the first
  /// downstream dispatch until inbound traffic can actually be received).
  /// Tests can await it to eliminate the start-vs-subscribe race that
  /// otherwise drops the first messages on the floor.
  /// </summary>
  /// <remarks>
  /// <para>This signals subscription <em>completion</em>, not health —
  /// failed destinations still resolve this task. Use
  /// <see cref="SubscriptionStatus"/> on individual states for granular
  /// health.</para>
  /// <para>The task completes exactly once per worker lifetime.</para>
  /// </remarks>
  /// <docs>messaging/transports/transport-consumer-readiness</docs>
  public Task SubscriptionsReady => _subscriptionsReadyTcs.Task;

  /// <summary>
  /// Convenience wrapper around <see cref="SubscriptionsReady"/> that respects
  /// a cancellation token. Equivalent to
  /// <c>await SubscriptionsReady.WaitAsync(cancellationToken)</c>.
  /// </summary>
  public Task WaitForSubscriptionsReadyAsync(CancellationToken cancellationToken = default)
    => SubscriptionsReady.WaitAsync(cancellationToken);

  /// <inheritdoc />
  string Whizbang.Core.Startup.IStartupReadinessContributor.ContributorName => "transport-consumer";

  /// <inheritdoc />
  Task Whizbang.Core.Startup.IStartupReadinessContributor.WaitForContributorReadyAsync(CancellationToken cancellationToken)
    => SubscriptionsReady.WaitAsync(cancellationToken);

  // Lazily-built set of event type names this service handles (has perspectives or receptors for).
  // Built from IEventTypeProvider on first use, immutable after. Used to pre-filter irrelevant inbox events.
  private HashSet<string>? _knownEventTypeNames;
  private readonly SemaphoreSlim? _concurrencySemaphore;
  private readonly TransportBatchOptions _transportBatchOptions;
  private readonly IWorkChannelWriter? _workChannelWriter;
  private readonly Dictionary<TransportDestination, SubscriptionState> _states = [];
  private CancellationTokenSource? _linkedCts;
  // Single source of truth for partition count across this service. Read from
  // WorkCoordinatorPublisherOptions so all three call sites — this consumer,
  // WorkBatchCoordinator, and WorkCoordinatorPublisherWorker — agree by construction.
  private readonly int _partitionCount;
  // Control class (topology arc phase 9): non-durable receive is a migration step consulted LIVE
  // off the shared options instance, so a rollback is a configuration edit, not a redeploy.
  private readonly Routing.ControlClassOptions? _controlClass;
  private readonly Tags.ControlClassResolver? _controlClassResolver;

  /// <summary>
  /// Initializes a new instance of TransportConsumerWorker.
  /// </summary>
  /// <param name="transport">The transport to consume messages from</param>
  /// <param name="options">Configuration options specifying destinations</param>
  /// <param name="resilienceOptions">Resilience options for subscription retry behavior</param>
  /// <param name="scopeFactory">Service scope factory for creating scoped services</param>
  /// <param name="jsonOptions">JSON serialization options</param>
  /// <param name="orderedProcessor">Ordered stream processor for message ordering</param>
  /// <param name="lifecycleMessageDeserializer">Optional lifecycle message deserializer for deserializing messages</param>
  /// <param name="metrics">Optional transport metrics for instrumentation</param>
  /// <param name="logger">Logger instance</param>
  /// <remarks>
  /// <para>
  /// <strong>IReceptorInvoker is scoped:</strong> The receptor invoker is resolved from the per-message scope
  /// rather than being injected as a constructor parameter. This follows industry patterns (MediatR, MassTransit)
  /// where handlers are scoped and resolved from the message processing scope.
  /// </para>
  /// </remarks>
#pragma warning disable S107 // Constructor uses DI injection — many parameters are idiomatic
  public TransportConsumerWorker(
    ITransport transport,
    TransportConsumerOptions options,
    SubscriptionResilienceOptions resilienceOptions,
    IServiceScopeFactory scopeFactory,
    JsonSerializerOptions jsonOptions,
    OrderedStreamProcessor orderedProcessor,
    ILifecycleMessageDeserializer? lifecycleMessageDeserializer,
    TransportMetrics? metrics,
    ILogger<TransportConsumerWorker> logger,
    Microsoft.Extensions.Options.IOptions<Routing.RoutingOptions>? routingOptions = null,
    IServiceInstanceProvider? serviceInstanceProvider = null,
    MessageProcessingOptions? messageProcessingOptions = null,
    TransportBatchOptions? transportBatchOptions = null,
    IWorkChannelWriter? workChannelWriter = null,
    Microsoft.Extensions.Options.IOptions<ClaimWorkerOptions>? claimWorkerOptions = null,
    IReceptorRegistryQuery? receptorRegistry = null,
    IReceptorRegistry? runtimeReceptorRegistry = null,
    IEphemeralModeResolver? ephemeralModeResolver = null,
    IEventMarkerResolver? eventMarkerResolver = null,
    // Startup barrier: subscribing lets the broker deliver, and delivery lands in the inbox —
    // database work against a schema that may not exist yet on a first boot. Optional only so
    // existing fixtures construct unchanged; DI always supplies it.
    ISchemaReadyGate? schemaReadyGate = null,
    // Control class (topology arc phase 9). Both optional: absent ⇒ every message takes the
    // durable path, i.e. pre-phase-9 behavior with no new branch reachable at all.
    Microsoft.Extensions.Options.IOptions<Routing.ControlClassOptions>? controlClass = null,
    Tags.ControlClassResolver? controlClassResolver = null
  ) {
#pragma warning restore S107
    ArgumentNullException.ThrowIfNull(transport);
    ArgumentNullException.ThrowIfNull(options);
    ArgumentNullException.ThrowIfNull(resilienceOptions);
    ArgumentNullException.ThrowIfNull(scopeFactory);
    ArgumentNullException.ThrowIfNull(jsonOptions);
    ArgumentNullException.ThrowIfNull(orderedProcessor);
    ArgumentNullException.ThrowIfNull(logger);

    _transport = transport;
    _options = options;
    _resilienceOptions = resilienceOptions;
    _scopeFactory = scopeFactory;
    _jsonOptions = jsonOptions;
    _orderedProcessor = orderedProcessor;
    _lifecycleMessageDeserializer = lifecycleMessageDeserializer;
    _metrics = metrics;
    _logger = logger;
    _ownedDomains = routingOptions?.Value?.OwnedDomains?.ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
    _serviceName = serviceInstanceProvider?.ServiceName;
    _receptorRegistry = receptorRegistry;
    _ephemeralModeResolver = ephemeralModeResolver;
    _eventMarkerResolver = eventMarkerResolver;
    _schemaReadyGate = schemaReadyGate;
    _runtimeReceptorRegistry = runtimeReceptorRegistry;
    _transportBatchOptions = transportBatchOptions ?? new TransportBatchOptions();
    _workChannelWriter = workChannelWriter;
    _partitionCount = claimWorkerOptions?.Value?.PartitionCount ?? new ClaimWorkerOptions().PartitionCount;
    _controlClass = controlClass?.Value;
    _controlClassResolver = controlClassResolver;

    var maxConcurrent = messageProcessingOptions?.MaxConcurrentMessages ?? 40;
    _concurrencySemaphore = maxConcurrent > 0 ? new SemaphoreSlim(maxConcurrent) : null;

    // Initialize state for each destination
    foreach (var destination in _options.Destinations) {
      _states[destination] = new SubscriptionState(destination);
    }

    // Register recovery handler if transport supports it
    if (_transport is ITransportWithRecovery recoveryTransport) {
      recoveryTransport.SetRecoveryHandler(_onConnectionRecoveredAsync);
    }
  }

  /// <summary>
  /// Gets the current subscription states for health monitoring.
  /// </summary>
  public IReadOnlyDictionary<TransportDestination, SubscriptionState> SubscriptionStates => _states;

  /// <summary>
  /// Executes the worker, creating subscriptions for all configured destinations.
  /// </summary>
  /// <param name="stoppingToken">Token to signal shutdown</param>
  protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
    // Startup barrier: nothing below may run before migrations complete — subscribing lets the
    // broker deliver, and delivery lands in the inbox tables the migration creates.
    if (_schemaReadyGate is not null) {
      try {
        await _schemaReadyGate.WaitForReadyAsync(stoppingToken);
      } catch (OperationCanceledException) {
        return;
      }
    }

    // Receive-path flag-derivation wiring diagnostic: a missing resolver or a host-only index count
    // means every flag-bearing event received over the transport silently loses its flags — surface
    // the wiring state once at startup so a broken chain is visible in service logs, not just in data.
    if (_logger.IsEnabled(LogLevel.Information)) {
      _logger.LogInformation(
        "Receive-path flag derivation: eventMarkerResolver={HasMarkerResolver}, ephemeralModeResolver={HasEphemeralResolver}, indexedTypeNames={IndexedTypeNames}",
        _eventMarkerResolver is not null,
        _ephemeralModeResolver is not null,
        (_eventMarkerResolver as EventMarkerResolver)?.IndexedTypeNameCount ?? -1);
    }
    if (_logger.IsEnabled(LogLevel.Information)) {
      var destinationCount = _options.Destinations.Count;
      _logger.LogInformation("TransportConsumerWorker starting with {DestinationCount} destinations", destinationCount);
    }

    // Log all destinations we're going to subscribe to
    // S3267: Loop has side effects (logging/state mutation) — LINQ not appropriate
#pragma warning disable S3267
    foreach (var destination in _options.Destinations) {
      if (_logger.IsEnabled(LogLevel.Debug)) {
        var address = destination.Address;
        var routingKey = destination.RoutingKey ?? "#";
        _logger.LogDebug(
          "  → Destination: {Address} (routing key: {RoutingKey})",
          address,
          routingKey
        );
      }
    }
#pragma warning restore S3267

    _linkedCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);

    try {
      // Wait for transport readiness if readiness check is configured
      using (var scope = _scopeFactory.CreateScope()) {
        var readinessCheck = scope.ServiceProvider.GetService<ITransportReadinessCheck>();
        if (readinessCheck != null) {
          _logger.LogDebug("Waiting for transport readiness");
          var isReady = await readinessCheck.IsReadyAsync(stoppingToken);
          if (!isReady) {
            _logger.LogWarning("Transport readiness check returned false");
            // No subscriptions will be issued — release readiness waiters so
            // they don't hang forever.
            _subscriptionsReadyTcs.TrySetResult();
            return;
          }
          _logger.LogDebug("Transport is ready");
        }

        // Provision infrastructure for owned domains before creating subscriptions
        var provisioner = scope.ServiceProvider.GetService<IInfrastructureProvisioner>();
        var routingOptions = scope.ServiceProvider.GetService<IOptions<RoutingOptions>>()?.Value;
        if (provisioner != null && routingOptions?.OwnedDomains.Count > 0) {
          if (_logger.IsEnabled(LogLevel.Debug)) {
            var ownedDomainsCount = routingOptions.OwnedDomains.Count;
            _logger.LogDebug(
              "Provisioning infrastructure for {Count} owned domains",
              ownedDomainsCount);
          }

          await provisioner.ProvisionOwnedDomainsAsync(routingOptions.OwnedDomains, stoppingToken);

          _logger.LogDebug("Infrastructure provisioning completed");
        }

        // Manifest-driven DARK provisioning (topology arc phase 5): when a TopologyManifest is
        // resolvable, create every entity it names — publish destinations AND subscription
        // entities — before subscribing, so new entities (per-namespace command inboxes) exist
        // and sit idle until publishers flip to them. No manifest ⇒ no change for existing
        // consumers; the default provisioner implementation is a no-op.
        if (provisioner != null) {
          var manifest = scope.ServiceProvider.GetService<TopologyManifest>();
          if (manifest != null) {
            if (_logger.IsEnabled(LogLevel.Debug)) {
              _logger.LogDebug(
                "Provisioning topology manifest for '{ServiceName}' ({PublishCount} publish destinations, {SubscriptionCount} subscriptions)",
                manifest.ServiceName,
                manifest.PublishDestinations.Count,
                manifest.Subscriptions.Count);
            }

            await provisioner.ProvisionManifestAsync(manifest, stoppingToken);

            _logger.LogDebug("Topology manifest provisioning completed");
          }
        }
      }

      // Subscribe to all destinations with retry
      await _subscribeToAllDestinationsAsync(stoppingToken);

      // Signal subscription readiness — public SubscriptionsReady consumers
      // (production readiness probes, tests) unblock at this point. Health
      // status (vs subscription completion) is reported separately via the
      // per-destination SubscriptionStatus.
      _subscriptionsReadyTcs.TrySetResult();
    } catch (Exception ex) {
      // Startup failed — surface the exception to readiness waiters so they
      // observe the failure instead of hanging forever waiting for a signal
      // that will never come.
      _subscriptionsReadyTcs.TrySetException(ex);
      throw;
    }

    if (_logger.IsEnabled(LogLevel.Information)) {
      var healthyCount = _states.Values.Count(s => s.Status == SubscriptionStatus.Healthy);
      var failedCount = _states.Values.Count(s => s.Status == SubscriptionStatus.Failed);

      _logger.LogInformation(
        "TransportConsumerWorker started with {HealthyCount} healthy, {FailedCount} failed subscriptions",
        healthyCount,
        failedCount
      );
    }

    // Start health monitor in background
    _ = _monitorSubscriptionHealthAsync(_linkedCts.Token);

    // Keep running until cancellation is requested
    try {
      await Task.Delay(Timeout.Infinite, stoppingToken);
    } catch (OperationCanceledException ex) {
      _logger.LogInformation(ex, "TransportConsumerWorker cancellation requested");
    }
  }

  /// <summary>
  /// Subscribes to all destinations with retry logic.
  /// </summary>
  private async Task _subscribeToAllDestinationsAsync(CancellationToken cancellationToken) {
    var tasks = _states.Values.Select(state =>
      _subscribeWithRetryAsync(state, cancellationToken)
    );

    if (_resilienceOptions.AllowPartialSubscriptions) {
      // Allow partial failures - wait for all tasks
      await Task.WhenAll(tasks);
    } else {
      // All must succeed - throw on first failure
      foreach (var task in tasks) {
        await task;
        var completedState = _states.Values.FirstOrDefault(s => s.Status == SubscriptionStatus.Failed);
        if (completedState != null) {
          throw new InvalidOperationException(
            $"Subscription to {completedState.Destination.Address} failed and AllowPartialSubscriptions=false"
          );
        }
      }
    }
  }

  /// <summary>
  /// Subscribes to a single destination with retry logic.
  /// </summary>
  private async Task _subscribeWithRetryAsync(SubscriptionState state, CancellationToken cancellationToken) {
    if (_logger.IsEnabled(LogLevel.Debug)) {
      var address = state.Destination.Address;
      var routingKey = state.Destination.RoutingKey;
      _logger.LogDebug(
        "Creating subscription for destination: {Address}, routing key: {RoutingKey}",
        address,
        routingKey
      );
    }

    await SubscriptionRetryHelper.SubscribeWithRetryAsync(
      _transport,
      state.Destination,
      async (batch, ct) => await _handleMessageBatchAsync(batch, ct),
      _transportBatchOptions,
      state,
      _resilienceOptions,
      _logger,
      cancellationToken
    );
  }

  /// <summary>
  /// Handles connection recovery by re-establishing all subscriptions.
  /// </summary>
  private async Task _onConnectionRecoveredAsync(CancellationToken cancellationToken) {
    _logger.LogInformation("Connection recovered, re-establishing subscriptions...");

    // Reset all states to Pending
    foreach (var state in _states.Values) {
      state.Subscription?.Dispose();
      state.Subscription = null;
      state.Status = SubscriptionStatus.Pending;
      state.ResetAttempts();
    }

    // Re-subscribe to all destinations
    await _subscribeToAllDestinationsAsync(cancellationToken);

    _logger.LogInformation("Subscription re-establishment completed");
  }

  /// <summary>
  /// Background task that monitors subscription health and attempts recovery.
  /// </summary>
  private async Task _monitorSubscriptionHealthAsync(CancellationToken cancellationToken) {
    while (!cancellationToken.IsCancellationRequested) {
      try {
        await Task.Delay(_resilienceOptions.HealthCheckInterval, cancellationToken);

        var failedStates = _states.Values
          .Where(s => s.Status == SubscriptionStatus.Failed)
          .ToList();

        if (failedStates.Count > 0) {
          if (_logger.IsEnabled(LogLevel.Information)) {
            var count = failedStates.Count;
            _logger.LogInformation(
              "Health monitor: attempting to recover {Count} failed subscriptions",
              count
            );
          }

          foreach (var state in failedStates) {
            // Reset and retry in background
            state.Status = SubscriptionStatus.Pending;
            state.ResetAttempts();
            _ = _subscribeWithRetryAsync(state, cancellationToken);
          }
        }
      } catch (OperationCanceledException) {
        break;
      } catch (Exception ex) {
        _logger.LogError(ex, "Error in subscription health monitor");
      }
    }
  }

  /// <summary>
  /// Handles a received message using the inbox/work-coordinator pattern.
  /// Messages are stored in the inbox via process_work_batch for atomic deduplication.
  /// Perspectives are processed asynchronously by PerspectiveWorker.
  /// </summary>
  /// <summary>
  /// Batch handler: ONE scope, serialize all N, ONE process_work_batch, return → transport ACKs.
  /// Processing (Process, PostInbox, completion) deferred to WorkCoordinatorPublisherWorker.
  /// </summary>
  /// <docs>messaging/transports/transport-consumer#batch-handler</docs>
  /// <tests>tests/Whizbang.Core.Tests/Workers/TransportConsumerWorkerBulkInsertInvariantTests.cs:BatchOf100SubscribedMessages_StoredViaSingleBulkInsertAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Workers/TransportConsumerWorkerBulkInsertInvariantTests.cs:MixedBatch_DroppedTypesFilteredBeforeBulkInsertAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Workers/TransportConsumerWorkerBulkInsertInvariantTests.cs:BatchProcessing_CreatesExactlyOneScopePerBatchAsync</tests>
  private async Task _handleMessageBatchAsync(
      IReadOnlyList<TransportMessage> messages, CancellationToken cancellationToken) {
    if (messages.Count == 0) {
      return;
    }

    if (_logger.IsEnabled(LogLevel.Information)) {
      _logger.LogInformation("Processing batch of {BatchCount} messages from transport", messages.Count);
    }

    await using var scope = _scopeFactory.CreateAsyncScope();
    var workCoordinator = scope.ServiceProvider.GetRequiredService<IWorkCoordinator>();

    // Ensure event type set is built before echo check (lazy, one-time init).
    _ensureKnownEventTypesInitialized(scope.ServiceProvider);

    // Collect inbox messages for direct INSERT (bypasses full process_work_batch)
    var inboxMessages = new List<InboxMessage>(messages.Count);
    // Active-cleanup claims accumulated by the rehydrator; fired post-commit
    // so a failed inbox INSERT doesn't leave bodies stranded in the store.
    List<Whizbang.Core.Offloads.MessageBodyClaim>? pendingCleanupClaims = null;
    foreach (var msg in messages) {
      // Slice 3 of pump-then-process.md (Half A): drop messages whose inner type has NO
      // consumer on this service BEFORE serialization runs. Mirror of the gate added to
      // ServiceBusConsumerWorker. The post-build _filterInboxMessagesByKnownEventTypes
      // stays as defense-in-depth — broader registry coverage here catches lifecycle
      // receptors and tag-attribute consumers the IEventTypeProvider list misses.
      //
      // Gate consults BOTH the compile-time IReceptorRegistryQuery AND the runtime
      // IReceptorRegistry. Without the runtime branch, services whose source-generated
      // contribution lists no compile-time consumer for a type would silently drop messages
      // even when a runtime receptor is listening (integration-test wait helpers, dynamic
      // registrations). Worse than the lifecycle-gate version of this bug because dropping
      // here means no inbox row gets written at all — no downstream anything can recover.
      // EXEMPTION — body-offload (claim-check): an offloaded message's wire type is the internal
      // MessageEnvelope<BodyClaimEnvelopePayload>, which NO service registers a consumer for. Running
      // this no-consumer gate against the claim would drop EVERY offloaded message here — before
      // rehydration, with no inbox row written, unrecoverable. Skip the gate for claims; the real
      // type is restored by BodyClaimRehydrator in _tryBuildInboxMessageFromTransportAsync below.
      // EXEMPTION — composites: a composite is wire-only (IMessage, never IEvent), so NO service
      // registers a consumer for the composite type itself; its consumers are registered for the
      // INNER events, which only become addressable once the dispatch seam fans it out (see the
      // fan-out note below). Running the gate against the composite drops the ENTIRE bundle here,
      // before any inbox row is written — unrecoverable, and only visible at Debug. See
      // CompositeInboxFanout.IsCompositeWireType.
      if (_receptorRegistry is not null && !string.IsNullOrWhiteSpace(msg.EnvelopeType)
          && !EnvelopeTypeNameHelper.IsBodyClaimEnvelope(msg.EnvelopeType)) {
        var innerMessageType = EnvelopeTypeNameHelper.ExtractInnerTypeName(msg.EnvelopeType);
        if (innerMessageType is not null
            && !_receptorRegistry.HasAnyConsumer(innerMessageType)
            && !(_runtimeReceptorRegistry?.HasAnyRuntimeReceptors(innerMessageType) ?? false)
            && !CompositeInboxFanout.IsCompositeWireType(innerMessageType, _eventMarkerResolver)) {
          _metrics?.InboxMessagesDeduplicated.Add(1);
          continue;
        }
      }

      // CONTROL CLASS — non-durable receive (topology arc phase 9): receive → compare → discard.
      // A supersedable control signal is re-derived on the next cadence, so a stored copy is
      // worthless when read AND not inert: the durable inbox feeds retry and recovery, so a burst
      // of control failures becomes a backlog every subsequent boot replays. This is the
      // receive-boundary extension of the rule DeadLetterDropPolicy already applies at the
      // dead-letter boundary. The comparison is not skipped — it MOVES here, at the same
      // lifecycle stage the durable path fires it at, so control receptors are unchanged.
      if (_controlClass?.NonDurableReceive == true
          && _controlClassResolver is not null
          && !string.IsNullOrWhiteSpace(msg.EnvelopeType)
          && _controlClassResolver.IsControlClass(EnvelopeTypeNameHelper.ExtractInnerTypeName(msg.EnvelopeType))) {
        await _compareAndDiscardControlMessageAsync(msg, scope.ServiceProvider, cancellationToken);
        continue;
      }

      var (inboxMessage, cleanupClaim) = await _tryBuildInboxMessageFromTransportAsync(msg, scope.ServiceProvider, cancellationToken);
      if (inboxMessage is not null) {
        // Composites are stored as ordinary inbox rows — fan-out moved to the dispatch seam
        // (InboxDispatchWorker, inside the durable retry/DLQ envelope), per Phase A of
        // plans/composite-events-turnkey.md. The transport edge no longer expands them; doing so
        // here orphaned the composite from durability/retry. The composite row is wire-only — it is
        // never event-stored (IsEvent == false); the dispatch worker recognizes the typed
        // ICompositeEvent payload and fans it out into per-inner-event inbox rows.
        inboxMessages.Add(inboxMessage);
        if (cleanupClaim is not null) {
          (pendingCleanupClaims ??= []).Add(cleanupClaim);
        }
      }
    }

    if (inboxMessages.Count == 0) {
      return;
    }

    _filterInboxMessagesByKnownEventTypes(inboxMessages);
    if (inboxMessages.Count == 0) {
      return;  // All messages filtered — nothing to store
    }

    // Direct INSERT into wh_inbox — bypasses process_work_batch entirely.
    // Event storage + perspective creation happens on next tick via self-healing
    // (Phase 5 claims → Phase 4.5B stores events → Phase 4.6/4.7 creates perspectives).
    // PartitionCount is sourced from WorkCoordinatorPublisherOptions — the single source
    // of truth across this service's TransportConsumerWorker, WorkBatchCoordinator, and
    // WorkCoordinatorPublisherWorker — preventing the partition mismatch wedge.
    //
    // Topology arc phase 8.5 — poison detection LAYER 2. When a detector is wired, use the store
    // overload that also reports durable REDELIVERY OBSERVATIONS. This is not a second query: the
    // store-side idempotency record is written on every delivery regardless, so the count comes
    // back as a by-product of work already being done. It is the only bound available for poison
    // that dies mid-processing rather than mid-lock, and for surfaces with no trustworthy broker
    // first-enqueue timestamp — on a session-enabled entity the broker's delivery counter never
    // rises under connection-death lock loss, so MaxDeliveryCount is structurally unreachable.
    // Without a detector nothing changes: same call, same shape, same behavior as before.
    var poisonDetector = scope.ServiceProvider.GetService<Routing.IPoisonMessageDetector>();
    if (poisonDetector is null) {
      await workCoordinator.StoreInboxMessagesAsync(
        [.. inboxMessages],
        partitionCount: _partitionCount,
        cancellationToken: cancellationToken);
    } else {
      var observations = await workCoordinator.StoreInboxMessagesWithObservationsAsync(
        [.. inboxMessages],
        partitionCount: _partitionCount,
        cancellationToken: cancellationToken);
      await _quarantinePoisonRedeliveriesAsync(
        observations, poisonDetector, scope.ServiceProvider, cancellationToken);
    }
    _metrics?.InboxMessagesProcessed.Add(inboxMessages.Count);

    if (_logger.IsEnabled(LogLevel.Debug)) {
      _logger.LogDebug("Batch of {BatchCount} messages inserted into inbox — ACKing transport", inboxMessages.Count);
    }

    // Signal the publisher worker to poll immediately so messages are claimed and processed promptly.
    _workChannelWriter?.SignalNewWorkAvailable();

    // Active-cleanup body-offload claims (MessageBodyOffloadOptions.ActiveCleanup=true).
    // Runs AFTER the inbox commit succeeds — a failed insert never deletes a body that's
    // still needed for redelivery. Fire-and-forget: provider's IgnoreMissing default
    // (true) tolerates fan-out double-delete in shared-store topologies; transient
    // delete failures fall back to the provider's storage-level TTL (the default
    // ActiveCleanup=false path).
    if (pendingCleanupClaims is { Count: > 0 }) {
      _ = _fireActiveCleanupAsync(pendingCleanupClaims, cancellationToken);
    }
    // Handler returns → transport ACKs all N messages → next batch starts collecting
  }

  /// <summary>
  /// The control class's non-durable receive: invoke the class's receptors inline (the "compare"),
  /// then discard. Never writes an inbox row, never records completion, and never dead-letters.
  /// </summary>
  /// <remarks>
  /// A failed comparison is swallowed BY DESIGN. Rethrowing would abandon the broker message,
  /// which is redelivery — the unbounded loop this class exists to make structurally impossible —
  /// and dead-lettering would resurrect the durable control backlog the path removes. The next
  /// cadence re-derives the signal and the comparison simply runs again on fresher data.
  /// </remarks>
  /// <tests>tests/Whizbang.Core.Tests/Workers/TransportConsumerWorkerControlClassReceiveTests.cs:ControlMessage_ComparisonThrows_DropsWithoutDeadLetteringAsync</tests>
  private async Task _compareAndDiscardControlMessageAsync(
      TransportMessage msg, IServiceProvider scopedProvider, CancellationToken cancellationToken) {
    try {
      var invoker = scopedProvider.GetService<IReceptorInvoker>();
      if (invoker is not null) {
        // The SAME stage the durable path fires for an inbox delivery, so a control receptor sees
        // no difference; only the durability around it disappears.
        await invoker.InvokeAsync(
          msg.Envelope,
          LifecycleStage.PostInboxInline,
          new LifecycleExecutionContext {
            CurrentStage = LifecycleStage.PostInboxInline,
            MessageSource = MessageSource.Inbox,
            AttemptNumber = 1,
          },
          cancellationToken);
      }
    } catch (Exception ex) when (ex is not OperationCanceledException) {
      _logger.LogWarning(ex,
        "Control-class comparison failed for {EnvelopeType}; dropping (control-plane failures drop "
        + "rather than dead-letter — the next cadence re-derives this signal)",
        msg.EnvelopeType);
    }
  }

  /// <summary>
  /// Fire-and-forget cleanup for active-mode body offload. Resolves the matching
  /// store per claim (different claims may use different providers) and issues
  /// DeleteAsync. Failures are logged but do not affect transport ACK — the
  /// provider's TTL is the backstop.
  /// </summary>
  private async Task _fireActiveCleanupAsync(
      List<Whizbang.Core.Offloads.MessageBodyClaim> claims,
      CancellationToken cancellationToken) {
    await using var cleanupScope = _scopeFactory.CreateAsyncScope();
    foreach (var claim in claims) {
      try {
        var store = cleanupScope.ServiceProvider.GetKeyedService<Whizbang.Core.Offloads.IMessageBodyStore>(claim.ProviderName);
        if (store is null) {
          _logger.LogWarning(
            "Active cleanup skipped for claim {StorageKey}: no IMessageBodyStore registered under provider '{ProviderName}'",
            claim.StorageKey, claim.ProviderName);
          continue;
        }
        await store.DeleteAsync(claim, options: null, cancellationToken);
      } catch (Exception ex) when (ex is not OperationCanceledException) {
        _logger.LogWarning(ex,
          "Active cleanup failed for claim {StorageKey} on provider '{ProviderName}'; provider TTL is the backstop",
          claim.StorageKey, claim.ProviderName);
      }
    }
  }

  /// <summary>
  /// Runs owned-domain echo discard, body-offload claim rehydrate (when the
  /// wire message was a claim envelope), OTEL activity, timestamp population,
  /// and envelope serialization for a single transport message. Returns null
  /// when the message should be dropped (echo, claim rehydrate failure, or
  /// serialize failure — all logged + metric already recorded).
  /// </summary>
  /// <docs>docs/transport-routing-architecture.md#transport-echo-suppression</docs>
  /// <tests>tests/Whizbang.Core.Tests/Workers/TransportConsumerWorkerOwnedEventDiscardTests.cs</tests>
  /// <tests>tests/Whizbang.Core.Tests/Workers/TransportConsumerWorkerBodyClaimRehydrateTests.cs</tests>
  private async Task<(InboxMessage? InboxMessage, Whizbang.Core.Offloads.MessageBodyClaim? PendingCleanupClaim)>
      _tryBuildInboxMessageFromTransportAsync(
      TransportMessage msg, IServiceProvider scopedProvider, CancellationToken cancellationToken) {
    var envelopeType = msg.EnvelopeType;
    var envelope = msg.Envelope;
    var messageType = envelopeType is not null ? TypeNameFormatter.GetSimpleName(envelopeType) : "Unknown";
    var messageTypeTag = new KeyValuePair<string, object?>("message_type", messageType);
    _metrics?.InboxMessagesReceived.Add(1, messageTypeTag);

    if (_shouldDiscardForeignTarget(envelope, messageType, messageTypeTag)) {
      return (null, null);
    }

    if (_shouldDiscardOwnedEcho(envelope, envelopeType, messageType, messageTypeTag)) {
      return (null, null);
    }

    // Body-offload claim rehydrate: when the transport handed us a claim
    // envelope (whizbang.is-claim was set on the wire), download the original
    // body via the registered IMessageBodyStore, verify SHA-256, and
    // deserialize as the original envelope type. Pass-through for non-claim
    // messages (the common case) is O(1) — only the payload-type check.
    Whizbang.Core.Offloads.MessageBodyClaim? cleanupClaim = null;
    var jsonOptions = scopedProvider.GetService<System.Text.Json.JsonSerializerOptions>();
    if (jsonOptions is not null) {
      var rehydrate = await Whizbang.Core.Offloads.BodyClaimRehydrator.MaybeRehydrateAsync(
        envelope, envelopeType, jsonOptions, scopedProvider, cancellationToken);
      if (rehydrate.IsDeadLetter) {
        _logger.LogError(
          "Body-offload claim rehydrate failed for message {MessageId}: {Reason} — {Description}; dropping",
          envelope.MessageId, rehydrate.FailureReason, rehydrate.FailureDescription);
        _metrics?.InboxMessagesFailed.Add(1, messageTypeTag);
        return (null, null);
      }
      envelope = rehydrate.Envelope!;
      envelopeType = rehydrate.EnvelopeType;
      cleanupClaim = rehydrate.PendingCleanupClaim;
      // Refresh the message-type tag now that we know the rehydrated identity.
      if (rehydrate.WasRehydrated && envelopeType is not null) {
        messageType = TypeNameFormatter.GetSimpleName(envelopeType);
        messageTypeTag = new KeyValuePair<string, object?>("message_type", messageType);
      }
    }

    var inboxActivity = _startInboxActivity(envelope, messageType);
    try {
      _populateDeliveredAtTimestamp(envelope, envelopeType);
      var inboxMessage = _serializeToNewInboxMessage(envelope, envelopeType, scopedProvider);
      inboxActivity?.SetStatus(ActivityStatusCode.Ok);
      return (inboxMessage, cleanupClaim);
    } catch (Exception ex) {
      _logger.LogError(ex, "Failed to serialize message {MessageId} for inbox — skipping", envelope.MessageId);
      _metrics?.InboxMessagesFailed.Add(1, messageTypeTag);
      inboxActivity?.SetStatus(ActivityStatusCode.Error, ex.Message);
      return (null, null);
    } finally {
      inboxActivity?.Dispose();
    }
  }

  /// <summary>
  /// Topology arc phase 8.5, layer 2 — asks Core's poison detector about each durable redelivery
  /// observation the store reported, and executes a quarantine verdict by moving the inbox row
  /// into the EXISTING dead-letter store, from which the existing recovery flow replays it.
  /// <para>
  /// The evaluation context deliberately carries NO first-enqueue timestamp: age (layer 1) is the
  /// transport receive boundary's job and has already run there. This gate exists for the cases
  /// layer 1 structurally cannot see, so it must decide on the durable count alone.
  /// </para>
  /// Best-effort by design — a dead-letter store that is absent or throwing must never fail the
  /// batch, because failing here would nack the whole delivery and feed the very redelivery loop
  /// being quarantined.
  /// </summary>
  private async Task _quarantinePoisonRedeliveriesAsync(
      IReadOnlyList<InboxRedeliveryObservation> observations,
      Routing.IPoisonMessageDetector poisonDetector,
      IServiceProvider scopedProvider,
      CancellationToken cancellationToken) {
    if (observations.Count == 0) {
      return;
    }

    var now = DateTimeOffset.UtcNow;
    foreach (var observation in observations) {
      var context = new Routing.PoisonEvaluationContext(
        MessageId: observation.MessageId.ToString(),
        FirstEnqueuedAt: null,
        BrokerDeliveryCount: null,
        DurableObservationCount: observation.ObservationCount,
        Now: now) { ProcessingAttempts = observation.ProcessingAttempts };

      var verdict = poisonDetector.Evaluate(context);
      if (!verdict.ShouldQuarantine) {
        continue;
      }

      poisonDetector.RecordQuarantine(
        Routing.PoisonQuarantineGate.Inbox, verdict, context,
        new Dictionary<string, object?> { ["source_table"] = DeadLetterSourceTable.INBOX });

      var deadLetterStore = scopedProvider.GetService<IDeadLetterStore>();
      var generationProvider = scopedProvider.GetService<IGenerationProvider>();
      var instanceProvider = scopedProvider.GetService<IServiceInstanceProvider>();
      if (deadLetterStore is null || generationProvider is null || instanceProvider is null) {
        continue;
      }

      try {
        await deadLetterStore.MoveAsync(
          deadLetterId: (Guid)Whizbang.Core.ValueObjects.TrackedGuid.NewMedo(),
          sourceTable: DeadLetterSourceTable.INBOX,
          sourceId: observation.MessageId,
          failureReason: MessageFailureReason.PoisonRedeliveryLoop,
          errorText: verdict.Detail,
          instanceId: instanceProvider.InstanceId,
          generation: generationProvider.GetGeneration(),
          ct: cancellationToken).ConfigureAwait(false);
      } catch (Exception ex) {
        _logger.LogError(
          ex,
          "Poison quarantine could not move inbox row {MessageId} to dead letters; the redelivery "
          + "loop remains unbounded for this message", observation.MessageId);
      }
    }
  }

  /// <summary>
  /// Owned events are ALWAYS echo (only the owning service publishes events in its namespace).
  /// Owned commands may come from other services, so the last-hop service name distinguishes
  /// echo from cross-service delivery.
  /// </summary>
  private bool _shouldDiscardOwnedEcho(
      IMessageEnvelope envelope,
      string? envelopeType,
      string messageType,
      KeyValuePair<string, object?> messageTypeTag) {
    if (_ownedDomains.Count == 0 || envelopeType is null) {
      return false;
    }
    var ns = TypeNameFormatter.GetPayloadNamespace(envelopeType);
    if (!_isOwnedNamespace(ns)) {
      return false;
    }
    if (_isKnownEventType(envelopeType)) {
      LogOwnedEventEchoDiscarded(_logger, messageType);
      _metrics?.InboxMessagesDeduplicated.Add(1, messageTypeTag);
      return true;
    }
    if (_serviceName is not null && _isSelfEcho(envelope)) {
      LogSelfEchoDiscarded(_logger, messageType, _serviceName);
      _metrics?.InboxMessagesDeduplicated.Add(1, messageTypeTag);
      return true;
    }
    return false;
  }

  /// <summary>
  /// Directed-message gate: a non-null envelope <see cref="IMessageEnvelope.Target"/> naming a
  /// DIFFERENT service discards the message before deserialization, inbox storage, or fan-out —
  /// targeted traffic (repair / control-plane / response) is point-to-point, and every
  /// non-target's copy is noise by definition. An absent target is broadcast (the default,
  /// unchanged). When this service's own name is unknown (no instance provider wired) targeted
  /// messages are ACCEPTED — fail-open: acceptance is idempotent downstream via the event-id
  /// conflict skip, while a wrong discard could starve the legitimate target.
  /// </summary>
  /// <docs>fundamentals/messaging/directed-messages</docs>
  /// <tests>tests/Whizbang.Core.Tests/Workers/TransportConsumerWorkerDirectedTargetTests.cs</tests>
  private bool _shouldDiscardForeignTarget(
      IMessageEnvelope envelope,
      string messageType,
      KeyValuePair<string, object?> messageTypeTag) {
    var target = envelope.Target;
    if (target is null || _serviceName is null
        || string.Equals(target, _serviceName, StringComparison.Ordinal)) {
      return false;
    }
    LogForeignTargetDiscarded(_logger, messageType, target, _serviceName);
    _metrics?.InboxMessagesDeduplicated.Add(1, messageTypeTag);
    return true;
  }

  /// <summary>
  /// Pre-filter: drops events this service has no perspectives or receptors for. Commands are
  /// always kept because they're routed to a specific service on purpose.
  /// </summary>
  private void _filterInboxMessagesByKnownEventTypes(List<InboxMessage> inboxMessages) {
    if (_knownEventTypeNames is null || _knownEventTypeNames.Count == 0) {
      return;
    }
    var beforeCount = inboxMessages.Count;
    inboxMessages.RemoveAll(msg => msg.IsEvent
      && !_knownEventTypeNames.Contains(EventTypeMatchingHelper.NormalizeTypeName(msg.MessageType)));
    var filtered = beforeCount - inboxMessages.Count;
    if (filtered > 0) {
      _metrics?.InboxMessagesDeduplicated.Add(filtered);
    }
  }

  private static Activity? _startInboxActivity(IMessageEnvelope envelope, string messageType) {
    var traceParent = envelope.Hops?
      .Where(h => h.Type == HopType.Current)
      .Select(h => h.TraceParent)
      .LastOrDefault(tp => tp is not null);

    if (traceParent is null || !ActivityContext.TryParse(traceParent, null, out var parentContext)) {
      return null;
    }

    var activity = WhizbangActivitySource.Transport.StartActivity(
      $"Inbox {messageType}", ActivityKind.Consumer, parentContext);
    activity?.SetTag("messaging.message_id", envelope.MessageId.ToString());
    activity?.SetTag("messaging.operation", "receive");
    activity?.SetTag("whizbang.hop_count", envelope.Hops?.Count ?? 0);
    return activity;
  }

  internal static async Task InvokePostLifecycleForEventAsync(
    InboxWork work, IMessageEnvelope typedEnvelope, IReceptorInvoker receptorInvoker,
    LifecycleExecutionContext lifecycleContext, IServiceProvider scopedProvider,
    CancellationToken cancellationToken, Action<Task>? trackDetachedTask = null) {
    var coordinator = scopedProvider.GetService<ILifecycleCoordinator>();
    if (coordinator is not null) {
      var eventId = work.Envelope.MessageId.Value;
      var tracking = coordinator.BeginTracking(
        eventId, typedEnvelope, LifecycleStage.PostAllPerspectivesDetached, MessageSource.Inbox);
      await tracking.AdvanceToAsync(LifecycleStage.PostAllPerspectivesDetached, scopedProvider, cancellationToken);
      await tracking.AdvanceToAsync(LifecycleStage.PostAllPerspectivesInline, scopedProvider, cancellationToken);
      await tracking.AdvanceToAsync(LifecycleStage.PostLifecycleDetached, scopedProvider, cancellationToken);
      await tracking.AdvanceToAsync(LifecycleStage.PostLifecycleInline, scopedProvider, cancellationToken);
      coordinator.AbandonTracking(eventId);
    } else {
      // Detached stages: fire-and-forget with own DI scope
      var scopeFactory = scopedProvider.GetRequiredService<IServiceScopeFactory>();
      var detached1 = _fireDetachedStageStaticAsync(scopeFactory, typedEnvelope, LifecycleStage.PostAllPerspectivesDetached, lifecycleContext);
      trackDetachedTask?.Invoke(detached1);

      lifecycleContext = lifecycleContext with { CurrentStage = LifecycleStage.PostAllPerspectivesInline };
      await receptorInvoker.InvokeAsync(typedEnvelope, LifecycleStage.PostAllPerspectivesInline, lifecycleContext, cancellationToken);
      await _invokeImmediateDetachedAsync(receptorInvoker, typedEnvelope, lifecycleContext, cancellationToken);

      var detached2 = _fireDetachedStageStaticAsync(scopeFactory, typedEnvelope, LifecycleStage.PostLifecycleDetached, lifecycleContext);
      trackDetachedTask?.Invoke(detached2);

      lifecycleContext = lifecycleContext with { CurrentStage = LifecycleStage.PostLifecycleInline };
      await receptorInvoker.InvokeAsync(typedEnvelope, LifecycleStage.PostLifecycleInline, lifecycleContext, cancellationToken);
      await _invokeImmediateDetachedAsync(receptorInvoker, typedEnvelope, lifecycleContext, cancellationToken);
    }
  }

  private static async Task _invokeImmediateDetachedAsync(IReceptorInvoker receptorInvoker, IMessageEnvelope typedEnvelope, LifecycleExecutionContext lifecycleContext, CancellationToken cancellationToken) {
    await receptorInvoker.InvokeAsync(typedEnvelope, LifecycleStage.ImmediateDetached,
      lifecycleContext with { CurrentStage = LifecycleStage.ImmediateDetached }, cancellationToken);
  }

  /// <summary>
  /// Waits for all in-flight detached tasks to complete.
  /// Used for graceful shutdown and testing.
  /// </summary>
  internal async ValueTask DrainDetachedAsync() {
    await Task.WhenAll(_detachedTasks).ConfigureAwait(false);
  }

  /// <summary>
  /// Static fire-and-forget for Detached stages in static methods (fallback path).
  /// Returns the Task for tracking/draining in tests.
  /// </summary>
  private static Task _fireDetachedStageStaticAsync(
      IServiceScopeFactory scopeFactory, IMessageEnvelope envelope,
      LifecycleStage stage, LifecycleExecutionContext context) {
    return Task.Run(async () => {
      try {
        await using var scope = scopeFactory.CreateAsyncScope();
        await SecurityContextHelper.EstablishFullContextAsync(envelope, scope.ServiceProvider, default);
        var invoker = scope.ServiceProvider.GetService<IReceptorInvoker>();
        if (invoker is null) {
          return;
        }
        var ctx = context with { CurrentStage = stage };
        await invoker.InvokeAsync(envelope, stage, ctx, default);
        await invoker.InvokeAsync(envelope, LifecycleStage.ImmediateDetached,
          ctx with { CurrentStage = LifecycleStage.ImmediateDetached }, default);
      } catch (OperationCanceledException) {
        // Graceful shutdown
      } catch (Exception ex) {
        // Errors normally surface via receptor telemetry, but a throw BEFORE telemetry (DI scope /
        // security-context failure) would fail the detached stage invisibly. Resolve a logger from a
        // fresh scope (the work scope is already disposed) and record it via LoggerMessage.
        using var logScope = scopeFactory.CreateScope();
        ILogger detachedLogger = logScope.ServiceProvider.GetService<ILogger<TransportConsumerWorker>>()
          ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<TransportConsumerWorker>.Instance;
        LogDetachedStageError(detachedLogger, ex, stage, envelope.MessageId.Value);
      }
    });
  }

  [LoggerMessage(
    Level = LogLevel.Error,
    Message = "Detached lifecycle stage {Stage} failed for message {MessageId}"
  )]
  private static partial void LogDetachedStageError(ILogger logger, Exception ex, LifecycleStage stage, Guid messageId);

  /// <summary>
  /// Creates InboxMessage for work coordinator pattern.
  /// Handles envelopes from transport which may be strongly-typed or JsonElement-typed.
  /// </summary>
  private InboxMessage _serializeToNewInboxMessage(
    IMessageEnvelope envelope,
    string? envelopeTypeFromTransport,
    IServiceProvider scopeServiceProvider
  ) {
    if (string.IsNullOrWhiteSpace(envelopeTypeFromTransport)) {
      throw new InvalidOperationException(
        $"EnvelopeType is required from transport but was null/empty. MessageId: {envelope.MessageId}");
    }

    // Extract message type from envelope type string
    var messageTypeName = _extractMessageTypeFromEnvelopeType(envelopeTypeFromTransport);

    // Get payload to check its type
    var payload = envelope.Payload;
    var payloadType = payload?.GetType() ?? typeof(object);

    // Check if envelope/payload is already in JsonElement form
    IMessageEnvelope<JsonElement> jsonEnvelope;
    if (envelope is IMessageEnvelope<JsonElement> alreadyJsonEnvelope) {
      jsonEnvelope = alreadyJsonEnvelope;
    } else if (payloadType == typeof(JsonElement)) {
      throw new InvalidOperationException(
        $"Envelope has JsonElement payload but envelope type is {envelope.GetType().Name}. MessageId: {envelope.MessageId}");
    } else {
      // Strongly-typed envelope - serialize it
      var serializer = scopeServiceProvider.GetService<IEnvelopeSerializer>()
        ?? throw new InvalidOperationException("IEnvelopeSerializer is required but not registered");

      // Call generic SerializeEnvelope method via reflection
      var genericMethod = typeof(IEnvelopeSerializer).GetMethod(nameof(IEnvelopeSerializer.SerializeEnvelope));
      var boundMethod = genericMethod!.MakeGenericMethod(payloadType);
      var serialized = (SerializedEnvelope)boundMethod.Invoke(serializer, [envelope])!;
      jsonEnvelope = serialized.JsonEnvelope;
    }

    // Determine if message is an event using IEventTypeProvider
    // This is more reliable than "payload is IEvent" when payload is JsonElement
    var isEvent = false;
    var eventTypeProvider = scopeServiceProvider.GetService<IEventTypeProvider>();
    if (eventTypeProvider != null) {
      var eventTypes = eventTypeProvider.GetEventTypes();
      isEvent = EventTypeMatchingHelper.IsEventType(messageTypeName, eventTypes);
    } else {
      // Fallback to runtime check if provider not available
      isEvent = payload is IEvent;
    }

    // Extract simple type name
    var simpleTypeName = TypeNameFormatter.GetSimpleName(messageTypeName);
    var handlerName = simpleTypeName + "Handler";

    var streamId = _extractStreamId(envelope);

    // Guard: fail-fast if StreamId is Guid.Empty for events
    if (isEvent) {
      StreamIdGuard.ThrowIfEmpty(streamId, envelope.MessageId.Value, "TransportConsumer.Inbox", messageTypeName);
    }

    // Name-first flag derivation: transport payloads are typically JsonElement here, where
    // `payload is ICollectiveEvent`-style checks are blind — the compile-time catalog stamp
    // (looked up by the wire type name) is what keeps Collective/Composite/Ephemeral/Compacted
    // flags intact across a service boundary. Typed checks remain the fallback for types the
    // local catalog does not know.
    var flags = Whizbang.Core.Messaging.EventFlagsDeriver.Derive(
      payload, messageTypeName, _eventMarkerResolver, _ephemeralModeResolver);
    // Diagnostic: an EVENT whose wire type name is absent from the catalog union means its flags
    // (and TTL) cannot be derived on this service — warn once per type so the exact name that
    // missed is visible in logs instead of silently storing flags=0.
    if (isEvent && _eventMarkerResolver is not null
        && Whizbang.Core.Messaging.EventFlagsDeriver.ToClrTypeName(messageTypeName) is { } diagClrName
        && _eventMarkerResolver.Resolve(diagClrName) is null
        && _warnedFlagMisses.Count < 100 && _warnedFlagMisses.TryAdd(diagClrName, 0)) {
      _logger.LogWarning(
        "Receive-path flag derivation MISS: event type {ClrTypeName} is not in the message-type catalog union — flags/TTL fall back to typed checks (blind for JsonElement payloads).",
        diagClrName);
    }
    return new InboxMessage {
      MessageId = envelope.MessageId.Value,
      HandlerName = handlerName,
      Envelope = jsonEnvelope,
      EnvelopeType = envelopeTypeFromTransport,
      StreamId = streamId,
      IsEvent = isEvent,
      Flags = flags,
      Scope = envelope.GetCurrentScope()?.Scope,
      Metadata = new EnvelopeMetadata {
        MessageId = envelope.MessageId,
        Hops = envelope.Hops?.ToList() ?? [],
        DispatchContext = envelope.DispatchContext,
        EphemeralTtlSeconds = Whizbang.Core.Messaging.EphemeralTtlDeriver.Derive(payload, messageTypeName, _ephemeralModeResolver)
      },
      MessageType = messageTypeName,
      // Slice 26.6: propagate source identity from envelope → wh_inbox columns.
      // Producer side populates these on publish (slice 26.6b); receive-side records
      // exactly what the source claimed. When envelope hasn't been populated (in-process
      // dispatch, legacy envelope before slice 26.5), defaults to Guid.Empty + 0; the
      // SQL trigger then COALESCEs to local wh_service_config.service_id.
      SourceServiceId = envelope.SourceServiceId,
      SourceCommitSequence = envelope.SourceCommitSequence,
    };
  }

  /// <summary>
  /// Extracts message type from envelope type name.
  /// </summary>
  private static string _extractMessageTypeFromEnvelopeType(string envelopeTypeName) {
    var startIndex = envelopeTypeName.IndexOf("[[", StringComparison.Ordinal);
    var endIndex = envelopeTypeName.IndexOf("]]", StringComparison.Ordinal);

    if (startIndex == -1 || endIndex == -1 || startIndex >= endIndex) {
      throw new InvalidOperationException($"Invalid envelope type name format: '{envelopeTypeName}'");
    }

    var messageTypeName = envelopeTypeName.Substring(startIndex + 2, endIndex - startIndex - 2);

    if (string.IsNullOrWhiteSpace(messageTypeName)) {
      throw new InvalidOperationException($"Failed to extract message type from envelope type: '{envelopeTypeName}'");
    }

    return messageTypeName;
  }

  /// <summary>
  /// Extracts stream_id from envelope for stream-based ordering.
  /// Uses [StreamId] attribute value stored in metadata as "AggregateId" for backward compatibility.
  /// </summary>
  private static Guid _extractStreamId(IMessageEnvelope envelope) {
    // Note: Metadata key is "AggregateId" for backward compatibility with existing envelopes
    // Defensive: Handle null Hops gracefully
    var firstHop = envelope.Hops?.FirstOrDefault();
    if (firstHop?.Metadata != null && firstHop.Metadata.TryGetValue("AggregateId", out var streamIdElem) &&
        streamIdElem.ValueKind == JsonValueKind.String) {
      var streamIdStr = streamIdElem.GetString();
      if (streamIdStr != null && Guid.TryParse(streamIdStr, out var parsedStreamId)) {
        return parsedStreamId;
      }
    }

    return envelope.MessageId.Value;
  }

  /// <summary>
  /// Pauses all active subscriptions.
  /// Messages will not be processed until resumed.
  /// </summary>
  public async Task PauseAllSubscriptionsAsync() {
    _logger.LogInformation("Pausing all subscriptions");

    // S3267: Loop contains await — LINQ doesn't support async lambdas
#pragma warning disable S3267
    foreach (var state in _states.Values) {
      if (state.Subscription != null) {
        await state.Subscription.PauseAsync();
      }
    }
#pragma warning restore S3267

    _logger.LogInformation("All subscriptions paused");
  }

  /// <summary>
  /// Resumes all paused subscriptions.
  /// Message processing will continue.
  /// </summary>
  public async Task ResumeAllSubscriptionsAsync() {
    _logger.LogInformation("Resuming all subscriptions");

    // S3267: Loop contains await — LINQ doesn't support async lambdas
#pragma warning disable S3267
    foreach (var state in _states.Values) {
      if (state.Subscription != null) {
        await state.Subscription.ResumeAsync();
      }
    }
#pragma warning restore S3267

    _logger.LogInformation("All subscriptions resumed");
  }

  /// <summary>
  /// Stops the worker and disposes all subscriptions.
  /// </summary>
  /// <param name="cancellationToken">Cancellation token</param>
  public override async Task StopAsync(CancellationToken cancellationToken) {
    _logger.LogInformation("Stopping TransportConsumerWorker");

    // Idempotent: host teardown calls StopAsync through more than one path (Host.StopAsync and
    // host disposal); the exchange makes the second call a no-op instead of cancelling an
    // already-disposed linked CTS. Dispose AFTER base.StopAsync so the base's stopping-token
    // cancellation never races the linked source's disposal.
    var linkedCts = Interlocked.Exchange(ref _linkedCts, null);
    if (linkedCts is not null) {
      await linkedCts.CancelAsync();
    }

    // Dispose all subscriptions
    foreach (var state in _states.Values) {
      state.Subscription?.Dispose();
    }

    _states.Clear();

    _logger.LogInformation("TransportConsumerWorker stopped");

    await base.StopAsync(cancellationToken);
    linkedCts?.Dispose();
  }

  /// <summary>
  /// Populates DeliveredAt timestamp properties on the message payload using JSON manipulation.
  /// AOT-safe: uses JsonNode, no reflection or Type.GetType().
  /// </summary>
  private static void _populateDeliveredAtTimestamp(IMessageEnvelope envelope, string? envelopeType) {
    if (envelopeType is null || envelope is not MessageEnvelope<JsonElement> concreteEnvelope) {
      return;
    }

    var messageTypeName = _extractMessageTypeFromEnvelopeType(envelopeType);
    concreteEnvelope.Payload = JsonAutoPopulateHelper.PopulateTimestampByName(
        concreteEnvelope.Payload,
        messageTypeName,
        TimestampKind.DeliveredAt,
        DateTimeOffset.UtcNow);
  }

  /// <summary>Logs that an owned event was discarded (owned events arriving from transport are always echo).</summary>
  /// <docs>docs/transport-routing-architecture.md#transport-echo-suppression</docs>
  /// <tests>tests/Whizbang.Core.Tests/Workers/TransportConsumerWorkerOwnedEventDiscardTests.cs</tests>
  [LoggerMessage(
    Level = LogLevel.Debug,
    Message = "Owned event echo discarded: {MessageType} (owned events never arrive from external services)"
  )]
  private static partial void LogOwnedEventEchoDiscarded(ILogger logger, string messageType);

  /// <summary>Logs that a self-echo command was discarded (owned command from this service arriving back via transport).</summary>
  /// <docs>docs/transport-routing-architecture.md#transport-echo-suppression</docs>
  /// <tests>tests/Whizbang.Core.Tests/Workers/TransportConsumerWorkerOwnedEventDiscardTests.cs</tests>
  [LoggerMessage(
    Level = LogLevel.Debug,
    Message = "Self-echo discarded: {MessageType} from {ServiceName}"
  )]
  private static partial void LogSelfEchoDiscarded(ILogger logger, string messageType, string serviceName);

  /// <summary>Logs that a directed message targeted at a different service was discarded at the receive seam.</summary>
  /// <docs>fundamentals/messaging/directed-messages</docs>
  /// <tests>tests/Whizbang.Core.Tests/Workers/TransportConsumerWorkerDirectedTargetTests.cs</tests>
  [LoggerMessage(
    Level = LogLevel.Debug,
    Message = "Directed message discarded: {MessageType} targeted at {Target} — this service is {ServiceName}"
  )]
  private static partial void LogForeignTargetDiscarded(ILogger logger, string messageType, string target, string serviceName);

  /// <summary>Logs that a detached lifecycle stage failed during fire-and-forget execution.</summary>
  [LoggerMessage(
    Level = LogLevel.Error,
    Message = "Detached lifecycle stage {Stage} failed for message {MessageId}"
  )]
  private static partial void LogDetachedStageError(ILogger logger, Exception ex, LifecycleStage stage, Guid? messageId);

  /// <summary>
  /// Checks if the message originated from this service (self-echo).
  /// The last hop is the most recent — it identifies the service that dispatched the message.
  /// </summary>
  private bool _isSelfEcho(IMessageEnvelope envelope) {
    if (_serviceName is null || envelope.Hops.Count == 0) {
      return false;
    }
    var lastHop = envelope.Hops[^1];
    return string.Equals(lastHop.ServiceInstance.ServiceName, _serviceName, StringComparison.OrdinalIgnoreCase);
  }

  /// <summary>
  /// Checks if the given namespace is owned by this service.
  /// Supports hierarchical matching: "A.B" owns "A.B.C.D".
  /// </summary>
  private bool _isOwnedNamespace(string? ns) {
    if (string.IsNullOrEmpty(ns) || _ownedDomains.Count == 0) {
      return false;
    }
    if (_ownedDomains.Contains(ns)) {
      return true;
    }
    foreach (var owned in _ownedDomains) {
      var prefix = owned.EndsWith('.') ? owned : owned + ".";
      if (ns.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) {
        return true;
      }
    }
    return false;
  }

  /// <summary>
  /// Lazily initializes <see cref="_knownEventTypeNames"/> from <see cref="IEventTypeProvider"/>.
  /// Thread-safe: first caller wins; subsequent calls are no-ops.
  /// </summary>
  private void _ensureKnownEventTypesInitialized(IServiceProvider serviceProvider) {
    if (_knownEventTypeNames is not null) {
      return;
    }
    var eventTypeProvider = serviceProvider.GetService<IEventTypeProvider>();
    if (eventTypeProvider is not null) {
      var eventTypes = eventTypeProvider.GetEventTypes();
      _knownEventTypeNames = new HashSet<string>(
        eventTypes.Select(t => EventTypeMatchingHelper.NormalizeTypeName(
          TypeNameFormatter.Format(t))),
        StringComparer.Ordinal);
    } else {
      _knownEventTypeNames = [];  // Empty = no filtering (allow all through)
    }
  }

  /// <summary>
  /// Checks if an envelope type string represents a known event type.
  /// Uses <see cref="_knownEventTypeNames"/> (must be initialized first via
  /// <see cref="_ensureKnownEventTypesInitialized"/>).
  /// Falls back to allowing through if the set is empty (no provider registered).
  /// </summary>
  /// <docs>docs/transport-routing-architecture.md#transport-echo-suppression</docs>
  /// <tests>tests/Whizbang.Core.Tests/Workers/TransportConsumerWorkerOwnedEventDiscardTests.cs</tests>
  private bool _isKnownEventType(string envelopeType) {
    if (_knownEventTypeNames is null || _knownEventTypeNames.Count == 0) {
      return false;  // No provider → can't determine, treat as non-event (safe: falls through to hop check)
    }
    var normalized = EventTypeMatchingHelper.NormalizeTypeName(envelopeType);
    return _knownEventTypeNames.Contains(normalized);
  }

  // Namespace extraction moved to TypeNameFormatter.GetPayloadNamespace (testable utility)
}
