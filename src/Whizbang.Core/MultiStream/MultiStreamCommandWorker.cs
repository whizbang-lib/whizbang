// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Events.System;
using Whizbang.Core.Notifications;
using Whizbang.Core.Observability;
using Whizbang.Core.Security;
using Whizbang.Core.Workers;

namespace Whizbang.Core.MultiStream;

/// <summary>
/// Works this instance's share of multi-stream command runs. Each pass claims the pending streams this instance owns,
/// calls each one's handler under the scope its command was sent with, records every outcome, and publishes
/// <see cref="MultiStreamCommandCompleted"/> for each run that completed. A pass runs when a new run is announced, and at
/// least every <see cref="MultiStreamCommandOptions.PollInterval"/>; a full batch is followed by another pass at once.
/// </summary>
/// <docs>fundamentals/dispatcher/multi-stream-commands#worker</docs>
/// <tests>tests/Whizbang.Core.Tests/MultiStream/MultiStreamCommandWorkerTests.cs:RunOnceAsync_CallsTheHandlerOncePerClaimedStreamAndRecordsEachSuccessAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/MultiStream/MultiStreamCommandWorkerTests.cs:RunOnceAsync_AHandlerThatThrows_RecordsTheFailureAndWorksTheRestAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/MultiStream/MultiStreamCommandWorkerTests.cs:RunOnceAsync_CanceledMidBatch_RecordsTheStreamsAlreadyDoneAndStopsAsync</tests>
/// <tests>tests/Whizbang.Core.Component.Tests/MultiStream/MultiStreamCommandWorkerLoopTests.cs:Loop_ANewRunNotice_WakesTheWorkerAsync</tests>
public sealed partial class MultiStreamCommandWorker : BackgroundService, INotifySubscription {
  /// <summary>The channel a newly recorded run is announced on.</summary>
  public const string CHANNEL = "wh_multi_stream_work";

  private readonly IMultiStreamCommandStore _store;
  private readonly Dictionary<string, IMultiStreamCommandBinding> _bindings;
  private readonly IServiceScopeFactory _scopeFactory;
  private readonly IServiceInstanceProvider _instance;
  private readonly MultiStreamCommandOptions _options;
  private readonly ILogger<MultiStreamCommandWorker> _logger;
  private readonly IPartitionAssignmentSource _assignment;
  private readonly ISchemaReadyGate _schemaReady;
  private readonly TimeProvider _time;
  private readonly SemaphoreSlim _wake = new(0, 1);
  private DateTimeOffset? _nextPurgeAt;

  /// <summary>Creates the worker.</summary>
  /// <param name="store">Where runs are kept.</param>
  /// <param name="bindings">One binding per registered command type.</param>
  /// <param name="scopeFactory">Creates the scope each stream's handler is resolved from.</param>
  /// <param name="instance">This instance.</param>
  /// <param name="options">Batch, lease, attempts, polling and retention.</param>
  /// <param name="logger">Logger.</param>
  /// <param name="assignment">The cached partition assignment; the claim ranks this instance from it, as the work claim
  /// does, and ranks the live instances itself when it has no current version.</param>
  /// <param name="schemaReady">The loop waits for the schema before its first pass.</param>
  /// <param name="timeProvider">Clock for retention; the system clock when null.</param>
  public MultiStreamCommandWorker(
      IMultiStreamCommandStore store,
      IEnumerable<IMultiStreamCommandBinding> bindings,
      IServiceScopeFactory scopeFactory,
      IServiceInstanceProvider instance,
      IOptions<MultiStreamCommandOptions> options,
      ILogger<MultiStreamCommandWorker> logger,
      IPartitionAssignmentSource assignment,
      ISchemaReadyGate schemaReady,
      TimeProvider? timeProvider = null) {
    ArgumentNullException.ThrowIfNull(store);
    ArgumentNullException.ThrowIfNull(bindings);
    ArgumentNullException.ThrowIfNull(scopeFactory);
    ArgumentNullException.ThrowIfNull(instance);
    ArgumentNullException.ThrowIfNull(options);
    ArgumentNullException.ThrowIfNull(logger);
    ArgumentNullException.ThrowIfNull(assignment);
    ArgumentNullException.ThrowIfNull(schemaReady);
    _store = store;
    _bindings = bindings.ToDictionary(b => b.CommandType, StringComparer.Ordinal);
    _scopeFactory = scopeFactory;
    _instance = instance;
    _options = options.Value;
    _logger = logger;
    _assignment = assignment;
    _schemaReady = schemaReady;
    _time = timeProvider ?? TimeProvider.System;
  }

