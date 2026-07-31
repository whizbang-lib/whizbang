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
/// <docs>proposals/stream-integrity</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RedeliveryRequestReceptorTests.cs</tests>
public sealed partial class RedeliveryRequestReceptor(
    IServiceScopeFactory scopeFactory,
    ILogger<RedeliveryRequestReceptor> logger) : IReceptor<RequestRedeliveryCommand> {

  /// <inheritdoc />
  public async ValueTask HandleAsync(RequestRedeliveryCommand message, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(message);
    await using var scope = scopeFactory.CreateAsyncScope();
    var services = scope.ServiceProvider;
    var coordinator = services.GetService<IWorkCoordinator>();
    var transport = services.GetService<ITransport>();
    var eventStore = services.GetService<IEventStore>();
    var eventTypeProvider = services.GetService<IEventTypeProvider>();
    var envelopeSerializer = services.GetService<IEnvelopeSerializer>();
    if (coordinator is null || transport is null || eventStore is null || eventTypeProvider is null || envelopeSerializer is null) {
      LogMissingInfrastructure(logger, coordinator is null, transport is null, eventStore is null,
        eventTypeProvider is null, envelopeSerializer is null);
      return;
    }

    var options = services.GetService<RedeliveryPumpOptions>() ?? new RedeliveryPumpOptions();
    var cap = options.MaxEventsPerRequest;
    var maxEvents = message.MaxEvents is { } requested ? Math.Min(requested, cap) : cap;

    var selected = await coordinator.SelectRedeliveryEventsAsync(new RedeliveryRequest {
      TenantScope = message.TenantScope,
      EventTypes = message.EventTypes,
      StreamIds = message.StreamIds,
      FromCommitSequence = message.FromCommitSequence,
      ToCommitSequence = message.ToCommitSequence,
      MaxEvents = maxEvents
    }, cancellationToken).ConfigureAwait(false);

    if (selected.Count == 0) {
      LogNothingSelected(logger, message.RequesterService);
      return;
    }

    var pump = new RedeliveryPump(
      transport, eventStore, eventTypeProvider, envelopeSerializer,
      services.GetService<IServiceInstanceProvider>(), options);
    var composites = await pump
      .PublishAsync(selected, message.Topic, message.RequesterService, cancellationToken)
      .ConfigureAwait(false);
    LogRedeliveryPublished(logger, selected.Count, composites, message.RequesterService, message.Topic);
  }

  [LoggerMessage(EventId = 47, Level = LogLevel.Warning,
    Message = "RequestRedeliveryCommand received but required infrastructure is missing " +
              "(coordinator={CoordinatorMissing}, transport={TransportMissing}, eventStore={EventStoreMissing}, " +
              "eventTypeProvider={EventTypeProviderMissing}, envelopeSerializer={EnvelopeSerializerMissing}); ignored")]
  static partial void LogMissingInfrastructure(ILogger logger, bool coordinatorMissing, bool transportMissing, bool eventStoreMissing, bool eventTypeProviderMissing, bool envelopeSerializerMissing);

  [LoggerMessage(EventId = 48, Level = LogLevel.Information,
    Message = "Re-delivery request from {RequesterService} selected no events; nothing published")]
  static partial void LogNothingSelected(ILogger logger, string requesterService);

  [LoggerMessage(EventId = 49, Level = LogLevel.Information,
    Message = "Re-delivered {EventCount} events as {CompositeCount} composites to {RequesterService} on {Topic}")]
  static partial void LogRedeliveryPublished(ILogger logger, int eventCount, int compositeCount, string requesterService, string topic);
}
