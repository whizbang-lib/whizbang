using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Whizbang.Transports.AzureServiceBus;

/// <summary>
/// Consume-liveness guard for Azure Service Bus subscriptions. Detects the
/// "alive but deaf" failure mode: a receiver whose underlying AMQP link has dropped
/// without surfacing any error — no <c>ProcessErrorAsync</c> callback fires, nothing is
/// dead-lettered, process-level health checks stay green — while messages accumulate
/// unconsumed on the subscription. Session receivers are particularly exposed: a dropped
/// session receiver strands every message routed to its subscription indefinitely.
/// </summary>
/// <remarks>
/// <para>
/// Detection combines two signals so genuinely idle services are never restarted:
/// a subscription must have been <b>silent</b> (no received messages) past
/// <see cref="AzureServiceBusOptions.ReceiveLivenessSilenceThreshold"/> AND have a
/// <b>non-empty backlog</b> reported by the admin plane. Silence with an empty backlog
/// is healthy idle and re-baselines the silence window.
/// </para>
/// <para>
/// On detection the recovery callback is invoked once per sweep (recovery re-establishes
/// every subscription, so one detection suffices) and all silence windows are reset to
/// give the new receivers a fresh start.
/// </para>
/// </remarks>
/// <docs>messaging/transports/azure-service-bus#receive-liveness</docs>
/// <tests>tests/Whizbang.Transports.AzureServiceBus.Tests/ReceiveLivenessWatchdogTests.cs</tests>
/// <tests>tests/Whizbang.Transports.AzureServiceBus.Tests/AzureServiceBusTransportLivenessWiringTests.cs</tests>
internal sealed partial class ReceiveLivenessWatchdog : IAsyncDisposable {
  private readonly AzureServiceBusOptions _options;
  private readonly Func<string, string, CancellationToken, Task<long>> _backlogProbeAsync;
  private readonly Func<CancellationToken, Task> _recoverAsync;
  private readonly TimeProvider _timeProvider;
  private readonly ILogger _logger;
  private readonly ConcurrentDictionary<(string Topic, string Subscription), DateTimeOffset> _lastReceiveActivity = new();
  private readonly CancellationTokenSource _stopTokenSource = new();
  private readonly Lock _startLock = new();
  private Task? _loopTask;
  private bool _disposed;

  /// <summary>
  /// Creates a watchdog over the given probe/recovery callbacks. Monitoring begins when
  /// subscriptions are <see cref="Track"/>ed and <see cref="Start"/> is called.
  /// </summary>
  /// <param name="options">Transport options carrying the probe interval and silence threshold.</param>
  /// <param name="backlogProbeAsync">Returns the active message count for a (topic, subscription) pair.</param>
  /// <param name="recoverAsync">Re-establishes the transport's subscriptions (same path as connection-error recovery).</param>
  /// <param name="timeProvider">Time source — injected so tests control the clock.</param>
  /// <param name="logger">Diagnostic logger.</param>
  public ReceiveLivenessWatchdog(
    AzureServiceBusOptions options,
    Func<string, string, CancellationToken, Task<long>> backlogProbeAsync,
    Func<CancellationToken, Task> recoverAsync,
    TimeProvider timeProvider,
    ILogger logger) {
    ArgumentNullException.ThrowIfNull(options);
    ArgumentNullException.ThrowIfNull(backlogProbeAsync);
    ArgumentNullException.ThrowIfNull(recoverAsync);
    ArgumentNullException.ThrowIfNull(timeProvider);
    ArgumentNullException.ThrowIfNull(logger);

    _options = options;
    _backlogProbeAsync = backlogProbeAsync;
    _recoverAsync = recoverAsync;
    _timeProvider = timeProvider;
    _logger = logger;
  }

  /// <summary>
  /// Registers a subscription for liveness monitoring with a fresh silence window.
  /// Called on every (re-)subscribe — a re-established subscription gets a new baseline.
  /// </summary>
  public void Track(string topicName, string subscriptionName) =>
    _lastReceiveActivity[(topicName, subscriptionName)] = _timeProvider.GetUtcNow();

  /// <summary>
  /// Records a received message — proof the subscription's receiver is alive.
  /// </summary>
  public void RecordActivity(string topicName, string subscriptionName) =>
    _lastReceiveActivity[(topicName, subscriptionName)] = _timeProvider.GetUtcNow();

