using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Whizbang.Core.Attributes;
using Whizbang.Core.AutoPopulate;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Security;
using Whizbang.Core.Tracing;
using Whizbang.Core.Transports;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Workers;

/// <summary>
/// Background worker that uses IWorkCoordinator for lease-based coordination.
/// Uses shared channel for async message publishing with concurrent processing.
/// The channel is shared with ScopedWorkCoordinatorStrategy to enable immediate
/// processing of work returned from process_work_batch.
/// Performs all operations in a single atomic call:
/// - Registers/updates instance with heartbeat
/// - Cleans up stale instances
/// - Marks completed/failed messages
/// - Claims and processes orphaned work (outbox and inbox)
/// </summary>
/// <docs>operations/workers/work-coordinator-publisher-worker</docs>
/// <tests>tests/Whizbang.Core.Tests/Workers/WorkCoordinatorPublisherWorkerMetricsTests.cs:TransportNotReady_SingleBuffer_LogsInformationAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Workers/WorkCoordinatorPublisherWorkerMetricsTests.cs:TransportNotReady_ConsecutiveBuffers_TracksCountAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Workers/WorkCoordinatorPublisherWorkerMetricsTests.cs:TransportNotReady_ExceedsThreshold_LogsWarningAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Workers/WorkCoordinatorPublisherWorkerMetricsTests.cs:TransportBecomesReady_ResetsConsecutiveCounterAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Workers/WorkCoordinatorPublisherWorkerMetricsTests.cs:BufferedMessages_TracksCountAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Workers/WorkCoordinatorPublisherWorkerMetricsTests.cs:LeaseRenewals_TrackedInMetricsAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Workers/WorkCoordinatorPublisherWorkerRaceConditionTests.cs:RaceCondition_MultipleInstances_NoDuplicatePublishingAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Workers/WorkCoordinatorPublisherWorkerRaceConditionTests.cs:RaceCondition_ImmediateProcessing_WithRealisticDelaysAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Workers/WorkCoordinatorPublisherWorkerRaceConditionTests.cs:RaceCondition_DatabaseSlowness_DoesNotBlockPublishingAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Workers/WorkCoordinatorPublisherWorkerRaceConditionTests.cs:RaceCondition_TransportFailures_RetriesSuccessfullyAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Workers/WorkCoordinatorPublisherWorkerRaceConditionTests.cs:RaceCondition_DatabaseNotReady_DelaysProcessingAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Workers/WorkCoordinatorPublisherWorkerDatabaseReadinessTests.cs:DatabaseNotReady_ProcessWorkBatchAsync_SkippedAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Workers/WorkCoordinatorPublisherWorkerDatabaseReadinessTests.cs:DatabaseReady_ProcessWorkBatchAsync_CalledAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Workers/WorkCoordinatorPublisherWorkerDatabaseReadinessTests.cs:DatabaseNotReady_ConsecutiveChecks_TracksCountAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Workers/WorkCoordinatorPublisherWorkerDatabaseReadinessTests.cs:DatabaseNotReady_ExceedsThreshold_LogsWarningAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Workers/WorkCoordinatorPublisherWorkerDatabaseReadinessTests.cs:DatabaseBecomesReady_ResetsConsecutiveCounterAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Workers/WorkCoordinatorPublisherWorkerDatabaseReadinessTests.cs:DatabaseNotReady_MessagesBuffered_UntilReadyAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Workers/WorkCoordinatorPublisherWorkerStartupTests.cs:ImmediateProcessing_OnStartup_ProcessesWorkBeforeFirstPollAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Workers/WorkCoordinatorPublisherWorkerStartupTests.cs:ImmediateProcessing_DatabaseNotReady_SkipsInitialProcessingAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Workers/WorkCoordinatorPublisherWorkerStartupTests.cs:ImmediateProcessing_ExceptionDuringInitial_ContinuesStartupAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Workers/WorkCoordinatorPublisherWorkerStartupTests.cs:ImmediateProcessing_NoWork_DoesNotLogAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Workers/WorkCoordinatorPublisherWorkerStartupTests.cs:ImmediateProcessing_WithWork_LogsMessageBatchAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/WorkCoordinatorPublisherWorkerIntegrationTests.cs:WorkerProcessesOutboxMessages_EndToEndAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/WorkCoordinatorPublisherWorkerIntegrationTests.cs:Worker_ProcessesMultipleMessages_InOrderAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/WorkCoordinatorPublisherWorkerIntegrationTests.cs:ProcessWorkBatch_ProcessesReturnedWorkFromCompletionsAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/WorkCoordinatorPublisherWorkerIntegrationTests.cs:ProcessWorkBatch_MultipleIterationsProcessAllWorkAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/WorkCoordinatorPublisherWorkerIntegrationTests.cs:ProcessWorkBatch_LoopTerminatesWhenNoWorkAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Workers/WorkCoordinatorPublisherWorkerSecurityContextTests.cs:PublisherLoop_EstablishesSecurityContext_BeforeInvokingReceptorsAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Workers/WorkCoordinatorPublisherWorkerSecurityContextTests.cs:PublisherLoop_SetsMessageContext_WithUserIdAndTenantIdAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Workers/WorkCoordinatorPublisherWorkerSecurityContextTests.cs:PublisherLoop_WithNoSecurityInEnvelope_DoesNotThrowAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Workers/WorkCoordinatorPublisherWorkerSecurityContextTests.cs:PublisherLoop_WithNoSecurity_StillSetsMessageContextAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Workers/WorkCoordinatorPublisherWorkerChannelTests.cs:TransportNotReady_MessageRequeuedToChannelAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Workers/WorkCoordinatorPublisherWorkerChannelTests.cs:TransportException_MessageRequeuedToChannelAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/WorkCoordinatorPublisherWorkerInboxIntegrationTests.cs:OrphanedInboxFailure_SentAsOutboxFailures_NeverReachesInboxTableAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/WorkCoordinatorPublisherWorkerInboxIntegrationTests.cs:OrphanedInboxFailure_SentAsInboxFailures_UpdatesInboxTableAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/WorkCoordinatorPublisherWorkerInboxIntegrationTests.cs:OrphanedInboxCompletion_SentAsInboxCompletions_UpdatesInboxTableAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/WorkCoordinatorPublisherWorkerInboxIntegrationTests.cs:InboxPurge_MessagesExceedingMaxAttempts_RemovedFromInboxAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/WorkCoordinatorPublisherWorkerInboxIntegrationTests.cs:InboxPurge_Disabled_MessagesRetainedRegardlessOfAttemptsAsync</tests>
/// <remarks>
/// <para>
/// <strong>IReceptorInvoker is scoped:</strong> The receptor invoker is resolved from a per-work-item scope
/// rather than being injected as a constructor parameter. This follows industry patterns (MediatR, MassTransit)
/// where handlers are scoped and resolved from the message processing scope.
/// </para>
/// </remarks>
public partial class WorkCoordinatorPublisherWorker(
  IServiceInstanceProvider instanceProvider,
  IServiceScopeFactory scopeFactory,
  IMessagePublishStrategy publishStrategy,
  IWorkChannelWriter workChannelWriter,
  IOptions<WorkCoordinatorPublisherOptions> options,
  IDatabaseReadinessCheck? databaseReadinessCheck = null,
  ILifecycleMessageDeserializer? lifecycleMessageDeserializer = null,
  IOptionsMonitor<TracingOptions>? tracingOptions = null,
  TransportMetrics? transportMetrics = null,
  WorkCoordinatorMetrics? workCoordinatorMetrics = null,
  ILogger<WorkCoordinatorPublisherWorker>? logger = null
) : BackgroundService {
  private readonly IServiceInstanceProvider _instanceProvider = instanceProvider ?? throw new ArgumentNullException(nameof(instanceProvider));
  private readonly IServiceScopeFactory _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
  private readonly IMessagePublishStrategy _publishStrategy = publishStrategy ?? throw new ArgumentNullException(nameof(publishStrategy));
  private readonly IWorkChannelWriter _workChannelWriter = workChannelWriter ?? throw new ArgumentNullException(nameof(workChannelWriter));
  private readonly IDatabaseReadinessCheck _databaseReadinessCheck = databaseReadinessCheck ?? new DefaultDatabaseReadinessCheck();
  private readonly ILifecycleMessageDeserializer? _lifecycleMessageDeserializer = lifecycleMessageDeserializer;
  private readonly IOptionsMonitor<TracingOptions>? _tracingOptions = tracingOptions;
  private readonly TransportMetrics? _transportMetrics = transportMetrics;
  private readonly WorkCoordinatorMetrics? _workCoordinatorMetrics = workCoordinatorMetrics;
  private readonly ILogger<WorkCoordinatorPublisherWorker> _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<WorkCoordinatorPublisherWorker>.Instance;
  private readonly WorkCoordinatorPublisherOptions _options = (options ?? throw new ArgumentNullException(nameof(options))).Value;

  // Completion trackers for acknowledgement-before-clear pattern
  // Initialize with retry configuration from options (default: 1s → 2s → 4s → 8s → 16s → 32s → 60s max)
  private readonly CompletionTracker<MessageCompletion> _completions = new(
    baseTimeout: TimeSpan.FromSeconds((options ?? throw new ArgumentNullException(nameof(options))).Value.RetryOptions.RetryTimeoutSeconds),
    backoffMultiplier: (options ?? throw new ArgumentNullException(nameof(options))).Value.RetryOptions.EnableExponentialBackoff
      ? (options ?? throw new ArgumentNullException(nameof(options))).Value.RetryOptions.BackoffMultiplier
      : 1.0,
    maxTimeout: TimeSpan.FromSeconds((options ?? throw new ArgumentNullException(nameof(options))).Value.RetryOptions.MaxBackoffSeconds)
  );
  private readonly CompletionTracker<MessageFailure> _failures = new(
    baseTimeout: TimeSpan.FromSeconds((options ?? throw new ArgumentNullException(nameof(options))).Value.RetryOptions.RetryTimeoutSeconds),
    backoffMultiplier: (options ?? throw new ArgumentNullException(nameof(options))).Value.RetryOptions.EnableExponentialBackoff
      ? (options ?? throw new ArgumentNullException(nameof(options))).Value.RetryOptions.BackoffMultiplier
      : 1.0,
    maxTimeout: TimeSpan.FromSeconds((options ?? throw new ArgumentNullException(nameof(options))).Value.RetryOptions.MaxBackoffSeconds)
  );
  private readonly CompletionTracker<Guid> _leaseRenewals = new(
    baseTimeout: TimeSpan.FromSeconds((options ?? throw new ArgumentNullException(nameof(options))).Value.RetryOptions.RetryTimeoutSeconds),
    backoffMultiplier: (options ?? throw new ArgumentNullException(nameof(options))).Value.RetryOptions.EnableExponentialBackoff
      ? (options ?? throw new ArgumentNullException(nameof(options))).Value.RetryOptions.BackoffMultiplier
      : 1.0,
    maxTimeout: TimeSpan.FromSeconds((options ?? throw new ArgumentNullException(nameof(options))).Value.RetryOptions.MaxBackoffSeconds)
  );

  // Inbox completion trackers (separate from outbox to route to correct SQL parameters)
  private readonly CompletionTracker<MessageCompletion> _inboxCompletions = new(
    baseTimeout: TimeSpan.FromSeconds((options ?? throw new ArgumentNullException(nameof(options))).Value.RetryOptions.RetryTimeoutSeconds),
    backoffMultiplier: (options ?? throw new ArgumentNullException(nameof(options))).Value.RetryOptions.EnableExponentialBackoff
      ? (options ?? throw new ArgumentNullException(nameof(options))).Value.RetryOptions.BackoffMultiplier
      : 1.0,
    maxTimeout: TimeSpan.FromSeconds((options ?? throw new ArgumentNullException(nameof(options))).Value.RetryOptions.MaxBackoffSeconds)
  );
  private readonly CompletionTracker<MessageFailure> _inboxFailures = new(
    baseTimeout: TimeSpan.FromSeconds((options ?? throw new ArgumentNullException(nameof(options))).Value.RetryOptions.RetryTimeoutSeconds),
    backoffMultiplier: (options ?? throw new ArgumentNullException(nameof(options))).Value.RetryOptions.EnableExponentialBackoff
      ? (options ?? throw new ArgumentNullException(nameof(options))).Value.RetryOptions.BackoffMultiplier
      : 1.0,
    maxTimeout: TimeSpan.FromSeconds((options ?? throw new ArgumentNullException(nameof(options))).Value.RetryOptions.MaxBackoffSeconds)
  );
  private readonly CompletionTracker<Guid> _inboxLeaseRenewals = new(
    baseTimeout: TimeSpan.FromSeconds((options ?? throw new ArgumentNullException(nameof(options))).Value.RetryOptions.RetryTimeoutSeconds),
    backoffMultiplier: (options ?? throw new ArgumentNullException(nameof(options))).Value.RetryOptions.EnableExponentialBackoff
      ? (options ?? throw new ArgumentNullException(nameof(options))).Value.RetryOptions.BackoffMultiplier
      : 1.0,
    maxTimeout: TimeSpan.FromSeconds((options ?? throw new ArgumentNullException(nameof(options))).Value.RetryOptions.MaxBackoffSeconds)
  );

  // Metrics tracking
  private int _consecutiveNotReadyChecks;
  private int _consecutiveDatabaseNotReadyChecks;
  private long _totalLeaseRenewals;
  private long _totalBufferedMessages;

  // Work processing state tracking
  private int _consecutiveEmptyPolls;
  private bool _isIdle = true;  // Start in idle state

  /// <summary>
  /// Gets the number of consecutive times the transport was not ready.
  /// Resets to 0 when transport becomes ready.
  /// </summary>
  /// <tests>tests/Whizbang.Core.Tests/Workers/WorkCoordinatorPublisherWorkerMetricsTests.cs:TransportNotReady_ConsecutiveBuffers_TracksCountAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Workers/WorkCoordinatorPublisherWorkerMetricsTests.cs:TransportBecomesReady_ResetsConsecutiveCounterAsync</tests>
  public int ConsecutiveNotReadyChecks => _consecutiveNotReadyChecks;

  /// <summary>
  /// Gets the number of consecutive times the database was not ready.
  /// Resets to 0 when database becomes ready.
  /// </summary>
  /// <tests>tests/Whizbang.Core.Tests/Workers/WorkCoordinatorPublisherWorkerDatabaseReadinessTests.cs:DatabaseNotReady_ConsecutiveChecks_TracksCountAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Workers/WorkCoordinatorPublisherWorkerDatabaseReadinessTests.cs:DatabaseBecomesReady_ResetsConsecutiveCounterAsync</tests>
  public int ConsecutiveDatabaseNotReadyChecks => _consecutiveDatabaseNotReadyChecks;

  /// <summary>
  /// Gets the total count of messages buffered due to transport not being ready.
  /// Accumulates across all batches.
  /// </summary>
  /// <tests>tests/Whizbang.Core.Tests/Workers/WorkCoordinatorPublisherWorkerMetricsTests.cs:BufferedMessages_TracksCountAsync</tests>
  public long BufferedMessageCount => _totalBufferedMessages;

  /// <summary>
  /// Gets the total number of lease renewals performed since worker started.
  /// Accumulates across all batches.
  /// </summary>
  /// <tests>tests/Whizbang.Core.Tests/Workers/WorkCoordinatorPublisherWorkerMetricsTests.cs:LeaseRenewals_TrackedInMetricsAsync</tests>
  public long TotalLeaseRenewals => _totalLeaseRenewals;

  /// <summary>
  /// Gets the number of consecutive empty work polls (no outbox or inbox work returned).
  /// Resets to 0 when work is found.
  /// </summary>
  public int ConsecutiveEmptyPolls => _consecutiveEmptyPolls;

  /// <summary>
  /// Gets whether the worker is currently in idle state (no work being processed).
  /// </summary>
  public bool IsIdle => _isIdle;

  /// <summary>
  /// Event fired when work processing starts (idle → active transition).
  /// Fires when work appears after consecutive empty polls.
  /// </summary>
  public event WorkProcessingStartedHandler? OnWorkProcessingStarted;

  /// <summary>
  /// Event fired when work processing becomes idle (active → idle transition).
  /// Fires after N consecutive polls returned no work (configured via IdleThresholdPolls).
  /// Useful for integration tests to wait for event processing completion.
  /// </summary>
  public event WorkProcessingIdleHandler? OnWorkProcessingIdle;

  protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
    LogWorkerStarting(
      _logger,
      _instanceProvider.InstanceId,
      _instanceProvider.ServiceName,
      _instanceProvider.HostName,
      _instanceProvider.ProcessId,
      _options.PollingIntervalMilliseconds
    );

    // Start both loops concurrently
    var coordinatorTask = _coordinatorLoopAsync(stoppingToken);
    var publisherTask = _publisherLoopAsync(stoppingToken);

    // Wait for both to complete
    await Task.WhenAll(coordinatorTask, publisherTask);

    LogWorkerStopping(_logger);
  }

  private async Task _coordinatorLoopAsync(CancellationToken stoppingToken) {
    // Process any pending outbox messages IMMEDIATELY on startup (before first polling delay)
    // This ensures seeded or pre-existing messages are published right away
    try {
      LogCheckingPendingMessages(_logger);
      var isDatabaseReady = await _databaseReadinessCheck.IsReadyAsync(stoppingToken);
      if (isDatabaseReady) {
        await _processWorkBatchAsync(stoppingToken);
        LogInitialWorkBatchComplete(_logger);
      } else {
        LogDatabaseNotReadyOnStartup(_logger);
      }
    } catch (Exception ex) when (ex is not OperationCanceledException and not ObjectDisposedException) {
      LogErrorProcessingInitialWorkBatch(_logger, ex);
    }

    while (!stoppingToken.IsCancellationRequested) {
      try {
        // Check database readiness before attempting work coordinator call
        var isDatabaseReady = await _databaseReadinessCheck.IsReadyAsync(stoppingToken);
        if (!isDatabaseReady) {
          // Database not ready - skip ProcessWorkBatchAsync, keep buffering in memory
          Interlocked.Increment(ref _consecutiveDatabaseNotReadyChecks);

          // Log at Information level (important operational event)
          LogDatabaseNotReady(_logger, _consecutiveDatabaseNotReadyChecks);

          // Warn if database has been continuously unavailable
          if (_consecutiveDatabaseNotReadyChecks > 10) {
            LogDatabaseNotReadyWarning(_logger, _consecutiveDatabaseNotReadyChecks);
          }

          // Wait before retry
          await Task.Delay(_options.PollingIntervalMilliseconds, stoppingToken);
          continue;
        }

        // Database is ready - reset consecutive counter
        Interlocked.Exchange(ref _consecutiveDatabaseNotReadyChecks, 0);

        await _processWorkBatchAsync(stoppingToken);
      } catch (ObjectDisposedException) {
        // Service provider disposed during host shutdown — exit gracefully
        break;
      } catch (Exception ex) when (ex is not OperationCanceledException) {
        LogErrorProcessingWorkBatch(_logger, ex);
      }

      // Wait before next poll (unless cancelled)
      try {
        await Task.Delay(_options.PollingIntervalMilliseconds, stoppingToken);
      } catch (OperationCanceledException) {
        // Graceful shutdown
        break;
      }
    }

    // Signal publisher loop to finish
    _workChannelWriter.Complete();
  }

  private async Task _publisherLoopAsync(CancellationToken stoppingToken) {
    LogPublisherLoopStarted(_logger);
    await foreach (var work in _workChannelWriter.Reader.ReadAllAsync(stoppingToken)) {
      LogPublisherLoopReceivedWork(_logger, work.MessageId, work.Destination);
      try {
        // Check transport readiness before attempting publish
        var readinessWaitSw = Stopwatch.StartNew();
        var isReady = await _publishStrategy.IsReadyAsync(stoppingToken);
        readinessWaitSw.Stop();
        LogTransportReadinessCheck(_logger, isReady);
        if (!isReady) {
          _transportMetrics?.OutboxReadinessWaitDuration.Record(readinessWaitSw.Elapsed.TotalMilliseconds);

          // Transport not ready - renew lease to buffer message
          _leaseRenewals.Add(work.MessageId);
          Interlocked.Increment(ref _consecutiveNotReadyChecks);
          Interlocked.Increment(ref _totalLeaseRenewals);
          Interlocked.Increment(ref _totalBufferedMessages);
          _workCoordinatorMetrics?.PublisherLeaseRenewals.Add(1);
          _workCoordinatorMetrics?.PublisherBufferedMessages.Add(1);

          // Log at Information level (important operational event)
          LogTransportNotReadyBuffering(_logger, work.MessageId, work.Destination);

          // Warn if transport has been continuously unavailable
          if (_consecutiveNotReadyChecks > 10) {
            LogTransportNotReadyWarning(_logger, _consecutiveNotReadyChecks);
          }

          // Re-queue work to channel for retry — without this, the message is consumed
          // from the channel and lost until lease expiry (which never happens because
          // the lease keeps being renewed)
          _workChannelWriter.TryWrite(work);
          continue;
        }

        // Transport is ready - reset consecutive counter
        Interlocked.Exchange(ref _consecutiveNotReadyChecks, 0);

        // Create scope for scoped services (IReceptorInvoker)
        // Following MediatR/MassTransit pattern: handlers are scoped, resolved from message processing scope
        await using var lifecycleScope = _scopeFactory.CreateAsyncScope();
        var receptorInvoker = lifecycleScope.ServiceProvider.GetService<IReceptorInvoker>();

        // Establish security context from envelope before invoking lifecycle receptors.
        // This sets IScopeContextAccessor.Current and IMessageContextAccessor.Current,
        // enabling receptors to inject IMessageContext and access UserId/TenantId.
        // Security context cascades to any commands/events dispatched by these receptors.
        await SecurityContextHelper.EstablishFullContextAsync(
          work.Envelope,
          lifecycleScope.ServiceProvider,
          stoppingToken);

        // Extract trace context from envelope hops FIRST to parent all lifecycle spans
        // This ensures all outbox processing appears as children of the original request trace
        // Defensive: Handle null Hops gracefully (shouldn't happen but be safe)
        var latestHop = work.Envelope.Hops?.LastOrDefault();
        ActivityContext traceContext = default;
        var traceParentValue = latestHop?.TraceParent;
        var parseSucceeded = false;
        if (traceParentValue is not null &&
            ActivityContext.TryParse(traceParentValue, null, out var parsedContext)) {
          traceContext = parsedContext;
          parseSucceeded = true;
        }

        // Check if Lifecycle tracing is enabled via TraceComponents
        var enableLifecycleSpans = _tracingOptions?.CurrentValue.IsEnabled(TraceComponents.Lifecycle) ?? false;

        // PreOutbox lifecycle stages (before publishing to transport)
        // ALL receptors registered at PreOutbox stages fire here, including:
        // - Receptors with [FireAt(PreOutboxAsync/PreOutboxInline)]
        // - DEFAULT receptors (without [FireAt]) - this is where they fire for the distributed send path
        // Only create lifecycle spans when TraceComponents.Lifecycle is enabled
        using (enableLifecycleSpans ? WhizbangActivitySource.Tracing.StartActivity("Lifecycle PreOutboxAsync", ActivityKind.Internal, parentContext: traceContext) : null) {
          if (_lifecycleMessageDeserializer is not null && receptorInvoker is not null) {
            var message = _lifecycleMessageDeserializer.DeserializeFromJsonElement(work.Envelope.Payload, work.MessageType);
            var typedEnvelope = work.Envelope.ReconstructWithPayload(message);
            var lifecycleContext = new LifecycleExecutionContext {
              CurrentStage = LifecycleStage.PreOutboxAsync,
              EventId = null,
              StreamId = null,
              LastProcessedEventId = null,
              MessageSource = MessageSource.Outbox,
              AttemptNumber = work.Attempts
            };
            await receptorInvoker.InvokeAsync(typedEnvelope, LifecycleStage.PreOutboxAsync, lifecycleContext, stoppingToken);

            // ImmediateAsync lifecycle receptors fire at the end of each stage
            await receptorInvoker.InvokeAsync(typedEnvelope, LifecycleStage.ImmediateAsync,
              lifecycleContext with { CurrentStage = LifecycleStage.ImmediateAsync }, stoppingToken);
          }
        }

        using (enableLifecycleSpans ? WhizbangActivitySource.Tracing.StartActivity("Lifecycle PreOutboxInline", ActivityKind.Internal, parentContext: traceContext) : null) {
          if (_lifecycleMessageDeserializer is not null && receptorInvoker is not null) {
            var message = _lifecycleMessageDeserializer.DeserializeFromJsonElement(work.Envelope.Payload, work.MessageType);
            var typedEnvelope = work.Envelope.ReconstructWithPayload(message);
            var lifecycleContext = new LifecycleExecutionContext {
              CurrentStage = LifecycleStage.PreOutboxInline,
              EventId = null,
              StreamId = null,
              LastProcessedEventId = null,
              MessageSource = MessageSource.Outbox,
              AttemptNumber = work.Attempts
            };
            await receptorInvoker.InvokeAsync(typedEnvelope, LifecycleStage.PreOutboxInline, lifecycleContext, stoppingToken);

            // ImmediateAsync lifecycle receptors fire at the end of each stage
            await receptorInvoker.InvokeAsync(typedEnvelope, LifecycleStage.ImmediateAsync,
              lifecycleContext with { CurrentStage = LifecycleStage.ImmediateAsync }, stoppingToken);
          }
        }

        // Populate QueuedAt timestamp on the message payload (JSON-level, AOT-safe)
        _populateQueuedAtTimestamp(work);

        // Publish via strategy
        LogAboutToPublishMessage(_logger, work.MessageId, work.Destination);
        MessagePublishResult result;
        var publishSw = Stopwatch.StartNew();

        using (var activity = WhizbangActivitySource.Tracing.StartActivity(
          "Outbox PublishAsync",
          ActivityKind.Producer,
          parentContext: traceContext)) {
          activity?.SetTag("whizbang.message.id", work.MessageId.ToString());
          activity?.SetTag("whizbang.message.type", work.MessageType);
          activity?.SetTag("whizbang.message.destination", work.Destination ?? "local");
          activity?.SetTag("whizbang.message.attempts", work.Attempts);
          activity?.SetTag("whizbang.trace.context_restored", traceContext != default);
          activity?.SetTag("whizbang.trace.hop_count", work.Envelope.Hops?.Count ?? 0);
          activity?.SetTag("whizbang.trace.traceparent_raw", traceParentValue ?? "(null)");
          activity?.SetTag("whizbang.trace.parse_succeeded", parseSucceeded);

          result = await _publishStrategy.PublishAsync(work, stoppingToken);
          publishSw.Stop();

          activity?.SetTag("whizbang.publish.success", result.Success.ToString());
          activity?.SetTag("whizbang.publish.status", result.CompletedStatus.ToString());
          if (!result.Success && result.Error != null) {
            activity?.SetTag("whizbang.publish.error", result.Error);
            activity?.SetTag("whizbang.publish.failure_reason", result.Reason.ToString());
          }
        }
        _transportMetrics?.OutboxPublishDuration.Record(publishSw.Elapsed.TotalMilliseconds);
        LogPublishResult(_logger, work.MessageId, result.Success, result.CompletedStatus);

        // PostOutbox lifecycle stages (after publishing to transport)
        // ALL receptors registered at PostOutbox stages fire here
        // NOTE: Default receptors do NOT fire here - only explicit [FireAt(PostOutbox*)] receptors
        // Only create lifecycle spans when TraceComponents.Lifecycle is enabled
        using (enableLifecycleSpans ? WhizbangActivitySource.Tracing.StartActivity("Lifecycle PostOutboxAsync", ActivityKind.Internal, parentContext: traceContext) : null) {
          if (_lifecycleMessageDeserializer is not null && receptorInvoker is not null) {
            var message = _lifecycleMessageDeserializer.DeserializeFromJsonElement(work.Envelope.Payload, work.MessageType);
            // Reconstruct envelope with deserialized payload to preserve security context
            var typedEnvelope = work.Envelope.ReconstructWithPayload(message);

            var lifecycleContext = new LifecycleExecutionContext {
              CurrentStage = LifecycleStage.PostOutboxAsync,
              EventId = null,
              StreamId = null,
              LastProcessedEventId = null,
              MessageSource = MessageSource.Outbox,
              AttemptNumber = work.Attempts
            };

            await receptorInvoker.InvokeAsync(typedEnvelope, LifecycleStage.PostOutboxAsync, lifecycleContext, stoppingToken);

            // ImmediateAsync lifecycle receptors fire at the end of each stage
            await receptorInvoker.InvokeAsync(typedEnvelope, LifecycleStage.ImmediateAsync,
              lifecycleContext with { CurrentStage = LifecycleStage.ImmediateAsync }, stoppingToken);
          }
        }

        using (enableLifecycleSpans ? WhizbangActivitySource.Tracing.StartActivity("Lifecycle PostOutboxInline", ActivityKind.Internal, parentContext: traceContext) : null) {
          if (_lifecycleMessageDeserializer is not null && receptorInvoker is not null) {
            var message = _lifecycleMessageDeserializer.DeserializeFromJsonElement(work.Envelope.Payload, work.MessageType);
            // Reconstruct envelope with deserialized payload to preserve security context
            var typedEnvelope = work.Envelope.ReconstructWithPayload(message);

            var lifecycleContext = new LifecycleExecutionContext {
              CurrentStage = LifecycleStage.PostOutboxInline,
              EventId = null,
              StreamId = null,
              LastProcessedEventId = null,
              MessageSource = MessageSource.Outbox,
              AttemptNumber = work.Attempts
            };

            await receptorInvoker.InvokeAsync(typedEnvelope, LifecycleStage.PostOutboxInline, lifecycleContext, stoppingToken);

            // ImmediateAsync lifecycle receptors fire at the end of each stage
            await receptorInvoker.InvokeAsync(typedEnvelope, LifecycleStage.ImmediateAsync,
              lifecycleContext with { CurrentStage = LifecycleStage.ImmediateAsync }, stoppingToken);
          }
        }

        // Collect results
        if (result.Success) {
          _transportMetrics?.OutboxMessagesPublished.Add(1);
          _completions.Add(new MessageCompletion {
            MessageId = work.MessageId,
            Status = result.CompletedStatus
          });
        } else {
          // For retryable failures, renew lease instead of marking as failed
          // This allows the message to be re-claimed and retried
          if (result.Reason == MessageFailureReason.TransportException) {
            _transportMetrics?.OutboxMessagesFailed.Add(1, new KeyValuePair<string, object?>("failure_reason", "transport_exception"));
            _transportMetrics?.OutboxPublishRetries.Add(1);
            _leaseRenewals.Add(work.MessageId);

            // Re-queue work to channel for retry — without this, the message is consumed
            // from the channel and lost until lease expiry (which never happens because
            // the lease keeps being renewed)
            _workChannelWriter.TryWrite(work);

            LogTransportFailureRenewingLease(_logger, work.MessageId, work.Destination, result.Error ?? "Unknown error");
          } else {
            // Non-retryable failures (serialization, validation, etc.) - mark as failed
            _transportMetrics?.OutboxMessagesFailed.Add(1, new KeyValuePair<string, object?>("failure_reason", result.Reason.ToString()));
            _failures.Add(new MessageFailure {
              MessageId = work.MessageId,
              CompletedStatus = result.CompletedStatus,
              Error = result.Error ?? "Unknown error",
              Reason = result.Reason
            });

            LogFailedToPublishMessage(_logger, work.MessageId, work.Destination, result.Error ?? "Unknown error", result.Reason);
          }
        }
      } catch (ObjectDisposedException) {
        // Service provider disposed during host shutdown — exit gracefully
        break;
      } catch (Exception ex) when (ex is not OperationCanceledException) {
        LogUnexpectedErrorPublishing(_logger, work.MessageId, ex);
        _transportMetrics?.OutboxMessagesFailed.Add(1, new KeyValuePair<string, object?>("failure_reason", "unexpected_exception"));

        _failures.Add(new MessageFailure {
          MessageId = work.MessageId,
          CompletedStatus = work.Status,
          Error = ex.Message,
          Reason = MessageFailureReason.Unknown
        });
      }
    }
  }

  private async Task _processWorkBatchAsync(CancellationToken cancellationToken) {
    // Create a scope to resolve scoped IWorkCoordinator
    using var scope = _scopeFactory.CreateScope();
    var workCoordinator = scope.ServiceProvider.GetRequiredService<IWorkCoordinator>();

    // 1. Get pending items (status = Pending, not yet sent)
    var pendingCompletions = _completions.GetPending();
    var pendingFailures = _failures.GetPending();
    var pendingLeaseRenewals = _leaseRenewals.GetPending();
    var pendingInboxCompletions = _inboxCompletions.GetPending();
    var pendingInboxFailures = _inboxFailures.GetPending();
    var pendingInboxLeaseRenewals = _inboxLeaseRenewals.GetPending();

    // 2. Extract actual completion data for ProcessWorkBatchAsync
    var completionsToSend = pendingCompletions.Select(tc => tc.Completion).ToArray();
    var failuresToSend = pendingFailures.Select(tc => tc.Completion).ToArray();
    var leaseRenewalsToSend = pendingLeaseRenewals.Select(tc => tc.Completion).ToArray();
    var inboxCompletionsToSend = pendingInboxCompletions.Select(tc => tc.Completion).ToArray();
    var inboxFailuresToSend = pendingInboxFailures.Select(tc => tc.Completion).ToArray();
    var inboxLeaseRenewalsToSend = pendingInboxLeaseRenewals.Select(tc => tc.Completion).ToArray();

    // 3. Mark as Sent BEFORE calling ProcessWorkBatchAsync
    var sentAt = DateTimeOffset.UtcNow;
    _completions.MarkAsSent(pendingCompletions, sentAt);
    _failures.MarkAsSent(pendingFailures, sentAt);
    _leaseRenewals.MarkAsSent(pendingLeaseRenewals, sentAt);
    _inboxCompletions.MarkAsSent(pendingInboxCompletions, sentAt);
    _inboxFailures.MarkAsSent(pendingInboxFailures, sentAt);
    _inboxLeaseRenewals.MarkAsSent(pendingInboxLeaseRenewals, sentAt);

    // 4. Call ProcessWorkBatchAsync (may throw or return partial acknowledgement)
    WorkBatch workBatch;
    try {
      // Get work batch (heartbeat, claim work, return for processing)
      // Each call:
      // - Reports previous results (if any)
      // - Registers/updates instance + heartbeat
      // - Cleans up stale instances
      // - Claims orphaned work via modulo-based partition distribution
      // - Renews leases for buffered messages awaiting transport readiness
      // - Returns work for this instance to process
      var request = new ProcessWorkBatchRequest {
        InstanceId = _instanceProvider.InstanceId,
        ServiceName = _instanceProvider.ServiceName,
        HostName = _instanceProvider.HostName,
        ProcessId = _instanceProvider.ProcessId,
        Metadata = _options.InstanceMetadata,
        OutboxCompletions = completionsToSend,
        OutboxFailures = failuresToSend,
        InboxCompletions = inboxCompletionsToSend,
        InboxFailures = inboxFailuresToSend,
        ReceptorCompletions = [],  // FUTURE: Add receptor processing support
        ReceptorFailures = [],
        PerspectiveCompletions = [],  // FUTURE: Add perspective cursor support
        PerspectiveEventCompletions = [],
        PerspectiveFailures = [],
        NewOutboxMessages = [],  // Not used in publisher worker (dispatcher handles new messages)
        NewInboxMessages = [],   // Not used in publisher worker (consumer handles new messages)
        RenewOutboxLeaseIds = leaseRenewalsToSend,
        RenewInboxLeaseIds = inboxLeaseRenewalsToSend,
        Flags = _options.DebugMode ? WorkBatchFlags.DebugMode : WorkBatchFlags.None,
        PartitionCount = _options.PartitionCount,
        LeaseSeconds = _options.LeaseSeconds,
        StaleThresholdSeconds = _options.StaleThresholdSeconds
      };
      workBatch = await workCoordinator.ProcessWorkBatchAsync(request, cancellationToken);
    } catch (Exception ex) {
      // Database failure: Completions remain in 'Sent' status
      // ResetStale() will move them back to 'Pending' after timeout
      LogErrorProcessingWorkBatch(_logger, ex);
      return; // Exit early, retry on next cycle
    }

    // 5. Extract acknowledgement counts from workBatch metadata (from first row)
    var completionsProcessed = 0;
    var failuresProcessed = 0;
    var leaseRenewalsProcessed = 0;
    var inboxCompletionsProcessed = 0;
    var inboxFailuresProcessed = 0;
    var inboxLeaseRenewalsProcessed = 0;

    // Extract ack counts from the first available metadata row (outbox → inbox → perspective)
    Dictionary<string, JsonElement>? metadataRow = null;
    var outboxFirstRow = workBatch.OutboxWork.FirstOrDefault();
    if (outboxFirstRow?.Metadata != null) {
      metadataRow = outboxFirstRow.Metadata;
    } else {
      var inboxFirstRow = workBatch.InboxWork.FirstOrDefault();
      if (inboxFirstRow?.Metadata != null) {
        metadataRow = inboxFirstRow.Metadata;
      } else {
        var perspectiveFirstRow = workBatch.PerspectiveWork.FirstOrDefault();
        if (perspectiveFirstRow?.Metadata != null) {
          metadataRow = perspectiveFirstRow.Metadata;
        }
      }
    }

    if (metadataRow != null) {
      if (metadataRow.TryGetValue("outbox_completions_processed", out var compCount)) {
        completionsProcessed = compCount.GetInt32();
      }
      if (metadataRow.TryGetValue("outbox_failures_processed", out var failCount)) {
        failuresProcessed = failCount.GetInt32();
      }
      if (metadataRow.TryGetValue("outbox_lease_renewals_processed", out var renewCount)) {
        leaseRenewalsProcessed = renewCount.GetInt32();
      }
      if (metadataRow.TryGetValue("inbox_completions_processed", out var inboxCompCount)) {
        inboxCompletionsProcessed = inboxCompCount.GetInt32();
      }
      if (metadataRow.TryGetValue("inbox_failures_processed", out var inboxFailCount)) {
        inboxFailuresProcessed = inboxFailCount.GetInt32();
      }
      if (metadataRow.TryGetValue("inbox_lease_renewals_processed", out var inboxRenewCount)) {
        inboxLeaseRenewalsProcessed = inboxRenewCount.GetInt32();
      }
    }

    // 6. Mark as Acknowledged based on counts from SQL
    _completions.MarkAsAcknowledged(completionsProcessed);
    _failures.MarkAsAcknowledged(failuresProcessed);
    _leaseRenewals.MarkAsAcknowledged(leaseRenewalsProcessed);
    _inboxCompletions.MarkAsAcknowledged(inboxCompletionsProcessed);
    _inboxFailures.MarkAsAcknowledged(inboxFailuresProcessed);
    _inboxLeaseRenewals.MarkAsAcknowledged(inboxLeaseRenewalsProcessed);

    // 7. Clear only Acknowledged items
    _completions.ClearAcknowledged();
    _failures.ClearAcknowledged();
    _leaseRenewals.ClearAcknowledged();
    _inboxCompletions.ClearAcknowledged();
    _inboxFailures.ClearAcknowledged();
    _inboxLeaseRenewals.ClearAcknowledged();

    // 8. Reset stale items (sent but not acknowledged for > timeout) back to Pending
    _completions.ResetStale(DateTimeOffset.UtcNow);
    _failures.ResetStale(DateTimeOffset.UtcNow);
    _leaseRenewals.ResetStale(DateTimeOffset.UtcNow);
    _inboxCompletions.ResetStale(DateTimeOffset.UtcNow);
    _inboxFailures.ResetStale(DateTimeOffset.UtcNow);
    _inboxLeaseRenewals.ResetStale(DateTimeOffset.UtcNow);

    // Log a summary of message processing activity
    int totalActivity = completionsToSend.Length + failuresToSend.Length + leaseRenewalsToSend.Length
      + inboxCompletionsToSend.Length + inboxFailuresToSend.Length
      + workBatch.OutboxWork.Count + workBatch.InboxWork.Count;
    if (totalActivity > 0) {
      LogMessageBatchSummary(
        _logger,
        completionsToSend.Length,
        failuresToSend.Length,
        leaseRenewalsToSend.Length,
        workBatch.OutboxWork.Count,
        workBatch.InboxWork.Count,
        inboxFailuresToSend.Length
      );
    } else {
      LogNoWorkClaimed(_logger);
    }

    // Write outbox work to channel for publisher loop
    if (workBatch.OutboxWork.Count > 0) {
      // Sort by MessageId (UUIDv7 has time-based ordering)
      var orderedOutboxWork = workBatch.OutboxWork.OrderBy(m => m.MessageId).ToList();

      foreach (var work in orderedOutboxWork) {
        await _workChannelWriter.WriteAsync(work, cancellationToken);
      }
    }

    // Process inbox work (orphaned messages recovered via claim_orphaned_inbox)
    if (workBatch.InboxWork.Count > 0) {
      await _processInboxWorkAsync(workBatch.InboxWork, cancellationToken);
    }

    // Track work state transitions for OnWorkProcessingStarted / OnWorkProcessingIdle callbacks
    bool hasWork = workBatch.OutboxWork.Count > 0 || workBatch.InboxWork.Count > 0;

    if (hasWork) {
      // Reset empty poll counter
      Interlocked.Exchange(ref _consecutiveEmptyPolls, 0);

      // Transition to active if was idle
      if (_isIdle) {
        _isIdle = false;
        OnWorkProcessingStarted?.Invoke();
        LogWorkProcessingStarted(_logger);
      }
    } else {
      // Increment empty poll counter
      Interlocked.Increment(ref _consecutiveEmptyPolls);

      // Check if should transition to idle
      if (!_isIdle && _consecutiveEmptyPolls >= _options.IdleThresholdPolls) {
        _isIdle = true;
        OnWorkProcessingIdle?.Invoke();
        LogWorkProcessingIdle(_logger, _consecutiveEmptyPolls);
      }
    }
  }

  /// <summary>
  /// Processes orphaned inbox messages recovered via claim_orphaned_inbox.
  /// Deserializes, invokes lifecycle receptors, processes via OrderedStreamProcessor,
  /// and queues completions/failures to inbox trackers.
  /// </summary>
  /// <docs>messaging/work-coordination</docs>
  private async Task _processInboxWorkAsync(List<InboxWork> inboxWork, CancellationToken cancellationToken) {
    LogProcessingInboxWork(_logger, inboxWork.Count);

    // Check for MaxInboxAttempts purge — skip messages that have exceeded the threshold
    var maxAttempts = _options.MaxInboxAttempts;
    List<InboxWork> workToProcess;
    if (maxAttempts.HasValue) {
      workToProcess = [];
      foreach (var work in inboxWork) {
        if (work.Attempts >= maxAttempts.Value) {
          // Purge: mark as completed to remove from inbox (dead-letter)
          LogInboxMessagePurged(_logger, work.MessageId, work.Attempts, maxAttempts.Value);
          _inboxCompletions.Add(new MessageCompletion {
            MessageId = work.MessageId,
            Status = work.Status | MessageProcessingStatus.Published  // Terminal status
          });
        } else {
          workToProcess.Add(work);
        }
      }
    } else {
      workToProcess = inboxWork;
    }

    if (workToProcess.Count == 0) {
      return;
    }

    // Create scope for scoped services
    await using var inboxScope = _scopeFactory.CreateAsyncScope();
    var receptorInvoker = inboxScope.ServiceProvider.GetService<IReceptorInvoker>();

    // Establish security context from first work item's envelope
    var firstWork = workToProcess[0];
    await SecurityContextHelper.EstablishFullContextAsync(
      firstWork.Envelope,
      inboxScope.ServiceProvider,
      cancellationToken);

    // PreInbox lifecycle stages
    foreach (var work in workToProcess) {
      if (_lifecycleMessageDeserializer is not null && receptorInvoker is not null) {
        try {
          var message = _lifecycleMessageDeserializer.DeserializeFromJsonElement(work.Envelope.Payload, work.MessageType);
          var typedEnvelope = work.Envelope.ReconstructWithPayload(message);
          var lifecycleContext = new LifecycleExecutionContext {
            CurrentStage = LifecycleStage.PreInboxAsync,
            EventId = null,
            StreamId = null,
            LastProcessedEventId = null,
            MessageSource = MessageSource.Inbox,
            AttemptNumber = work.Attempts
          };

          await receptorInvoker.InvokeAsync(typedEnvelope, LifecycleStage.PreInboxAsync, lifecycleContext, cancellationToken);
          await receptorInvoker.InvokeAsync(typedEnvelope, LifecycleStage.ImmediateAsync,
            lifecycleContext with { CurrentStage = LifecycleStage.ImmediateAsync }, cancellationToken);

          lifecycleContext = lifecycleContext with { CurrentStage = LifecycleStage.PreInboxInline };
          await receptorInvoker.InvokeAsync(typedEnvelope, LifecycleStage.PreInboxInline, lifecycleContext, cancellationToken);
          await receptorInvoker.InvokeAsync(typedEnvelope, LifecycleStage.ImmediateAsync,
            lifecycleContext with { CurrentStage = LifecycleStage.ImmediateAsync }, cancellationToken);
        } catch (Exception ex) {
          LogInboxLifecycleError(_logger, work.MessageId, "PreInbox", ex);
        }
      }
    }

    // Process via OrderedStreamProcessor (maintains stream ordering)
    var orderedProcessor = new OrderedStreamProcessor();
    await orderedProcessor.ProcessInboxWorkAsync(
      workToProcess,
      processor: (work) => {
        // For orphaned recovery, mark as EventStored (same as TransportConsumerWorker)
        return Task.FromResult(MessageProcessingStatus.EventStored);
      },
      completionHandler: (msgId, status) => {
        _inboxCompletions.Add(new MessageCompletion {
          MessageId = msgId,
          Status = status
        });
      },
      failureHandler: (msgId, status, error) => {
        _inboxFailures.Add(new MessageFailure {
          MessageId = msgId,
          CompletedStatus = status,
          Error = error,
          Reason = MessageFailureReason.Unknown
        });
      },
      cancellationToken
    );

    // PostInbox lifecycle stages
    foreach (var work in workToProcess) {
      if (_lifecycleMessageDeserializer is not null && receptorInvoker is not null) {
        try {
          var message = _lifecycleMessageDeserializer.DeserializeFromJsonElement(work.Envelope.Payload, work.MessageType);
          var typedEnvelope = work.Envelope.ReconstructWithPayload(message);
          var lifecycleContext = new LifecycleExecutionContext {
            CurrentStage = LifecycleStage.PostInboxAsync,
            EventId = null,
            StreamId = null,
            LastProcessedEventId = null,
            MessageSource = MessageSource.Inbox,
            AttemptNumber = work.Attempts
          };

          await receptorInvoker.InvokeAsync(typedEnvelope, LifecycleStage.PostInboxAsync, lifecycleContext, cancellationToken);
          await receptorInvoker.InvokeAsync(typedEnvelope, LifecycleStage.ImmediateAsync,
            lifecycleContext with { CurrentStage = LifecycleStage.ImmediateAsync }, cancellationToken);

          lifecycleContext = lifecycleContext with { CurrentStage = LifecycleStage.PostInboxInline };
          await receptorInvoker.InvokeAsync(typedEnvelope, LifecycleStage.PostInboxInline, lifecycleContext, cancellationToken);
          await receptorInvoker.InvokeAsync(typedEnvelope, LifecycleStage.ImmediateAsync,
            lifecycleContext with { CurrentStage = LifecycleStage.ImmediateAsync }, cancellationToken);
        } catch (Exception ex) {
          LogInboxLifecycleError(_logger, work.MessageId, "PostInbox", ex);
        }
      }
    }
  }

  // ========================================
  // High-Performance LoggerMessage Delegates
  // ========================================

  [LoggerMessage(
    EventId = 1,
    Level = LogLevel.Information,
    Message = "WorkCoordinator publisher starting: Instance {InstanceId} ({ServiceName}@{HostName}:{ProcessId}), interval: {Interval}ms"
  )]
  static partial void LogWorkerStarting(
    ILogger logger,
    Guid instanceId,
    string serviceName,
    string hostName,
    int processId,
    int interval
  );

  [LoggerMessage(
    EventId = 2,
    Level = LogLevel.Information,
    Message = "WorkCoordinator publisher stopping"
  )]
  static partial void LogWorkerStopping(ILogger logger);

  [LoggerMessage(
    EventId = 3,
    Level = LogLevel.Debug,
    Message = "Checking for pending outbox messages on startup..."
  )]
  static partial void LogCheckingPendingMessages(ILogger logger);

  [LoggerMessage(
    EventId = 4,
    Level = LogLevel.Debug,
    Message = "Initial work batch processing complete"
  )]
  static partial void LogInitialWorkBatchComplete(ILogger logger);

  [LoggerMessage(
    EventId = 5,
    Level = LogLevel.Warning,
    Message = "Database not ready on startup - skipping initial work batch processing"
  )]
  static partial void LogDatabaseNotReadyOnStartup(ILogger logger);

  [LoggerMessage(
    EventId = 6,
    Level = LogLevel.Error,
    Message = "Error processing initial work batch on startup"
  )]
  static partial void LogErrorProcessingInitialWorkBatch(ILogger logger, Exception ex);

  [LoggerMessage(
    EventId = 7,
    Level = LogLevel.Information,
    Message = "Database not ready, skipping work batch processing (consecutive checks: {ConsecutiveCount})"
  )]
  static partial void LogDatabaseNotReady(ILogger logger, int consecutiveCount);

  [LoggerMessage(
    EventId = 8,
    Level = LogLevel.Warning,
    Message = "Database not ready for {ConsecutiveCount} consecutive polling cycles. Work coordinator is paused."
  )]
  static partial void LogDatabaseNotReadyWarning(ILogger logger, int consecutiveCount);

  [LoggerMessage(
    EventId = 9,
    Level = LogLevel.Error,
    Message = "Error processing work batch"
  )]
  static partial void LogErrorProcessingWorkBatch(ILogger logger, Exception ex);

  [LoggerMessage(
    EventId = 10,
    Level = LogLevel.Debug,
    Message = "PublisherLoop started, waiting for work from channel..."
  )]
  static partial void LogPublisherLoopStarted(ILogger logger);

  [LoggerMessage(
    EventId = 11,
    Level = LogLevel.Debug,
    Message = "PublisherLoop received work from channel: MessageId={MessageId}, Destination={Destination}"
  )]
  static partial void LogPublisherLoopReceivedWork(ILogger logger, Guid messageId, string? destination);

  [LoggerMessage(
    EventId = 12,
    Level = LogLevel.Debug,
    Message = "Transport readiness check: IsReady={IsReady}"
  )]
  static partial void LogTransportReadinessCheck(ILogger logger, bool isReady);

  [LoggerMessage(
    EventId = 13,
    Level = LogLevel.Information,
    Message = "Transport not ready, buffering message {MessageId} (destination: {Destination})"
  )]
  static partial void LogTransportNotReadyBuffering(ILogger logger, Guid messageId, string? destination);

  [LoggerMessage(
    EventId = 14,
    Level = LogLevel.Warning,
    Message = "Transport not ready for {ConsecutiveCount} consecutive messages. Messages are being buffered with lease renewal."
  )]
  static partial void LogTransportNotReadyWarning(ILogger logger, int consecutiveCount);

  [LoggerMessage(
    EventId = 15,
    Level = LogLevel.Debug,
    Message = "About to publish message {MessageId} to {Destination}"
  )]
  static partial void LogAboutToPublishMessage(ILogger logger, Guid messageId, string? destination);

  [LoggerMessage(
    EventId = 16,
    Level = LogLevel.Debug,
    Message = "Publish result for {MessageId}: Success={Success}, Status={Status}"
  )]
  static partial void LogPublishResult(ILogger logger, Guid messageId, bool success, MessageProcessingStatus status);

  [LoggerMessage(
    EventId = 17,
    Level = LogLevel.Warning,
    Message = "Transport failure for message {MessageId} to {Destination}: {Error}. Renewing lease for retry."
  )]
  static partial void LogTransportFailureRenewingLease(ILogger logger, Guid messageId, string? destination, string error);

  [LoggerMessage(
    EventId = 18,
    Level = LogLevel.Error,
    Message = "Failed to publish outbox message {MessageId} to {Destination}: {Error} (Reason: {Reason})"
  )]
  static partial void LogFailedToPublishMessage(ILogger logger, Guid messageId, string? destination, string error, MessageFailureReason reason);

  [LoggerMessage(
    EventId = 19,
    Level = LogLevel.Error,
    Message = "Unexpected error publishing outbox message {MessageId}"
  )]
  static partial void LogUnexpectedErrorPublishing(ILogger logger, Guid messageId, Exception ex);

  [LoggerMessage(
    EventId = 20,
    Level = LogLevel.Debug,
    Message = "Message batch: Outbox published={Published}, failed={OutboxFailed}, buffered={Buffered}, claimed={Claimed} | Inbox claimed={InboxClaimed}, failed={InboxFailed}"
  )]
  static partial void LogMessageBatchSummary(
    ILogger logger,
    int published,
    int outboxFailed,
    int buffered,
    int claimed,
    int inboxClaimed,
    int inboxFailed
  );

  [LoggerMessage(
    EventId = 21,
    Level = LogLevel.Debug,
    Message = "Work batch processing: no work claimed (all partitions assigned to other instances or no pending messages)"
  )]
  static partial void LogNoWorkClaimed(ILogger logger);

  [LoggerMessage(
    EventId = 22,
    Level = LogLevel.Debug,
    Message = "Work processing started (idle → active)"
  )]
  static partial void LogWorkProcessingStarted(ILogger logger);

  [LoggerMessage(
    EventId = 23,
    Level = LogLevel.Debug,
    Message = "Work processing idle (active → idle) after {EmptyPolls} empty polls"
  )]
  static partial void LogWorkProcessingIdle(ILogger logger, int emptyPolls);

  [LoggerMessage(
    EventId = 24,
    Level = LogLevel.Information,
    Message = "Processing {Count} orphaned inbox messages"
  )]
  static partial void LogProcessingInboxWork(ILogger logger, int count);

  [LoggerMessage(
    EventId = 25,
    Level = LogLevel.Warning,
    Message = "Purging inbox message {MessageId}: attempts ({Attempts}) exceeded max ({MaxAttempts})"
  )]
  static partial void LogInboxMessagePurged(ILogger logger, Guid messageId, int attempts, int maxAttempts);

  [LoggerMessage(
    EventId = 26,
    Level = LogLevel.Error,
    Message = "Error in {Stage} lifecycle for inbox message {MessageId}"
  )]
  static partial void LogInboxLifecycleError(ILogger logger, Guid messageId, string stage, Exception ex);

  /// <summary>
  /// Populates QueuedAt timestamp properties on the message payload using JSON manipulation.
  /// AOT-safe: uses JsonNode, no reflection or Type.GetType().
  /// </summary>
  private static void _populateQueuedAtTimestamp(OutboxWork work) {
    if (work.Envelope is not MessageEnvelope<JsonElement> concreteEnvelope) {
      return;
    }

    concreteEnvelope.Payload = JsonAutoPopulateHelper.PopulateTimestampByName(
        concreteEnvelope.Payload,
        work.MessageType,
        TimestampKind.QueuedAt,
        DateTimeOffset.UtcNow);
  }
}


