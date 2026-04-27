using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Workers;

/// <summary>
/// Drains outbox work from <see cref="IWorkChannelWriter"/> and publishes each item to transport via
/// <see cref="IMessagePublishStrategy"/>. On success enqueues to <see cref="IOutboxCompletionChannel"/>;
/// on failure routes to <see cref="IFailureChannel"/>; on transport-not-ready re-buffers to the same
/// channel and queues a lease renewal via <see cref="ILeaseRenewalChannel"/>.
/// </summary>
/// <remarks>
/// Replaces the publish-half of the legacy <c>WorkCoordinatorPublisherWorker</c>. The claim-half lives
/// in <see cref="ClaimWorker"/>; the DB-flush half lives in <see cref="OutboxCompletionFlushWorker"/>.
/// Picks bulk vs singular path from <see cref="IMessagePublishStrategy.SupportsBulkPublish"/>.
/// </remarks>
/// <docs>fundamentals/work-coordinator/outbox-publish</docs>
public sealed partial class OutboxPublishWorker : BackgroundService {
  private readonly IServiceScopeFactory _scopeFactory;
  private readonly IMessagePublishStrategy _publishStrategy;
  private readonly IWorkChannelWriter _workChannelWriter;
  private readonly IOutboxCompletionChannel _outboxCompletionChannel;
  private readonly IFailureChannel _failureChannel;
  private readonly ILeaseRenewalChannel _leaseRenewalChannel;
  private readonly ISchemaReadyGate _schemaReadyGate;
  private readonly OutboxPublishWorkerOptions _options;
  private readonly ILogger<OutboxPublishWorker> _logger;

  /// <summary>Constructor.</summary>
  public OutboxPublishWorker(
    IServiceScopeFactory scopeFactory,
    IMessagePublishStrategy publishStrategy,
    IWorkChannelWriter workChannelWriter,
    IOutboxCompletionChannel outboxCompletionChannel,
    IFailureChannel failureChannel,
    ILeaseRenewalChannel leaseRenewalChannel,
    ISchemaReadyGate schemaReadyGate,
    IOptions<OutboxPublishWorkerOptions> options,
    ILogger<OutboxPublishWorker> logger) {
    _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
    _publishStrategy = publishStrategy ?? throw new ArgumentNullException(nameof(publishStrategy));
    _workChannelWriter = workChannelWriter ?? throw new ArgumentNullException(nameof(workChannelWriter));
    _outboxCompletionChannel = outboxCompletionChannel ?? throw new ArgumentNullException(nameof(outboxCompletionChannel));
    _failureChannel = failureChannel ?? throw new ArgumentNullException(nameof(failureChannel));
    _leaseRenewalChannel = leaseRenewalChannel ?? throw new ArgumentNullException(nameof(leaseRenewalChannel));
    _schemaReadyGate = schemaReadyGate ?? throw new ArgumentNullException(nameof(schemaReadyGate));
    _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
    _logger = logger ?? throw new ArgumentNullException(nameof(logger));
  }

