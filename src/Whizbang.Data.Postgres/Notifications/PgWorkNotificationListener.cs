// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Whizbang.Core.Notifications;
using Whizbang.Core.Signals;

namespace Whizbang.Data.Postgres.Notifications;

/// <summary>
/// Surfaces the work doorbells as <see cref="IWorkNotificationListener.OnSignal"/> events. A subscriber of the signal
/// bus (#2046): the outbox, inbox and perspective doorbells, the reap's orphan wake and the dead-letter doorbell arrive
/// as signals on the instance's channel, and each becomes its <see cref="WorkSignalCategory"/>.
/// </summary>
/// <remarks>
/// <para>
/// The listener owns no channel and no connection. The bus's Postgres transport LISTENs on the instance's channel through
/// the one shared connection, and the bus hands each signal here; a signal a poll source raised arrives the same way,
/// which only wakes a worker that would have found the work on its next poll.
/// </para>
/// <para>
/// <see cref="IsHealthy"/> delegates to <see cref="INotifySignalingGate.IsAvailable"/>; <see cref="OnHealthChanged"/>
/// mirrors the gate's <see cref="INotifySignalingGate.OnAvailabilityChanged"/>.
/// </para>
/// </remarks>
/// <docs>fundamentals/work-coordinator/notifications-and-pgbouncer</docs>
/// <tests>tests/Whizbang.Core.Component.Tests/Notifications/PgWorkNotificationListenerTests.cs</tests>
public sealed partial class PgWorkNotificationListener : IWorkNotificationListener, IHostedService, IDisposable {
  private readonly ISignalBus _signalBus;
  private readonly INotifySignalingGate _gate;
  private readonly ILogger<PgWorkNotificationListener> _logger;
  private readonly TimeProvider _time;
  private ISignalSubscription[] _subscriptions = [];
  private DateTimeOffset? _lastSignalAt;

  /// <summary>Constructor used by DI.</summary>
  /// <param name="signalBus">The bus the work doorbells arrive on.</param>
  /// <param name="gate">The notification gate whose availability is this listener's health.</param>
  /// <param name="logger">Optional logger.</param>
  /// <param name="timeProvider">Optional clock for <see cref="LastSignalAt"/>.</param>
  public PgWorkNotificationListener(
    ISignalBus signalBus,
    INotifySignalingGate gate,
    ILogger<PgWorkNotificationListener>? logger = null,
    TimeProvider? timeProvider = null) {
    ArgumentNullException.ThrowIfNull(signalBus);
    _signalBus = signalBus;
    ArgumentNullException.ThrowIfNull(gate);
    _gate = gate;
    _logger = logger ?? NullLogger<PgWorkNotificationListener>.Instance;
    _time = timeProvider ?? TimeProvider.System;
    _gate.OnAvailabilityChanged += _forwardAvailability;
  }

  /// <inheritdoc />
  public bool IsHealthy => _gate.IsAvailable;
  /// <inheritdoc />
  public DateTimeOffset? LastSignalAt => _lastSignalAt;
  /// <inheritdoc />
  public event Action<WorkSignalCategory>? OnSignal;
  /// <inheritdoc />
  public event Action<bool>? OnHealthChanged;

  private ValueTask _raise(WorkSignalCategory category) {
    _lastSignalAt = _time.GetUtcNow();
    OnSignal?.Invoke(category);
    return ValueTask.CompletedTask;
  }

  Task IHostedService.StartAsync(CancellationToken cancellationToken) {
    _subscriptions = [
      _signalBus.Subscribe<WorkOutboxAvailableSignal>(_ => _raise(WorkSignalCategory.Outbox)),
      _signalBus.Subscribe<WorkInboxAvailableSignal>(_ => _raise(WorkSignalCategory.Inbox)),
      _signalBus.Subscribe<WorkPerspectiveAvailableSignal>(_ => _raise(WorkSignalCategory.Perspective)),
      // The reap's wake after it released a dead instance's leases (cleanup_stale_instances).
      _signalBus.Subscribe<WorkOrphanedSignal>(_ => _raise(WorkSignalCategory.OrphanRedistribute)),
      // The dead-letter table's insert trigger.
      _signalBus.Subscribe<DeadLetterReadySignal>(_ => _raise(WorkSignalCategory.DeadLetterReady)),
    ];
    LogSubscribed(_logger);
    return Task.CompletedTask;
  }

  Task IHostedService.StopAsync(CancellationToken cancellationToken) {
    _unsubscribe();
    return Task.CompletedTask;
  }

  /// <inheritdoc />
  public void Dispose() {
    _gate.OnAvailabilityChanged -= _forwardAvailability;
    _unsubscribe();
  }

  private void _unsubscribe() {
    foreach (var subscription in Interlocked.Exchange(ref _subscriptions, [])) {
      subscription.Dispose();
    }
  }

  private void _forwardAvailability(bool available) => OnHealthChanged?.Invoke(available);

  [LoggerMessage(EventId = 1, Level = LogLevel.Information,
    Message = "PgWorkNotificationListener subscribed to the work doorbells on the signal bus")]
  static partial void LogSubscribed(ILogger logger);
}
