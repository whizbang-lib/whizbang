// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Startup;

namespace Whizbang.Core.Workers;

/// <summary>
/// The maintainer's integrity delivery drain (#2001): handles the stream-integrity deliveries every instance records
/// (received checkpoints, manifest requests and chunks, redelivery requests, bundle reconciles), on the instance holding
/// the maintainer duty, under the housekeeping idle gate.
/// </summary>
/// <remarks>
/// <para>
/// Every <see cref="StreamIntegrityOptions.IntegrityDeliveryIntervalSeconds"/> a pass runs. It does nothing unless
/// this instance holds the maintainer duty (<see cref="MaintainerDuty"/>), and nothing while the idle gate defers
/// integrity work; once admitted, or forced through after <c>Whizbang:Housekeeping:MaxDeferral</c>, it handles up to
/// <see cref="StreamIntegrityOptions.MaxIntegrityDeliveriesPerPass"/> deliveries in arrival order, one at a time,
/// each inside the drain context so the receptor it runs does the work rather than recording it again.
/// </para>
/// <para>
/// A handler failure is retried after <see cref="StreamIntegrityOptions.IntegrityDeliveryRetryBackoffSeconds"/>,
/// doubled per attempt and capped at an hour. A delivery that has spent
/// <see cref="StreamIntegrityOptions.MaxIntegrityDeliveryAttempts"/>, or whose type no handler is registered for (a
/// delivery recorded by another version), is dropped with a warning naming its type: integrity traffic is control-plane
/// and is dropped at its bound rather than dead-lettered, because the next checkpoint, audit or repair cycle asks again.
/// </para>
/// </remarks>
/// <docs>resilience/stream-integrity#integrity-work-runs-on-the-maintainer</docs>
/// <tests>tests/Whizbang.Core.Tests/Workers/IntegrityDeliveryWorkerTests.cs</tests>
/// <tests>tests/Whizbang.Core.Component.Tests/Workers/IntegrityDeliveryWorkerLoopTests.cs</tests>
public sealed partial class IntegrityDeliveryWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<StreamIntegrityOptions> options,
    IntegrityDeliveryRegistry deliveries,
    ILogger<IntegrityDeliveryWorker> logger,
    TimeProvider? timeProvider = null) : BackgroundService {
  private static readonly TimeSpan _maxBackoff = TimeSpan.FromHours(1);
  private readonly IServiceScopeFactory _scopeFactory = ArgumentGuard.NotNull(scopeFactory);
  private readonly StreamIntegrityOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
  private readonly IntegrityDeliveryRegistry _deliveries = ArgumentGuard.NotNull(deliveries);
  private readonly ILogger<IntegrityDeliveryWorker> _logger = ArgumentGuard.NotNull(logger);
  private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
  private DateTimeOffset? _deferredSince;

  /// <summary>What one pass did.</summary>
  internal enum PassOutcome {
    /// <summary>Another instance holds the maintainer duty.</summary>
    NotThisInstancesTurn,

    /// <summary>No coordinator in scope: nothing to drain from.</summary>
    NoCoordinator,

    /// <summary>The idle gate deferred integrity work.</summary>
    Deferred,

    /// <summary>The pass was admitted and drained what it could.</summary>
    Drained,
  }

  /// <summary>One pass's outcome and counts.</summary>
  internal readonly record struct Pass(PassOutcome Outcome, int Processed = 0, int Failed = 0, int Dropped = 0);

  private enum DeliveryOutcome { Processed, Failed, Dropped }

  /// <summary>
  /// When the idle gate first deferred the drain in the current run of deferrals; null while the drain is being
  /// admitted. A received checkpoint waits in the queue for as long as this, so the consumer-side liveness check widens
  /// its staleness bound by it rather than reporting a live origin as silent.
  /// </summary>
  /// <docs>resilience/stream-integrity#integrity-work-runs-on-the-maintainer</docs>
  /// <tests>tests/Whizbang.Core.Tests/Workers/IntegrityDeliveryWorkerTests.cs:OnABusyService_ThePassIsDeferred_AndTheSlotIsNotHeldAsync</tests>
  public DateTimeOffset? DeferredSince => _deferredSince;

  /// <summary>Raised after every pass the loop runs, whatever it did; a signal for monitoring and tests.</summary>
  /// <docs>resilience/stream-integrity#integrity-work-runs-on-the-maintainer</docs>
  /// <tests>tests/Whizbang.Core.Component.Tests/Workers/IntegrityDeliveryWorkerLoopTests.cs</tests>
  public event Action? OnPassCompleted;

  /// <inheritdoc />
  protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
    // Hosted at Accept commands (#1636): the registration decides when this starts, so the loop holds no gate of its own.
    var interval = TimeSpan.FromSeconds(Math.Max(1, _options.IntegrityDeliveryIntervalSeconds));
    while (!stoppingToken.IsCancellationRequested) {
      try {
        _ = await RunPassAsync(stoppingToken);
      } catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) {
        break;
      } catch (Exception ex) {
        LogPassFailed(_logger, ex);
      }
      OnPassCompleted?.Invoke();
      try {
        await Task.Delay(interval, _time, stoppingToken);
      } catch (OperationCanceledException) {
        break;
      }
    }
  }

  /// <summary>One drain pass. Internal so tests drive passes directly on a fake clock.</summary>
  internal async Task<Pass> RunPassAsync(CancellationToken cancellationToken) {
    await using var scope = _scopeFactory.CreateAsyncScope();
    var services = scope.ServiceProvider;
    if (!MaintainerDuty.IsThisInstancesTurn(services)) {
      return new Pass(PassOutcome.NotThisInstancesTurn);
    }
    if (services.GetService<IWorkCoordinator>() is not { } coordinator) {
      return new Pass(PassOutcome.NoCoordinator);
    }
    using var admission = await IntegrityAdmission.TryAdmitAsync(
      services, coordinator, nameof(IntegrityDeliveryWorker), _logger, cancellationToken).ConfigureAwait(false);
    if (!admission.Granted) {
      _deferredSince ??= _time.GetUtcNow();
      return new Pass(PassOutcome.Deferred);
    }
    _deferredSince = null;

    var metrics = services.GetService<StreamIntegrityMetrics>();
    var batchSize = Math.Max(1, _options.IntegrityDeliveryBatchSize);
    var lease = TimeSpan.FromSeconds(Math.Max(1, _options.IntegrityDeliveryLeaseSeconds));
    var budget = Math.Max(1, _options.MaxIntegrityDeliveriesPerPass);
    int processed = 0, failed = 0, dropped = 0;
    while (budget > 0) {
      var asked = Math.Min(budget, batchSize);
      var batch = await coordinator.LeaseIntegrityDeliveriesAsync(asked, lease, cancellationToken).ConfigureAwait(false);
      budget -= batch.Count;
      foreach (var delivery in batch) {
        switch (await _handleAsync(coordinator, delivery, metrics, cancellationToken).ConfigureAwait(false)) {
          case DeliveryOutcome.Processed: processed++; break;
          case DeliveryOutcome.Failed: failed++; break;
          default: dropped++; break;
        }
      }
      if (batch.Count < asked) {
        break;   // a short lease: nothing more is due
      }
    }
    return new Pass(PassOutcome.Drained, processed, failed, dropped);
  }

  private async Task<DeliveryOutcome> _handleAsync(
      IWorkCoordinator coordinator, IntegrityDelivery delivery, StreamIntegrityMetrics? metrics,
      CancellationToken cancellationToken) {
    var type = new KeyValuePair<string, object?>("type", delivery.DeliveryType);
    if (!_deliveries.TryGetHandler(delivery.DeliveryType, out var handler)) {
      LogDroppedNoHandler(_logger, delivery.DeliveryType, delivery.DeliveryId);
      await coordinator.CompleteIntegrityDeliveryAsync(delivery.DeliveryId, cancellationToken).ConfigureAwait(false);
      metrics?.IntegrityDeliveriesDropped.Add(1, type, new KeyValuePair<string, object?>("reason", "no_handler"));
      return DeliveryOutcome.Dropped;
    }
    try {
      await using var deliveryScope = _scopeFactory.CreateAsyncScope();
      using (IntegrityDrainContext.Enter()) {
        await handler!(delivery.Payload, deliveryScope.ServiceProvider, cancellationToken).ConfigureAwait(false);
      }
    } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
      throw;
    } catch (Exception ex) {
      return await _failAsync(coordinator, delivery, ex, metrics, type, cancellationToken).ConfigureAwait(false);
    }
    await coordinator.CompleteIntegrityDeliveryAsync(delivery.DeliveryId, cancellationToken).ConfigureAwait(false);
    metrics?.IntegrityDeliveriesProcessed.Add(1, type);
    return DeliveryOutcome.Processed;
  }

  private async Task<DeliveryOutcome> _failAsync(
      IWorkCoordinator coordinator, IntegrityDelivery delivery, Exception failure, StreamIntegrityMetrics? metrics,
      KeyValuePair<string, object?> type, CancellationToken cancellationToken) {
    var attempts = delivery.Attempts + 1;
    if (attempts >= Math.Max(1, _options.MaxIntegrityDeliveryAttempts)) {
      LogDroppedExhausted(_logger, delivery.DeliveryType, delivery.DeliveryId, attempts, failure.Message, failure);
      await coordinator.CompleteIntegrityDeliveryAsync(delivery.DeliveryId, cancellationToken).ConfigureAwait(false);
      metrics?.IntegrityDeliveriesDropped.Add(1, type, new KeyValuePair<string, object?>("reason", "attempts_exhausted"));
      return DeliveryOutcome.Dropped;
    }
    var backoff = TimeSpan.FromSeconds(Math.Min(
      Math.Max(1, _options.IntegrityDeliveryRetryBackoffSeconds) * Math.Pow(2, attempts - 1), _maxBackoff.TotalSeconds));
    LogDeliveryFailed(_logger, delivery.DeliveryType, delivery.DeliveryId, attempts, backoff.TotalSeconds, failure);
    await coordinator.FailIntegrityDeliveryAsync(delivery.DeliveryId, failure.Message, backoff, cancellationToken)
      .ConfigureAwait(false);
    metrics?.IntegrityDeliveriesFailed.Add(1, type);
    return DeliveryOutcome.Failed;
  }

  [LoggerMessage(Level = LogLevel.Warning,
    Message = "Integrity delivery drain pass failed; the deliveries stay queued and the next pass retries")]
  private static partial void LogPassFailed(ILogger logger, Exception exception);

  [LoggerMessage(Level = LogLevel.Warning,
    Message = "Integrity delivery {DeliveryType} ({DeliveryId}) failed on attempt {Attempts}; retrying in {BackoffSeconds}s")]
  private static partial void LogDeliveryFailed(
    ILogger logger, string deliveryType, Guid deliveryId, int attempts, double backoffSeconds, Exception exception);

  [LoggerMessage(Level = LogLevel.Warning,
    Message = "Integrity delivery {DeliveryType} ({DeliveryId}) dropped after {Attempts} failed attempt(s): {Error}. "
            + "Integrity traffic is control-plane and is not dead-lettered; the next checkpoint, audit or repair cycle asks again")]
  private static partial void LogDroppedExhausted(
    ILogger logger, string deliveryType, Guid deliveryId, int attempts, string error, Exception exception);

  [LoggerMessage(Level = LogLevel.Warning,
    Message = "Integrity delivery {DeliveryType} ({DeliveryId}) dropped: no handler is registered for that type on this "
            + "instance (recorded by another version, or by a host with a receptor this one does not register)")]
  private static partial void LogDroppedNoHandler(ILogger logger, string deliveryType, Guid deliveryId);
}
