// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;
using Whizbang.Core.Perspectives;

namespace Whizbang.Data.Postgres;

/// <summary>Where a schema's strong-id document gate stands (#2518).</summary>
public enum StrongIdDocumentGateState {
  /// <summary>The schema has no gate yet (its registry or the gate function is missing): nothing is known open.</summary>
  Unavailable,

  /// <summary>A live instance does not read a strongly-typed id stored as its UUID string: write the object form.</summary>
  Closed,

  /// <summary>Every live instance reads the UUID string, but one still writes the object form: the rewrite waits.</summary>
  Open,

  /// <summary>Every live instance reads and writes the UUID string: the rewrite may run.</summary>
  Ready,
}

/// <summary>What a schema's strong-id document gate answered.</summary>
/// <param name="State">Where the gate stands.</param>
/// <param name="Status">The gate's own words, for the log.</param>
public sealed record StrongIdDocumentGateAnswer(StrongIdDocumentGateState State, string Status);

/// <summary>
/// Asks a schema's strong-id document gate (<c>wh_strong_id_document_gate</c>, migration 562) whether every live instance
/// reads, and writes, a strongly-typed id inside a perspective document as its UUID string, and tells this process's
/// write form (<see cref="WhizbangIdDocumentForm"/>) what it answered (#2518).
/// </summary>
/// <remarks>
/// Asked once at startup and once a maintenance cycle, never on a write path: the write path reads the process's one
/// flag. A gate that is closed or missing holds the schema, so the process writes the object form; an open one opens it.
/// </remarks>
/// <docs>fundamentals/identity/whizbang-ids#stored-form</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/StrongIdDocumentGateTests.cs</tests>
public static class StrongIdDocumentGate {
  /// <summary>Asks the gate, and records the answer in this process's write form.</summary>
  /// <param name="connection">An open connection.</param>
  /// <param name="schema">The schema; null or empty is <c>public</c>.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>The gate's answer.</returns>
  public static async Task<StrongIdDocumentGateAnswer> CheckAsync(
      NpgsqlConnection connection, string? schema, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(connection);
    var target = string.IsNullOrEmpty(schema) ? "public" : schema;
    var answer = await _askAsync(connection, target, cancellationToken).ConfigureAwait(false);
    if (answer.State is StrongIdDocumentGateState.Open or StrongIdDocumentGateState.Ready) {
      WhizbangIdDocumentForm.Open(connection.Database, target);
    } else {
      WhizbangIdDocumentForm.Hold(connection.Database, target);
    }
    return answer;
  }

  /// <summary>
  /// Why the strong-id stored-form rewrite must wait, as a batched rewrite's hold asks it: null once every live instance
  /// reads and writes the UUID string, the gate's words otherwise. Records the answer as <see cref="CheckAsync"/> does.
  /// </summary>
  /// <param name="connection">An open connection.</param>
  /// <param name="schema">The schema; null or empty is <c>public</c>.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>The reason to wait, or null.</returns>
  public static async Task<string?> RewriteHoldAsync(
      NpgsqlConnection connection, string? schema, CancellationToken cancellationToken = default) {
    var answer = await CheckAsync(connection, schema, cancellationToken).ConfigureAwait(false);
    return answer.State == StrongIdDocumentGateState.Ready ? null : "strong-id document gate " + answer.Status;
  }

  private static async Task<StrongIdDocumentGateAnswer> _askAsync(
      NpgsqlConnection connection, string schema, CancellationToken cancellationToken) {
    var quoted = PgIdentifier.Quote(schema);
    await using (var exists = new NpgsqlCommand(
        "SELECT to_regclass($1) IS NOT NULL AND to_regclass($2) IS NOT NULL "
        + "AND to_regprocedure($3) IS NOT NULL AND to_regprocedure($4) IS NOT NULL", connection)) {
      exists.Parameters.AddWithValue($"{quoted}.wh_service_instances");
      exists.Parameters.AddWithValue($"{quoted}.wh_settings");
      exists.Parameters.AddWithValue($"{quoted}.wh_live_instance_ids()");
      exists.Parameters.AddWithValue($"{quoted}.wh_strong_id_document_gate()");
      if (await exists.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not true) {
        return new StrongIdDocumentGateAnswer(StrongIdDocumentGateState.Unavailable, "unavailable");
      }
    }

    await using var command = new NpgsqlCommand($"SELECT {quoted}.wh_strong_id_document_gate()", connection);
    var status = (string)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    return new StrongIdDocumentGateAnswer(StateOf(status), status);
  }

  /// <summary>The state a gate status names.</summary>
  /// <param name="status">What <c>wh_strong_id_document_gate</c> returned.</param>
  /// <returns>The state.</returns>
  internal static StrongIdDocumentGateState StateOf(string status) {
    ArgumentNullException.ThrowIfNull(status);
    if (status == "ready") {
      return StrongIdDocumentGateState.Ready;
    }
    return status == "opened" || status.StartsWith("open ", StringComparison.Ordinal)
      ? StrongIdDocumentGateState.Open
      : StrongIdDocumentGateState.Closed;
  }
}

/// <summary>
/// The advisory-lock key a schema's batched stored-form rewrites take, a batch at a time (#2518): their own, not the
/// schema initialization lock, so a starting instance's schema pass does not read a running batch as a migration in
/// progress and wait for it.
/// </summary>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/StrongIdScalarRewriteIntegrationTests.cs:AStartingInstance_DoesNotWaitOnARunningSliceAsync</tests>
public static class BatchedRewriteLockKey {
  private const string KEY_NAMESPACE = "wh_batched_rewrite:";

  /// <summary>The key for <paramref name="schema"/>.</summary>
  /// <param name="schema">The schema; null or empty is <c>public</c>, as for the schema initialization lock.</param>
  /// <returns>A signed 64-bit key for <c>pg_try_advisory_xact_lock(bigint)</c>.</returns>
  public static long Compute(string? schema) =>
    FnvHash64.Compute(KEY_NAMESPACE + (string.IsNullOrEmpty(schema) ? "public" : schema));
}