/// <summary>
/// Configuration options for the WorkCoordinator publisher worker.
/// </summary>
/// <tests>tests/Whizbang.Core.Tests/Workers/WorkCoordinatorPublisherWorkerMetricsTests.cs:TransportNotReady_SingleBuffer_LogsInformationAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Workers/WorkCoordinatorPublisherWorkerRaceConditionTests.cs:RaceCondition_MultipleInstances_NoDuplicatePublishingAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Workers/WorkCoordinatorPublisherWorkerRaceConditionTests.cs:RaceCondition_ImmediateProcessing_WithRealisticDelaysAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Workers/WorkCoordinatorPublisherWorkerRaceConditionTests.cs:RaceCondition_DatabaseSlowness_DoesNotBlockPublishingAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Workers/WorkCoordinatorPublisherWorkerRaceConditionTests.cs:RaceCondition_TransportFailures_RetriesSuccessfullyAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Workers/WorkCoordinatorPublisherWorkerRaceConditionTests.cs:RaceCondition_DatabaseNotReady_DelaysProcessingAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Workers/WorkCoordinatorPublisherWorkerDatabaseReadinessTests.cs:DatabaseNotReady_ProcessWorkBatchAsync_SkippedAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Workers/WorkCoordinatorPublisherWorkerStartupTests.cs:ImmediateProcessing_OnStartup_ProcessesWorkBeforeFirstPollAsync</tests>
public class WorkCoordinatorPublisherOptions {
  /// <summary>
  /// Milliseconds to wait between polling for work.
  /// Default: 1000 (1 second)
  /// </summary>
  /// <tests>tests/Whizbang.Core.Tests/Workers/WorkCoordinatorPublisherWorkerMetricsTests.cs:TransportNotReady_SingleBuffer_LogsInformationAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Workers/WorkCoordinatorPublisherWorkerRaceConditionTests.cs:RaceCondition_MultipleInstances_NoDuplicatePublishingAsync</tests>
  public int PollingIntervalMilliseconds { get; set; } = 1000;

