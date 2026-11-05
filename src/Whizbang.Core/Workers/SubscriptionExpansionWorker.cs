// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Transports;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Workers;

/// <summary>
/// Stream-integrity Phase S: the startup subscription-expansion reconciler. Diffs this service's
/// consumed event types (its consumer registry, #2058) against the persisted consumed-type registry. FIRST boot baselines
/// the whole catalog (nothing existed to miss). A type appearing on a LATER boot is an EXPANSION —
/// history exists this service never received — recorded as Pending and, when
/// <see cref="StreamIntegrityOptions.BackfillOnSubscriptionGrowth"/> (default true), repaired via
/// STATE-ONLY <see cref="RequestRedeliveryCommand"/>s: one directed at each known domain owner (#2032), and ONE
/// broadcast for the types with no usable owner; each origin answers with its own history targeted back at this service; bundles build state without re-firing triggers, and
/// duplicate sources converge by event-id identity. Disabled = expansions stay Pending (the audit
/// surface). One-shot: runs once when it starts at Accept commands, then completes.
/// </summary>
/// <docs>resilience/stream-integrity</docs>
/// <tests>tests/Whizbang.Core.Component.Tests/Workers/SubscriptionExpansionWorkerTests.cs</tests>
public sealed partial class SubscriptionExpansionWorker(
  IServiceScopeFactory scopeFactory,
  IOptions<StreamIntegrityOptions> options,
  ILogger<SubscriptionExpansionWorker> logger) : BackgroundService {
  private readonly IServiceScopeFactory _scopeFactory = ArgumentGuard.NotNull(scopeFactory);
  private readonly StreamIntegrityOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
  private readonly ILogger<SubscriptionExpansionWorker> _logger = ArgumentGuard.NotNull(logger);

  /// <inheritdoc />
  protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
    try {
      await RunOnceAsync(stoppingToken);
    } catch (OperationCanceledException) {
      // shutdown during startup — nothing to do
    } catch (Exception ex) {
      // Non-fatal: the expansion stays recorded (or re-detects next boot); the audit phases
      // surface anything left pending.
      LogError(_logger, ex);
    }
  }

  /// <summary>One reconciliation pass: baseline, or detect expansions and request their backfill.</summary>
  public async Task RunOnceAsync(CancellationToken cancellationToken) {
    await using var scope = _scopeFactory.CreateAsyncScope();
    var services = scope.ServiceProvider;
    var coordinator = services.GetService<IWorkCoordinator>();
    if (coordinator is null) {
      return;   // schema-only / diagnostic hosts: nothing to reconcile with.
    }

    // The consumed event types (#2058): what the consumer registry says this service consumes, receptors included, not
    // the perspective event list, which is empty for a service with no perspectives.
    // The registry keys on the no-assembly CLR name (persisted rows predate any form change and
    // must not re-baseline), but the ORIGIN matches redelivery types against event_type in the
    // assembly-qualified WIRE form — so the outgoing request maps through this table.
    var wireByClrName = new Dictionary<string, string>(StringComparer.Ordinal);
    foreach (var wire in ConsumedMessageTypes.EventTypeNames(services)) {
      wireByClrName.TryAdd(TypeNameFormatter.GetFullName(wire), wire);
    }
    var catalogTypes = wireByClrName.Keys.ToList();
    if (catalogTypes.Count == 0) {
      return;
    }

    var registered = await coordinator.GetConsumedTypeRegistrationsAsync(cancellationToken).ConfigureAwait(false);
    if (registered.Count == 0) {
      // FIRST boot: the whole catalog baselines — nothing existed before this service to miss.
      await coordinator.RegisterConsumedTypesAsync(catalogTypes, asBaseline: true, cancellationToken).ConfigureAwait(false);
      LogBaselined(_logger, catalogTypes.Count);
      return;
    }

    var registeredNames = registered.Select(r => r.EventType).ToHashSet(StringComparer.Ordinal);
    var expansions = catalogTypes.Where(t => !registeredNames.Contains(t)).ToList();
    if (expansions.Count > 0) {
      await coordinator.RegisterConsumedTypesAsync(expansions, asBaseline: false, cancellationToken).ConfigureAwait(false);
      LogExpansionDetected(_logger, expansions.Count, string.Join(", ", expansions));
    }

    // Everything still Pending: the expansions just registered PLUS any prior boot's leftovers
    // (backfill disabled then, or the request failed) — retry semantics across restarts for free.
    var pending = (await coordinator.GetConsumedTypeRegistrationsAsync(cancellationToken).ConfigureAwait(false))
      .Where(r => r.Status == ConsumedTypeBackfillStatus.Pending)
      .Select(r => r.EventType)
      .ToList();
    if (pending.Count == 0) {
      return;
    }

    // Gated on BOTH the dedicated flag and RepairMode. Every other repair emitter honors
    // RepairMode, and the diagnostics recommend ReportOnly by name to stop repair traffic; a path
    // that ignored it left an operator following that advice watching the traffic continue. A
    // package upgrade changes the consumed-type catalog, so this fires on every service at once
    // from a version bump alone — which is how it became a storm rather than a one-off repair.
    if (!SubscriptionBackfillGate.ShouldRequestBackfill(
          _options.BackfillOnSubscriptionGrowth, _options.RepairMode)) {
      // Recorded, not repaired — the audit reports "pending backfill", never silent divergence.
      LogBackfillDisabled(_logger, pending.Count);
      return;
    }

    // A pending name with no catalog entry (type since removed) passes through unchanged —
    // an unmatched name at the origin is a no-op, same as today.
    var pendingWireForms = pending
      .ConvertAll(p => wireByClrName.TryGetValue(p, out var wire) ? wire : p)
;
    if (await _sendBackfillRequestsAsync(services, coordinator, pendingWireForms, cancellationToken).ConfigureAwait(false)) {
      await coordinator.MarkConsumedTypeBackfillRequestedAsync(pending, cancellationToken).ConfigureAwait(false);
      services.GetService<Observability.StreamIntegrityMetrics>()?.BackfillsRequested.Add(pending.Count);
      LogBackfillRequested(_logger, pending.Count);
    }
  }

  /// <summary>
  /// STATE-ONLY, wire-only requests. #2032: a type whose domain owner is known from the ownership map, with a request
  /// address, is asked of that owner alone, directed on its request topic; the types with no usable owner go out as ONE
  /// broadcast (no Target), as before. A lost request re-detects as Pending next boot.
  /// </summary>
  private async Task<bool> _sendBackfillRequestsAsync(
      IServiceProvider services, IWorkCoordinator coordinator, List<string> pendingTypes, CancellationToken cancellationToken) {
    var transport = services.GetService<ITransport>();
    var serializer = services.GetService<IEnvelopeSerializer>();
    var instanceProvider = services.GetService<IServiceInstanceProvider>();
    var requester = instanceProvider?.ServiceName;
    var topic = RepairTopicResolver.Resolve(_options.RepairTopic, services);
    if (transport is null || serializer is null || string.IsNullOrEmpty(requester) || string.IsNullOrEmpty(topic)) {
      LogBackfillSkipped(_logger,
        transport is null, serializer is null, string.IsNullOrEmpty(requester), string.IsNullOrEmpty(topic));
      return false;
    }

    var ownership = new IntegrityDomainOwnership(
      await coordinator.GetIntegrityDomainClaimsAsync(cancellationToken).ConfigureAwait(false));
    var self = await coordinator.GetLocalServiceIdAsync(cancellationToken).ConfigureAwait(false);
    var unowned = new List<string>();
    var byOwner = new Dictionary<Guid, (IntegrityDomainClaim Owner, List<string> Types)>();
    foreach (var type in pendingTypes) {
      if (ownership.FindOwner(type) is { } owner && owner.OwnerServiceId != self && !string.IsNullOrEmpty(owner.RequestTopic)) {
        if (!byOwner.TryGetValue(owner.OwnerServiceId, out var group)) {
          byOwner[owner.OwnerServiceId] = group = (owner, []);
        }
        group.Types.Add(type);
      } else {
        unowned.Add(type);
      }
    }

    foreach (var (owner, types) in byOwner.Values) {
      var command = new RequestRedeliveryCommand { EventTypes = types, RequesterService = requester, Topic = topic, StateOnly = true };
      await RedeliveryRequestDispatch.PublishAsync(
        transport, serializer, instanceProvider, command, owner.OwnerServiceName,
        IntegrityControlDestination.For(owner.RequestTopic!, owner.OwnerServiceId, typeof(RequestRedeliveryCommand), _options),
        DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
      LogBackfillDirected(_logger, types.Count, owner.OwnerServiceName);
    }
    if (unowned.Count == 0) {
      return true;
    }
    // #2078: the reply comes back to this service's own inbox, but a broadcast must ride the shared topic every service
    // reads, never this service's own inbox.
    // Never null here: the reply topic resolved, so the service has a configured topic or a destination to broadcast on.
    var broadcastTopic = RepairTopicResolver.ResolveBroadcast(_options.RepairTopic, services)!;
    await _broadcastAsync(transport, serializer, instanceProvider, requester, topic, broadcastTopic, unowned, cancellationToken).ConfigureAwait(false);
    return true;
  }

  /// <summary>ONE broadcast (no Target) for the types with no usable owner; the answering side ships only what it
  /// authored, so in practice only the author answers.</summary>
  [System.Diagnostics.CodeAnalysis.SuppressMessage("Major Code Smell", "S107:Methods should not have too many parameters",
    Justification = "One broadcast send: the transport and serializer, the sender's identity and name, the reply and broadcast topics, the types and cancellation, all held separately by the one caller.")]
  private async Task _broadcastAsync(
      ITransport transport, IEnvelopeSerializer serializer, IServiceInstanceProvider? instanceProvider, string requester,
      string topic, string broadcastTopic, List<string> pendingTypes, CancellationToken cancellationToken) {
    var envelope = new MessageEnvelope<RequestRedeliveryCommand> {
      Priority = Whizbang.Core.Priority.WorkPriority.BACKGROUND,
      MessageId = new MessageId(TrackedGuid.New()),
      Payload = new RequestRedeliveryCommand {
        EventTypes = pendingTypes,
        RequesterService = requester,
        Topic = topic,
        StateOnly = true,
      },
      Hops = [
        Whizbang.Core.Messaging.ControlPlaneHop.Create(typeof(RequestRedeliveryCommand), instanceProvider, DateTimeOffset.UtcNow)
      ],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Outbox },
    };
    var serialized = serializer.SerializeEnvelope(envelope);
    await transport.PublishAsync(serialized.JsonEnvelope,
      IntegrityControlDestination.For(broadcastTopic, envelope.MessageId.Value, typeof(RequestRedeliveryCommand), _options), serialized.EnvelopeType,
      cancellationToken: cancellationToken).ConfigureAwait(false);
  }

  [LoggerMessage(EventId = 70, Level = LogLevel.Information,
    Message = "Consumed-type registry baselined with {TypeCount} type(s) (first boot — no backfill)")]
  static partial void LogBaselined(ILogger logger, int typeCount);

  [LoggerMessage(EventId = 71, Level = LogLevel.Warning,
    Message = "Subscription expansion detected: {ExpansionCount} new consumed type(s) [{Types}] — history exists this service never received")]
  static partial void LogExpansionDetected(ILogger logger, int expansionCount, string types);

  [LoggerMessage(EventId = 72, Level = LogLevel.Information,
    Message = "Backfill requested for {PendingCount} expanded type(s): state-only re-delivery requests sent to their owners, or broadcast where no owner is known")]
  static partial void LogBackfillRequested(ILogger logger, int pendingCount);

  [LoggerMessage(EventId = 76, Level = LogLevel.Information,
    Message = "Backfill for {TypeCount} expanded type(s) directed at their owner '{OwnerServiceName}'")]
  static partial void LogBackfillDirected(ILogger logger, int typeCount, string ownerServiceName);

  [LoggerMessage(EventId = 73, Level = LogLevel.Warning,
    Message = "Backfill disabled — {PendingCount} expanded type(s) stay PENDING (audit surface)")]
  static partial void LogBackfillDisabled(ILogger logger, int pendingCount);

  [LoggerMessage(EventId = 74, Level = LogLevel.Warning,
    Message = "Backfill request skipped — missing infrastructure (transport={TransportMissing}, " +
              "serializer={SerializerMissing}, requester={RequesterMissing}, topic={TopicMissing}); types stay PENDING")]
  static partial void LogBackfillSkipped(ILogger logger, bool transportMissing, bool serializerMissing, bool requesterMissing, bool topicMissing);

  [LoggerMessage(EventId = 75, Level = LogLevel.Error,
    Message = "Subscription-expansion reconciliation failed; expansions re-detect next boot")]
  static partial void LogError(ILogger logger, Exception exception);
}
