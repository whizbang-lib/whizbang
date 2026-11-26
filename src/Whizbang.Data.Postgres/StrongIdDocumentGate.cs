// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data.Common;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Whizbang.Core.Perspectives;

namespace Whizbang.Data.Postgres;

/// <summary>Where a schema's strong-id document gate stands (#2518).</summary>
/// <docs>fundamentals/identity/whizbang-ids#stored-form-switch</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/StrongIdDocumentGateTests.cs</tests>
public enum StrongIdDocumentGateState {
  /// <summary>
  /// The schema has no gate (its registry or the gate function is missing): no fleet in it to wait on, so it is left out
  /// of the process's write form rather than holding every other schema.
  /// </summary>
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
/// <param name="Unreadable">
/// Once the gate is open, the live instances that cannot read a strongly-typed id stored as its UUID string: instances
/// of an earlier release that joined after the switch, which fail on every converted row. Empty otherwise.
/// </param>
/// <docs>fundamentals/identity/whizbang-ids#stored-form-switch</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/StrongIdDocumentGateTests.cs</tests>
public sealed record StrongIdDocumentGateAnswer(
    StrongIdDocumentGateState State, string Status, IReadOnlyList<StrongIdUnreadableInstance> Unreadable) {
  /// <summary>An answer with no unreadable instance.</summary>
  /// <param name="state">Where the gate stands.</param>
  /// <param name="status">The gate's own words.</param>
  public StrongIdDocumentGateAnswer(StrongIdDocumentGateState state, string status) : this(state, status, []) {
  }
}

/// <summary>A live instance below stored form level 2, which cannot read a strongly-typed id stored as its UUID string.</summary>
/// <param name="InstanceId">The instance.</param>
/// <param name="ServiceName">Its service.</param>
/// <param name="HostName">Its host.</param>
/// <param name="StoredFormLevel">The level its heartbeat announces, or null when it announces none.</param>
/// <docs>fundamentals/identity/whizbang-ids#stored-form-switch</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/StrongIdDocumentGateTests.cs</tests>
public sealed record StrongIdUnreadableInstance(Guid InstanceId, string ServiceName, string HostName, string? StoredFormLevel);

/// <summary>
/// Asks a schema's strong-id document gate (<c>wh_strong_id_document_gate</c>, migration 562) whether every live instance
/// reads, and writes, a strongly-typed id inside a perspective document as its UUID string, and tells this process's
/// write form (<see cref="WhizbangIdDocumentForm"/>) what it answered (#2518).
/// </summary>
/// <remarks>
/// Asked at startup, once a maintenance cycle and before each batch of the rewrite, never on a write path: the write path
/// reads the process's one flag. A closed gate holds the schema, so the process writes the object form; an open one opens
/// it; a schema with no gate is left out. Operators read where a schema stands with
/// <c>wh_strong_id_document_status()</c>, which never latches.
/// </remarks>
/// <docs>fundamentals/identity/whizbang-ids#stored-form-switch</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/StrongIdDocumentGateTests.cs</tests>
public static class StrongIdDocumentGate {
  /// <summary>The instrument that names each live instance that cannot read the UUID string after the switch.</summary>
  public const string UNREADABLE_INSTRUMENT_NAME = "whizbang.perspective.strong_id_unreadable_instances";

  /// <summary>The meter <see cref="UNREADABLE_INSTRUMENT_NAME"/> is on.</summary>
  public const string METER_NAME = StoredFormFallbacks.METER_NAME;

  private static readonly ConcurrentDictionary<string, IReadOnlyList<StrongIdUnreadableInstance>> _unreadable = new(StringComparer.Ordinal);
  /// <summary>
  /// The meter <see cref="UNREADABLE_INSTRUMENT_NAME"/> is on. It lives as long as the process: its one instrument reads
  /// the unreadable instances each time it is observed.
  /// </summary>
  internal static Meter UnreadableMeter { get; } = _createMeter();

  private static Meter _createMeter() {
    var meter = new Meter(METER_NAME);
    meter.CreateObservableGauge(
      UNREADABLE_INSTRUMENT_NAME,
      _observeUnreadable,
      description: "1 for each live instance that cannot read a strongly-typed id stored as its UUID string while its "
        + "schema's gate is open; tagged by database, schema, instance, service and host");
    return meter;
  }

