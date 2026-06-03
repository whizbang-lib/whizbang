using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;
using Whizbang.Core.Messaging;

namespace Whizbang.Data.Dapper.Postgres;

/// <summary>
/// Dapper + Npgsql implementation of <see cref="IDeadLetterStore"/>. Symmetric with
/// <c>EFCoreDeadLetterStore</c>; both call the same <c>move_to_dead_letters()</c> SQL
/// function.
/// </summary>
/// <docs>operations/dead-letter-queue/internal-dlq</docs>
public sealed class DapperDeadLetterStore(
  string connectionString,
  ILogger<DapperDeadLetterStore> logger,
  WorkCoordinatorGate? gate = null
) : IDeadLetterStore {
  private readonly string _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
  private readonly ILogger<DapperDeadLetterStore> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
  private readonly WorkCoordinatorGate? _gate = gate;

  /// <inheritdoc />
  public async Task<Guid?> MoveAsync(
      Guid deadLetterId,
      string sourceTable,
      Guid sourceId,
      MessageFailureReason failureReason,
      string? errorText,
      Guid instanceId,
      string generation,
      CancellationToken ct = default) {
    ArgumentException.ThrowIfNullOrEmpty(sourceTable);
    ArgumentException.ThrowIfNullOrEmpty(generation);

    using var __ = _gate is null ? default : await _gate.AcquireAsync(ct).ConfigureAwait(false);

    await using var conn = new NpgsqlConnection(_connectionString);
    await conn.OpenAsync(ct).ConfigureAwait(false);
    var result = await conn.ExecuteScalarAsync<Guid?>(
      "SELECT move_to_dead_letters(@DeadLetterId, @SourceTable, @SourceId, @Reason, @Error, @InstanceId, @Generation)",
      new {
        DeadLetterId = deadLetterId,
        SourceTable = sourceTable,
        SourceId = sourceId,
        Reason = (int)failureReason,
        Error = errorText,
        InstanceId = instanceId,
        Generation = generation,
      }).ConfigureAwait(false);
    return result;
  }
}
