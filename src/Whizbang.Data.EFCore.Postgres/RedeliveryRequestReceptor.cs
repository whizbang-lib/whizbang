using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Whizbang.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Transports;

namespace Whizbang.Data.EFCore.Postgres;

/// <summary>
/// Stream-integrity R1b — the built-in origin-side bridge that turns a received
/// <see cref="RequestRedeliveryCommand"/> into a coordinator selection
/// (<see cref="IWorkCoordinator.SelectRedeliveryEventsAsync"/>) pumped back to the wire as
/// targeted <see cref="RedeliveryComposite"/> bundles (<see cref="RedeliveryPump"/>). Lives in the
/// driver assembly (not <c>Whizbang.Core</c>) because a receptor in Core would make the
/// receptor-discovery generator emit dispatcher registrations that collide with every consumer's;
/// and the selection needs the Postgres store anyway. The requester's <c>MaxEvents</c> is clamped
/// by this origin's <see cref="RedeliveryPumpOptions.MaxEventsPerRequest"/> — a requester can
/// never raise the origin's storm cap. Inert (logged) if the host lacks the transport, event
/// store, coordinator, or event-type provider.
/// </summary>
/// <docs>resilience/stream-integrity</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RedeliveryRequestReceptorTests.cs</tests>
public sealed partial class RedeliveryRequestReceptor(
    IServiceScopeFactory scopeFactory,
    ILogger<RedeliveryRequestReceptor> logger) : IReceptor<RequestRedeliveryCommand> {

  /// <summary>
  /// One repair build at a time per process. Redelivery requests arrive in bursts — per-bucket
  /// auto-repair and subscription-expansion broadcasts land together — and each build holds a
  /// page of event bodies plus its serialized composite; unbounded concurrency multiplies that
  /// footprint and has OOM-killed origins under a first full audit. Requests queue here; inbox
  /// redelivery semantics make waiting safe.
  /// </summary>
  private static readonly SemaphoreSlim _buildGate = new(1, 1);

  /// <inheritdoc />
  public async ValueTask HandleAsync(RequestRedeliveryCommand message, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(message);
    await using var scope = scopeFactory.CreateAsyncScope();
    var services = scope.ServiceProvider;
    var coordinator = services.GetService<IWorkCoordinator>();
    var transport = services.GetService<ITransport>();
    var envelopeSerializer = services.GetService<IEnvelopeSerializer>();
    if (coordinator is null || transport is null || envelopeSerializer is null) {
      LogMissingInfrastructure(logger, coordinator is null, transport is null, envelopeSerializer is null);
      return;
    }

    var metrics = services.GetService<Whizbang.Core.Observability.StreamIntegrityMetrics>();
    metrics?.RedeliveryRequestsReceived.Add(1);
    var buildTimer = System.Diagnostics.Stopwatch.StartNew();
    var options = services.GetService<RedeliveryPumpOptions>() ?? new RedeliveryPumpOptions();
    var cap = options.MaxEventsPerRequest;
    var maxEvents = message.MaxEvents is { } requested ? Math.Min(requested, cap) : cap;
    var pageSize = Math.Max(1, options.SelectPageSize);

    await _buildGate.WaitAsync(cancellationToken).ConfigureAwait(false);
    try {
      // Phase B: the bundle names THIS origin so fanned-out children carry their original source
      // identity and repaired windows recount correctly at the consumer.
      var originServiceId = await coordinator.GetLocalServiceIdAsync(cancellationToken).ConfigureAwait(false);
      var pump = new RedeliveryPump(
        transport, envelopeSerializer,
        services.GetService<IServiceInstanceProvider>(), options,
        compositeFactory: services.GetService<Whizbang.Core.Minting.ICompositeFactory>());

      // Select-and-publish in keyset pages so memory is bounded by ONE page of bodies no matter
      // how wide the request is — materializing the whole cap at once has OOM-killed origins.
      var totalSelected = 0;
      var totalComposites = 0;
      Guid? afterStream = null;
      long? afterVersion = null;
      while (totalSelected < maxEvents) {
        var page = await coordinator.SelectRedeliveryEventsAsync(new RedeliveryRequest {
          TenantScope = message.TenantScope,
          EventTypes = message.EventTypes,
          StreamIds = message.StreamIds,
          FromCommitSequence = message.FromCommitSequence,
          ToCommitSequence = message.ToCommitSequence,
          MaxEvents = Math.Min(pageSize, maxEvents - totalSelected),
          AfterStreamId = afterStream,
          AfterVersion = afterVersion
        }, cancellationToken).ConfigureAwait(false);
        if (page.Count == 0) {
          break;
        }

        totalComposites += await pump
          .PublishAsync(page, message.Topic, message.RequesterService, originServiceId, message.StateOnly, cancellationToken)
          .ConfigureAwait(false);
        totalSelected += page.Count;
        var last = page[page.Count - 1];
        afterStream = last.StreamId;
        afterVersion = last.Version;
      }

      if (totalSelected == 0) {
        LogNothingSelected(logger, message.RequesterService);
        return;
      }
      metrics?.RedeliveryEventsShipped.Add(totalSelected);
      metrics?.RedeliveryBuildDuration.Record(buildTimer.Elapsed.TotalSeconds);
      LogRedeliveryPublished(logger, totalSelected, totalComposites, message.RequesterService, message.Topic);
    } finally {
      _buildGate.Release();
    }
  }

  [LoggerMessage(EventId = 47, Level = LogLevel.Warning,
    Message = "RequestRedeliveryCommand received but required infrastructure is missing " +
              "(coordinator={CoordinatorMissing}, transport={TransportMissing}, envelopeSerializer={EnvelopeSerializerMissing}); ignored")]
  static partial void LogMissingInfrastructure(ILogger logger, bool coordinatorMissing, bool transportMissing, bool envelopeSerializerMissing);

  [LoggerMessage(EventId = 48, Level = LogLevel.Information,
    Message = "Re-delivery request from {RequesterService} selected no events; nothing published")]
  static partial void LogNothingSelected(ILogger logger, string requesterService);

  [LoggerMessage(EventId = 49, Level = LogLevel.Information,
    Message = "Re-delivered {EventCount} events as {CompositeCount} composites to {RequesterService} on {Topic}")]
  static partial void LogRedeliveryPublished(ILogger logger, int eventCount, int compositeCount, string requesterService, string topic);
}
