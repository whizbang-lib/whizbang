// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Whizbang.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.SystemEvents;

namespace Whizbang.Data.EFCore.Postgres;

/// <summary>
/// The built-in origin-side receptor for <see cref="RequestAuditRebuildCommand"/> (#1724): it runs an
/// <see cref="AuditLedgerRebuild"/> over this service's stored events through the redelivery selection
/// (<see cref="IWorkCoordinator.SelectRedeliveryEventsAsync"/>) and queues the re-derived records to the
/// <see cref="IDeferredOutboxChannel"/>, from which the next work batch writes them to the outbox like any live audit
/// record. Lives in the driver assembly for the reason <see cref="RedeliveryRequestReceptor"/> does: a receptor in
/// <c>Whizbang.Core</c> would make the receptor-discovery generator emit registrations that collide with every
/// consumer's. The selection is capped and paged by <see cref="RedeliveryPumpOptions"/>. Inert (logged) on a host
/// without a work coordinator or the deferred outbox channel.
/// </summary>
/// <docs>fundamentals/security/audit-logging#rebuilding-the-audit-ledger</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/AuditRebuildRequestReceptorTests.cs</tests>
public sealed partial class AuditRebuildRequestReceptor(
    IServiceScopeFactory scopeFactory,
    ILogger<AuditRebuildRequestReceptor> logger) : IReceptor<RequestAuditRebuildCommand> {

  /// <inheritdoc />
  public async ValueTask HandleAsync(RequestAuditRebuildCommand message, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(message);
    await using var scope = scopeFactory.CreateAsyncScope();
    var services = scope.ServiceProvider;
    var coordinator = services.GetService<IWorkCoordinator>();
    var channel = services.GetService<IDeferredOutboxChannel>();
    if (coordinator is null || channel is null) {
      LogMissingInfrastructure(logger, coordinator is null, channel is null);
      return;
    }

    // Defaults where the host registered nothing: audit off (so nothing is rebuilt), an empty catalog (every stored
    // type decided by the mode alone), the system clock, and the redelivery pump's cap and page size.
    var rebuild = new AuditLedgerRebuild(
      services.GetService<IMessageTypeCatalog>() ?? NullMessageTypeCatalog.Instance,
      services.GetService<IOptions<SystemEventOptions>>()?.Value ?? new SystemEventOptions(),
      services.GetService<TimeProvider>());
    var pump = services.GetService<RedeliveryPumpOptions>() ?? new RedeliveryPumpOptions();
    var result = await rebuild.RunAsync(
      coordinator.SelectRedeliveryEventsAsync, channel, message, pump.MaxEventsPerRequest, Math.Max(1, pump.SelectPageSize),
      cancellationToken).ConfigureAwait(false);
    LogRebuilt(logger, result.Rederived, result.Selected);
  }

  [LoggerMessage(EventId = 99, Level = LogLevel.Warning,
    Message = "RequestAuditRebuildCommand received but required infrastructure is missing " +
              "(coordinator={CoordinatorMissing}, deferredOutboxChannel={ChannelMissing}); ignored")]
  static partial void LogMissingInfrastructure(ILogger logger, bool coordinatorMissing, bool channelMissing);

  [LoggerMessage(EventId = 100, Level = LogLevel.Information,
    Message = "Audit ledger rebuild queued {RecordCount} records from {EventCount} stored events")]
  static partial void LogRebuilt(ILogger logger, int recordCount, int eventCount);
}
