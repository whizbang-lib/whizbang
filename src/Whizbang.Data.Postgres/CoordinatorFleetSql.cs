// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Data.Common;
using Npgsql;
using NpgsqlTypes;
using Whizbang.Core.Messaging;
using Whizbang.Core.Priority;
using Whizbang.Core.Transports;

namespace Whizbang.Data.Postgres;

/// <summary>
/// The recovery and fleet statements both Postgres work coordinators run: the stuck-row sentinels, the release of
/// unprocessed inbox rows and unstarted leases, the service backlog the housekeeping gate reads, the broker dead-letter
/// import, and the standby and eviction calls of a rolling upgrade.
/// </summary>
/// <remarks>
/// <para>
/// One copy, so the drivers cannot drift. The Dapper coordinator used to inherit the interface's inert defaults for every
/// one of them, so on that driver a stuck row was never reported, a released lease stayed held until it expired, the
/// backlog read as unmeasured, a broker dead letter was never taken into custody, and a standby request went nowhere
/// (#2051).
/// </para>
/// <para>
/// Each coordinator supplies a command on its own connection and the prefix it qualifies names with: the quoted schema
/// followed by a dot (<c>"svc".</c>), or empty where the caller resolves through <c>search_path</c>. It is always built
/// by <see cref="PgIdentifier"/>, never from input, and every value is a bound parameter.
/// </para>
/// </remarks>
/// <docs>data/drivers#dapper-coordinator-parity</docs>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperFleetRecoveryTests.cs</tests>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "S2077:Formatting SQL queries is security-sensitive", Justification = "Identifiers cannot be parameterized: the schema prefix is built by PgIdentifier and the object names are constants. Every value is a bound parameter.")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Major Code Smell", "S3265:Non-flags enums should not be used in bitwise operations", Justification = "NpgsqlDbType composes Array with the element type by design.")]
internal static class CoordinatorFleetSql {
  /// <summary>The sentinel function that finds outbox rows attempted past the limit.</summary>
  internal const string STUCK_OUTBOX = "find_stuck_outbox_rows";

  /// <summary>The sentinel function that finds inbox rows attempted past the limit.</summary>
  internal const string STUCK_INBOX = "find_stuck_inbox_rows";