  /// <summary>
  /// Lease duration in seconds.
  /// Messages claimed will be locked for this duration.
  /// Default: 300 (5 minutes)
  /// </summary>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/WorkCoordinatorPublisherWorkerIntegrationTests.cs:WorkerProcessesOutboxMessages_EndToEndAsync</tests>
  public int LeaseSeconds { get; set; } = 300;

  /// <summary>
  /// Stale instance threshold in seconds.
  /// Instances that haven't sent a heartbeat for this duration will be removed.
  /// Default: 600 (10 minutes)
  /// </summary>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/WorkCoordinatorPublisherWorkerIntegrationTests.cs:WorkerProcessesOutboxMessages_EndToEndAsync</tests>
  public int StaleThresholdSeconds { get; set; } = 600;

  /// <summary>
  /// Optional metadata to attach to this service instance.
  /// Can include version, environment, etc.
  /// Supports any JSON value type via JsonElement.
  /// </summary>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/WorkCoordinatorPublisherWorkerIntegrationTests.cs:WorkerProcessesOutboxMessages_EndToEndAsync</tests>
  public Dictionary<string, JsonElement>? InstanceMetadata { get; set; }

  /// <summary>
  /// Keep completed messages for debugging (default: false).
  /// When enabled, completed messages are preserved instead of deleted.
  /// </summary>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/WorkCoordinatorPublisherWorkerIntegrationTests.cs:WorkerProcessesOutboxMessages_EndToEndAsync</tests>
  public bool DebugMode { get; set; }

