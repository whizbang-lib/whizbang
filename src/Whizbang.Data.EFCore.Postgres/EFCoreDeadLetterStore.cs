using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Whizbang.Core.Messaging;
// WorkCoordinatorGate lives in Whizbang.Core.Messaging — using-directive covers it.

namespace Whizbang.Data.EFCore.Postgres;


/// <summary>
/// EFCore + Npgsql implementation of <see cref="IDeadLetterStore"/>. Thin wrapper over
/// the <c>move_to_dead_letters()</c> SQL function — the atomic INSERT-into-DLQ + DELETE-
/// from-source lives in plpgsql so neither EF nor Dapper has to coordinate the two
/// statements.
/// </summary>
/// <docs>operations/dead-letter-queue/internal-dlq</docs>
public sealed class EFCoreDeadLetterStore<TDbContext>(
  TDbContext dbContext,
  ILogger<EFCoreDeadLetterStore<TDbContext>> logger,
  WorkCoordinatorGate? gate = null
) : IDeadLetterStore where TDbContext : DbContext {
  private readonly TDbContext _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
  private readonly ILogger<EFCoreDeadLetterStore<TDbContext>> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
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

    var conn = _dbContext.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync(ct).ConfigureAwait(false);
    }
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT move_to_dead_letters(@dlq, @tbl, @src, @reason, @err, @inst, @gen)";
    cmd.Parameters.Add(new Npgsql.NpgsqlParameter("dlq", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = deadLetterId });
    cmd.Parameters.Add(new Npgsql.NpgsqlParameter("tbl", NpgsqlTypes.NpgsqlDbType.Text) { Value = sourceTable });
    cmd.Parameters.Add(new Npgsql.NpgsqlParameter("src", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = sourceId });
    cmd.Parameters.Add(new Npgsql.NpgsqlParameter("reason", NpgsqlTypes.NpgsqlDbType.Integer) { Value = (int)failureReason });
    cmd.Parameters.Add(new Npgsql.NpgsqlParameter("err", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)errorText ?? DBNull.Value });
    cmd.Parameters.Add(new Npgsql.NpgsqlParameter("inst", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = instanceId });
    cmd.Parameters.Add(new Npgsql.NpgsqlParameter("gen", NpgsqlTypes.NpgsqlDbType.Text) { Value = generation });
    var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
    return result switch {
      null => null,
      DBNull => null,
      _ => (Guid)result,
    };
  }
}
