// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using Npgsql;
using Whizbang.Core.Lifecycle;

namespace Whizbang.Data.Postgres;

/// <summary>
/// The <see cref="IStreamFinalizer"/> both Postgres drivers register: one call to <c>wh_finalize_stream</c> (migration
/// 266) per finalize, on a connection of its own.
/// </summary>
/// <param name="openConnection">Opens a connection to the service's database.</param>
/// <param name="schema">The schema, unquoted; null resolves the function through the connection's search path.</param>
/// <param name="timeProvider">The clock the seal time is read from.</param>
/// <param name="logger">Logs each seal.</param>
/// <docs>fundamentals/events/stream-end-of-life#finalizing-a-stream</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamSealingSqlTests.cs</tests>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperStreamSealingTests.cs</tests>
#pragma warning disable CS9113 // RED stub: the parameters are read by the implementation
public sealed class PostgresStreamFinalizer(
  Func<CancellationToken, ValueTask<NpgsqlConnection>> openConnection,
  string? schema = null,
  TimeProvider? timeProvider = null,
  ILogger<PostgresStreamFinalizer>? logger = null) : IStreamFinalizer {

  /// <inheritdoc />
  public Task<StreamFinalizeResult> FinalizeAsync(Guid streamId, string reason, CancellationToken cancellationToken = default) =>
    throw new NotImplementedException();
}
#pragma warning restore CS9113