  /// <summary>
  /// Number of partitions for work distribution.
  /// Default: 10000
  /// </summary>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/WorkCoordinatorPublisherWorkerIntegrationTests.cs:WorkerProcessesOutboxMessages_EndToEndAsync</tests>
  public int PartitionCount { get; set; } = 10_000;

  /// <summary>
  /// Number of consecutive empty work polls required to trigger OnWorkProcessingIdle callback.
  /// Default: 2
  /// </summary>
  public int IdleThresholdPolls { get; set; } = 2;

  /// <summary>
  /// Maximum number of processing attempts for inbox messages before purging.
  /// When set, inbox messages that have been retried more than this many times
  /// are marked as completed (dead-lettered) to prevent infinite retry loops.
  /// Default: null (disabled — messages retry indefinitely).
  /// </summary>
  public int? MaxInboxAttempts { get; set; }

  /// <summary>
  /// Retry configuration for completion acknowledgement.
  /// Controls exponential backoff when ProcessWorkBatchAsync fails.
  /// </summary>
  public WorkerRetryOptions RetryOptions { get; set; } = new();
}

/// <summary>
/// Callback invoked when work processing transitions from idle to active state.
/// Fires when work appears after consecutive empty polls.
/// </summary>
public delegate void WorkProcessingStartedHandler();

/// <summary>
/// Callback invoked when work processing transitions from active to idle state.
/// Fires after N consecutive polls returned no work (configurable via IdleThresholdPolls).
/// Useful for integration tests to wait for event processing completion.
/// </summary>
public delegate void WorkProcessingIdleHandler();
