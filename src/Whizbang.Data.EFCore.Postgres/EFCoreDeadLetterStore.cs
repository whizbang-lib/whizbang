using System.Diagnostics.CodeAnalysis;
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
[SuppressMessage("csharpsquid", "S2077:Formatting SQL queries is security-sensitive",
  Justification = "The only interpolated value is a schema-qualified SQL identifier (\"schema\".fn) " +
    "resolved from the EF Core model's configured schema (HasDefaultSchema), not user input. SQL " +
    "identifiers cannot be parameterized; there is no injection vector. All row values are @parameters.")]
public sealed class EFCoreDeadLetterStore<TDbContext>(
  TDbContext dbContext,
  ILogger<EFCoreDeadLetterStore<TDbContext>> logger,
  WorkCoordinatorGate? gate = null
) : IDeadLetterStore where TDbContext : DbContext {
  private readonly TDbContext _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
  private readonly ILogger<EFCoreDeadLetterStore<TDbContext>> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
  private readonly WorkCoordinatorGate? _gate = gate;

  // move_to_dead_letters lives in the service schema (__SCHEMA__.<fn>); qualify it like
  // EFCoreWorkCoordinator does — a bare call only resolves when the connection's search_path
  // includes the service schema, which is not guaranteed (multi-schema: 42883).
  private string _fn(string name) {
    var schema = _dbContext.Model.FindEntityType(typeof(OutboxRecord))?.GetSchema();
    return string.IsNullOrWhiteSpace(schema) || schema == "public" ? name : $"\"{schema}\".{name}";
  }

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
    cmd.CommandText = $"SELECT {_fn("move_to_dead_letters")}(@dlq, @tbl, @src, @reason, @err, @inst, @gen)";
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
