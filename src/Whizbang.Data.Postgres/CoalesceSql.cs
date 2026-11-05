// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Data.Common;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.Postgres;

/// <summary>
/// Tag-bound coalescing's statements, as both Postgres work coordinators run them: the pending groups and their age, the
/// pending singles of a group, the fold that stores the composite and completes the singles in one transaction, and the
/// deadline release that ships a matured group's singles individually.
/// </summary>
/// <remarks>
/// <para>
/// One copy, so the drivers cannot drift. The Dapper coordinator used to inherit the interface's inert defaults for all
/// four, so on that driver a coalesced message was parked and never shipped, folded or released (#2051).
/// </para>
/// <para>
/// Each coordinator supplies a command on its own connection and the prefix it qualifies names with: the quoted schema
/// followed by a dot (<c>"svc".</c>), or empty where the caller resolves through <c>search_path</c>. It is always built
/// by <see cref="PgIdentifier"/>, never from input, and every value is a bound parameter.
/// </para>
/// </remarks>
/// <docs>data/drivers#dapper-coordinator-parity</docs>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperCoalesceTests.cs</tests>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "S2077:Formatting SQL queries is security-sensitive", Justification = "Identifiers cannot be parameterized: the schema prefix is built by PgIdentifier and the object names are constants. Every value is a bound parameter.")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Major Code Smell", "S3265:Non-flags enums should not be used in bitwise operations", Justification = "NpgsqlDbType composes Array with the element type by design.")]
internal static class CoalesceSql {
  /// <summary>
  /// Every group with pending singles: how many, and the oldest and newest. Served by <c>idx_outbox_coalesce_pending</c>
  /// (coalesce_group, created_at); only pending singles ever live in that partial index.
  /// </summary>
  internal static async Task<IReadOnlyList<CoalesceGroupStats>> PendingGroupStatsAsync(DbCommand cmd, string prefix, CancellationToken cancellationToken) {
    cmd.CommandText = $@"
      SELECT coalesce_group, COUNT(*)::bigint, MIN(created_at), MAX(created_at)
      FROM {prefix}wh_outbox
      WHERE coalesce_group IS NOT NULL AND processed_at IS NULL
      GROUP BY coalesce_group";
    var results = new List<CoalesceGroupStats>();
    await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
      results.Add(new CoalesceGroupStats {
        Group = reader.GetString(0),
        PendingCount = reader.GetInt64(1),
        OldestCreatedAt = await reader.GetFieldValueAsync<DateTimeOffset>(2, cancellationToken).ConfigureAwait(false),
        NewestCreatedAt = await reader.GetFieldValueAsync<DateTimeOffset>(3, cancellationToken).ConfigureAwait(false),
      });
    }
    return results;
  }

  /// <summary>
  /// The group's pending singles, oldest first, locked <c>FOR UPDATE SKIP LOCKED</c> so two shippers folding the same
  /// group partition the rows. When <paramref name="callerInstanceId"/> is given, a caller whose own row says it may not
  /// claim folds nothing (300, #1636).
  /// </summary>
  internal static async Task<IReadOnlyList<OutboxMessage>> FetchPendingAsync(
      DbCommand cmd, string prefix, string group, int limit, Guid? callerInstanceId, JsonSerializerOptions jsonOptions,
      CancellationToken cancellationToken) {
    var callerMayClaim = callerInstanceId is null
      ? string.Empty
      : $@"
        AND NOT EXISTS (
          SELECT 1 FROM {prefix}wh_service_instances si
          WHERE si.instance_id = @p_instance_id AND NOT {prefix}wh_instance_may_claim(si))";
    cmd.CommandText = $@"
      SELECT message_id, stream_id, destination, message_type, envelope_type,
             event_data::text, metadata::text, is_event, scheduled_for, priority
      FROM {prefix}wh_outbox
      WHERE coalesce_group = @p_group AND processed_at IS NULL{callerMayClaim}
      ORDER BY created_at
      LIMIT @p_limit
      FOR UPDATE SKIP LOCKED";
    cmd.Parameters.Add(new NpgsqlParameter("p_group", group));
    cmd.Parameters.Add(new NpgsqlParameter("p_limit", limit));
    if (callerInstanceId is { } instanceId) {
      cmd.Parameters.Add(new NpgsqlParameter("p_instance_id", NpgsqlDbType.Uuid) { Value = instanceId });
    }

    var envelopeTypeInfo = jsonOptions.GetTypeInfo(typeof(MessageEnvelope<JsonElement>));
    var metadataTypeInfo = jsonOptions.GetTypeInfo(typeof(EnvelopeMetadata));
    var results = new List<OutboxMessage>();
    await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
      var messageId = reader.GetGuid(0);
      var envelope = JsonSerializer.Deserialize(reader.GetString(5), envelopeTypeInfo) as IMessageEnvelope<JsonElement>
        ?? throw new InvalidOperationException($"Failed to deserialize envelope for coalesce-pending message {messageId}.");
      MessageParseDiagnostics.Record(messageId, MessageParseSource.OutboxRead, MessageParseKind.Envelope);
      EnvelopeMetadata metadata;
      try {
        metadata = JsonSerializer.Deserialize(reader.GetString(6), metadataTypeInfo) as EnvelopeMetadata
          ?? new EnvelopeMetadata { MessageId = new MessageId(messageId), Hops = [] };
      } catch (JsonException) {
        // Legacy or minimal metadata: the fold only needs the payload, so the minimal metadata is synthesized rather
        // than stranding the row.
        metadata = new EnvelopeMetadata { MessageId = new MessageId(messageId), Hops = [] };
      }
      results.Add(new OutboxMessage {
        MessageId = messageId,
        StreamId = await reader.IsDBNullAsync(1, cancellationToken).ConfigureAwait(false) ? null : reader.GetGuid(1),
        Destination = await reader.IsDBNullAsync(2, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(2),
        MessageType = reader.GetString(3),
        EnvelopeType = await reader.IsDBNullAsync(4, cancellationToken).ConfigureAwait(false) ? string.Empty : reader.GetString(4),
        Envelope = envelope,
        Metadata = metadata,
        IsEvent = reader.GetBoolean(7),
        ScheduledFor = await reader.IsDBNullAsync(8, cancellationToken).ConfigureAwait(false)
          ? null
          : await reader.GetFieldValueAsync<DateTimeOffset>(8, cancellationToken).ConfigureAwait(false),
        Priority = reader.GetInt32(9),
        CoalesceGroup = group,
      });
    }
    return results;
  }

  /// <summary>
  /// Stores the composite rows and completes the folded singles inside the caller's transaction, so a single is either
  /// still pending or folded, never both and never neither. The caller commits, then rings the doorbells the store queued.
  /// </summary>
  internal static async Task StoreCompositeAndCompleteSinglesAsync(
      DbTransaction transaction, string prefix, string compositeMessagesJson, DateTime now,
      int partitionCount, Guid[] foldedIds, CancellationToken cancellationToken) {
    // The caller's transaction is open, so its connection is set: both coordinators begin it on that connection.
    var connection = transaction.Connection!;
    await using (var store = CoordinatorCommandTimeout.Apply(connection.CreateCommand())) {
      store.Transaction = transaction;
      store.CommandText = $"SELECT * FROM {prefix}store_outbox_messages(@messages::jsonb, NULL::uuid, NULL::timestamptz, @now, @partitions)";
      store.Parameters.Add(new NpgsqlParameter("messages", compositeMessagesJson));
      store.Parameters.Add(new NpgsqlParameter(nameof(now), now));
      store.Parameters.Add(new NpgsqlParameter("partitions", partitionCount));
      await store.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
    await using var complete = CoordinatorCommandTimeout.Apply(connection.CreateCommand());
    complete.Transaction = transaction;
    complete.CommandText = $"UPDATE {prefix}wh_outbox SET processed_at = NOW() WHERE message_id = ANY(@ids) AND processed_at IS NULL";
    complete.Parameters.Add(new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = foldedIds });
    await complete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
  }

  /// <summary>
  /// The deadline degrade: clears the group and the floor of the group's matured singles, which moves them into the
  /// eligible scan so the normal pump ships them individually. Returns how many were released.
  /// </summary>
  internal static async Task<int> ReleaseMaturedAsync(DbCommand cmd, string prefix, string group, CancellationToken cancellationToken) {
    cmd.CommandText = $@"
      UPDATE {prefix}wh_outbox
      SET coalesce_group = NULL, scheduled_for = NULL
      WHERE coalesce_group = @p_group AND scheduled_for <= NOW() AND processed_at IS NULL";
    cmd.Parameters.Add(new NpgsqlParameter("p_group", group));
    return await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
  }
}
