// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Workers;

/// <summary>
/// The registered <see cref="OutboxFlushFailureCallback"/>: keeps outbox messages the batcher could not store, so
/// none is lost (#1871). It writes them to the dead-letter table through
/// <see cref="IWorkCoordinator.StoreOutboxDeadLettersAsync"/> (which also records the loss in <c>wh_log</c>), counts
/// them as dead-letter arrivals, and tells every <see cref="IOutboxFlushFailureObserver"/>.
/// </summary>
/// <remarks>
/// The dead-letter write takes a slot of the same <see cref="WorkCoordinatorGate"/> the store does, and resolves the
/// coordinator from a fresh scope the way the flush does. When the database is what failed, the dead-letter write
/// usually fails too: the batch is then reported to the observers with <see cref="OutboxFlushFailure.DeadLettered"/>
/// false, and the strategy logs every message id it held.
/// </remarks>
/// <docs>extending/internals/outbox-batch-strategy#when-a-flush-fails</docs>
/// <tests>tests/Whizbang.Core.Tests/Workers/OutboxFlushFailureCallbackTests.cs</tests>
public sealed partial class OutboxFlushFailureRecorder {
  private const string ABANDONED_AT_SHUTDOWN = "abandoned at shutdown: the stop's deadline passed before the messages were stored";
  private const string GATE_CALLER = "OutboxFlushDeadLetter";

  private readonly IServiceScopeFactory _scopeFactory;
  private readonly WorkCoordinatorGate? _gate;
  private readonly IServiceInstanceProvider _instanceProvider;
  private readonly IGenerationProvider _generationProvider;
  private readonly DeadLetterMetrics? _deadLetterMetrics;
  private readonly IOutboxFlushFailureObserver[] _observers;
  private readonly ILogger _logger;
  private readonly LogLevel _deadLetterLevel = LogLevel.Error;

  /// <summary>Creates the recorder.</summary>
  /// <param name="scopeFactory">Opens the scope the coordinator is resolved from.</param>
  /// <param name="gate">The process-wide cap on coordinator calls; null when none is registered.</param>
  /// <param name="instanceProvider">Names the instance the dead letters record.</param>
  /// <param name="generationProvider">The generation the dead letters carry, read by the recovery worker's generation replay.</param>
  /// <param name="deadLetterMetrics">Counts the dead letters written; null counts nothing.</param>
  /// <param name="observers">Told about every batch that was not stored.</param>
  /// <param name="logger">Logs a dead-letter write or an observer that failed.</param>
  /// <docs>extending/internals/outbox-batch-strategy#when-a-flush-fails</docs>
  /// <tests>tests/Whizbang.Core.Tests/Workers/OutboxFlushFailureCallbackTests.cs</tests>
  [System.Diagnostics.CodeAnalysis.SuppressMessage("Major Code Smell", "S107:Methods should not have too many parameters", Justification = "The recorder's collaborators; DI builds it.")]
  public OutboxFlushFailureRecorder(
      IServiceScopeFactory scopeFactory,
      WorkCoordinatorGate? gate,
      IServiceInstanceProvider instanceProvider,
      IGenerationProvider generationProvider,
      DeadLetterMetrics? deadLetterMetrics,
      IEnumerable<IOutboxFlushFailureObserver> observers,
      ILogger<OutboxFlushFailureRecorder> logger) {
    ArgumentNullException.ThrowIfNull(scopeFactory);
    ArgumentNullException.ThrowIfNull(instanceProvider);
    ArgumentNullException.ThrowIfNull(generationProvider);
    ArgumentNullException.ThrowIfNull(observers);
    ArgumentNullException.ThrowIfNull(logger);
    _scopeFactory = scopeFactory;
    _gate = gate;
    _instanceProvider = instanceProvider;
    _generationProvider = generationProvider;
    _deadLetterMetrics = deadLetterMetrics;
    _observers = [.. observers];
    _logger = logger;
  }

