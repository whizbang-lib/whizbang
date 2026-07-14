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
public sealed partial class PgScheduleClaimer : IScheduleClaimer {
  private readonly WhizbangNotificationOptions _options;
  private readonly IConfiguration _configuration;
  private readonly IServiceInstanceProvider _instanceProvider;
  private readonly INotificationConnectionStringFallback? _connectionStringFallback;
  private readonly int _partitionCount;
  private readonly int _leaseSeconds;
  private readonly ILogger<PgScheduleClaimer> _logger;

  /// <summary>Constructor.</summary>
  public PgScheduleClaimer(
    IOptions<WhizbangNotificationOptions> options,
    IConfiguration configuration,
    IServiceInstanceProvider instanceProvider,
    IOptions<ClaimWorkerOptions> claimWorkerOptions,
    IOptions<TemporalOptions> temporalOptions,
    ILogger<PgScheduleClaimer> logger,
    INotificationConnectionStringFallback? connectionStringFallback = null) {
    _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
    _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
    _instanceProvider = instanceProvider ?? throw new ArgumentNullException(nameof(instanceProvider));
    _partitionCount = (claimWorkerOptions?.Value ?? new ClaimWorkerOptions()).PartitionCount;
    _leaseSeconds = (temporalOptions?.Value ?? new TemporalOptions()).LeaseDurationSeconds;
    _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    _connectionStringFallback = connectionStringFallback;
  }

  /// <inheritdoc />
  public async Task<int> ClaimDueSchedulesAsync(int limit, CancellationToken cancellationToken = default) {
    var resolution = NotificationConnectionStringResolver.Resolve(_options, _configuration, _connectionStringFallback);
    if (resolution.ConnectionString is null) {
      return 0;   // no DB connection yet — the doorbell / backstop drives us when it returns
    }

    await using var conn = new NpgsqlConnection(resolution.ConnectionString);
    await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
    await using var cmd = conn.CreateCommand();
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
    cmd.Parameters.Add(new NpgsqlParameter("limit", NpgsqlDbType.Integer) { Value = limit });
    var result = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
    return result is null or DBNull ? 0 : Convert.ToInt32(result, CultureInfo.InvariantCulture);
  }

  /// <inheritdoc />
  public async Task<DateTimeOffset?> GetNextFireTimeAsync(CancellationToken cancellationToken = default) {
    var resolution = NotificationConnectionStringResolver.Resolve(_options, _configuration, _connectionStringFallback);
    if (resolution.ConnectionString is null) {
      return null;
    }

    await using var conn = new NpgsqlConnection(resolution.ConnectionString);
    await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = @"
      SELECT MIN(sc.next_fire_at)
      FROM wh_schedules sc
      JOIN wh_active_streams s ON s.stream_id = sc.stream_id
      WHERE s.assigned_instance_id = @i AND sc.status = 0";
    cmd.Parameters.Add(new NpgsqlParameter("i", NpgsqlDbType.Uuid) { Value = _instanceProvider.InstanceId });
    var result = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
    if (result is null or DBNull) {
      return null;
    }
    return new DateTimeOffset(DateTime.SpecifyKind((DateTime)result, DateTimeKind.Utc), TimeSpan.Zero);
  }
}
