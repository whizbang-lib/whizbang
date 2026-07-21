using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Whizbang.Core.Notifications;
using Whizbang.Core.Observability;
using Whizbang.Core.Signals;

namespace Whizbang.Data.Postgres.Notifications;

/// <summary>
/// Concrete Postgres pull sources for the three work-available signal types. Each subclass
/// supplies a small <c>EXISTS</c> query against its work table scoped to this pod's
/// <see cref="IServiceInstanceProvider.InstanceId"/>. The shared plumbing (connection resolve,
/// parameter bind, error logging) lives on <see cref="PgWorkAvailablePollSourceBase{TSignal}"/>.
/// </summary>
/// <docs>fundamentals/signal-bus/signal-bus</docs>
internal static class PgWorkAvailablePollSourcesOverview { }

/// <summary>Pull source for <see cref="WorkOutboxAvailableSignal"/>.</summary>
public sealed class PgOutboxWorkAvailablePollSource(
  TimeProvider clock,
  IOptions<WhizbangNotificationOptions> options,
  IConfiguration configuration,
  IServiceInstanceProvider instanceProvider,
  ILogger<PgOutboxWorkAvailablePollSource> logger,
  INotificationConnectionStringFallback? connectionStringFallback = null,
  INotifySignalingGate? signalingGate = null
) : PgWorkAvailablePollSourceBase<WorkOutboxAvailableSignal>(
  clock,
  TimeSpan.FromMilliseconds(WorkAvailablePollDefaults.INTERVAL_MILLISECONDS),
  options, configuration, instanceProvider, logger, connectionStringFallback, signalingGate) {
  /// <inheritdoc />
  protected override string DetectSql => @"
    SELECT EXISTS (
      SELECT 1
      FROM wh_outbox o
      JOIN wh_active_streams s ON s.stream_id = o.stream_id
      WHERE s.assigned_instance_id = @instance_id
        AND o.processed_at IS NULL
      LIMIT 1
    )";
}

/// <summary>Pull source for <see cref="WorkInboxAvailableSignal"/>.</summary>
public sealed class PgInboxWorkAvailablePollSource(
  TimeProvider clock,
  IOptions<WhizbangNotificationOptions> options,
  IConfiguration configuration,
  IServiceInstanceProvider instanceProvider,
  ILogger<PgInboxWorkAvailablePollSource> logger,
  INotificationConnectionStringFallback? connectionStringFallback = null,
  INotifySignalingGate? signalingGate = null
) : PgWorkAvailablePollSourceBase<WorkInboxAvailableSignal>(
  clock,
  TimeSpan.FromMilliseconds(WorkAvailablePollDefaults.INTERVAL_MILLISECONDS),
  options, configuration, instanceProvider, logger, connectionStringFallback, signalingGate) {
  /// <inheritdoc />
  protected override string DetectSql => @"
    SELECT EXISTS (
      SELECT 1
      FROM wh_inbox i
      JOIN wh_active_streams s ON s.stream_id = i.stream_id
      WHERE s.assigned_instance_id = @instance_id
        AND i.processed_at IS NULL
      LIMIT 1
    )";
}

/// <summary>Pull source for <see cref="WorkPerspectiveAvailableSignal"/>.</summary>
public sealed class PgPerspectiveWorkAvailablePollSource(
  TimeProvider clock,
  IOptions<WhizbangNotificationOptions> options,
  IConfiguration configuration,
  IServiceInstanceProvider instanceProvider,
  ILogger<PgPerspectiveWorkAvailablePollSource> logger,
  INotificationConnectionStringFallback? connectionStringFallback = null,
  INotifySignalingGate? signalingGate = null
) : PgWorkAvailablePollSourceBase<WorkPerspectiveAvailableSignal>(
  clock,
  TimeSpan.FromMilliseconds(WorkAvailablePollDefaults.INTERVAL_MILLISECONDS),
  options, configuration, instanceProvider, logger, connectionStringFallback, signalingGate) {
  /// <inheritdoc />
  protected override string DetectSql => @"
    SELECT EXISTS (
      SELECT 1
      FROM wh_perspective_events e
      WHERE e.processed_at IS NULL
        AND (
          -- Orphaned/unleased work (e.g. cascade-created rows with instance_id NULL, or an expired
          -- lease): no wh_active_streams owner yet, so it must wake SOME instance to run claim_work
          -- (claim_orphaned assigns it via partition-modulo). Without this, orphaned perspective work
          -- is invisible to the backstop and never claimed when the bus is wired (no adaptive poll).
          e.instance_id IS NULL OR e.lease_expiry < NOW()
          -- Work already leased to this instance's owned stream.
          OR EXISTS (
            SELECT 1 FROM wh_active_streams s
            WHERE s.stream_id = e.stream_id AND s.assigned_instance_id = @instance_id
          )
        )
      LIMIT 1
    )";
}

internal static class WorkAvailablePollDefaults {
#pragma warning disable CA1707 // project convention: public const values use UPPER_CASE_WITH_UNDERSCORES
  // Relaxed backstop cadence — NOTIFY carries latency, the poll only exists to plug missed
  // notifies + drive when NOTIFY is unavailable. Tightening happens with a follow-on adaptive
  // interval when we wire the health gate through the poll sources.
  public const int INTERVAL_MILLISECONDS = 5_000;
#pragma warning restore CA1707
}
