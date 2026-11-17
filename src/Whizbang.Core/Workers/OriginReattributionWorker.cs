// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Whizbang.Core.DependencyInjection;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Routing;
using Whizbang.Core.Startup;

namespace Whizbang.Core.Workers;

/// <summary>
/// Re-attributes this store's own pre-origin history to the services that wrote it (#2008). A store that received
/// other services' events before origins were recorded holds them with no origin, in its local lane. The walk reads
/// the store in commit-sequence batches; for each batch it hands the event types this service does not own to the
/// store with their owner from the ownership map (the domains other services announce on their checkpoints). The store
/// gives each such row its owner as origin, moves its digest to the owner's lane, refolds sealed local epochs and
/// re-arms the owner's repair ledger buckets the rows land in. Ownership is the primary signal: a row whose stored first
/// hop names a third service is a disagreement, logged and left. A non-owned type with no known owner is counted as
/// unattributed and left; it is out of the local lane logically, because the lane follows ownership.
/// </summary>
/// <remarks>
/// Runs on the instance holding the maintainer duty, under the housekeeping idle gate like the rest of integrity work
/// (forced through after <c>Whizbang:Housekeeping:MaxDeferral</c>), every
/// <see cref="StreamIntegrityOptions.ReattributionIntervalSeconds"/>, at most
/// <see cref="StreamIntegrityOptions.MaxReattributionBatchesPerPass"/> batches of
/// <see cref="StreamIntegrityOptions.ReattributionBatchSize"/> rows per pass. The walk's cursor is durable, so it resumes
/// across restarts; a changed ownership map or set of owned domains restarts it, so history left unattributed reaches an
/// owner learned later. A service with no owned domains registered is left exactly as it was.
/// </remarks>
/// <docs>resilience/stream-integrity#re-attribution</docs>
/// <tests>tests/Whizbang.Core.Tests/Workers/OriginReattributionWorkerTests.cs</tests>
/// <tests>tests/Whizbang.Core.Component.Tests/Workers/OriginReattributionWorkerLoopTests.cs</tests>
public sealed partial class OriginReattributionWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<StreamIntegrityOptions> options,
    ILogger<OriginReattributionWorker> logger,
    TimeProvider? timeProvider = null) : BackgroundService {
  private readonly IServiceScopeFactory _scopeFactory = ArgumentGuard.NotNull(scopeFactory);
  private readonly StreamIntegrityOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
  private readonly ILogger<OriginReattributionWorker> _logger = ArgumentGuard.NotNull(logger);
  private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
  private readonly HashSet<string> _reportedUnattributed = new(StringComparer.Ordinal);

  /// <inheritdoc />
  protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
    if (!_options.ReattributionEnabled) {
      LogDisabled(_logger);
      try { await Task.Delay(Timeout.InfiniteTimeSpan, _time, stoppingToken); } catch (OperationCanceledException) { /* stopping is the normal way out of this wait */ }
      return;
    }
    while (!stoppingToken.IsCancellationRequested) {
      try {
        await RunPassAsync(stoppingToken);
      } catch (OperationCanceledException) {
        break;
      } catch (Exception ex) {
        LogPassFailed(_logger, ex);
      }
      try {
        await Task.Delay(TimeSpan.FromSeconds(_options.ReattributionIntervalSeconds), _time, stoppingToken);
      } catch (OperationCanceledException) {
        break;
      }
    }
  }

  /// <summary>
  /// One pass: on the maintainer, once the idle gate admits integrity work, walks up to
  /// <see cref="StreamIntegrityOptions.MaxReattributionBatchesPerPass"/> batches, stopping early when the walk catches
  /// up or the store reports a batch it did not apply.
  /// </summary>
  /// <param name="cancellationToken">Cancellation.</param>
  /// <returns>The batches applied.</returns>
  public async Task<int> RunPassAsync(CancellationToken cancellationToken) {
    if (!_options.ReattributionEnabled) {
      return 0;
    }
    await using var scope = _scopeFactory.CreateAsyncScope();
    var services = scope.ServiceProvider;
    if (!MaintainerDuty.IsThisInstancesTurn(services) || services.GetService<IWorkCoordinator>() is not { } coordinator) {
      return 0;
    }
    var ownedDomains = services.GetOptionsValue<RoutingOptions>()?.OwnedDomains;
    if (ownedDomains is not { Count: > 0 }) {
      return 0;   // no owned domains: every event without an origin is this service's own, as before.
    }
    using var priorityScope = Whizbang.Core.Priority.PriorityContext.Enter(Whizbang.Core.Priority.WorkPriority.BACKGROUND);
    await using var admission = await IntegrityAdmission.TryAdmitAsync(
      services, coordinator, nameof(OriginReattributionWorker), _logger, cancellationToken).ConfigureAwait(false);
    if (!admission.Granted) {
      return 0;
    }

    var ownership = new IntegrityDomainOwnership(
      await coordinator.GetIntegrityDomainClaimsAsync(cancellationToken).ConfigureAwait(false));
    var fingerprint = ownership.Fingerprint([.. ownedDomains]);
    var metrics = services.GetService<StreamIntegrityMetrics>();
    var applied = 0;
    while (applied < _options.MaxReattributionBatchesPerPass) {
      var window = await coordinator.GetOriginReattributionWindowAsync(
        fingerprint, _options.ReattributionBatchSize, cancellationToken).ConfigureAwait(false);
      if (window is null || window.ThroughCommitSequence <= window.AfterCommitSequence) {
        break;
      }
      var targets = _targets(window, ownedDomains, ownership);
      var result = await coordinator.ReattributeOriginsAsync(new OriginReattributionBatch {
        AfterCommitSequence = window.AfterCommitSequence,
        ThroughCommitSequence = window.ThroughCommitSequence,
        Targets = targets,
      }, cancellationToken).ConfigureAwait(false);
      if (result is null) {
        break;
      }
      _record(metrics, window, result);
      if (!result.Advanced) {
        break;
      }
      applied++;
    }
    return applied;
  }

  private List<OriginReattributionTarget> _targets(
      OriginReattributionWindow window, IReadOnlySet<string> ownedDomains, IntegrityDomainOwnership ownership) {
    var targets = new List<OriginReattributionTarget>();
    foreach (var type in window.EventTypes.Where(t => !OwnedNamespaceMatcher.IsLocalEventType(t, ownedDomains))) {
      var owner = ownership.FindOwner(type);
      if (owner is null && _reportedUnattributed.Add(type)) {
        LogUnattributedType(_logger, type);
      }
      targets.Add(new OriginReattributionTarget {
        EventType = type,
        OwnerServiceId = owner?.OwnerServiceId,
        OwnerServiceName = owner?.OwnerServiceName,
      });
    }
    return targets;
  }

  private void _record(StreamIntegrityMetrics? metrics, OriginReattributionWindow window, OriginReattributionResult result) {
    metrics?.OriginsReattributed.Add(result.Reattributed);
    metrics?.OriginsUnattributed.Add(result.Unattributed);
    metrics?.ReattributionHopDisagreements.Add(result.HopDisagreements);
    if (result.HopDisagreements > 0) {
      LogHopDisagreements(_logger, result.HopDisagreements, window.AfterCommitSequence, window.ThroughCommitSequence);
    }
    if (result.Reattributed > 0) {
      LogReattributed(_logger, result.Reattributed, window.AfterCommitSequence, window.ThroughCommitSequence);
    }
  }

  [LoggerMessage(Level = LogLevel.Information,
    Message = "Origin re-attribution disabled by configuration; history received before origins were recorded stays where it is")]
  private static partial void LogDisabled(ILogger logger);

  [LoggerMessage(Level = LogLevel.Warning, Message = "Origin re-attribution pass failed; the next pass resumes from the durable cursor")]
  private static partial void LogPassFailed(ILogger logger, Exception exception);

  [LoggerMessage(Level = LogLevel.Information,
    Message = "Re-attributed {Count} event(s) received before origins were recorded to their owners (commit sequences {After} to {Through})")]
  private static partial void LogReattributed(ILogger logger, int count, long after, long through);

  [LoggerMessage(Level = LogLevel.Warning,
    Message = "{Count} event(s) left in place: their first hop names a service other than the owner of their type " +
              "(commit sequences {After} to {Through}). Ownership decides; the rows are kept for an operator to inspect")]
  private static partial void LogHopDisagreements(ILogger logger, int count, long after, long through);

  [LoggerMessage(Level = LogLevel.Information,
    Message = "Event type '{EventType}' is not owned here and no service has announced owning it; its events are counted " +
              "as unattributed and are re-attributed once an owner announces the domain")]
  private static partial void LogUnattributedType(ILogger logger, string eventType);
}