  /// <summary>Asks the gate, and records the answer in this process's write form.</summary>
  /// <param name="connection">An open connection.</param>
  /// <param name="schema">The schema; null or empty is <c>public</c>.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>The gate's answer.</returns>
  public static Task<StrongIdDocumentGateAnswer> CheckAsync(
      NpgsqlConnection connection, string? schema, CancellationToken cancellationToken = default) =>
    CheckAsync(connection, schema, logger: null, cancellationToken);

  /// <summary>
  /// Asks the gate, and records the answer in this process's write form: open or ready opens the schema, closed holds it,
  /// and so does a schema with no gate (not yet migrated, or migrated by an earlier release), so nothing writes the UUID
  /// string into it. Once the gate is open, each live instance that cannot read the UUID string
  /// is reported: a Warning naming them whenever the set changes, and <see cref="UNREADABLE_INSTRUMENT_NAME"/>.
  /// </summary>
  /// <param name="connection">An open connection.</param>
  /// <param name="schema">The schema; null or empty is <c>public</c>.</param>
  /// <param name="logger">Where an unreadable instance is reported.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>The gate's answer.</returns>
  public static async Task<StrongIdDocumentGateAnswer> CheckAsync(
      NpgsqlConnection connection, string? schema, ILogger? logger, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(connection);
    var target = string.IsNullOrEmpty(schema) ? "public" : schema;
    var database = await PostgresServerDatabase.ResolveAsync(connection, cancellationToken).ConfigureAwait(false);
    var answer = await _askAsync(connection, target, cancellationToken).ConfigureAwait(false);
    switch (answer.State) {
      case StrongIdDocumentGateState.Open or StrongIdDocumentGateState.Ready:
        WhizbangIdDocumentForm.Open(database, target);
        break;
      default:
        WhizbangIdDocumentForm.Hold(database, target);
        break;
    }
    _report(database, target, answer.Unreadable, logger ?? NullLogger.Instance);
    return answer;
  }

  /// <summary>
  /// Holds a schema whose gate could not be asked (a lost connection, a timeout), so the process keeps writing the object
  /// form every release reads until a later check answers. Holding only ever keeps the form every release reads.
  /// </summary>
  /// <param name="connection">The connection the gate was to be asked on, open or not: the schema is held under the name
  /// the server gives its database, or a provisional one until that is known (<see cref="PostgresServerDatabase"/>).</param>
  /// <param name="schema">The schema; null or empty is <c>public</c>.</param>
  public static void HoldUnasked(DbConnection connection, string? schema) =>
    WhizbangIdDocumentForm.Hold(PostgresServerDatabase.KeyOf(connection), schema);

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
    var answer = await CheckAsync(connection, schema, logger: null, cancellationToken).ConfigureAwait(false);
    return answer.State == StrongIdDocumentGateState.Ready ? null : "strong-id document gate " + answer.Status;
  }

  /// <summary>Records which live instances cannot read the UUID string, and logs them whenever that set changes.</summary>
  private static void _report(string database, string schema, IReadOnlyList<StrongIdUnreadableInstance> unreadable, ILogger logger) {
    var key = database + "/" + schema;
    var before = _unreadable.TryGetValue(key, out var previous) ? previous : [];
    if (unreadable.Count == 0) {
      _unreadable.TryRemove(key, out _);
    } else {
      _unreadable[key] = unreadable;
    }
    if (unreadable.Count > 0 && !before.SequenceEqual(unreadable)) {
      StrongIdDocumentGateLog.Unreadable(
        logger, schema, unreadable.Count,
        string.Join(", ", unreadable.Select(i => $"{i.InstanceId} ({i.ServiceName} on {i.HostName}, level {i.StoredFormLevel ?? "none"})")));
    }
  }

