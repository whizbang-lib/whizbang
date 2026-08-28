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
/// consumed event-type catalog against the persisted consumed-type registry. FIRST boot baselines
/// the whole catalog (nothing existed to miss). A type appearing on a LATER boot is an EXPANSION —
/// history exists this service never received — recorded as Pending and, when
/// <see cref="StreamIntegrityOptions.BackfillOnSubscriptionGrowth"/> (default true), repaired via
/// ONE broadcast, STATE-ONLY <see cref="RequestRedeliveryCommand"/>: every origin answers with its
/// own history targeted back at this service; bundles build state without re-firing triggers, and
/// duplicate sources converge by event-id identity. Disabled = expansions stay Pending (the audit
/// surface). One-shot: runs once after the schema gate, then completes.
/// </summary>
/// <docs>resilience/stream-integrity</docs>
/// <tests>tests/Whizbang.Core.Tests/Workers/SubscriptionExpansionWorkerTests.cs</tests>
public sealed partial class SubscriptionExpansionWorker(
  IServiceScopeFactory scopeFactory,
  ISchemaReadyGate schemaReadyGate,
  IOptions<StreamIntegrityOptions> options,
  ILogger<SubscriptionExpansionWorker> logger) : BackgroundService {
  private readonly IServiceScopeFactory _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
  private readonly ISchemaReadyGate _schemaReadyGate = schemaReadyGate ?? throw new ArgumentNullException(nameof(schemaReadyGate));
  private readonly StreamIntegrityOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
  private readonly ILogger<SubscriptionExpansionWorker> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

  /// <inheritdoc />
  protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
    try {
      await _schemaReadyGate.WaitForReadyAsync(stoppingToken);
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
    var typeProvider = services.GetService<IEventTypeProvider>();
    if (coordinator is null || typeProvider is null) {
      return;   // schema-only / diagnostic hosts: nothing to reconcile with.
    }

    // The registry keys on the no-assembly CLR name (persisted rows predate any form change and
    // must not re-baseline), but the ORIGIN matches redelivery types against event_type in the
    // assembly-qualified WIRE form — so the outgoing request maps through this table.
    var wireByClrName = new Dictionary<string, string>(StringComparer.Ordinal);
    foreach (var type in typeProvider.GetEventTypes()) {
      wireByClrName.TryAdd(TypeNameFormatter.FormatClrTypeName(type), TypeNameFormatter.Format(type));
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
      .Select(p => wireByClrName.TryGetValue(p, out var wire) ? wire : p)
      .ToList();
    if (await _sendBackfillRequestAsync(services, pendingWireForms, cancellationToken).ConfigureAwait(false)) {
      await coordinator.MarkConsumedTypeBackfillRequestedAsync(pending, cancellationToken).ConfigureAwait(false);
      services.GetService<Observability.StreamIntegrityMetrics>()?.BackfillsRequested.Add(pending.Count);
      LogBackfillRequested(_logger, pending.Count);
    }
  }

  /// <summary>
  /// ONE broadcast (no Target — the expanding consumer cannot know which origins hold the
  /// history), STATE-ONLY, wire-only. A lost request re-detects as Pending next boot.
  /// </summary>
  private async Task<bool> _sendBackfillRequestAsync(
      IServiceProvider services, List<string> pendingTypes, CancellationToken cancellationToken) {
    var transport = services.GetService<ITransport>();
    var serializer = services.GetService<IEnvelopeSerializer>();
    var instanceProvider = services.GetService<IServiceInstanceProvider>();
    var requester = instanceProvider?.ServiceName;
    var topic = _options.RepairTopic
      ?? services.GetService<TransportConsumerOptions>()?.Destinations.FirstOrDefault()?.Address;
    // Guard the SOURCE, not just the value derived from it: testing instanceProvider directly is
    // what lets both the compiler and the analyzer see that the later use cannot be null.
    if (instanceProvider is null || transport is null || serializer is null
        || string.IsNullOrEmpty(requester) || string.IsNullOrEmpty(topic)) {
      LogBackfillSkipped(_logger,
        transport is null, serializer is null, string.IsNullOrEmpty(requester), string.IsNullOrEmpty(topic));
      return false;
    }

    var envelope = new MessageEnvelope<RequestRedeliveryCommand> {
      MessageId = new MessageId(TrackedGuid.NewMedo()),
      Payload = new RequestRedeliveryCommand {
        EventTypes = pendingTypes,
        RequesterService = requester,
        Topic = topic,
        StateOnly = true,
      },
      Hops = [
        new MessageHop {
          Type = HopType.Current,
          Timestamp = DateTimeOffset.UtcNow,
          // instanceProvider is non-null here: requester is read from it above and an empty
          // requester returns early, so the conditional access was dead.
          ServiceInstance = instanceProvider.ToInfo(),
          // Control-plane traffic has no ambient user by design. Saying so explicitly is what
          // keeps an ABSENT scope meaningful: without it, an intentional blank and a business
          // event that lost its scope are the same bytes in storage.
          Scope = Whizbang.Core.Security.SystemScopeResolver.ForUnscoped(typeof(RequestRedeliveryCommand)),
        }
      ],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Outbox },
    };
    var serialized = serializer.SerializeEnvelope(envelope);
    await transport.PublishAsync(serialized.JsonEnvelope,
      ControlPlaneDestination.For(topic, envelope.MessageId.Value, typeof(RequestRedeliveryCommand)), serialized.EnvelopeType,
      cancellationToken: cancellationToken).ConfigureAwait(false);
    return true;
  }

  [LoggerMessage(EventId = 70, Level = LogLevel.Information,
    Message = "Consumed-type registry baselined with {TypeCount} type(s) (first boot — no backfill)")]
  static partial void LogBaselined(ILogger logger, int typeCount);

  [LoggerMessage(EventId = 71, Level = LogLevel.Warning,
    Message = "Subscription expansion detected: {ExpansionCount} new consumed type(s) [{Types}] — history exists this service never received")]
  static partial void LogExpansionDetected(ILogger logger, int expansionCount, string types);

  [LoggerMessage(EventId = 72, Level = LogLevel.Information,
    Message = "Backfill requested for {PendingCount} expanded type(s) — broadcast state-only re-delivery request sent")]
  static partial void LogBackfillRequested(ILogger logger, int pendingCount);

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
