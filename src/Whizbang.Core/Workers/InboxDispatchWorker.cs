using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Workers;

/// <summary>
/// Drains orphan inbox work from <see cref="IInboxChannelWriter"/> and packages each item as a
/// <see cref="HandlerCommitRequest"/> routed to <see cref="IInboxHandlerCommitChannel"/>, which
/// the <see cref="InboxHandlerWorker"/> commits via <c>commit_handler_batch</c>.
/// </summary>
/// <remarks>
/// <para>
/// This worker handles ORPHAN inbox messages — rows whose lease expired or whose original
/// processing was interrupted. Live receive-from-transport flow stays in
/// <c>TransportConsumerWorker</c> (which invokes the receptor and stores the inbox row in the
/// same scope). When a row is later picked up by <see cref="ClaimWorker"/> and routed onto
/// <see cref="IInboxChannelWriter"/>, it lands here for re-completion.
/// </para>
/// <para>
/// MaxInboxAttempts purge: when configured, work whose <c>Attempts</c> meets or exceeds the
/// threshold is dead-lettered with a terminal completion (status |= Published) instead of
/// being re-tried.
/// </para>
/// <para>
/// Lifecycle stage invocation (Pre/Post Inbox Inline/Detached, PostAllPerspectives,
/// PostLifecycle) is deferred to a follow-up cycle. The legacy publisher only fires those
/// when <c>ILifecycleMessageDeserializer</c> + <c>IReceptorInvoker</c> are both present;
/// when absent it skips them — v1 of this worker does the same (skips). Add lifecycle
/// invocation alongside ECommerce sample fixture rewiring when integration tests demand it.
/// </para>
/// </remarks>
/// <docs>fundamentals/work-coordinator/inbox-dispatch</docs>
public sealed partial class InboxDispatchWorker : BackgroundService {
  private readonly IServiceInstanceProvider _instanceProvider;
  private readonly IInboxChannelWriter _inboxChannelWriter;
  private readonly IInboxHandlerCommitChannel _handlerCommitChannel;
  private readonly IFailureChannel _failureChannel;
  private readonly ISchemaReadyGate _schemaReadyGate;
  private readonly InboxDispatchWorkerOptions _options;
  private readonly ILogger<InboxDispatchWorker> _logger;

  /// <summary>Constructor.</summary>
  public InboxDispatchWorker(
    IServiceInstanceProvider instanceProvider,
    IInboxChannelWriter inboxChannelWriter,
    IInboxHandlerCommitChannel handlerCommitChannel,
    IFailureChannel failureChannel,
    ISchemaReadyGate schemaReadyGate,
    IOptions<InboxDispatchWorkerOptions> options,
    ILogger<InboxDispatchWorker> logger) {
    _instanceProvider = instanceProvider ?? throw new ArgumentNullException(nameof(instanceProvider));
    _inboxChannelWriter = inboxChannelWriter ?? throw new ArgumentNullException(nameof(inboxChannelWriter));
    _handlerCommitChannel = handlerCommitChannel ?? throw new ArgumentNullException(nameof(handlerCommitChannel));
    _failureChannel = failureChannel ?? throw new ArgumentNullException(nameof(failureChannel));
    _schemaReadyGate = schemaReadyGate ?? throw new ArgumentNullException(nameof(schemaReadyGate));
    _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
    _logger = logger ?? throw new ArgumentNullException(nameof(logger));
  }

  /// <inheritdoc />
  protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
    LogStarted(_logger, _options.MaxInboxAttempts ?? -1);

    if (!_options.Enabled) {
      LogDisabled(_logger);
      try { await Task.Delay(Timeout.Infinite, stoppingToken); } catch (OperationCanceledException) { }
      LogStopped(_logger);
      return;
    }

    try {
      await _schemaReadyGate.WaitForReadyAsync(stoppingToken);
    } catch (OperationCanceledException) {
      return;
    }

