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
using Whizbang.Core.Lifecycle;
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
  private const string LIFECYCLE_PRE_OUTBOX_ASYNC = "Lifecycle PreOutboxAsync";
  private const string LIFECYCLE_PRE_OUTBOX_INLINE = "Lifecycle PreOutboxInline";
  private const string LIFECYCLE_POST_OUTBOX_ASYNC = "Lifecycle PostOutboxAsync";
  private const string LIFECYCLE_POST_OUTBOX_INLINE = "Lifecycle PostOutboxInline";
  private const string METRIC_FAILURE_REASON = "failure_reason";
  private const string UNKNOWN_ERROR = "Unknown error";

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
    await _processInitialWorkBatchAsync(stoppingToken);

    while (!stoppingToken.IsCancellationRequested) {
      try {
        if (!await _checkDatabaseReadinessAsync(stoppingToken)) {
          await Task.Delay(_options.PollingIntervalMilliseconds, stoppingToken);
          continue;
        }
        await _processWorkBatchAsync(stoppingToken);
      } catch (ObjectDisposedException) {
        break;
      } catch (Exception ex) when (ex is not OperationCanceledException) {
        LogErrorProcessingWorkBatch(_logger, ex);
      }

      try {
        await Task.Delay(_options.PollingIntervalMilliseconds, stoppingToken);
      } catch (OperationCanceledException) {
        break;
      }
    }

    _workChannelWriter.Complete();
  }

  private async Task _processInitialWorkBatchAsync(CancellationToken stoppingToken) {
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
  }

  private async Task<bool> _checkDatabaseReadinessAsync(CancellationToken stoppingToken) {
    var isDatabaseReady = await _databaseReadinessCheck.IsReadyAsync(stoppingToken);
    if (isDatabaseReady) {
      Interlocked.Exchange(ref _consecutiveDatabaseNotReadyChecks, 0);
      return true;
    }

    Interlocked.Increment(ref _consecutiveDatabaseNotReadyChecks);
    LogDatabaseNotReady(_logger, _consecutiveDatabaseNotReadyChecks);
    if (_consecutiveDatabaseNotReadyChecks > 10) {
      LogDatabaseNotReadyWarning(_logger, _consecutiveDatabaseNotReadyChecks);
    }
    return false;
  }

  private async Task _publisherLoopAsync(CancellationToken stoppingToken) {
    LogPublisherLoopStarted(_logger);

    if (_publishStrategy.SupportsBulkPublish) {
      await _publisherLoopBulkAsync(stoppingToken);
    } else {
      await _publisherLoopSingularAsync(stoppingToken);
    }
  }

  private async Task _publisherLoopBulkAsync(CancellationToken stoppingToken) {
    var maxBatchSize = _options.MaxBulkPublishBatchSize;
    await foreach (var firstWork in _workChannelWriter.Reader.ReadAllAsync(stoppingToken)) {
      // Drain additional items from the channel up to max batch size
      var batch = new List<OutboxWork>(maxBatchSize) { firstWork };
      while (batch.Count < maxBatchSize && _workChannelWriter.Reader.TryRead(out var additionalWork)) {
        batch.Add(additionalWork);
      }

      try {
        // Check transport readiness once for the entire batch
        var readinessWaitSw = Stopwatch.StartNew();
        var isReady = await _publishStrategy.IsReadyAsync(stoppingToken);
        readinessWaitSw.Stop();
        LogTransportReadinessCheck(_logger, isReady);

        if (!isReady) {
          _transportMetrics?.OutboxReadinessWaitDuration.Record(readinessWaitSw.Elapsed.TotalMilliseconds);

          // Re-queue all items and renew leases
          foreach (var work in batch) {
            _leaseRenewals.Add(work.MessageId);
            Interlocked.Increment(ref _consecutiveNotReadyChecks);
            Interlocked.Increment(ref _totalLeaseRenewals);
            Interlocked.Increment(ref _totalBufferedMessages);
            _workCoordinatorMetrics?.PublisherLeaseRenewals.Add(1);
            _workCoordinatorMetrics?.PublisherBufferedMessages.Add(1);
            _workChannelWriter.TryWrite(work);
          }

          if (_consecutiveNotReadyChecks > 10) {
            LogTransportNotReadyWarning(_logger, _consecutiveNotReadyChecks);
          }
          continue;
        }

        Interlocked.Exchange(ref _consecutiveNotReadyChecks, 0);

        // Pre-outbox lifecycle per message
        var batchContexts = new List<(OutboxWork Work, AsyncServiceScope Scope, ILifecycleTracking? Tracking, IMessageEnvelope? TypedEnvelope, ActivityContext TraceContext, bool EnableLifecycleSpans)>(batch.Count);
        foreach (var work in batch) {
          LogPublisherLoopReceivedWork(_logger, work.MessageId, work.Destination);

          var scope = _scopeFactory.CreateAsyncScope();
          var receptorInvoker = scope.ServiceProvider.GetService<IReceptorInvoker>();

          await SecurityContextHelper.EstablishFullContextAsync(
            work.Envelope, scope.ServiceProvider, stoppingToken);

          var latestHop = work.Envelope.Hops?.LastOrDefault();
          ActivityContext traceContext = default;
          if (latestHop?.TraceParent is not null &&
              ActivityContext.TryParse(latestHop.TraceParent, null, out var parsedContext)) {
            traceContext = parsedContext;
          }

          var enableLifecycleSpans = _tracingOptions?.CurrentValue.IsEnabled(TraceComponents.Lifecycle) ?? false;
          ILifecycleTracking? outboxTracking = null;
          IMessageEnvelope? outboxTypedEnvelope = null;
          var coordinator = scope.ServiceProvider.GetService<ILifecycleCoordinator>();

          if (_lifecycleMessageDeserializer is not null && receptorInvoker is not null) {
            var message = _lifecycleMessageDeserializer.DeserializeFromJsonElement(work.Envelope.Payload, work.MessageType);
            outboxTypedEnvelope = work.Envelope.ReconstructWithPayload(message);

            if (coordinator is not null) {
              outboxTracking = coordinator.BeginTracking(
                work.MessageId, outboxTypedEnvelope, LifecycleStage.PreOutboxAsync, MessageSource.Outbox);
              using (enableLifecycleSpans ? WhizbangActivitySource.Tracing.StartActivity(LIFECYCLE_PRE_OUTBOX_ASYNC, ActivityKind.Internal, parentContext: traceContext) : null) {
                await outboxTracking.AdvanceToAsync(LifecycleStage.PreOutboxAsync, scope.ServiceProvider, stoppingToken);
              }
              using (enableLifecycleSpans ? WhizbangActivitySource.Tracing.StartActivity(LIFECYCLE_PRE_OUTBOX_INLINE, ActivityKind.Internal, parentContext: traceContext) : null) {
                await outboxTracking.AdvanceToAsync(LifecycleStage.PreOutboxInline, scope.ServiceProvider, stoppingToken);
              }
            } else {
              using (enableLifecycleSpans ? WhizbangActivitySource.Tracing.StartActivity(LIFECYCLE_PRE_OUTBOX_ASYNC, ActivityKind.Internal, parentContext: traceContext) : null) {
                var lifecycleContext = new LifecycleExecutionContext { CurrentStage = LifecycleStage.PreOutboxAsync, MessageSource = MessageSource.Outbox, AttemptNumber = work.Attempts };
                await receptorInvoker.InvokeAsync(outboxTypedEnvelope, LifecycleStage.PreOutboxAsync, lifecycleContext, stoppingToken);
                await receptorInvoker.InvokeAsync(outboxTypedEnvelope, LifecycleStage.ImmediateAsync, lifecycleContext with { CurrentStage = LifecycleStage.ImmediateAsync }, stoppingToken);
              }
              using (enableLifecycleSpans ? WhizbangActivitySource.Tracing.StartActivity(LIFECYCLE_PRE_OUTBOX_INLINE, ActivityKind.Internal, parentContext: traceContext) : null) {
                var lifecycleContext = new LifecycleExecutionContext { CurrentStage = LifecycleStage.PreOutboxInline, MessageSource = MessageSource.Outbox, AttemptNumber = work.Attempts };
                await receptorInvoker.InvokeAsync(outboxTypedEnvelope, LifecycleStage.PreOutboxInline, lifecycleContext, stoppingToken);
                await receptorInvoker.InvokeAsync(outboxTypedEnvelope, LifecycleStage.ImmediateAsync, lifecycleContext with { CurrentStage = LifecycleStage.ImmediateAsync }, stoppingToken);
              }
            }
          }

          _populateQueuedAtTimestamp(work);
          batchContexts.Add((work, scope, outboxTracking, outboxTypedEnvelope, traceContext, enableLifecycleSpans));
        }

        // Batch publish
        var publishSw = Stopwatch.StartNew();
        var results = await _publishStrategy.PublishBatchAsync(batch, stoppingToken);
        publishSw.Stop();
        _transportMetrics?.OutboxPublishDuration.Record(publishSw.Elapsed.TotalMilliseconds);

        // Post-outbox lifecycle per message + track results
        for (var i = 0; i < batchContexts.Count; i++) {
          var (work, scope, outboxTracking, outboxTypedEnvelope, traceContext, enableLifecycleSpans) = batchContexts[i];
          var result = results.FirstOrDefault(r => r.MessageId == work.MessageId);

          if (result is null) {
            // Safety: if transport didn't return a result for this item, treat as failure
            result = new MessagePublishResult {
              MessageId = work.MessageId,
              Success = false,
              CompletedStatus = work.Status,
              Error = "No result returned from batch publish",
              Reason = MessageFailureReason.Unknown
            };
          }

          LogPublishResult(_logger, work.MessageId, result.Success, result.CompletedStatus);

          // PostOutbox lifecycle
          var coordinator = scope.ServiceProvider.GetService<ILifecycleCoordinator>();
          if (outboxTracking is not null && coordinator is not null) {
            using (enableLifecycleSpans ? WhizbangActivitySource.Tracing.StartActivity(LIFECYCLE_POST_OUTBOX_ASYNC, ActivityKind.Internal, parentContext: traceContext) : null) {
              await outboxTracking.AdvanceToAsync(LifecycleStage.PostOutboxAsync, scope.ServiceProvider, stoppingToken);
            }
            using (enableLifecycleSpans ? WhizbangActivitySource.Tracing.StartActivity(LIFECYCLE_POST_OUTBOX_INLINE, ActivityKind.Internal, parentContext: traceContext) : null) {
              await outboxTracking.AdvanceToAsync(LifecycleStage.PostOutboxInline, scope.ServiceProvider, stoppingToken);
            }
            using (enableLifecycleSpans ? WhizbangActivitySource.Tracing.StartActivity("Lifecycle PostLifecycleAsync", ActivityKind.Internal, parentContext: traceContext) : null) {
              await outboxTracking.AdvanceToAsync(LifecycleStage.PostLifecycleAsync, scope.ServiceProvider, stoppingToken);
            }
            using (enableLifecycleSpans ? WhizbangActivitySource.Tracing.StartActivity("Lifecycle PostLifecycleInline", ActivityKind.Internal, parentContext: traceContext) : null) {
              await outboxTracking.AdvanceToAsync(LifecycleStage.PostLifecycleInline, scope.ServiceProvider, stoppingToken);
            }
            coordinator.AbandonTracking(work.MessageId);
          } else if (outboxTypedEnvelope is not null) {
            var receptorInvoker = scope.ServiceProvider.GetService<IReceptorInvoker>();
            if (receptorInvoker is not null) {
              using (enableLifecycleSpans ? WhizbangActivitySource.Tracing.StartActivity(LIFECYCLE_POST_OUTBOX_ASYNC, ActivityKind.Internal, parentContext: traceContext) : null) {
                var lifecycleContext = new LifecycleExecutionContext { CurrentStage = LifecycleStage.PostOutboxAsync, MessageSource = MessageSource.Outbox, AttemptNumber = work.Attempts };
                await receptorInvoker.InvokeAsync(outboxTypedEnvelope, LifecycleStage.PostOutboxAsync, lifecycleContext, stoppingToken);
                await receptorInvoker.InvokeAsync(outboxTypedEnvelope, LifecycleStage.ImmediateAsync, lifecycleContext with { CurrentStage = LifecycleStage.ImmediateAsync }, stoppingToken);
              }
              using (enableLifecycleSpans ? WhizbangActivitySource.Tracing.StartActivity(LIFECYCLE_POST_OUTBOX_INLINE, ActivityKind.Internal, parentContext: traceContext) : null) {
                var lifecycleContext = new LifecycleExecutionContext { CurrentStage = LifecycleStage.PostOutboxInline, MessageSource = MessageSource.Outbox, AttemptNumber = work.Attempts };
                await receptorInvoker.InvokeAsync(outboxTypedEnvelope, LifecycleStage.PostOutboxInline, lifecycleContext, stoppingToken);
                await receptorInvoker.InvokeAsync(outboxTypedEnvelope, LifecycleStage.ImmediateAsync, lifecycleContext with { CurrentStage = LifecycleStage.ImmediateAsync }, stoppingToken);
              }
            }
          }

          // Track completion/failure
          if (result.Success) {
            _transportMetrics?.OutboxMessagesPublished.Add(1);
            _completions.Add(new MessageCompletion { MessageId = work.MessageId, Status = result.CompletedStatus });
          } else if (result.Reason == MessageFailureReason.TransportException) {
            _transportMetrics?.OutboxMessagesFailed.Add(1, new KeyValuePair<string, object?>(METRIC_FAILURE_REASON, "transport_exception"));
            _transportMetrics?.OutboxPublishRetries.Add(1);
            _leaseRenewals.Add(work.MessageId);
            _workChannelWriter.TryWrite(work);
            LogTransportFailureRenewingLease(_logger, work.MessageId, work.Destination, result.Error ?? UNKNOWN_ERROR);
          } else {
            _transportMetrics?.OutboxMessagesFailed.Add(1, new KeyValuePair<string, object?>(METRIC_FAILURE_REASON, result.Reason.ToString()));
            _failures.Add(new MessageFailure { MessageId = work.MessageId, CompletedStatus = result.CompletedStatus, Error = result.Error ?? UNKNOWN_ERROR, Reason = result.Reason });
            LogFailedToPublishMessage(_logger, work.MessageId, work.Destination, result.Error ?? UNKNOWN_ERROR, result.Reason);
          }

          // Dispose scope
          await scope.DisposeAsync();
        }
      } catch (ObjectDisposedException) {
        break;
      } catch (Exception ex) when (ex is not OperationCanceledException) {
        // If the entire batch fails unexpectedly, fail all items
        foreach (var work in batch) {
          LogUnexpectedErrorPublishing(_logger, work.MessageId, ex);
          _transportMetrics?.OutboxMessagesFailed.Add(1, new KeyValuePair<string, object?>(METRIC_FAILURE_REASON, "unexpected_exception"));
          _failures.Add(new MessageFailure { MessageId = work.MessageId, CompletedStatus = work.Status, Error = ex.Message, Reason = MessageFailureReason.Unknown });
        }
      }
    }
  }

  private async Task _publisherLoopSingularAsync(CancellationToken stoppingToken) {
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
        ILifecycleTracking? outboxTracking = null;
        IMessageEnvelope? outboxTypedEnvelope = null;
        var coordinator = lifecycleScope.ServiceProvider.GetService<ILifecycleCoordinator>();

        if (_lifecycleMessageDeserializer is not null && receptorInvoker is not null) {
          var message = _lifecycleMessageDeserializer.DeserializeFromJsonElement(work.Envelope.Payload, work.MessageType);
          outboxTypedEnvelope = work.Envelope.ReconstructWithPayload(message);

          if (coordinator is not null) {
            var eventId = work.MessageId;
            outboxTracking = coordinator.BeginTracking(
              eventId, outboxTypedEnvelope, LifecycleStage.PreOutboxAsync, MessageSource.Outbox);

            using (enableLifecycleSpans ? WhizbangActivitySource.Tracing.StartActivity(LIFECYCLE_PRE_OUTBOX_ASYNC, ActivityKind.Internal, parentContext: traceContext) : null) {
              await outboxTracking.AdvanceToAsync(LifecycleStage.PreOutboxAsync, lifecycleScope.ServiceProvider, stoppingToken);
            }

            using (enableLifecycleSpans ? WhizbangActivitySource.Tracing.StartActivity(LIFECYCLE_PRE_OUTBOX_INLINE, ActivityKind.Internal, parentContext: traceContext) : null) {
              await outboxTracking.AdvanceToAsync(LifecycleStage.PreOutboxInline, lifecycleScope.ServiceProvider, stoppingToken);
            }
          } else {
            // Fallback: direct invocation when coordinator not registered
            using (enableLifecycleSpans ? WhizbangActivitySource.Tracing.StartActivity(LIFECYCLE_PRE_OUTBOX_ASYNC, ActivityKind.Internal, parentContext: traceContext) : null) {
              var lifecycleContext = new LifecycleExecutionContext {
                CurrentStage = LifecycleStage.PreOutboxAsync,
                MessageSource = MessageSource.Outbox,
                AttemptNumber = work.Attempts
              };
              await receptorInvoker.InvokeAsync(outboxTypedEnvelope, LifecycleStage.PreOutboxAsync, lifecycleContext, stoppingToken);
              await receptorInvoker.InvokeAsync(outboxTypedEnvelope, LifecycleStage.ImmediateAsync,
                lifecycleContext with { CurrentStage = LifecycleStage.ImmediateAsync }, stoppingToken);
            }

            using (enableLifecycleSpans ? WhizbangActivitySource.Tracing.StartActivity(LIFECYCLE_PRE_OUTBOX_INLINE, ActivityKind.Internal, parentContext: traceContext) : null) {
              var lifecycleContext = new LifecycleExecutionContext {
                CurrentStage = LifecycleStage.PreOutboxInline,
                MessageSource = MessageSource.Outbox,
                AttemptNumber = work.Attempts
              };
              await receptorInvoker.InvokeAsync(outboxTypedEnvelope, LifecycleStage.PreOutboxInline, lifecycleContext, stoppingToken);
              await receptorInvoker.InvokeAsync(outboxTypedEnvelope, LifecycleStage.ImmediateAsync,
                lifecycleContext with { CurrentStage = LifecycleStage.ImmediateAsync }, stoppingToken);
            }
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
        if (outboxTracking is not null && coordinator is not null) {
          using (enableLifecycleSpans ? WhizbangActivitySource.Tracing.StartActivity(LIFECYCLE_POST_OUTBOX_ASYNC, ActivityKind.Internal, parentContext: traceContext) : null) {
            await outboxTracking.AdvanceToAsync(LifecycleStage.PostOutboxAsync, lifecycleScope.ServiceProvider, stoppingToken);
          }

          using (enableLifecycleSpans ? WhizbangActivitySource.Tracing.StartActivity(LIFECYCLE_POST_OUTBOX_INLINE, ActivityKind.Internal, parentContext: traceContext) : null) {
            await outboxTracking.AdvanceToAsync(LifecycleStage.PostOutboxInline, lifecycleScope.ServiceProvider, stoppingToken);
          }

          // PostLifecycle: OutboxWorker is the last worker when event leaves the service
          using (enableLifecycleSpans ? WhizbangActivitySource.Tracing.StartActivity("Lifecycle PostLifecycleAsync", ActivityKind.Internal, parentContext: traceContext) : null) {
            await outboxTracking.AdvanceToAsync(LifecycleStage.PostLifecycleAsync, lifecycleScope.ServiceProvider, stoppingToken);
          }

          using (enableLifecycleSpans ? WhizbangActivitySource.Tracing.StartActivity("Lifecycle PostLifecycleInline", ActivityKind.Internal, parentContext: traceContext) : null) {
            await outboxTracking.AdvanceToAsync(LifecycleStage.PostLifecycleInline, lifecycleScope.ServiceProvider, stoppingToken);
          }

          // EXIT: event sent to transport, PostLifecycle complete
          coordinator.AbandonTracking(work.MessageId);
        } else if (outboxTypedEnvelope is not null && receptorInvoker is not null) {
          // Fallback: direct invocation
          using (enableLifecycleSpans ? WhizbangActivitySource.Tracing.StartActivity(LIFECYCLE_POST_OUTBOX_ASYNC, ActivityKind.Internal, parentContext: traceContext) : null) {
            var lifecycleContext = new LifecycleExecutionContext {
              CurrentStage = LifecycleStage.PostOutboxAsync,
              MessageSource = MessageSource.Outbox,
              AttemptNumber = work.Attempts
            };
            await receptorInvoker.InvokeAsync(outboxTypedEnvelope, LifecycleStage.PostOutboxAsync, lifecycleContext, stoppingToken);
            await receptorInvoker.InvokeAsync(outboxTypedEnvelope, LifecycleStage.ImmediateAsync,
              lifecycleContext with { CurrentStage = LifecycleStage.ImmediateAsync }, stoppingToken);
          }

          using (enableLifecycleSpans ? WhizbangActivitySource.Tracing.StartActivity(LIFECYCLE_POST_OUTBOX_INLINE, ActivityKind.Internal, parentContext: traceContext) : null) {
            var lifecycleContext = new LifecycleExecutionContext {
              CurrentStage = LifecycleStage.PostOutboxInline,
              MessageSource = MessageSource.Outbox,
              AttemptNumber = work.Attempts
            };
            await receptorInvoker.InvokeAsync(outboxTypedEnvelope, LifecycleStage.PostOutboxInline, lifecycleContext, stoppingToken);
            await receptorInvoker.InvokeAsync(outboxTypedEnvelope, LifecycleStage.ImmediateAsync,
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
            _transportMetrics?.OutboxMessagesFailed.Add(1, new KeyValuePair<string, object?>(METRIC_FAILURE_REASON, "transport_exception"));
            _transportMetrics?.OutboxPublishRetries.Add(1);
            _leaseRenewals.Add(work.MessageId);

            // Re-queue work to channel for retry — without this, the message is consumed
            // from the channel and lost until lease expiry (which never happens because
            // the lease keeps being renewed)
            _workChannelWriter.TryWrite(work);

            LogTransportFailureRenewingLease(_logger, work.MessageId, work.Destination, result.Error ?? UNKNOWN_ERROR);
          } else {
            // Non-retryable failures (serialization, validation, etc.) - mark as failed
            _transportMetrics?.OutboxMessagesFailed.Add(1, new KeyValuePair<string, object?>(METRIC_FAILURE_REASON, result.Reason.ToString()));
            _failures.Add(new MessageFailure {
              MessageId = work.MessageId,
              CompletedStatus = result.CompletedStatus,
              Error = result.Error ?? UNKNOWN_ERROR,
              Reason = result.Reason
            });

            LogFailedToPublishMessage(_logger, work.MessageId, work.Destination, result.Error ?? UNKNOWN_ERROR, result.Reason);
          }
        }
      } catch (ObjectDisposedException) {
        // Service provider disposed during host shutdown — exit gracefully
        break;
      } catch (Exception ex) when (ex is not OperationCanceledException) {
        LogUnexpectedErrorPublishing(_logger, work.MessageId, ex);
        _transportMetrics?.OutboxMessagesFailed.Add(1, new KeyValuePair<string, object?>(METRIC_FAILURE_REASON, "unexpected_exception"));

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

    // 5-8. Extract ack counts, mark acknowledged, clear, and reset stale
    _processAcknowledgements(workBatch);

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

    _trackWorkStateTransitions(workBatch.OutboxWork.Count > 0 || workBatch.InboxWork.Count > 0);
  }

  private void _trackWorkStateTransitions(bool hasWork) {
    if (hasWork) {
      Interlocked.Exchange(ref _consecutiveEmptyPolls, 0);
      if (_isIdle) {
        _isIdle = false;
        OnWorkProcessingStarted?.Invoke();
        LogWorkProcessingStarted(_logger);
      }
    } else {
      Interlocked.Increment(ref _consecutiveEmptyPolls);
      if (!_isIdle && _consecutiveEmptyPolls >= _options.IdleThresholdPolls) {
        _isIdle = true;
        OnWorkProcessingIdle?.Invoke();
        LogWorkProcessingIdle(_logger, _consecutiveEmptyPolls);
      }
    }
  }

  /// <summary>
  /// Extracts acknowledgement counts from work batch metadata and processes tracker state.
  /// </summary>
  private void _processAcknowledgements(WorkBatch workBatch) {
    var metadataRow = _extractMetadataRow(workBatch);

    _completions.MarkAsAcknowledged(_extractAckCount(metadataRow, "outbox_completions_processed"));
    _failures.MarkAsAcknowledged(_extractAckCount(metadataRow, "outbox_failures_processed"));
    _leaseRenewals.MarkAsAcknowledged(_extractAckCount(metadataRow, "outbox_lease_renewals_processed"));
    _inboxCompletions.MarkAsAcknowledged(_extractAckCount(metadataRow, "inbox_completions_processed"));
    _inboxFailures.MarkAsAcknowledged(_extractAckCount(metadataRow, "inbox_failures_processed"));
    _inboxLeaseRenewals.MarkAsAcknowledged(_extractAckCount(metadataRow, "inbox_lease_renewals_processed"));

    _completions.ClearAcknowledged();
    _failures.ClearAcknowledged();
    _leaseRenewals.ClearAcknowledged();
    _inboxCompletions.ClearAcknowledged();
    _inboxFailures.ClearAcknowledged();
    _inboxLeaseRenewals.ClearAcknowledged();

    var now = DateTimeOffset.UtcNow;
    _completions.ResetStale(now);
    _failures.ResetStale(now);
    _leaseRenewals.ResetStale(now);
    _inboxCompletions.ResetStale(now);
    _inboxFailures.ResetStale(now);
    _inboxLeaseRenewals.ResetStale(now);
  }

  private static Dictionary<string, JsonElement>? _extractMetadataRow(WorkBatch workBatch) {
    var outboxFirstRow = workBatch.OutboxWork.FirstOrDefault();
    if (outboxFirstRow?.Metadata != null) {
      return outboxFirstRow.Metadata;
    }
    var inboxFirstRow = workBatch.InboxWork.FirstOrDefault();
    if (inboxFirstRow?.Metadata != null) {
      return inboxFirstRow.Metadata;
    }
    var perspectiveFirstRow = workBatch.PerspectiveWork.FirstOrDefault();
    return perspectiveFirstRow?.Metadata;
  }

  private static int _extractAckCount(Dictionary<string, JsonElement>? metadataRow, string key) {
    if (metadataRow != null && metadataRow.TryGetValue(key, out var value)) {
      return value.GetInt32();
    }
    return 0;
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

    await _invokeInboxLifecycleStagesAsync(workToProcess, receptorInvoker,
      LifecycleStage.PreInboxAsync, LifecycleStage.PreInboxInline, "PreInbox", cancellationToken);

    var orderedProcessor = new OrderedStreamProcessor();
    await orderedProcessor.ProcessInboxWorkAsync(
      workToProcess,
      processor: (_) => Task.FromResult(MessageProcessingStatus.EventStored),
      completionHandler: (msgId, status) => {
        _inboxCompletions.Add(new MessageCompletion { MessageId = msgId, Status = status });
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

    await _invokeInboxLifecycleStagesAsync(workToProcess, receptorInvoker,
      LifecycleStage.PostInboxAsync, LifecycleStage.PostInboxInline, "PostInbox", cancellationToken);
  }

  /// <summary>
  /// Invokes inbox lifecycle stages (async + inline) for all work items with error isolation.
  /// </summary>
  private async Task _invokeInboxLifecycleStagesAsync(
    List<InboxWork> workToProcess, IReceptorInvoker? receptorInvoker,
    LifecycleStage asyncStage, LifecycleStage inlineStage,
    string stageName, CancellationToken cancellationToken) {
    if (_lifecycleMessageDeserializer is null || receptorInvoker is null) {
      return;
    }

    foreach (var work in workToProcess) {
      try {
        var message = _lifecycleMessageDeserializer.DeserializeFromJsonElement(work.Envelope.Payload, work.MessageType);
        var typedEnvelope = work.Envelope.ReconstructWithPayload(message);
        var lifecycleContext = new LifecycleExecutionContext {
          CurrentStage = asyncStage,
          EventId = null,
          StreamId = null,
          LastProcessedEventId = null,
          MessageSource = MessageSource.Inbox,
          AttemptNumber = work.Attempts
        };

        await receptorInvoker.InvokeAsync(typedEnvelope, asyncStage, lifecycleContext, cancellationToken);
        await receptorInvoker.InvokeAsync(typedEnvelope, LifecycleStage.ImmediateAsync,
          lifecycleContext with { CurrentStage = LifecycleStage.ImmediateAsync }, cancellationToken);

        lifecycleContext = lifecycleContext with { CurrentStage = inlineStage };
        await receptorInvoker.InvokeAsync(typedEnvelope, inlineStage, lifecycleContext, cancellationToken);
        await receptorInvoker.InvokeAsync(typedEnvelope, LifecycleStage.ImmediateAsync,
          lifecycleContext with { CurrentStage = LifecycleStage.ImmediateAsync }, cancellationToken);
      } catch (Exception ex) {
        LogInboxLifecycleError(_logger, work.MessageId, stageName, ex);
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

  /// <summary>
  /// Maximum number of messages to include in a single bulk publish batch.
  /// Only applies when the transport supports the BulkPublish capability.
  /// Default: 50.
  /// </summary>
  /// <tests>tests/Whizbang.Core.Tests/Workers/WorkCoordinatorPublisherWorkerBulkPublishTests.cs:BulkPublish_MaxBatchSize_LimitsDrainCountAsync</tests>
  public int MaxBulkPublishBatchSize { get; set; } = 50;
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
