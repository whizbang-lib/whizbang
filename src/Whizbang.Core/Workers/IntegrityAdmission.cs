// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Workers;

/// <summary>
/// The one way a scheduled integrity activity asks the housekeeping idle gate whether it may run (#2001): measure the
/// service's settledness, ask for <see cref="HousekeepingCoordinator.Activity.Integrity"/> under the requester's own
/// deferral budget, and release the slot when the returned ticket is disposed.
/// </summary>
/// <remarks>
/// Used by the integrity delivery drain, the deep audit and the paced repair drain. Checkpoint publishing does not
/// ask: a consumer reads a missing checkpoint as its liveness alarm. A host without a
/// <see cref="HousekeepingCoordinator"/> runs ungated, and a reading that cannot be taken is logged and admitted
/// unmeasured: a gate that cannot measure must never silently disable what it gates.
/// </remarks>
/// <docs>operations/workers/housekeeping-arbitration</docs>
/// <tests>tests/Whizbang.Core.Tests/Workers/IntegrityDeliveryWorkerTests.cs</tests>
internal static partial class IntegrityAdmission {
  /// <summary>An admission decision, and the slot it holds until disposed.</summary>
  /// <remarks>
  /// Asynchronously disposable only, so a caller cannot release the slot without also clearing the requester's stored
  /// first deferral (#2288): a synchronous <c>using</c> does not compile.
  /// </remarks>
  internal readonly struct Ticket : IAsyncDisposable {
    private readonly HousekeepingCoordinator? _holder;
    private readonly IWorkCoordinator? _coordinator;
    private readonly HousekeepingCoordinator.Decision _decision;
    private readonly ILogger _logger;

    // A ticket that holds no slot: admitted ungated (no coordinator to ask), or refused.
    internal Ticket(bool granted) : this(granted, holder: null, coordinator: null, decision: default, logger: null) { }

    internal Ticket(
        bool granted, HousekeepingCoordinator? holder, IWorkCoordinator? coordinator,
        HousekeepingCoordinator.Decision decision, ILogger? logger) {
      Granted = granted;
      _holder = holder;
      _coordinator = coordinator;
      _decision = decision;
      _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
    }

    /// <summary>Whether the activity may run now.</summary>
    public bool Granted { get; }

    /// <summary>
    /// Releases the integrity slot, if this ticket holds it, and clears the requester's stored first deferral when the
    /// admitting decision asked for it.
    /// </summary>
    /// <returns>A task that completes when the slot is released and the record cleared.</returns>
    public async ValueTask DisposeAsync() {
      if (_holder is null) {
        return;
      }
      _holder.End(HousekeepingCoordinator.Activity.Integrity);
      await HousekeepingDeferralStore.ClearAsync(_coordinator, _decision, _logger).ConfigureAwait(false);
    }
  }

  /// <summary>Asks the idle gate whether <paramref name="requester"/> may run integrity work now.</summary>
  /// <param name="services">A scope's services, where the housekeeping coordinator is found.</param>
  /// <param name="coordinator">The coordinator that measures the service's backlog.</param>
  /// <param name="requester">The asking activity; it spends and re-arms its own deferral budget.</param>
  /// <param name="logger">Where a refused admission and an unmeasured reading are reported.</param>
  /// <param name="cancellationToken">Cancellation.</param>
  /// <returns>The decision; dispose it when the activity ends.</returns>
  internal static async ValueTask<Ticket> TryAdmitAsync(
      IServiceProvider services, IWorkCoordinator coordinator, string requester, ILogger logger,
      CancellationToken cancellationToken) {
    if (services.GetService<HousekeepingCoordinator>() is not { } housekeeping) {
      return new Ticket(granted: true);
    }
    ServiceBacklog? backlog = null;
    try {
      backlog = await coordinator.CountServiceBacklogAsync(cancellationToken).ConfigureAwait(false);
    } catch (Exception ex) when (ex is not OperationCanceledException) {
      LogBacklogUnmeasured(logger, requester, ex);
    }
    var decision = housekeeping.TryBegin(HousekeepingCoordinator.Activity.Integrity, backlog, requester);
    // #2288: the requester's first deferral of a run goes to the store, so its forced-pass ceiling survives a restart.
    await HousekeepingDeferralStore.RecordAsync(coordinator, decision, logger, cancellationToken).ConfigureAwait(false);
    if (!decision.Granted) {
      LogDeferred(logger, requester, decision.Reason);
      return new Ticket(granted: false);
    }
    return new Ticket(granted: true, holder: housekeeping, coordinator, decision, logger);
  }

  [LoggerMessage(Level = LogLevel.Warning,
    Message = "{Requester}: the service backlog could not be measured, so integrity work runs unmeasured this time")]
  private static partial void LogBacklogUnmeasured(ILogger logger, string requester, Exception exception);

  [LoggerMessage(Level = LogLevel.Debug,
    Message = "{Requester}: integrity work deferred by the housekeeping idle gate ({Reason})")]
  private static partial void LogDeferred(ILogger logger, string requester, HousekeepingCoordinator.Verdict reason);
}
