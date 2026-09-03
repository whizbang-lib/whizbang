using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;
using Whizbang.Core.Notifications;
using Whizbang.Core.Observability;
using Whizbang.Core.Temporal;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Data.Postgres.Notifications;

/// <summary>
/// Postgres <see cref="IScheduleManager"/> — the management API over <c>wh_create_schedule</c> and
/// <c>wh_transition_schedule</c>. Opens a direct connection (same resolution as the poll sources /
/// claimer); the DB is the source of truth. Transition status codes: 0=resume/Active, 1=Pause, 3=Cancel.
/// </summary>
/// <docs>fundamentals/temporal/temporal-engine</docs>
public sealed class PgScheduleManager : IScheduleManager {
  private const short STATUS_ACTIVE = 0;
  private const short STATUS_PAUSED = 1;
  private const short STATUS_CANCELED = 3;

  private readonly WhizbangNotificationOptions _options;
  private readonly IConfiguration _configuration;
  private readonly INotificationConnectionStringFallback? _connectionStringFallback;
  private readonly INotificationDataSource? _notificationDataSource;
  private readonly ILogger<PgScheduleManager> _logger;
  private readonly IServiceInstanceProvider _instanceProvider;
  private readonly int _partitionCount;
  private readonly int _leaseSeconds;

  /// <summary>Constructor.</summary>
  public PgScheduleManager(
    IOptions<WhizbangNotificationOptions> options,
    IConfiguration configuration,
    IServiceInstanceProvider instanceProvider,
    IOptions<ClaimWorkerOptions> claimWorkerOptions,
    IOptions<TemporalOptions> temporalOptions,
    ILogger<PgScheduleManager> logger,
    INotificationConnectionStringFallback? connectionStringFallback = null,
    INotificationDataSource? notificationDataSource = null) {
    _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
    _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
    _instanceProvider = instanceProvider ?? throw new ArgumentNullException(nameof(instanceProvider));
    _partitionCount = (claimWorkerOptions?.Value ?? new ClaimWorkerOptions()).PartitionCount;
    _leaseSeconds = (temporalOptions?.Value ?? new TemporalOptions()).LeaseDurationSeconds;
    _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    _connectionStringFallback = connectionStringFallback;
    _notificationDataSource = notificationDataSource;
  }