  private static IEnumerable<Measurement<int>> _observeUnreadable() {
    foreach (var (key, instances) in _unreadable) {
      var slash = key.IndexOf('/', StringComparison.Ordinal);
      var (database, schema) = (key[..slash], key[(slash + 1)..]);
      foreach (var instance in instances) {
        yield return new Measurement<int>(1,
          new KeyValuePair<string, object?>("database", database),
          new KeyValuePair<string, object?>("schema", schema),
          new KeyValuePair<string, object?>("instance_id", instance.InstanceId.ToString()),
          new KeyValuePair<string, object?>("service_name", instance.ServiceName),
          new KeyValuePair<string, object?>("host_name", instance.HostName));
      }
    }
  }

  private static async Task<StrongIdDocumentGateAnswer> _askAsync(
      NpgsqlConnection connection, string schema, CancellationToken cancellationToken) {
    var quoted = PgIdentifier.Quote(schema);
    await using (var exists = new NpgsqlCommand(
        "SELECT to_regclass($1) IS NOT NULL AND to_regclass($2) IS NOT NULL "
        + "AND to_regprocedure($3) IS NOT NULL AND to_regprocedure($4) IS NOT NULL AND to_regprocedure($5) IS NOT NULL",
        connection)) {
      exists.Parameters.AddWithValue($"{quoted}.wh_service_instances");
      exists.Parameters.AddWithValue($"{quoted}.wh_settings");
      exists.Parameters.AddWithValue($"{quoted}.wh_live_instance_ids()");
      exists.Parameters.AddWithValue($"{quoted}.wh_strong_id_document_gate()");
      exists.Parameters.AddWithValue($"{quoted}.wh_strong_id_document_unreadable_instances()");
      if (await exists.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not true) {
        return new StrongIdDocumentGateAnswer(StrongIdDocumentGateState.Unavailable, "unavailable");
      }
    }

    string status;
    await using (var command = new NpgsqlCommand($"SELECT {quoted}.wh_strong_id_document_gate()", connection)) {
      status = (string)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }
    var state = StateOf(status);
    if (state is StrongIdDocumentGateState.Closed) {
      return new StrongIdDocumentGateAnswer(state, status);
    }

    // Open: an instance below level 2 now is one of an earlier release that joined after the switch.
    var unreadable = new List<StrongIdUnreadableInstance>();
    await using (var command = new NpgsqlCommand(
        $"SELECT instance_id, service_name, host_name, stored_form_level FROM {quoted}.wh_strong_id_document_unreadable_instances()",
        connection))
    await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false)) {
      while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
        unreadable.Add(new StrongIdUnreadableInstance(
          reader.GetGuid(0),
          reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
          reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
          reader.IsDBNull(3) ? null : reader.GetString(3)));
      }
    }
    return new StrongIdDocumentGateAnswer(state, status, unreadable);
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

/// <summary>Source-generated logging for the strong-id document gate.</summary>
internal static partial class StrongIdDocumentGateLog {
  [LoggerMessage(
    EventId = 1,
    Level = LogLevel.Warning,
    Message = "The strong-id document gate in {Schema} is open, so documents there store strongly-typed ids as UUID "
      + "strings, but {Count} live instances cannot read them and fail on every converted row: {Instances}. The switch "
      + "is one-way and rolling back below the release that made it is not supported; upgrade or stop these instances")]
  public static partial void Unreadable(ILogger logger, string schema, int count, string instances);
}

/// <summary>
/// The advisory-lock key a schema's batched stored-form rewrites take, a batch at a time (#2518): their own, not the
/// schema initialization lock, so a starting instance's schema pass does not read a running batch as a migration in
/// progress and wait for it.
/// </summary>
/// <docs>fundamentals/identity/whizbang-ids#stored-form-rewrite</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/StrongIdScalarRewriteIntegrationTests.cs:AStartingInstance_DoesNotWaitOnARunningSliceAsync</tests>
public static class BatchedRewriteLockKey {
  private const string KEY_NAMESPACE = "wh_batched_rewrite:";

  /// <summary>The key for <paramref name="schema"/>.</summary>
  /// <param name="schema">The schema; null or empty is <c>public</c>, as for the schema initialization lock.</param>
  /// <returns>A signed 64-bit key for <c>pg_try_advisory_xact_lock(bigint)</c>.</returns>
  public static long Compute(string? schema) =>
    FnvHash64.Compute(KEY_NAMESPACE + (string.IsNullOrEmpty(schema) ? "public" : schema));
}
