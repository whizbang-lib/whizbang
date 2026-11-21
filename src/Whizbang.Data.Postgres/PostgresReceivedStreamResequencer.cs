// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using Npgsql;
using Whizbang.Core.Messaging;

namespace Whizbang.Data.Postgres;

/// <summary>The Postgres <see cref="IReceivedStreamResequencer"/> (#1629). Not implemented yet.</summary>
/// <param name="openConnection">Opens a connection to the service's database.</param>
/// <param name="schema">The service's schema, or null for the connection's search path.</param>
/// <param name="logger">Records the run.</param>
/// <docs>operations/infrastructure/resequencing-received-streams</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ReceivedStreamResequenceSqlTests.cs</tests>
public sealed class PostgresReceivedStreamResequencer(
  Func<CancellationToken, ValueTask<NpgsqlConnection>> openConnection,
  string? schema = null,
  ILogger<PostgresReceivedStreamResequencer>? logger = null) : IReceivedStreamResequencer {
  /// <inheritdoc />
  public Task<ReceivedStreamResequenceReport> ResequenceAsync(
      ReceivedStreamResequenceRequest request, CancellationToken cancellationToken = default) =>
    throw new NotImplementedException($"{openConnection}{schema}{logger}");
}