  /// <summary>Raised once for each run this instance completed, after its completion event was published (or failed to be).</summary>
  public event Action<MultiStreamRunCompletion>? OnRunCompleted;

  /// <summary>Raised after each pass of the loop with the number of streams it claimed; a pass that failed reports zero.</summary>
  public event Action<int>? OnPassCompleted;

  /// <inheritdoc/>
  public string ChannelName => CHANNEL;

  /// <inheritdoc/>
  public void OnNotification(string payload) => RequestImmediateRun();

  /// <summary>Wakes the loop for a pass now instead of at the next poll.</summary>
  public void RequestImmediateRun() {
    try {
      _wake.Release();
    } catch (SemaphoreFullException) {
      // A wake is already pending; the pass it starts sees this run too. Nothing was lost.
    }
  }

  /// <inheritdoc/>
  protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
    if (!_options.Enabled || !_store.IsConfigured || _bindings.Count == 0) {
      LogInactive(_logger, _options.Enabled, _store.IsConfigured, _bindings.Count);
      return;
    }
    _options.Validate();
    await _schemaReady.WaitForReadyAsync(stoppingToken).ConfigureAwait(false);
    while (!stoppingToken.IsCancellationRequested) {
      var claimed = 0;
      try {
        claimed = await RunOnceAsync(stoppingToken).ConfigureAwait(false);
        await PurgeIfDueAsync(stoppingToken).ConfigureAwait(false);
      } catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) {
        break;
      } catch (Exception ex) {
        // A failed pass is reported and the next one tries again; it never stops the host.
        LogPassFailed(_logger, ex);
      }
      OnPassCompleted?.Invoke(claimed);
      if (claimed < _options.BatchSize) {
        // A full batch may have more behind it, so only a short pass waits. Shutdown ends the wait and the loop.
        await ((Task)_wake.WaitAsync(_options.PollInterval, stoppingToken)).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
      }
    }
  }

  /// <summary>One pass: claim, work each stream, record the outcomes, announce completed runs.</summary>
  /// <param name="cancellationToken">The host's stopping token.</param>
  /// <returns>How many streams were claimed.</returns>
  internal async Task<int> RunOnceAsync(CancellationToken cancellationToken) {
    var instanceId = _instance.InstanceId;
    var version = await _assignment.ForClaimAsync(instanceId, cancellationToken).ConfigureAwait(false);
    var items = await _store.ClaimAsync(
      new MultiStreamClaim(instanceId, _options.BatchSize, _options.Lease, version), cancellationToken).ConfigureAwait(false);
    var outcomes = new List<MultiStreamOutcome>(items.Count);
    try {
      foreach (var item in items) {
        cancellationToken.ThrowIfCancellationRequested();
        outcomes.Add(await _workAsync(item, cancellationToken).ConfigureAwait(false));
      }
    } finally {
      // Recorded even when the host is stopping, so finished streams are not worked again; unfinished ones record nothing
      // and are offered again when their lease lapses.
      if (outcomes.Count > 0) {
        await _completeAsync(instanceId, outcomes).ConfigureAwait(false);
      }
    }
    return items.Count;
  }

  /// <summary>Deletes completed runs past their retention, at most once per <see cref="MultiStreamCommandOptions.PurgeInterval"/>.</summary>
  /// <param name="cancellationToken">Cancellation token.</param>
  internal async Task PurgeIfDueAsync(CancellationToken cancellationToken) {
    var now = _time.GetUtcNow();
    if (_nextPurgeAt is { } next && now < next) {
      return;
    }
    _nextPurgeAt = now + _options.PurgeInterval;
    var purged = await _store.PurgeCompletedAsync(now - _options.CompletedRunRetention, cancellationToken).ConfigureAwait(false);
    LogPurged(_logger, purged);
  }

  private async Task<MultiStreamOutcome> _workAsync(MultiStreamWorkItem item, CancellationToken cancellationToken) {
    if (!_bindings.TryGetValue(item.CommandType, out var binding)) {
      // A rolling update can leave a stream with an owner on an older build. Handing it back without counting the attempt
      // keeps a slow rollout from using up the stream's attempts; it is offered again after the retry delay.
      LogNoHandler(_logger, item.CommandType, item.RunId, item.StreamId, _options.NoHandlerRetryDelay);
      return MultiStreamOutcome.Release(item.RunId, item.StreamId, $"No handler for {item.CommandType} is registered on this instance.");
    }
    var previous = ScopeContextAccessor.CurrentContext;
    var previousInitiating = ScopeContextAccessor.CurrentInitiatingContext;
    // The command's scope, and only it: a run sent with none is worked with none, not under the worker's own.
    if (!string.IsNullOrEmpty(item.Scope)) {
      MessageParseDiagnostics.Record(item.RunId, MessageParseSource.MultiStreamRun, MessageParseKind.Scope);
    }
    ScopeContextAccessor.CurrentContext = MultiStreamScope.Restore(item.Scope);
    ScopeContextAccessor.CurrentInitiatingContext = null;
    try {
      var scope = _scopeFactory.CreateAsyncScope();
      await using (scope.ConfigureAwait(false)) {
        await binding.InvokeAsync(scope.ServiceProvider, item.Payload, item.StreamId, cancellationToken).ConfigureAwait(false);
      }
      return new MultiStreamOutcome(item.RunId, item.StreamId, null);
    } catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested) {
      LogStreamFailed(_logger, ex, item.CommandType, item.RunId, item.StreamId, item.Attempt);
      return new MultiStreamOutcome(item.RunId, item.StreamId, $"{ex.GetType().Name}: {ex.Message}");
    } finally {
      ScopeContextAccessor.CurrentContext = previous;
      ScopeContextAccessor.CurrentInitiatingContext = previousInitiating;
    }
  }

  private async Task _completeAsync(Guid instanceId, List<MultiStreamOutcome> outcomes) {
    var completions = await _store.CompleteAsync(
      instanceId, outcomes, _options.MaxAttempts, _options.NoHandlerRetryDelay, CancellationToken.None).ConfigureAwait(false);
    foreach (var completion in completions) {
      LogRunCompleted(_logger, completion.RunId, completion.CommandType, completion.Succeeded, completion.Failed);
      await _publishAsync(completion).ConfigureAwait(false);
      OnRunCompleted?.Invoke(completion);
    }
  }

  private async Task _publishAsync(MultiStreamRunCompletion completion) {
    try {
      var scope = _scopeFactory.CreateAsyncScope();
      await using (scope.ConfigureAwait(false)) {
        var dispatcher = scope.ServiceProvider.GetService<IDispatcher>();
        if (dispatcher is null) {
          return;
        }
        await dispatcher.AsSystem().ForAllTenants().PublishAsync(new MultiStreamCommandCompleted(
          completion.RunId, completion.CommandType, completion.StreamCount, completion.Succeeded, completion.Failed)).ConfigureAwait(false);
      }
    } catch (Exception ex) when (ex is not OperationCanceledException) {
      // The streams are worked and recorded; a lost announcement must not undo or repeat that.
      LogCompletionNotPublished(_logger, ex, completion.RunId);
    }
  }

  [LoggerMessage(EventId = 1, Level = LogLevel.Information,
    Message = "Multi-stream command worker is not running (enabled {Enabled}, store configured {StoreConfigured}, {HandlerCount} handlers).")]
  static partial void LogInactive(ILogger logger, bool enabled, bool storeConfigured, int handlerCount);

  [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "A multi-stream command pass failed; the next pass tries again.")]
  static partial void LogPassFailed(ILogger logger, Exception exception);

  [LoggerMessage(EventId = 3, Level = LogLevel.Warning,
    Message = "No handler for {CommandType} is registered on this instance; stream {StreamId} of run {RunId} is handed back without counting an attempt and offered again in {RetryDelay}.")]
  static partial void LogNoHandler(ILogger logger, string commandType, Guid runId, Guid streamId, TimeSpan retryDelay);

  [LoggerMessage(EventId = 4, Level = LogLevel.Warning,
    Message = "{CommandType} failed for stream {StreamId} of run {RunId} on attempt {Attempt}.")]
  static partial void LogStreamFailed(ILogger logger, Exception exception, string commandType, Guid runId, Guid streamId, int attempt);

  [LoggerMessage(EventId = 5, Level = LogLevel.Information,
    Message = "Multi-stream run {RunId} of {CommandType} completed: {Succeeded} succeeded, {Failed} failed.")]
  static partial void LogRunCompleted(ILogger logger, Guid runId, string commandType, int succeeded, int failed);

  [LoggerMessage(EventId = 6, Level = LogLevel.Warning, Message = "The completion of multi-stream run {RunId} could not be published.")]
  static partial void LogCompletionNotPublished(ILogger logger, Exception exception, Guid runId);

  [LoggerMessage(EventId = 7, Level = LogLevel.Debug, Message = "Purged {Count} completed multi-stream runs past their retention.")]
  static partial void LogPurged(ILogger logger, int count);
}
