using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
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
public partial class WorkCoordinatorPublisherWorker(
  IServiceInstanceProvider instanceProvider,
  IServiceScopeFactory scopeFactory,
  IMessagePublishStrategy publishStrategy,
  IWorkChannelWriter workChannelWriter,
  IOptions<WorkCoordinatorPublisherOptions> options,
  IDatabaseReadinessCheck? databaseReadinessCheck = null,
  ILifecycleInvoker? lifecycleInvoker = null,
  ILifecycleMessageDeserializer? lifecycleMessageDeserializer = null,
  ILogger<WorkCoordinatorPublisherWorker>? logger = null
) : BackgroundService {
  private readonly IServiceInstanceProvider _instanceProvider = instanceProvider ?? throw new ArgumentNullException(nameof(instanceProvider));
  private readonly IServiceScopeFactory _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
  private readonly IMessagePublishStrategy _publishStrategy = publishStrategy ?? throw new ArgumentNullException(nameof(publishStrategy));
  private readonly IWorkChannelWriter _workChannelWriter = workChannelWriter ?? throw new ArgumentNullException(nameof(workChannelWriter));
  private readonly IDatabaseReadinessCheck _databaseReadinessCheck = databaseReadinessCheck ?? new DefaultDatabaseReadinessCheck();
  private readonly ILifecycleInvoker? _lifecycleInvoker = lifecycleInvoker;
  private readonly ILifecycleMessageDeserializer? _lifecycleMessageDeserializer = lifecycleMessageDeserializer;
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
    } catch (Exception ex) when (ex is not OperationCanceledException) {
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
        var isReady = await _publishStrategy.IsReadyAsync(stoppingToken);
        LogTransportReadinessCheck(_logger, isReady);
        if (!isReady) {
          // Transport not ready - renew lease to buffer message
          _leaseRenewals.Add(work.MessageId);
          Interlocked.Increment(ref _consecutiveNotReadyChecks);
          Interlocked.Increment(ref _totalLeaseRenewals);
          Interlocked.Increment(ref _totalBufferedMessages);

          // Log at Information level (important operational event)
          LogTransportNotReadyBuffering(_logger, work.MessageId, work.Destination);

          // Warn if transport has been continuously unavailable
          if (_consecutiveNotReadyChecks > 10) {
            LogTransportNotReadyWarning(_logger, _consecutiveNotReadyChecks);
          }

          continue;
        }

        // Transport is ready - reset consecutive counter
        Interlocked.Exchange(ref _consecutiveNotReadyChecks, 0);

        // PreOutbox lifecycle stages (before publishing to transport)
        if (_lifecycleInvoker is not null && _lifecycleMessageDeserializer is not null) {
          var message = _lifecycleMessageDeserializer.DeserializeFromJsonElement(work.Envelope.Payload, work.MessageType);

          var lifecycleContext = new LifecycleExecutionContext {
            CurrentStage = LifecycleStage.PreOutboxAsync,
            EventId = null,
            StreamId = null,
            LastProcessedEventId = null,
            MessageSource = MessageSource.Outbox,
            AttemptNumber = work.Attempts
          };

          await _lifecycleInvoker.InvokeAsync(message, LifecycleStage.PreOutboxAsync, lifecycleContext, stoppingToken);

          lifecycleContext = lifecycleContext with { CurrentStage = LifecycleStage.PreOutboxInline };
          await _lifecycleInvoker.InvokeAsync(message, LifecycleStage.PreOutboxInline, lifecycleContext, stoppingToken);
        }

        // Publish via strategy
        LogAboutToPublishMessage(_logger, work.MessageId, work.Destination);
        var result = await _publishStrategy.PublishAsync(work, stoppingToken);
        LogPublishResult(_logger, work.MessageId, result.Success, result.CompletedStatus);

        // PostOutbox lifecycle stages (after publishing to transport)
        if (_lifecycleInvoker is not null && _lifecycleMessageDeserializer is not null) {
          var message = _lifecycleMessageDeserializer.DeserializeFromJsonElement(work.Envelope.Payload, work.MessageType);

          var lifecycleContext = new LifecycleExecutionContext {
            CurrentStage = LifecycleStage.PostOutboxAsync,
            EventId = null,
            StreamId = null,
            LastProcessedEventId = null,
            MessageSource = MessageSource.Outbox,
            AttemptNumber = work.Attempts
          };

          await _lifecycleInvoker.InvokeAsync(message, LifecycleStage.PostOutboxAsync, lifecycleContext, stoppingToken);

          lifecycleContext = lifecycleContext with { CurrentStage = LifecycleStage.PostOutboxInline };
          await _lifecycleInvoker.InvokeAsync(message, LifecycleStage.PostOutboxInline, lifecycleContext, stoppingToken);
        }

        // Collect results
        if (result.Success) {
          _completions.Add(new MessageCompletion {
            MessageId = work.MessageId,
            Status = result.CompletedStatus
          });
        } else {
          // For retryable failures, renew lease instead of marking as failed
          // This allows the message to be re-claimed and retried
          if (result.Reason == MessageFailureReason.TransportException) {
            _leaseRenewals.Add(work.MessageId);

            LogTransportFailureRenewingLease(_logger, work.MessageId, work.Destination, result.Error ?? "Unknown error");
          } else {
            // Non-retryable failures (serialization, validation, etc.) - mark as failed
            _failures.Add(new MessageFailure {
              MessageId = work.MessageId,
              CompletedStatus = result.CompletedStatus,
              Error = result.Error ?? "Unknown error",
              Reason = result.Reason
            });

            LogFailedToPublishMessage(_logger, work.MessageId, work.Destination, result.Error ?? "Unknown error", result.Reason);
          }
        }
      } catch (Exception ex) when (ex is not OperationCanceledException) {
        LogUnexpectedErrorPublishing(_logger, work.MessageId, ex);

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

    // 2. Extract actual completion data for ProcessWorkBatchAsync
    var completionsToSend = pendingCompletions.Select(tc => tc.Completion).ToArray();
    var failuresToSend = pendingFailures.Select(tc => tc.Completion).ToArray();
    var leaseRenewalsToSend = pendingLeaseRenewals.Select(tc => tc.Completion).ToArray();

    // 3. Mark as Sent BEFORE calling ProcessWorkBatchAsync
    var sentAt = DateTimeOffset.UtcNow;
    _completions.MarkAsSent(pendingCompletions, sentAt);
    _failures.MarkAsSent(pendingFailures, sentAt);
    _leaseRenewals.MarkAsSent(pendingLeaseRenewals, sentAt);

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
      workBatch = await workCoordinator.ProcessWorkBatchAsync(
        _instanceProvider.InstanceId,
        _instanceProvider.ServiceName,
        _instanceProvider.HostName,
        _instanceProvider.ProcessId,
        metadata: _options.InstanceMetadata,
        outboxCompletions: completionsToSend,
        outboxFailures: failuresToSend,
        inboxCompletions: [],
        inboxFailures: [],
        receptorCompletions: [],  // TODO: Add receptor processing support
        receptorFailures: [],
        perspectiveCompletions: [],  // TODO: Add perspective checkpoint support
        perspectiveFailures: [],
        newOutboxMessages: [],  // Not used in publisher worker (dispatcher handles new messages)
        newInboxMessages: [],   // Not used in publisher worker (consumer handles new messages)
        renewOutboxLeaseIds: leaseRenewalsToSend,
        renewInboxLeaseIds: [],
        flags: _options.DebugMode ? WorkBatchFlags.DebugMode : WorkBatchFlags.None,
        partitionCount: _options.PartitionCount,
        leaseSeconds: _options.LeaseSeconds,
        staleThresholdSeconds: _options.StaleThresholdSeconds,
        cancellationToken: cancellationToken
      );
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

    // Check outbox work first (priority for publisher worker)
    var outboxFirstRow = workBatch.OutboxWork.FirstOrDefault();
    if (outboxFirstRow?.Metadata != null) {
      if (outboxFirstRow.Metadata.TryGetValue("outbox_completions_processed", out var compCount)) {
        completionsProcessed = compCount.GetInt32();
      }
      if (outboxFirstRow.Metadata.TryGetValue("outbox_failures_processed", out var failCount)) {
        failuresProcessed = failCount.GetInt32();
      }
      if (outboxFirstRow.Metadata.TryGetValue("outbox_lease_renewals_processed", out var renewCount)) {
        leaseRenewalsProcessed = renewCount.GetInt32();
      }
    } else {
      // Check inbox work if no outbox work
      var inboxFirstRow = workBatch.InboxWork.FirstOrDefault();
      if (inboxFirstRow?.Metadata != null) {
        if (inboxFirstRow.Metadata.TryGetValue("outbox_completions_processed", out var compCount)) {
          completionsProcessed = compCount.GetInt32();
        }
        if (inboxFirstRow.Metadata.TryGetValue("outbox_failures_processed", out var failCount)) {
          failuresProcessed = failCount.GetInt32();
        }
        if (inboxFirstRow.Metadata.TryGetValue("outbox_lease_renewals_processed", out var renewCount)) {
          leaseRenewalsProcessed = renewCount.GetInt32();
        }
      } else {
        // Check perspective work if no outbox/inbox work
        var perspectiveFirstRow = workBatch.PerspectiveWork.FirstOrDefault();
        if (perspectiveFirstRow?.Metadata != null) {
          if (perspectiveFirstRow.Metadata.TryGetValue("outbox_completions_processed", out var compCount)) {
            completionsProcessed = compCount.GetInt32();
          }
          if (perspectiveFirstRow.Metadata.TryGetValue("outbox_failures_processed", out var failCount)) {
            failuresProcessed = failCount.GetInt32();
          }
          if (perspectiveFirstRow.Metadata.TryGetValue("outbox_lease_renewals_processed", out var renewCount)) {
            leaseRenewalsProcessed = renewCount.GetInt32();
          }
        }
      }
    }

    // 6. Mark as Acknowledged based on counts from SQL
    _completions.MarkAsAcknowledged(completionsProcessed);
    _failures.MarkAsAcknowledged(failuresProcessed);
    _leaseRenewals.MarkAsAcknowledged(leaseRenewalsProcessed);

    // 7. Clear only Acknowledged items
    _completions.ClearAcknowledged();
    _failures.ClearAcknowledged();
    _leaseRenewals.ClearAcknowledged();

    // 8. Reset stale items (sent but not acknowledged for > timeout) back to Pending
    _completions.ResetStale(DateTimeOffset.UtcNow);
    _failures.ResetStale(DateTimeOffset.UtcNow);
    _leaseRenewals.ResetStale(DateTimeOffset.UtcNow);

    // Log a summary of message processing activity
    int totalActivity = completionsToSend.Length + failuresToSend.Length + leaseRenewalsToSend.Length + workBatch.OutboxWork.Count + workBatch.InboxWork.Count;
    if (totalActivity > 0) {
      LogMessageBatchSummary(
        _logger,
        completionsToSend.Length,
        failuresToSend.Length,
        leaseRenewalsToSend.Length,
        workBatch.OutboxWork.Count,
        workBatch.InboxWork.Count,
        workBatch.InboxWork.Count  // All inbox currently marked as failed (not yet implemented)
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

    // Process inbox work
    // TODO: Implement inbox processing - requires deserializing to typed messages and invoking receptors
    // For now, mark as failed to prevent infinite retry loops
    if (workBatch.InboxWork.Count > 0) {
      foreach (var inboxMessage in workBatch.InboxWork) {
        _failures.Add(new MessageFailure {
          MessageId = inboxMessage.MessageId,
          CompletedStatus = inboxMessage.Status,  // Preserve what was already completed
          Error = "Inbox processing not yet implemented",
          Reason = MessageFailureReason.Unknown
        });
      }
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
    Level = LogLevel.Warning,
    Message = "DIAGNOSTIC: PublisherLoop started, waiting for work from channel..."
  )]
  static partial void LogPublisherLoopStarted(ILogger logger);

  [LoggerMessage(
    EventId = 11,
    Level = LogLevel.Warning,
    Message = "DIAGNOSTIC: PublisherLoop received work from channel: MessageId={MessageId}, Destination={Destination}"
  )]
  static partial void LogPublisherLoopReceivedWork(ILogger logger, Guid messageId, string destination);

  [LoggerMessage(
    EventId = 12,
    Level = LogLevel.Warning,
    Message = "DIAGNOSTIC: Transport readiness check: IsReady={IsReady}"
  )]
  static partial void LogTransportReadinessCheck(ILogger logger, bool isReady);

  [LoggerMessage(
    EventId = 13,
    Level = LogLevel.Information,
    Message = "Transport not ready, buffering message {MessageId} (destination: {Destination})"
  )]
  static partial void LogTransportNotReadyBuffering(ILogger logger, Guid messageId, string destination);

  [LoggerMessage(
    EventId = 14,
    Level = LogLevel.Warning,
    Message = "Transport not ready for {ConsecutiveCount} consecutive messages. Messages are being buffered with lease renewal."
  )]
  static partial void LogTransportNotReadyWarning(ILogger logger, int consecutiveCount);

  [LoggerMessage(
    EventId = 15,
    Level = LogLevel.Warning,
    Message = "DIAGNOSTIC: About to publish message {MessageId} to {Destination}"
  )]
  static partial void LogAboutToPublishMessage(ILogger logger, Guid messageId, string destination);

  [LoggerMessage(
    EventId = 16,
    Level = LogLevel.Warning,
    Message = "DIAGNOSTIC: Publish result for {MessageId}: Success={Success}, Status={Status}"
  )]
  static partial void LogPublishResult(ILogger logger, Guid messageId, bool success, MessageProcessingStatus status);

  [LoggerMessage(
    EventId = 17,
    Level = LogLevel.Warning,
    Message = "Transport failure for message {MessageId} to {Destination}: {Error}. Renewing lease for retry."
  )]
  static partial void LogTransportFailureRenewingLease(ILogger logger, Guid messageId, string destination, string error);

  [LoggerMessage(
    EventId = 18,
    Level = LogLevel.Error,
    Message = "Failed to publish outbox message {MessageId} to {Destination}: {Error} (Reason: {Reason})"
  )]
  static partial void LogFailedToPublishMessage(ILogger logger, Guid messageId, string destination, string error, MessageFailureReason reason);

  [LoggerMessage(
    EventId = 19,
    Level = LogLevel.Error,
    Message = "Unexpected error publishing outbox message {MessageId}"
  )]
  static partial void LogUnexpectedErrorPublishing(ILogger logger, Guid messageId, Exception ex);

  [LoggerMessage(
    EventId = 20,
    Level = LogLevel.Information,
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