  /// <summary>The rows the sentinel <paramref name="function"/> finds attempted <paramref name="maxAttempts"/> or more times.</summary>
  internal static async Task<IReadOnlyList<StuckRow>> FindStuckRowsAsync(
      DbCommand cmd, string prefix, string function, int maxAttempts, int limit, CancellationToken cancellationToken) {
    cmd.CommandText = $"SELECT message_id, message_type, stream_id, attempts, claimed_since FROM {prefix}{function}(@p_max, @p_limit)";
    cmd.Parameters.Add(new NpgsqlParameter("p_max", NpgsqlDbType.Integer) { Value = maxAttempts });
    cmd.Parameters.Add(new NpgsqlParameter("p_limit", NpgsqlDbType.Integer) { Value = limit });
    var rows = new List<StuckRow>();
    await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
      rows.Add(new StuckRow {
        MessageId = reader.GetGuid(0),
        MessageType = reader.GetString(1),
        StreamId = await reader.IsDBNullAsync(2, cancellationToken).ConfigureAwait(false) ? null : reader.GetGuid(2),
        Attempts = reader.GetInt32(3),
        ClaimedSince = reader.GetDateTime(4),
      });
    }
    return rows;
  }

  /// <summary>
  /// Returns the instance's unprocessed inbox rows among <paramref name="messageIds"/> to unassigned
  /// (<c>release_unprocessed_inbox</c>). The caller returns before opening a connection for an empty set.
  /// </summary>
  internal static async Task<int> ReleaseUnprocessedInboxAsync(
      DbCommand cmd, string prefix, Guid instanceId, IReadOnlyList<Guid> messageIds, CancellationToken cancellationToken) {
    cmd.CommandText = $"SELECT {prefix}release_unprocessed_inbox(@p_instance, @p_ids)";
    cmd.Parameters.Add(new NpgsqlParameter("p_instance", NpgsqlDbType.Uuid) { Value = instanceId });
    cmd.Parameters.Add(new NpgsqlParameter("p_ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = messageIds.ToArray() });
    return Convert.ToInt32(await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);
  }

  /// <summary>
  /// Returns the instance's leased but unstarted inbox and perspective rows of the given streams to unassigned
  /// (<c>release_unstarted_leases</c>, which always emits one row). The caller returns before opening a connection when
  /// both sets are empty.
  /// </summary>
  internal static async Task<UnstartedLeaseRelease> ReleaseUnstartedLeasesAsync(
      DbCommand cmd, string prefix, Guid instanceId, IReadOnlyList<Guid> inboxStreamIds, IReadOnlyList<Guid> perspectiveStreamIds,
      CancellationToken cancellationToken) {
    cmd.CommandText = $"SELECT inbox_released, perspective_released FROM {prefix}release_unstarted_leases(@p_instance, @p_inbox, @p_persp)";
    cmd.Parameters.Add(new NpgsqlParameter("p_instance", NpgsqlDbType.Uuid) { Value = instanceId });
    cmd.Parameters.Add(new NpgsqlParameter<Guid[]>("p_inbox", inboxStreamIds.ToArray()));
    cmd.Parameters.Add(new NpgsqlParameter<Guid[]>("p_persp", perspectiveStreamIds.ToArray()));
    await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
    _ = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
    return new UnstartedLeaseRelease(reader.GetInt32(0), reader.GetInt32(1));
  }

  /// <summary>
  /// The service's backlog, bounded: claimable inbox rows, live leases, the age of the oldest claimable inbox row, the
  /// pending outbox and perspective rows, and the idle band apart from all of them (167). Null when the query answered
  /// no row, which is not the same as settled.
  /// </summary>
  /// <remarks>
  /// Bounded counts, deliberately: the gate needs to know whether these are zero, not their size, and an unbounded
  /// count on the checkpoint cadence would make a gate that protects the store into a periodic full scan of it.
  /// Settledness uses the claim's eligibility predicate, so rows parked with a future <c>scheduled_for</c> are not
  /// backlog; a live lease is in-flight work whatever its schedule.
  /// </remarks>
  internal static async Task<ServiceBacklog?> CountServiceBacklogAsync(DbCommand cmd, string prefix, CancellationToken cancellationToken) {
    var inbox = prefix + "wh_inbox_state";
    var outbox = prefix + "wh_outbox";
    var perspectiveEvents = prefix + "wh_perspective_events";
    const int idleBandStart = WorkPriority.BACKGROUND_BAND_END;
    cmd.CommandText = $@"
      SELECT
        (SELECT count(*) FROM (SELECT 1 FROM {inbox} WHERE processed_at IS NULL
           AND (scheduled_for IS NULL OR scheduled_for <= now())
           AND priority <= {idleBandStart} LIMIT 1000) a),
        (SELECT count(*) FROM (SELECT 1 FROM {inbox}
           WHERE instance_id IS NOT NULL AND lease_expiry > now() LIMIT 1000) b),
        COALESCE(EXTRACT(EPOCH FROM (now() - (
          SELECT received_at FROM {inbox} WHERE processed_at IS NULL
            AND (scheduled_for IS NULL OR scheduled_for <= now())
            AND priority <= {idleBandStart}
          ORDER BY received_at LIMIT 1))), 0),
        (SELECT count(*) FROM (SELECT 1 FROM {outbox} WHERE processed_at IS NULL
           AND (scheduled_for IS NULL OR scheduled_for <= now())
           AND priority <= {idleBandStart} LIMIT 1000) c),
        (SELECT count(*) FROM (SELECT 1 FROM {perspectiveEvents} WHERE processed_at IS NULL
           AND (scheduled_for IS NULL OR scheduled_for <= now())
           AND priority <= {idleBandStart} LIMIT 1000) d),
        (SELECT count(*) FROM (
           SELECT 1 FROM {inbox} WHERE processed_at IS NULL
             AND (scheduled_for IS NULL OR scheduled_for <= now())
             AND priority > {idleBandStart}
           UNION ALL
           SELECT 1 FROM {outbox} WHERE processed_at IS NULL
             AND (scheduled_for IS NULL OR scheduled_for <= now())
             AND priority > {idleBandStart}
           UNION ALL
           SELECT 1 FROM {perspectiveEvents} WHERE processed_at IS NULL
             AND (scheduled_for IS NULL OR scheduled_for <= now())
             AND priority > {idleBandStart}
           LIMIT 1000) e)";
    await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
    return !await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? null : new ServiceBacklog {
      UnprocessedInboxRows = reader.GetInt64(0),
      ActiveLeasedRows = reader.GetInt64(1),
      // Clamped at zero: clock skew between writer and reader must not report negative lag.
      OldestUnprocessedAge = TimeSpan.FromSeconds(Math.Max(0, reader.GetDouble(2))),
      PendingOutboxRows = reader.GetInt64(3),
      PendingPerspectiveRows = reader.GetInt64(4),
      PendingIdleRows = reader.GetInt64(5),
    };
  }

  /// <summary>
  /// Takes a broker dead letter into custody (<c>wh_import_dead_letter</c>): true when imported, false when custody
  /// already existed (a duplicate the drainer may settle at the broker).
  /// </summary>
  internal static async Task<bool> ImportBrokerDeadLetterAsync(
      DbCommand cmd, string prefix, BrokerDeadLetterImport import, Guid deadLetterId, Guid instanceId, string generation,
      CancellationToken cancellationToken) {
    cmd.Parameters.Add(new NpgsqlParameter("p_dead_letter_id", deadLetterId));
    cmd.Parameters.Add(new NpgsqlParameter("p_message_id", import.MessageId));
    cmd.Parameters.Add(new NpgsqlParameter("p_stream_id", NpgsqlDbType.Uuid) { Value = (object?)import.StreamId ?? DBNull.Value });
    cmd.Parameters.Add(new NpgsqlParameter("p_message_type", NpgsqlDbType.Text) {
      Value = import.MessageType is null ? DBNull.Value : EventTypeMatchingHelper.ExtractInnerPayloadTypeName(import.MessageType)
    });
    cmd.Parameters.Add(new NpgsqlParameter("p_destination", import.Destination));
    cmd.Parameters.Add(new NpgsqlParameter("p_envelope_json", import.EnvelopeJson));
    cmd.Parameters.Add(new NpgsqlParameter("p_broker_reason", NpgsqlDbType.Text) { Value = (object?)import.BrokerReason ?? DBNull.Value });
    cmd.Parameters.Add(new NpgsqlParameter("p_broker_description", NpgsqlDbType.Text) { Value = (object?)import.BrokerDescription ?? DBNull.Value });
    cmd.Parameters.Add(new NpgsqlParameter("p_enqueued_at", NpgsqlDbType.TimestampTz) { Value = (object?)import.EnqueuedAt ?? DBNull.Value });
    cmd.Parameters.Add(new NpgsqlParameter("p_delivery_count", NpgsqlDbType.Integer) { Value = (object?)import.DeliveryCount ?? DBNull.Value });
    cmd.Parameters.Add(new NpgsqlParameter("p_instance_id", instanceId));
    cmd.Parameters.Add(new NpgsqlParameter("p_generation", generation));
    cmd.CommandText = $"SELECT {prefix}wh_import_dead_letter(@p_dead_letter_id,@p_message_id,@p_stream_id,@p_message_type,@p_destination,@p_envelope_json,@p_broker_reason,@p_broker_description,@p_enqueued_at,@p_delivery_count,@p_instance_id,@p_generation)";
    return ScalarResult.IsTrue(await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
  }

  /// <summary>
  /// The exception a failed broker dead-letter import throws, never a false: false means a duplicate the drainer may
  /// settle at the broker, so a failure reported as false would lose the message.
  /// </summary>
  internal static InvalidOperationException ImportFailed(BrokerDeadLetterImport import, Exception inner) =>
    new($"ImportBrokerDeadLetterAsync failed for message {import.MessageId} from {import.Destination} — the message stays on the broker DLQ for the next drain pass", inner);

  /// <summary>Runs one of the standby or eviction functions, whose arguments are all values, and returns its scalar.</summary>
  internal static async Task<object?> ScalarFunctionAsync(
      DbCommand cmd, string prefix, string function, CancellationToken cancellationToken, params (string Name, object Value)[] args) {
    cmd.CommandText = $"SELECT {prefix}{function}({string.Join(", ", args.Select(a => "@" + a.Name))})";
    foreach (var (name, value) in args) {
      cmd.Parameters.Add(new NpgsqlParameter(name, value));
    }
    return await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
  }

  /// <summary>The standing standby request with the requester's liveness, or null when none stands.</summary>
  internal static async Task<StandbyRequest?> GetStandbyRequestAsync(DbCommand cmd, string prefix, CancellationToken cancellationToken) {
    cmd.CommandText = $@"
      SELECT r.requested_by, r.requested_version, r.requested_at,
             -- #2027: the one liveness rule (Preflight's, so present before migrations run); no row is not live.
             {prefix}wh_instance_is_live(r.requested_by) AS is_live
      FROM {prefix}wh_standby_requests r";
    await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
    if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
      return null;
    }
    var at = await reader.GetFieldValueAsync<DateTime>(2, cancellationToken).ConfigureAwait(false);
    return new StandbyRequest(
      reader.GetGuid(0),
      reader.GetString(1),
      new DateTimeOffset(DateTime.SpecifyKind(at, DateTimeKind.Utc)),
      reader.GetBoolean(3));
  }
}
