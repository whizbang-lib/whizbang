// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Whizbang.Core.Notifications;
using Whizbang.Core.Observability;
using Whizbang.Core.Signals;

namespace Whizbang.Data.Postgres.Notifications;

/// <summary>
/// Postgres pull source for <see cref="ScheduleDueSignal"/> — the correctness backstop for the
/// temporal engine's fast path (the arm-on-mutation NOTIFY + the in-memory timer). On its tick it
/// runs an <c>EXISTS</c> over <c>wh_schedules</c> scoped to the schedules this pod may claim (its own streams, and any
/// stream no live instance holds, by the claim's rule <c>wh_schedule_owner_admits</c>, #2575) and, when an
/// Active schedule is due (<c>next_fire_at &lt;= NOW()</c>), raises <see cref="ScheduleDueSignal"/> so the
/// worker claims + fires due schedules. Exists to catch missed notifies, NOTIFY-less providers, and
/// rebalance staleness — cadence adapts via <see cref="INotifySignalingGate"/>.
/// </summary>
/// <docs>fundamentals/temporal/temporal-engine</docs>
public sealed class PgScheduleDuePollSource(
  TimeProvider clock,
  IOptions<WhizbangNotificationOptions> options,
  IConfiguration configuration,
  IServiceInstanceProvider instanceProvider,
  ILogger<PgScheduleDuePollSource> logger,
  INotificationConnectionStringFallback? connectionStringFallback = null,
  INotifySignalingGate? signalingGate = null
) : PgWorkAvailablePollSourceBase<ScheduleDueSignal>(
  clock,
  TimeSpan.FromMilliseconds(WorkAvailablePollDefaults.INTERVAL_MILLISECONDS),
  options, configuration, instanceProvider, logger, connectionStringFallback, signalingGate) {
  /// <inheritdoc />
  protected override string DetectSql => @"
    SELECT EXISTS (
      SELECT 1
      FROM wh_schedules sc
      LEFT JOIN wh_active_streams s ON s.stream_id = sc.stream_id
      WHERE sc.status = 0
        AND wh_schedule_owner_admits(
          s.assigned_instance_id, @instance_id, ARRAY(SELECT l.instance_id FROM wh_live_instance_ids() l))
        AND sc.next_fire_at <= NOW()
      LIMIT 1
    )";
}
