// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Whizbang.Core.Lifecycle;

namespace Whizbang.Data.Postgres;

/// <summary>
/// The <see cref="IStreamFinalizer"/> both Postgres drivers register: one call to <c>wh_finalize_stream</c> (migration
/// 266) per finalize, on a connection of its own, so the seal commits on its own whatever the caller is doing.
/// </summary>
/// <remarks>
/// The seal time is read from <paramref name="timeProvider"/> (the host's registered clock, else the system clock), the
/// same clock the write paths stamp their events with, so a sealed stream's <c>sealed_at</c> and its events' times are
/// comparable. The function takes the stream's append lock, so an append in flight is stored before the seal.
/// </remarks>
/// <param name="openConnection">Opens a connection to the service's database.</param>
/// <param name="schema">The schema, unquoted; null resolves the function through the connection's search path.</param>
/// <param name="timeProvider">The clock the seal time is read from; null is the system clock.</param>
/// <param name="logger">Logs each seal.</param>
/// <docs>fundamentals/events/stream-end-of-life#finalizing-a-stream</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamSealingSqlTests.cs</tests>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperStreamSealingTests.cs</tests>
public sealed partial class PostgresStreamFinalizer(
  Func<CancellationToken, ValueTask<NpgsqlConnection>> openConnection,
  string? schema = null,
  TimeProvider? timeProvider = null,
  ILogger<PostgresStreamFinalizer>? logger = null) : IStreamFinalizer {

  private readonly string _prefix = schema is null ? string.Empty : SqlText.Identifier(schema) + ".";
  private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
  private readonly ILogger _logger = logger ?? (ILogger)NullLogger.Instance;

  /// <inheritdoc />
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamSealingSqlTests.cs:Finalize_SealsTheStream_WithTheTimeAndTheReasonAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamSealingSqlTests.cs:Finalize_IsIdempotent_TheSecondCallKeepsTheFirstSealAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamSealingSqlTests.cs:Finalize_WithoutAReason_IsRefusedAsync</tests>
  public async Task<StreamFinalizeResult> FinalizeAsync(Guid streamId, string reason, CancellationToken cancellationToken = default) {
    if (streamId == Guid.Empty) {
      throw new ArgumentException("The empty stream id is \"no stream\"; there is nothing to finalize.", nameof(streamId));
    }
    ArgumentException.ThrowIfNullOrWhiteSpace(reason);

    await using var connection = await openConnection(cancellationToken).ConfigureAwait(false);
    await using var command = new NpgsqlCommand(
      $"SELECT finalize_status, stream_sealed_at, stream_sealed_reason FROM {_prefix}wh_finalize_stream($1, $2, $3)",
      connection);
    command.Parameters.AddWithValue(streamId);
    command.Parameters.AddWithValue(reason);
    command.Parameters.AddWithValue(_timeProvider.GetUtcNow());
    await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
    await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
    var result = new StreamFinalizeResult(
      streamId,
      ToStatus(reader.GetString(0)),
      await reader.IsDBNullAsync(1, cancellationToken).ConfigureAwait(false) ? null : reader.GetFieldValue<DateTimeOffset>(1),
      await reader.IsDBNullAsync(2, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(2));
    LogFinalize(_logger, streamId, result.Status, result.SealedAt, result.Reason);
    return result;
  }

  /// <summary>The status <c>wh_finalize_stream</c> returns, as <see cref="StreamFinalizeStatus"/>.</summary>
  /// <param name="status">The function's <c>finalize_status</c>.</param>
  /// <returns>The status.</returns>
  /// <exception cref="InvalidOperationException">The function returned a status this reader does not know.</exception>
  /// <tests>tests/Whizbang.Core.Tests/Lifecycle/PostgresStreamFinalizerStatusTests.cs</tests>
  internal static StreamFinalizeStatus ToStatus(string status) => status switch {
    "sealed" => StreamFinalizeStatus.Sealed,
    "already_sealed" => StreamFinalizeStatus.AlreadySealed,
    "nothing_to_seal" => StreamFinalizeStatus.NothingToSeal,
    _ => throw new InvalidOperationException($"wh_finalize_stream returned '{status}', which this version does not know."),
  };

  [LoggerMessage(Level = LogLevel.Information,
    Message = "Finalize of stream {StreamId}: {Status}, sealed at {SealedAt}, reason {Reason}")]
  static partial void LogFinalize(ILogger logger, Guid streamId, StreamFinalizeStatus status, DateTimeOffset? sealedAt, string? reason);
}