    try {
      await foreach (var work in _inboxChannelWriter.Reader.ReadAllAsync(stoppingToken)) {
        try {
          await _processOneAsync(work, stoppingToken);
        } catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) {
          throw;
        } catch (Exception ex) {
          LogDispatchError(_logger, work.MessageId, ex);
          _inboxChannelWriter.RemoveInFlight(work.MessageId);
          await _failureChannel.EnqueueAsync(WorkCategory.Inbox, new MessageFailure {
            MessageId = work.MessageId,
            CompletedStatus = work.Status,
            Error = ex.Message,
            Reason = MessageFailureReason.Unknown
          }, stoppingToken);
        }
      }
    } catch (OperationCanceledException) {
      // expected on shutdown
    }

    LogStopped(_logger);
  }

  private async Task _processOneAsync(InboxWork work, CancellationToken ct) {
    var maxAttempts = _options.MaxInboxAttempts;
    if (maxAttempts.HasValue && work.Attempts >= maxAttempts.Value) {
      // Dead-letter: terminal completion (status |= Published). Mirrors legacy publisher's
      // _processInboxWorkAsync purge branch (lines 1252-1259). The DB row is still removed
      // by commit_handler_batch — the difference is no retry will re-claim it.
      LogDeadLettered(_logger, work.MessageId, work.Attempts, maxAttempts.Value);
      var terminalRequest = _buildCommitRequest(work, status: (int)(work.Status | MessageProcessingStatus.Published));
      await _handlerCommitChannel.EnqueueAsync(terminalRequest, ct);
      return;
    }

    var commitRequest = _buildCommitRequest(work, status: (int)MessageProcessingStatus.EventStored);
    await _handlerCommitChannel.EnqueueAsync(commitRequest, ct);
  }

  private HandlerCommitRequest _buildCommitRequest(InboxWork work, int status)
    => new(
      HandlerId: work.MessageId,                 // one handler per orphan; HandlerId == MessageId is fine
      InstanceId: _instanceProvider.InstanceId,
      ServiceName: _instanceProvider.ServiceName,
      HostName: _instanceProvider.HostName,
      ProcessId: _instanceProvider.ProcessId,
      PartitionCount: _options.PartitionCount,
      InboxCompletion: new HandlerInboxCompletion(work.MessageId, status),
      NewOutboxMessages: null,
      NewInboxMessages: null);

  [LoggerMessage(EventId = 1, Level = LogLevel.Information,
    Message = "InboxDispatchWorker started: maxInboxAttempts={MaxInboxAttempts}")]
  static partial void LogStarted(ILogger logger, int maxInboxAttempts);

  [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "InboxDispatchWorker stopped")]
  static partial void LogStopped(ILogger logger);

  [LoggerMessage(EventId = 3, Level = LogLevel.Information, Message = "InboxDispatchWorker disabled via options — dispatch loop skipped")]
  static partial void LogDisabled(ILogger logger);

  [LoggerMessage(EventId = 4, Level = LogLevel.Warning, Message = "InboxDispatchWorker dispatch failed for message {MessageId}; routing to failure channel")]
  static partial void LogDispatchError(ILogger logger, Guid messageId, Exception ex);

  [LoggerMessage(EventId = 5, Level = LogLevel.Warning, Message = "InboxDispatchWorker dead-lettered message {MessageId}: attempts={Attempts} >= max={MaxAttempts}")]
  static partial void LogDeadLettered(ILogger logger, Guid messageId, int attempts, int maxAttempts);
}

/// <summary>Configuration for <see cref="InboxDispatchWorker"/>.</summary>
/// <docs>fundamentals/work-coordinator/configuration-reference</docs>
public sealed class InboxDispatchWorkerOptions {
  /// <summary>
  /// Killswitch. Set to <c>false</c> to disable the dispatch loop entirely. Default <c>true</c>.
  /// </summary>
  public bool Enabled { get; set; } = true;

  /// <summary>
  /// Dead-letter threshold. When set, work whose <see cref="InboxWork.Attempts"/> meets or
  /// exceeds this value is committed with a terminal status (no further retries) instead of
  /// being re-processed. Null disables the threshold (retry forever — production default
  /// matches legacy: prefer null and let alerting catch poison messages). Default <c>null</c>.
  /// </summary>
  public int? MaxInboxAttempts { get; set; }

  /// <summary>
  /// Modulo partition count carried into <see cref="HandlerCommitRequest"/>. Must match the
  /// rest of the work-pump pipeline (default 10000, matches <c>ClaimWorkerOptions</c>).
  /// </summary>
  public int PartitionCount { get; set; } = 10_000;
}
