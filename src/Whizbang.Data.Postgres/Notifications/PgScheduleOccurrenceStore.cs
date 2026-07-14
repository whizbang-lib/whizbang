using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;
using Whizbang.Core.Notifications;
using Whizbang.Core.Temporal;

namespace Whizbang.Data.Postgres.Notifications;

/// <summary>
/// Postgres <see cref="IScheduleOccurrenceStore"/> — the occurrence-level operations the pre-fire gate
/// needs (<c>wh_defer_occurrence</c> / <c>wh_log_schedule_run</c> / <c>wh_refresh_schedule_authority</c>).
/// Uses the same direct-connection resolution as the claimer and manager.
/// </summary>
/// <docs>fundamentals/temporal/pre-fire-hook</docs>
public sealed class PgScheduleOccurrenceStore : IScheduleOccurrenceStore {
  private readonly WhizbangNotificationOptions _options;
  private readonly IConfiguration _configuration;
  private readonly INotificationConnectionStringFallback? _connectionStringFallback;
  private readonly ILogger<PgScheduleOccurrenceStore> _logger;

  /// <summary>Constructor.</summary>
  public PgScheduleOccurrenceStore(
    IOptions<WhizbangNotificationOptions> options,
    IConfiguration configuration,
    ILogger<PgScheduleOccurrenceStore> logger,
    INotificationConnectionStringFallback? connectionStringFallback = null) {
    _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
    _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
    _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    _connectionStringFallback = connectionStringFallback;
  }

  /// <inheritdoc />
  public async Task DeferAsync(Guid occurrenceId, DateTimeOffset until, CancellationToken cancellationToken = default) {
    await using var conn = await _openAsync(cancellationToken).ConfigureAwait(false);
    if (conn is null) {
      return;
    }
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT wh_defer_occurrence(@id, @until)";
    cmd.Parameters.Add(new NpgsqlParameter("id", NpgsqlDbType.Uuid) { Value = occurrenceId });
    cmd.Parameters.Add(new NpgsqlParameter("until", NpgsqlDbType.TimestampTz) { Value = until });
    _ = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
  }

  /// <inheritdoc />
  public async Task LogRunAsync(
      Guid scheduleId, Guid occurrenceId, short status, string? note, CancellationToken cancellationToken = default) {
    await using var conn = await _openAsync(cancellationToken).ConfigureAwait(false);
    if (conn is null) {
      return;
    }
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT wh_log_schedule_run(@sid, @oid, @status, @note)";
    cmd.Parameters.Add(new NpgsqlParameter("sid", NpgsqlDbType.Uuid) { Value = scheduleId });
    cmd.Parameters.Add(new NpgsqlParameter("oid", NpgsqlDbType.Uuid) { Value = occurrenceId });
    cmd.Parameters.Add(new NpgsqlParameter("status", NpgsqlDbType.Smallint) { Value = status });
    cmd.Parameters.Add(new NpgsqlParameter("note", NpgsqlDbType.Text) { Value = (object?)note ?? DBNull.Value });
    _ = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
  }

  /// <inheritdoc />
  public async Task RefreshAuthorityClaimsAsync(
      Guid scheduleId, string claimsJson, CancellationToken cancellationToken = default) {
    ArgumentException.ThrowIfNullOrWhiteSpace(claimsJson);
    await using var conn = await _openAsync(cancellationToken).ConfigureAwait(false);
    if (conn is null) {
      return;
    }
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT wh_refresh_schedule_authority(@sid, @claims)";
    cmd.Parameters.Add(new NpgsqlParameter("sid", NpgsqlDbType.Uuid) { Value = scheduleId });
    cmd.Parameters.Add(new NpgsqlParameter("claims", NpgsqlDbType.Jsonb) { Value = claimsJson });
    _ = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
  }

  private async Task<NpgsqlConnection?> _openAsync(CancellationToken cancellationToken) {
    var resolution = NotificationConnectionStringResolver.Resolve(_options, _configuration, _connectionStringFallback);
    if (resolution.ConnectionString is null) {
      return null;
    }
    var conn = new NpgsqlConnection(resolution.ConnectionString);
    await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
    return conn;
  }
}
