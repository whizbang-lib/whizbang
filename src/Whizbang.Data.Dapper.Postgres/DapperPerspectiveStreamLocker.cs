#pragma warning disable S3604 // Primary constructor field/property initializers are intentional

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Whizbang.Core.Perspectives;

namespace Whizbang.Data.Dapper.Postgres;

/// <summary>
/// Dapper/Npgsql implementation of <see cref="IPerspectiveStreamLocker"/>.
/// Manages stream-level locks on wh_perspective_cursors for rewind, bootstrap, and purge operations.
/// </summary>
/// <docs>fundamentals/perspectives/stream-locking</docs>
public sealed partial class DapperPerspectiveStreamLocker(
  string connectionString,
  IOptions<PerspectiveStreamLockOptions> lockOptions,
  ILogger<DapperPerspectiveStreamLocker>? logger = null) : IPerspectiveStreamLocker {

  private readonly PerspectiveStreamLockOptions _lockOptions = lockOptions.Value;

  public async Task<bool> TryAcquireLockAsync(Guid streamId, string perspectiveName, Guid instanceId, string reason, CancellationToken ct = default) {
    await using var connection = new NpgsqlConnection(connectionString);
    await connection.OpenAsync(ct);

    var lockExpiry = DateTimeOffset.UtcNow.Add(_lockOptions.LockTimeout);

    const string sql = """
      UPDATE wh_perspective_cursors
      SET stream_lock_instance_id = @p_instance_id,
          stream_lock_expiry = @p_lock_expiry,
          stream_lock_reason = @p_reason
      WHERE stream_id = @p_stream_id AND perspective_name = @p_perspective_name
        AND (stream_lock_instance_id IS NULL
             OR stream_lock_expiry <= NOW()
             OR stream_lock_instance_id = @p_instance_id)
      """;

    await using var cmd = new NpgsqlCommand(sql, connection);
    cmd.Parameters.AddWithValue("p_stream_id", streamId);
    cmd.Parameters.AddWithValue("p_perspective_name", perspectiveName);
    cmd.Parameters.AddWithValue("p_instance_id", instanceId);
    cmd.Parameters.AddWithValue("p_lock_expiry", lockExpiry);
    cmd.Parameters.AddWithValue("p_reason", reason);

    var affected = await cmd.ExecuteNonQueryAsync(ct);
    var acquired = affected > 0;

    if (logger?.IsEnabled(LogLevel.Debug) == true) {
      if (acquired) {
        LogLockAcquired(logger, perspectiveName, streamId, instanceId, reason);
      } else {
        LogLockNotAcquired(logger, perspectiveName, streamId, instanceId, reason);
      }
    }

    return acquired;
  }

  public async Task RenewLockAsync(Guid streamId, string perspectiveName, Guid instanceId, CancellationToken ct = default) {
    await using var connection = new NpgsqlConnection(connectionString);
    await connection.OpenAsync(ct);

    var newExpiry = DateTimeOffset.UtcNow.Add(_lockOptions.LockTimeout);

    const string sql = """
      UPDATE wh_perspective_cursors
      SET stream_lock_expiry = @p_new_expiry
      WHERE stream_id = @p_stream_id AND perspective_name = @p_perspective_name
        AND stream_lock_instance_id = @p_instance_id
      """;

    await using var cmd = new NpgsqlCommand(sql, connection);
    cmd.Parameters.AddWithValue("p_stream_id", streamId);
    cmd.Parameters.AddWithValue("p_perspective_name", perspectiveName);
    cmd.Parameters.AddWithValue("p_instance_id", instanceId);
    cmd.Parameters.AddWithValue("p_new_expiry", newExpiry);

    await cmd.ExecuteNonQueryAsync(ct);
  }

  public async Task ReleaseLockAsync(Guid streamId, string perspectiveName, Guid instanceId, CancellationToken ct = default) {
    await using var connection = new NpgsqlConnection(connectionString);
    await connection.OpenAsync(ct);

    const string sql = """
      UPDATE wh_perspective_cursors
      SET stream_lock_instance_id = NULL,
          stream_lock_expiry = NULL,
          stream_lock_reason = NULL
      WHERE stream_id = @p_stream_id AND perspective_name = @p_perspective_name
        AND stream_lock_instance_id = @p_instance_id
      """;

    await using var cmd = new NpgsqlCommand(sql, connection);
    cmd.Parameters.AddWithValue("p_stream_id", streamId);
    cmd.Parameters.AddWithValue("p_perspective_name", perspectiveName);
    cmd.Parameters.AddWithValue("p_instance_id", instanceId);

    await cmd.ExecuteNonQueryAsync(ct);

    if (logger?.IsEnabled(LogLevel.Debug) == true) {
      LogLockReleased(logger, perspectiveName, streamId, instanceId);
    }
  }

  [LoggerMessage(EventId = 1, Level = LogLevel.Debug, Message = "Stream lock acquired for {PerspectiveName} stream {StreamId} by instance {InstanceId} (reason: {Reason})")]
  static partial void LogLockAcquired(ILogger logger, string perspectiveName, Guid streamId, Guid instanceId, string reason);

  [LoggerMessage(EventId = 2, Level = LogLevel.Debug, Message = "Stream lock NOT acquired for {PerspectiveName} stream {StreamId} by instance {InstanceId} (reason: {Reason}) — held by another instance")]
  static partial void LogLockNotAcquired(ILogger logger, string perspectiveName, Guid streamId, Guid instanceId, string reason);

  [LoggerMessage(EventId = 3, Level = LogLevel.Debug, Message = "Stream lock released for {PerspectiveName} stream {StreamId} by instance {InstanceId}")]
  static partial void LogLockReleased(ILogger logger, string perspectiveName, Guid streamId, Guid instanceId);
}