  /// <inheritdoc />
  public async Task<ScheduleHandle> CreateAsync(ScheduleDefinition definition, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(definition);
    if (string.IsNullOrWhiteSpace(definition.EventType)) {
      throw new ArgumentException("ScheduleDefinition.EventType is required.", nameof(definition));
    }
    if (definition.AuthorityPrincipalId == Guid.Empty) {
      throw new ArgumentException(
        "ScheduleDefinition.AuthorityPrincipalId is required — an occurrence fires with no interactive user, "
        + "so the principal it runs as must be stated explicitly.", nameof(definition));
    }
    if (definition.Kind == RecurrenceKind.Interval && definition.Interval is null) {
      throw new ArgumentException("Interval recurrence requires ScheduleDefinition.Interval.", nameof(definition));
    }
    if (definition.Kind == RecurrenceKind.Cron && string.IsNullOrWhiteSpace(definition.Cron)) {
      throw new ArgumentException("Cron recurrence requires ScheduleDefinition.Cron.", nameof(definition));
    }

    await using var conn = await _openAsync(cancellationToken).ConfigureAwait(false);
    if (conn is null) {
      throw new InvalidOperationException("No database connection available to create a schedule.");
    }

    await using var cmd = conn.CreateCommand();
    cmd.CommandText = @"
      SELECT o_schedule_id, o_next_fire_at, o_was_created FROM wh_create_schedule(
        p_schedule_id => @id, p_schedule_key => @key, p_stream_id => @stream, p_partition_number => @part,
        p_recurrence_kind => @kind, p_interval_ms => @interval, p_cron => @cron, p_timezone => @tz,
        p_start_at => @start, p_until_at => @until, p_max_occurrences => @maxocc,
        p_misfire_policy => @misfire, p_delivery_guarantee => @delivery,
        p_event_type => @etype, p_event_data => @edata, p_scope => @scope,
        p_catch_up_lookback_ms => @lookback,
        p_authority_principal_id => @authority, p_authority_claims => @claims)";
    cmd.Parameters.Add(new NpgsqlParameter("id", NpgsqlDbType.Uuid) {
      Value = definition.ScheduleId ?? TrackedGuid.NewMedo().Value
    });
    cmd.Parameters.Add(new NpgsqlParameter("key", NpgsqlDbType.Text) { Value = (object?)definition.Key ?? DBNull.Value });
    cmd.Parameters.Add(new NpgsqlParameter("stream", NpgsqlDbType.Uuid) { Value = definition.StreamId });
    cmd.Parameters.Add(new NpgsqlParameter("part", NpgsqlDbType.Integer) { Value = definition.PartitionNumber });
    cmd.Parameters.Add(new NpgsqlParameter("kind", NpgsqlDbType.Smallint) { Value = (short)definition.Kind });
    cmd.Parameters.Add(new NpgsqlParameter("interval", NpgsqlDbType.Bigint) {
      Value = definition.Interval is { } iv ? (long)iv.TotalMilliseconds : (object)DBNull.Value
    });
    cmd.Parameters.Add(new NpgsqlParameter("cron", NpgsqlDbType.Text) { Value = (object?)definition.Cron ?? DBNull.Value });
    cmd.Parameters.Add(new NpgsqlParameter("tz", NpgsqlDbType.Text) { Value = (object?)definition.TimeZone ?? DBNull.Value });
    cmd.Parameters.Add(new NpgsqlParameter("start", NpgsqlDbType.TimestampTz) { Value = (object?)definition.StartAt ?? DBNull.Value });
    cmd.Parameters.Add(new NpgsqlParameter("until", NpgsqlDbType.TimestampTz) { Value = (object?)definition.UntilAt ?? DBNull.Value });
    cmd.Parameters.Add(new NpgsqlParameter("maxocc", NpgsqlDbType.Bigint) { Value = (object?)definition.MaxOccurrences ?? DBNull.Value });
    cmd.Parameters.Add(new NpgsqlParameter("misfire", NpgsqlDbType.Smallint) { Value = (short)definition.MisfirePolicy });
    cmd.Parameters.Add(new NpgsqlParameter("delivery", NpgsqlDbType.Smallint) { Value = (short)definition.DeliveryGuarantee });
    cmd.Parameters.Add(new NpgsqlParameter("etype", NpgsqlDbType.Text) { Value = definition.EventType });
    cmd.Parameters.Add(new NpgsqlParameter("edata", NpgsqlDbType.Jsonb) { Value = (object?)definition.EventDataJson ?? DBNull.Value });
    cmd.Parameters.Add(new NpgsqlParameter("scope", NpgsqlDbType.Jsonb) { Value = (object?)definition.ScopeJson ?? DBNull.Value });
    cmd.Parameters.Add(new NpgsqlParameter("lookback", NpgsqlDbType.Bigint) {
      Value = definition.CatchUpLookback is { } lb ? (long)lb.TotalMilliseconds : (object)DBNull.Value
    });
    cmd.Parameters.Add(new NpgsqlParameter("authority", NpgsqlDbType.Uuid) { Value = definition.AuthorityPrincipalId });
    cmd.Parameters.Add(new NpgsqlParameter("claims", NpgsqlDbType.Jsonb) {
      Value = (object?)definition.AuthorityClaimsJson ?? DBNull.Value
    });

    await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
    _ = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
    var scheduleId = reader.GetGuid(0);
    var nextFire = new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(1), DateTimeKind.Utc), TimeSpan.Zero);
    var wasCreated = reader.GetBoolean(2);
    return new ScheduleHandle(scheduleId, nextFire, wasCreated);
  }

  /// <inheritdoc />
  public Task<bool> PauseAsync(Guid scheduleId, long? expectedVersion = null, CancellationToken cancellationToken = default) =>
    _transitionAsync(scheduleId, STATUS_PAUSED, expectedVersion, cancellationToken);

  /// <inheritdoc />
  public Task<bool> ResumeAsync(Guid scheduleId, long? expectedVersion = null, CancellationToken cancellationToken = default) =>
    _transitionAsync(scheduleId, STATUS_ACTIVE, expectedVersion, cancellationToken);

  /// <inheritdoc />
  public Task<bool> CancelAsync(Guid scheduleId, long? expectedVersion = null, CancellationToken cancellationToken = default) =>
    _transitionAsync(scheduleId, STATUS_CANCELED, expectedVersion, cancellationToken);

  /// <inheritdoc />
  public async Task<Guid?> TriggerNowAsync(Guid scheduleId, CancellationToken cancellationToken = default) {
    await using var conn = await _openAsync(cancellationToken).ConfigureAwait(false);
    if (conn is null) {
      return null;
    }
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT o_triggered, o_occurrence_id FROM wh_trigger_schedule_now(@id, @i, @lease, @pc)";
    cmd.Parameters.Add(new NpgsqlParameter("id", NpgsqlDbType.Uuid) { Value = scheduleId });
    cmd.Parameters.Add(new NpgsqlParameter("i", NpgsqlDbType.Uuid) { Value = _instanceProvider.InstanceId });
    cmd.Parameters.Add(new NpgsqlParameter("lease", NpgsqlDbType.TimestampTz) {
      Value = DateTimeOffset.UtcNow.AddSeconds(_leaseSeconds)
    });
    cmd.Parameters.Add(new NpgsqlParameter("pc", NpgsqlDbType.Integer) { Value = _partitionCount });

    await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
    if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) || !reader.GetBoolean(0)) {
      return null;   // missing or terminal
    }
    return reader.GetGuid(1);
  }

  /// <inheritdoc />
  public async Task<ScheduleUpdateResult?> UpdateAsync(
      Guid scheduleId, ScheduleUpdate update, long? expectedVersion = null, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(update);
    if (update.Kind == RecurrenceKind.Interval && update.Interval is null) {
      throw new ArgumentException("Interval recurrence requires ScheduleUpdate.Interval.", nameof(update));
    }
    if (update.Kind == RecurrenceKind.Cron && string.IsNullOrWhiteSpace(update.Cron)) {
      throw new ArgumentException("Cron recurrence requires ScheduleUpdate.Cron.", nameof(update));
    }

    await using var conn = await _openAsync(cancellationToken).ConfigureAwait(false);
    if (conn is null) {
      return null;
    }
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = @"
      SELECT o_updated, o_next_fire_at, o_version FROM wh_update_schedule(
        @id, @ver, @kind, @interval, @cron, @tz, @start, @until, @maxocc, @misfire, @delivery, @edata, @scope,
        @lookback)";
    cmd.Parameters.Add(new NpgsqlParameter("id", NpgsqlDbType.Uuid) { Value = scheduleId });
    cmd.Parameters.Add(new NpgsqlParameter("ver", NpgsqlDbType.Bigint) { Value = (object?)expectedVersion ?? DBNull.Value });
    cmd.Parameters.Add(new NpgsqlParameter("kind", NpgsqlDbType.Smallint) { Value = (short)update.Kind });
    cmd.Parameters.Add(new NpgsqlParameter("interval", NpgsqlDbType.Bigint) {
      Value = update.Interval is { } iv ? (long)iv.TotalMilliseconds : (object)DBNull.Value
    });
    cmd.Parameters.Add(new NpgsqlParameter("cron", NpgsqlDbType.Text) { Value = (object?)update.Cron ?? DBNull.Value });
    cmd.Parameters.Add(new NpgsqlParameter("tz", NpgsqlDbType.Text) { Value = (object?)update.TimeZone ?? DBNull.Value });
    cmd.Parameters.Add(new NpgsqlParameter("start", NpgsqlDbType.TimestampTz) { Value = (object?)update.StartAt ?? DBNull.Value });
    cmd.Parameters.Add(new NpgsqlParameter("until", NpgsqlDbType.TimestampTz) { Value = (object?)update.UntilAt ?? DBNull.Value });
    cmd.Parameters.Add(new NpgsqlParameter("maxocc", NpgsqlDbType.Bigint) { Value = (object?)update.MaxOccurrences ?? DBNull.Value });
    cmd.Parameters.Add(new NpgsqlParameter("misfire", NpgsqlDbType.Smallint) { Value = (short)update.MisfirePolicy });
    cmd.Parameters.Add(new NpgsqlParameter("delivery", NpgsqlDbType.Smallint) { Value = (short)update.DeliveryGuarantee });
    cmd.Parameters.Add(new NpgsqlParameter("edata", NpgsqlDbType.Jsonb) { Value = (object?)update.EventDataJson ?? DBNull.Value });
    cmd.Parameters.Add(new NpgsqlParameter("scope", NpgsqlDbType.Jsonb) { Value = (object?)update.ScopeJson ?? DBNull.Value });
    cmd.Parameters.Add(new NpgsqlParameter("lookback", NpgsqlDbType.Bigint) {
      Value = update.CatchUpLookback is { } lb ? (long)lb.TotalMilliseconds : (object)DBNull.Value
    });

    await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
    if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) || !reader.GetBoolean(0)) {
      return null;   // missing / terminal / version mismatch
    }
    var nextFire = new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(1), DateTimeKind.Utc), TimeSpan.Zero);
    return new ScheduleUpdateResult(nextFire, reader.GetInt64(2));
  }

  private async Task<bool> _transitionAsync(Guid scheduleId, short targetStatus, long? expectedVersion, CancellationToken cancellationToken) {
    await using var conn = await _openAsync(cancellationToken).ConfigureAwait(false);
    if (conn is null) {
      return false;
    }
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT o_updated FROM wh_transition_schedule(@id, @target, @ver)";
    cmd.Parameters.Add(new NpgsqlParameter("id", NpgsqlDbType.Uuid) { Value = scheduleId });
    cmd.Parameters.Add(new NpgsqlParameter("target", NpgsqlDbType.Smallint) { Value = targetStatus });
    cmd.Parameters.Add(new NpgsqlParameter("ver", NpgsqlDbType.Bigint) { Value = (object?)expectedVersion ?? DBNull.Value });
    var result = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
    return result is bool b && b;
  }

  private async Task<NpgsqlConnection?> _openAsync(CancellationToken cancellationToken) {
    var resolution = NotificationConnectionStringResolver.Resolve(_options, _configuration, _connectionStringFallback).WithAppliedSearchPath();
    // Prefer the registered notification data source - the only path that
    // works under UseNpgsql(NpgsqlDataSource), where the resolver's fallback
    // string has had its credentials stripped by Npgsql.
    var plan = NotificationConnectionPlan.Create(_notificationDataSource, resolution);
    if (!plan.IsAvailable) {
      return null;
    }
    return await plan.OpenAsync(cancellationToken).ConfigureAwait(false);
  }
}