  /// <inheritdoc />
  protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
    LogStarted(_logger, _publishStrategy.SupportsBulkPublish, _options.MaxBulkPublishBatchSize);

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
      if (_publishStrategy.SupportsBulkPublish) {
        await _bulkLoopAsync(stoppingToken);
      } else {
        await _singularLoopAsync(stoppingToken);
      }
    } catch (OperationCanceledException) {
      // expected on shutdown
    }

    LogStopped(_logger);
  }

  private async Task _singularLoopAsync(CancellationToken stoppingToken) {
    await foreach (var work in _workChannelWriter.Reader.ReadAllAsync(stoppingToken)) {
      try {
        if (!await _publishStrategy.IsReadyAsync(stoppingToken)) {
          await _handleTransportNotReadyAsync(work, stoppingToken);
          continue;
        }

        var result = await _publishStrategy.PublishAsync(work, stoppingToken);
        await _routeResultAsync(work, result, stoppingToken);
      } catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) {
        throw;
      } catch (Exception ex) {
        LogPublishFailed(_logger, work.MessageId, ex);
        _workChannelWriter.RemoveInFlight(work.MessageId);
        await _failureChannel.EnqueueAsync(WorkCategory.Outbox, new MessageFailure {
          MessageId = work.MessageId,
          CompletedStatus = work.Status,
          Error = ex.Message,
          Reason = MessageFailureReason.Unknown
        }, stoppingToken);
      }
    }
  }

  private async Task _bulkLoopAsync(CancellationToken stoppingToken) {
    var maxBatchSize = _options.MaxBulkPublishBatchSize;
    await foreach (var firstWork in _workChannelWriter.Reader.ReadAllAsync(stoppingToken)) {
      var batch = new List<OutboxWork>(maxBatchSize) { firstWork };
      while (batch.Count < maxBatchSize && _workChannelWriter.Reader.TryRead(out var more)) {
        batch.Add(more);
      }

      try {
        if (!await _publishStrategy.IsReadyAsync(stoppingToken)) {
          foreach (var w in batch) {
            await _handleTransportNotReadyAsync(w, stoppingToken);
          }
          continue;
        }

        var results = await _publishStrategy.PublishBatchAsync(batch, stoppingToken);
        foreach (var w in batch) {
          var r = results.FirstOrDefault(x => x.MessageId == w.MessageId)
            ?? new MessagePublishResult {
              MessageId = w.MessageId,
              Success = false,
              CompletedStatus = w.Status,
              Error = "No result returned from batch publish",
              Reason = MessageFailureReason.Unknown
            };
          await _routeResultAsync(w, r, stoppingToken);
        }
      } catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) {
        throw;
      } catch (Exception ex) {
        LogPublishFailed(_logger, batch[0].MessageId, ex);
        foreach (var w in batch) {
          _workChannelWriter.RemoveInFlight(w.MessageId);
          await _failureChannel.EnqueueAsync(WorkCategory.Outbox, new MessageFailure {
            MessageId = w.MessageId,
            CompletedStatus = w.Status,
            Error = ex.Message,
            Reason = MessageFailureReason.Unknown
          }, stoppingToken);
        }
      }
    }
  }

  private async Task _routeResultAsync(OutboxWork work, MessagePublishResult result, CancellationToken ct) {
    if (result.Success) {
      await _outboxCompletionChannel.EnqueueAsync(work.MessageId, ct);
    } else {
      _workChannelWriter.RemoveInFlight(work.MessageId);
      await _failureChannel.EnqueueAsync(WorkCategory.Outbox, new MessageFailure {
        MessageId = work.MessageId,
        CompletedStatus = result.CompletedStatus,
        Error = result.Error ?? "publish failed",
        Reason = result.Reason
      }, ct);
    }
  }

  private async Task _handleTransportNotReadyAsync(OutboxWork work, CancellationToken ct) {
    // Re-buffer to the same channel so it gets retried; queue a lease renewal so the
    // claim path doesn't reclaim it from under us while transport is unavailable.
    await _leaseRenewalChannel.EnqueueAsync(WorkCategory.Outbox, work.MessageId, ct);
    _workChannelWriter.TryWrite(work);
    // Small backoff so we don't busy-loop hammering IsReadyAsync when transport stays down.
    // Without this, a permanently-down transport with re-buffered work pegs a CPU core.
    var delayMs = _options.TransportNotReadyRetryDelayMilliseconds;
    if (delayMs > 0) {
      try {
        await Task.Delay(delayMs, ct);
      } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
        throw;
      }
    }
  }

  [LoggerMessage(EventId = 1, Level = LogLevel.Information,
    Message = "OutboxPublishWorker started: bulk={SupportsBulk}, maxBulkBatchSize={MaxBulkBatchSize}")]
  static partial void LogStarted(ILogger logger, bool supportsBulk, int maxBulkBatchSize);

  [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "OutboxPublishWorker stopped")]
  static partial void LogStopped(ILogger logger);

  [LoggerMessage(EventId = 3, Level = LogLevel.Information, Message = "OutboxPublishWorker disabled via options — publish loop skipped")]
  static partial void LogDisabled(ILogger logger);

  [LoggerMessage(EventId = 4, Level = LogLevel.Warning, Message = "OutboxPublishWorker publish failed for message {MessageId}; routing to failure channel")]
  static partial void LogPublishFailed(ILogger logger, Guid messageId, Exception ex);
}

/// <summary>Configuration for <see cref="OutboxPublishWorker"/>.</summary>
/// <docs>fundamentals/work-coordinator/configuration-reference</docs>
public sealed class OutboxPublishWorkerOptions {
  /// <summary>
  /// Killswitch. Set to <c>false</c> to disable the publish loop entirely; the worker stays
  /// registered but skips its <see cref="OutboxPublishWorker.ExecuteAsync"/> body. Default <c>true</c>.
  /// </summary>
  public bool Enabled { get; set; } = true;

  /// <summary>Maximum batch size per bulk-publish call. Default 100.</summary>
  public int MaxBulkPublishBatchSize { get; set; } = 100;

  /// <summary>
  /// When the transport reports not-ready, the worker re-buffers the message and waits
  /// this long before retrying so it doesn't busy-loop. Default 100 ms; 0 disables (busy-loop).
  /// </summary>
  public int TransportNotReadyRetryDelayMilliseconds { get; set; } = 100;
}