  /// <summary>
  /// One liveness sweep over all tracked subscriptions. Invoked by the periodic loop;
  /// exposed so tests drive sweeps deterministically without the timer.
  /// </summary>
  public async Task ProbeAsync(CancellationToken cancellationToken = default) {
    var now = _timeProvider.GetUtcNow();

    // Ordinal ordering keeps sweep order deterministic regardless of culture.
    var entries = _lastReceiveActivity
      .OrderBy(e => e.Key.Topic, StringComparer.Ordinal)
      .ThenBy(e => e.Key.Subscription, StringComparer.Ordinal);

    foreach (var ((topicName, subscriptionName), lastActivity) in entries) {
      var silence = now - lastActivity;
      if (silence < _options.ReceiveLivenessSilenceThreshold) {
        continue;
      }

      long backlog;
      try {
        backlog = await _backlogProbeAsync(topicName, subscriptionName, cancellationToken);
      } catch (Exception ex) {
        // Inconclusive: an unreachable admin plane is not evidence of a stalled receiver.
        LogBacklogProbeFailed(_logger, ex, topicName, subscriptionName);
        continue;
      }

      if (backlog == 0) {
        // Healthy idle: nothing to consume explains the silence. Re-baseline so quiet
        // services do not query the admin plane on every subsequent sweep.
        _lastReceiveActivity[(topicName, subscriptionName)] = now;
        continue;
      }

      LogStallDetected(_logger, topicName, subscriptionName, silence.TotalSeconds, backlog);

      await _recoverAsync(cancellationToken);

      // Recovery re-establishes every subscription; reset all windows so the new
      // receivers get a fresh start and this sweep stops (one recovery per sweep).
      var recoveredAt = _timeProvider.GetUtcNow();
      foreach (var key in _lastReceiveActivity.Keys) {
        _lastReceiveActivity[key] = recoveredAt;
      }
      return;
    }
  }

  /// <summary>
  /// Starts the periodic sweep loop. Idempotent — a second call is a no-op.
  /// </summary>
  public void Start() {
    lock (_startLock) {
      if (_loopTask is not null || _disposed) {
        return;
      }
      _loopTask = _runLoopAsync(_stopTokenSource.Token);
    }
  }

  private async Task _runLoopAsync(CancellationToken stopToken) {
    using var timer = new PeriodicTimer(_options.ReceiveLivenessProbeInterval, _timeProvider);
    try {
      while (await timer.WaitForNextTickAsync(stopToken)) {
        try {
          await ProbeAsync(stopToken);
        } catch (OperationCanceledException) when (stopToken.IsCancellationRequested) {
          return;
        } catch (Exception ex) {
          // The loop must survive any sweep failure — a watchdog that dies on first
          // error reproduces the very blind spot it exists to remove.
          LogSweepFailed(_logger, ex);
        }
      }
    } catch (OperationCanceledException) {
      // Normal shutdown.
    }
  }

  [LoggerMessage(Level = LogLevel.Warning, Message = "Receive-liveness backlog probe failed for {TopicName}/{SubscriptionName} - skipping this subscription for this sweep")]
  private static partial void LogBacklogProbeFailed(ILogger logger, Exception exception, string topicName, string subscriptionName);

  [LoggerMessage(Level = LogLevel.Warning, Message = "Receive-liveness stall detected on {TopicName}/{SubscriptionName}: silent for {SilenceSeconds}s with {BacklogCount} active messages waiting - triggering subscription recovery")]
  private static partial void LogStallDetected(ILogger logger, string topicName, string subscriptionName, double silenceSeconds, long backlogCount);

  [LoggerMessage(Level = LogLevel.Error, Message = "Receive-liveness sweep failed; will retry on the next interval")]
  private static partial void LogSweepFailed(ILogger logger, Exception exception);

  /// <inheritdoc />
  public async ValueTask DisposeAsync() {
    Task? loopTask;
    lock (_startLock) {
      if (_disposed) {
        return;
      }
      _disposed = true;
      loopTask = _loopTask;
    }

    await _stopTokenSource.CancelAsync();
    if (loopTask is not null) {
      await loopTask;
    }
    _stopTokenSource.Dispose();
  }
}