  /// <summary>
  /// Dead-letters the batch, counts it, and tells the observers. Never throws for a failed dead-letter write or a
  /// failing observer: both are logged, and the return value says whether the messages were kept.
  /// </summary>
  /// <param name="failure">What was not stored, and why.</param>
  /// <param name="cancellationToken">Cancellation.</param>
  /// <returns>True when the messages were written to the dead-letter table.</returns>
  /// <docs>extending/internals/outbox-batch-strategy#when-a-flush-fails</docs>
  /// <tests>tests/Whizbang.Core.Tests/Workers/OutboxFlushFailureCallbackTests.cs</tests>
  public async Task<bool> RecordAsync(OutboxFlushFailure failure, CancellationToken cancellationToken) {
    ArgumentNullException.ThrowIfNull(failure);
    var errorText = failure.Error?.ToString()
      ?? ABANDONED_AT_SHUTDOWN;
    var deadLettered = false;
    try {
      deadLettered = await _deadLetterAsync(failure, errorText, cancellationToken).ConfigureAwait(false);
    } catch (Exception ex) {
      LogDeadLetterWriteFailed(_logger, ex, failure.Messages.Count, failure.StreamId);
    }
    if (deadLettered && _deadLetterMetrics is not null) {
      _deadLetterMetrics.Added.Add(failure.Messages.Count,
        new KeyValuePair<string, object?>(DeadLetterMetrics.SOURCE_TABLE_TAG, DeadLetterSourceTable.OUTBOX),
        new KeyValuePair<string, object?>(DeadLetterMetrics.REASON_TAG, nameof(MessageFailureReason.OutboxStoreFailed)));
      _deadLetterMetrics.RecordArrival(DeadLetterSourceTable.OUTBOX, (int)MessageFailureReason.OutboxStoreFailed, errorText);
    }
    if (deadLettered) {
      _logDeadLettered(failure);
    }

    var reported = failure with { DeadLettered = deadLettered };
    foreach (var observer in _observers) {
      try {
        await observer.OnOutboxFlushFailedAsync(reported, cancellationToken).ConfigureAwait(false);
      } catch (Exception ex) {
        LogObserverFailed(_logger, ex, TypeNameFormatter.DisplayName(observer.GetType()), failure.Messages.Count, failure.StreamId);
      }
    }
    return deadLettered;
  }

  /// <summary>
  /// Logs the dead-letter record (#1901) for each message the batch held: the store failure is the last failure's
  /// exception, and a batch abandoned at shutdown, which has none, records why it was not stored.
  /// </summary>
  /// <tests>tests/Whizbang.Core.Tests/Workers/OutboxFlushFailureCallbackTests.cs:RecordAsync_CoordinatorDeadLetters_LogsOneDeadLetterRecordPerMessageAtErrorAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Workers/OutboxFlushFailureCallbackTests.cs:RecordAsync_AbandonedWithoutAnError_RecordSaysWhyAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Workers/OutboxFlushFailureCallbackTests.cs:RecordAsync_DeadLetterWriteFails_LogsNoDeadLetterRecordAsync</tests>
  private void _logDeadLettered(OutboxFlushFailure failure) {
    var lastError = failure.Error?.Message ?? ABANDONED_AT_SHUTDOWN;
    foreach (var message in failure.Messages) {
      DeadLetterLog.MessageDeadLettered(_logger, _deadLetterLevel, failure.Error, message.MessageId, message.MessageType,
        nameof(MessageFailureReason.OutboxStoreFailed), lastError);
    }
  }

  private async Task<bool> _deadLetterAsync(OutboxFlushFailure failure, string errorText, CancellationToken cancellationToken) {
    var request = new OutboxDeadLetterRequest(
      MessageFailureReason.OutboxStoreFailed,
      errorText,
      failure.Attempts,
      _instanceProvider.InstanceId,
      _generationProvider.GetGeneration());
    using var scope = _scopeFactory.CreateScope();
    var coordinator = scope.ServiceProvider.GetRequiredService<IWorkCoordinator>();
    using (_gate is null ? default : await _gate.AcquireAsync(cancellationToken, GATE_CALLER).ConfigureAwait(false)) {
      return await coordinator.StoreOutboxDeadLettersAsync([.. failure.Messages], request, cancellationToken).ConfigureAwait(false);
    }
  }

  [LoggerMessage(Level = LogLevel.Error,
    Message = "Writing {BatchSize} outbox message(s) for stream {StreamKey} that were not stored to wh_dead_letters failed")]
  private static partial void LogDeadLetterWriteFailed(ILogger logger, Exception exception, int batchSize, Guid streamKey);

  [LoggerMessage(Level = LogLevel.Error,
    Message = "Outbox flush failure observer {Observer} failed for {BatchSize} message(s) of stream {StreamKey}; the other observers are still told")]
  private static partial void LogObserverFailed(ILogger logger, Exception exception, string observer, int batchSize, Guid streamKey);
}
