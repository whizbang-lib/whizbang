// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Npgsql;
using NpgsqlTypes;
using Whizbang.Core.Archival;

namespace Whizbang.Data.Postgres;

/// <summary>
/// Stores the configured append-to-archived policies in <c>wh_stream_type_policies</c> through
/// <c>wh_sync_stream_type_policies</c> (migration 271), which replaces the stored set whole. Shared by both coordinators,
/// so the two drivers store the same rows.
/// </summary>
/// <docs>fundamentals/events/archive-store#the-policy-per-stream-type</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchiveRestoreRequestSqlTests.cs:SyncPolicies_ThroughTheCoordinator_StoresTheConfiguredSetAsync</tests>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperArchiveAppendPolicyTests.cs:SyncStreamTypePolicies_ThroughTheCoordinator_StoresTheConfiguredSetAsync</tests>
public static class StreamTypePolicyWriter {
  /// <summary>The function the policies are stored through.</summary>
  public const string FUNCTION_NAME = "wh_sync_stream_type_policies";

  /// <summary>Replaces the stored policies with <paramref name="policies"/>, on an open connection.</summary>
  /// <param name="connection">An open connection to the service's database.</param>
  /// <param name="functionName">The function, qualified with the service's schema when it has one.</param>
  /// <param name="policies">Every configured stream type and its policy.</param>
  /// <param name="cancellationToken">Cancellation.</param>
  /// <returns>A task that completes when the policies are stored.</returns>
  public static async Task WriteAsync(
      NpgsqlConnection connection, string functionName, IReadOnlyList<StreamTypePolicy> policies, CancellationToken cancellationToken) {
    ArgumentNullException.ThrowIfNull(connection);
    ArgumentNullException.ThrowIfNull(policies);
    await using var command = new NpgsqlCommand($"SELECT {functionName}($1, $2)", connection);
    var types = policies.Select(p => p.StreamType).ToArray();
    var appendToArchived = policies.Select(p => p.AppendToArchived.ToString()).ToArray();
    command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text, Value = types });
    command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text, Value = appendToArchived });
    _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
  }
}
