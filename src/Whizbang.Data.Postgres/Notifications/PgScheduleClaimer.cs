// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;
using Whizbang.Core.Notifications;
using Whizbang.Core.Observability;
using Whizbang.Core.Temporal;
using Whizbang.Core.Workers;

namespace Whizbang.Data.Postgres.Notifications;

/// <summary>
/// Postgres <see cref="IScheduleClaimer"/> — the provider side of the temporal engine's authoritative
/// fire. Opens a direct connection (same resolution as the poll sources) and calls
/// <c>wh_claim_due_schedules</c>, which atomically leases due owned schedules, spawns each occurrence
/// into the outbox, advances <c>next_fire_at</c>, and logs the run. The DB clock decides which schedules
/// are due (p_now omitted), so multi-instance clock skew can't fire early or double-fire.
/// </summary>
/// <docs>fundamentals/temporal/temporal-engine</docs>
#pragma warning disable S107 // DI-injection constructor: every parameter is a registered service or an optional seam, and a parameter object would only move the list (same reasoning as Dispatcher)
public sealed partial class PgScheduleClaimer(
  IOptions<WhizbangNotificationOptions> options,
  IConfiguration configuration,
  IServiceInstanceProvider instanceProvider,
  IOptions<ClaimWorkerOptions> claimWorkerOptions,
  IOptions<TemporalOptions> temporalOptions,
  ILogger<PgScheduleClaimer> logger,
  INotificationConnectionStringFallback? connectionStringFallback = null,
  INotificationDataSource? notificationDataSource = null) : IScheduleClaimer {
#pragma warning restore S107
  // #2575: the claim's ownership rule (SchedulePredicate), built once.
  private static readonly string _nextFireSql = @"
      SELECT MIN(sc.next_fire_at)
      FROM wh_schedules sc
      LEFT JOIN wh_active_streams s ON s.stream_id = sc.stream_id
      WHERE sc.status = 0
        AND " + SchedulePredicate.OWNED_BY_INSTANCE.Replace(SchedulePredicate.INSTANCE_PARAMETER, "@i", StringComparison.Ordinal);

  private readonly WhizbangNotificationOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
  private readonly IConfiguration _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
  private readonly IServiceInstanceProvider _instanceProvider = instanceProvider ?? throw new ArgumentNullException(nameof(instanceProvider));
  private readonly INotificationConnectionStringFallback? _connectionStringFallback = connectionStringFallback;
  private readonly INotificationDataSource? _notificationDataSource = notificationDataSource;
  private readonly int _partitionCount = (claimWorkerOptions?.Value ?? new ClaimWorkerOptions()).PartitionCount;
  private readonly int _leaseSeconds = (temporalOptions?.Value ?? new TemporalOptions()).LeaseDurationSeconds;
  private readonly ILogger<PgScheduleClaimer> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

  /// <inheritdoc />
  public async Task<int> ClaimDueSchedulesAsync(int limit, CancellationToken cancellationToken = default) {
    var resolution = NotificationConnectionStringResolver.Resolve(_options, _configuration, _connectionStringFallback).WithAppliedSearchPath();
    // Prefer the registered notification data source - the only path that
    // works under UseNpgsql(NpgsqlDataSource), where the resolver's fallback
    // string has had its credentials stripped by Npgsql.
    var plan = NotificationConnectionPlan.Create(_notificationDataSource, resolution);
    if (!plan.IsAvailable) {
      return 0;   // no DB connection yet — the doorbell / backstop drives us when it returns
    }

    await using var conn = await plan.OpenAsync(cancellationToken).ConfigureAwait(false);
    await using var cmd = conn.CreateWhizbangCommand();
    // p_now omitted => the function uses NOW() (DB-clock authority). p_lease_expiry is the outbox
    // publish lease for the spawned occurrences.
    cmd.CommandText = @"
      SELECT count(*)::int FROM wh_claim_due_schedules(
        p_instance_id => @i, p_lease_expiry => @lease, p_partition_count => @pc, p_limit => @limit)";
    cmd.Parameters.Add(new NpgsqlParameter("i", NpgsqlDbType.Uuid) { Value = _instanceProvider.InstanceId });
    cmd.Parameters.Add(new NpgsqlParameter("lease", NpgsqlDbType.TimestampTz) {
      Value = DateTimeOffset.UtcNow.AddSeconds(_leaseSeconds)
    });
    cmd.Parameters.Add(new NpgsqlParameter("pc", NpgsqlDbType.Integer) { Value = _partitionCount });
    cmd.Parameters.Add(new NpgsqlParameter(nameof(limit), NpgsqlDbType.Integer) { Value = limit });
    var result = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
    // count(*) always yields one non-null row.
    var claimed = Convert.ToInt32(result, CultureInfo.InvariantCulture);
    if (claimed > 0) {
      // #720: each spawned occurrence queued its doorbell inside the claim's transaction; ring them after the commit.
      await DoorbellRinger.RingAsync(conn, DoorbellRinger.FUNCTION_NAME, _logger, cancellationToken).ConfigureAwait(false);
    }
    return claimed;
  }

  /// <inheritdoc />
  public async Task<DateTimeOffset?> GetNextFireTimeAsync(CancellationToken cancellationToken = default) {
    var resolution = NotificationConnectionStringResolver.Resolve(_options, _configuration, _connectionStringFallback).WithAppliedSearchPath();
    // Prefer the registered notification data source - the only path that
    // works under UseNpgsql(NpgsqlDataSource), where the resolver's fallback
    // string has had its credentials stripped by Npgsql.
    var plan = NotificationConnectionPlan.Create(_notificationDataSource, resolution);
    if (!plan.IsAvailable) {
      return null;
    }

    await using var conn = await plan.OpenAsync(cancellationToken).ConfigureAwait(false);
    await using var cmd = conn.CreateWhizbangCommand();
    // #2575: the claim's ownership rule, so the wake is armed for every schedule the claim would take: in map mode the
    // schedule's partition owner (wh_i_own), otherwise the stream's stored owner or, when no live instance holds the
    // stream, anyone (wh_schedule_owner_admits). The switch and the live set are each read once per statement.
    cmd.CommandText = _nextFireSql;
    cmd.Parameters.Add(new NpgsqlParameter("i", NpgsqlDbType.Uuid) { Value = _instanceProvider.InstanceId });
    var result = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
    if (result is null or DBNull) {
      return null;
    }
    return new DateTimeOffset(DateTime.SpecifyKind((DateTime)result, DateTimeKind.Utc), TimeSpan.Zero);
  }

  /// <inheritdoc />
  /// <remarks>
  /// #2575: every Active schedule, not only this instance's, since the failure it reports is a schedule no instance
  /// takes. The age and the bound are judged by the database clock, as the claim's due decision is. The scan is the
  /// partial index on Active schedules' fire times.
  /// </remarks>
  public async Task<ScheduleOverdueReading?> GetOverdueSchedulesAsync(TimeSpan bound, CancellationToken cancellationToken = default) {
    var resolution = NotificationConnectionStringResolver.Resolve(_options, _configuration, _connectionStringFallback).WithAppliedSearchPath();
    var plan = NotificationConnectionPlan.Create(_notificationDataSource, resolution);
    if (!plan.IsAvailable) {
      return null;
    }

    await using var conn = await plan.OpenAsync(cancellationToken).ConfigureAwait(false);
    await using var cmd = conn.CreateWhizbangCommand();
    // The window count runs before the LIMIT, so the one row carries the whole count beside the oldest schedule.
    cmd.CommandText = @"
      SELECT count(*) OVER ()::int, EXTRACT(EPOCH FROM (NOW() - sc.next_fire_at))::float8, sc.schedule_id, sc.schedule_key
      FROM wh_schedules sc
      WHERE sc.status = 0 AND sc.next_fire_at < NOW() - @bound
      ORDER BY sc.next_fire_at
      LIMIT 1";
    cmd.Parameters.Add(new NpgsqlParameter(nameof(bound), NpgsqlDbType.Interval) { Value = bound });
    await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
    if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
      return ScheduleOverdueReading.None;
    }
    return new ScheduleOverdueReading(
      reader.GetInt32(0),
      TimeSpan.FromSeconds(reader.GetDouble(1)),
      reader.GetGuid(2),
      await reader.IsDBNullAsync(3, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(3));
  }
}
