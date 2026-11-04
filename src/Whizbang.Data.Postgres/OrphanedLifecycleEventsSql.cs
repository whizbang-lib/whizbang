// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Data.Common;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Security;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.Postgres;

/// <summary>
/// The lifecycle reconciliation both Postgres work coordinators run: the completion marker a finished lifecycle writes,
/// and the startup query behind <see cref="Whizbang.Core.Messaging.IWorkCoordinator.GetOrphanedLifecycleEventsAsync"/>
/// with the envelope each orphan is replayed with. The query text is held apart so a plan-shape test can EXPLAIN the
/// very text the coordinator runs.
/// </summary>
/// <remarks>
/// The Dapper coordinator used to inherit the interface's inert defaults for both, so on that driver no completion was
/// recorded and the startup reconciliation found nothing to replay, however many lifecycles a crash had orphaned (#2051).
/// </remarks>
/// <docs>operations/infrastructure/storage-maintenance#brin-indexes</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Migrations/BrinIndexCandidatesTests.cs:TheOrphanedLifecycleQuery_ReadsTheWindowThroughTheBrinIndexAsync</tests>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperLifecycleAndCatalogTests.cs</tests>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "S2077:Formatting SQL queries is security-sensitive", Justification = "Identifiers cannot be parameterized: the schema prefix is built by PgIdentifier and the object names are constants. Every value is a bound parameter.")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Major Code Smell", "S3265:Non-flags enums should not be used in bitwise operations", Justification = "NpgsqlDbType composes Array with the element type by design.")]
internal static class OrphanedLifecycleEventsSql {
  /// <summary>A stored event the reconciliation found orphaned, as read.</summary>
  internal sealed record Row(Guid EventId, Guid StreamId, string EventData, string EventType, string? Scope);

  /// <summary>
  /// The query text for the given tables: events inside the lookback window whose expected perspectives all have a
  /// cursor at or past them and that have no completion marker, oldest first. The markers name the expected event types
  /// and the expected perspective names (parallel arrays), the window's start, and the row cap; by default they are the
  /// EF Core positional placeholders <c>{0}</c> to <c>{3}</c>.
  /// </summary>
  /// <remarks>
  /// The window (<c>created_at &gt;= start</c>) is the selective predicate: thirty minutes of a store that keeps every
  /// event. #1745 serves it with <c>idx_event_store_created_at_brin</c> (migration 258).
  /// </remarks>
  /// <docs>operations/infrastructure/storage-maintenance#brin-indexes</docs>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Migrations/BrinIndexCandidatesTests.cs:TheOrphanedLifecycleQuery_ReadsTheWindowThroughTheBrinIndexAsync</tests>
  internal static string Build(
      string eventStoreTable, string bodyTable, string cursorsTable, string completionsTable,
      string types = "{0}", string names = "{1}", string start = "{2}", string cap = "{3}") =>
    $@"
      WITH expected AS (
        SELECT * FROM unnest({types}::text[], {names}::text[]) AS x(event_type, perspective_name)
      ),
      expected_counts AS (
        SELECT event_type, COUNT(*) AS cnt FROM expected GROUP BY event_type
      )
      SELECT e.event_id, e.stream_id, eb.event_data AS event_data, eb.metadata AS metadata, e.event_type, e.scope
      FROM {eventStoreTable} e
      JOIN expected_counts ec ON ec.event_type = e.event_type
      LEFT JOIN {bodyTable} eb ON eb.event_id = e.event_id
      WHERE e.created_at >= {start}
        AND NOT EXISTS (
          SELECT 1 FROM {completionsTable} lc WHERE lc.event_id = e.event_id
        )
        AND (
          SELECT COUNT(DISTINCT pc.perspective_name)
          FROM {cursorsTable} pc
          JOIN expected x ON x.event_type = e.event_type AND x.perspective_name = pc.perspective_name
          WHERE pc.stream_id = e.stream_id
            AND pc.last_event_id >= e.event_id
        ) = ec.cnt
      ORDER BY e.created_at
      LIMIT {cap}";

  /// <summary>
  /// The expectation pairs the query takes as parallel arrays: one (event type, perspective) per expected perspective.
  /// Empty when no type expects a perspective.
  /// </summary>
  internal static (string[] Types, string[] Names) Pairs(Dictionary<string, IReadOnlyList<string>> perspectivesPerEventType) {
    var types = new List<string>();
    var names = new List<string>();
    foreach (var (eventType, perspectives) in perspectivesPerEventType) {
      foreach (var perspective in perspectives) {
        types.Add(eventType);
        names.Add(perspective);
      }
    }
    return ([.. types], [.. names]);
  }

  /// <summary>The orphaned events, read with named parameters on a coordinator's own command, oldest first.</summary>
  internal static async Task<IReadOnlyList<Row>> ReadAsync(
      DbCommand cmd, string prefix, string[] types, string[] names, DateTimeOffset start, int cap, CancellationToken cancellationToken) {
    cmd.CommandText = Build(
      prefix + "wh_event_store", prefix + "wh_event_body", prefix + "wh_perspective_cursors", prefix + "wh_lifecycle_completions",
      "@types", "@names", "@start", "@cap");
    cmd.Parameters.Add(new NpgsqlParameter(nameof(types), NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = types });
    cmd.Parameters.Add(new NpgsqlParameter(nameof(names), NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = names });
    cmd.Parameters.Add(new NpgsqlParameter(nameof(start), NpgsqlDbType.TimestampTz) { Value = start });
    cmd.Parameters.Add(new NpgsqlParameter(nameof(cap), NpgsqlDbType.Integer) { Value = Math.Max(1, cap) });
    var rows = new List<Row>();
    await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
      rows.Add(new Row(
        reader.GetGuid(0), reader.GetGuid(1),
        await reader.IsDBNullAsync(2, cancellationToken).ConfigureAwait(false) ? string.Empty : reader.GetString(2),
        reader.GetString(4),
        await reader.IsDBNullAsync(5, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(5)));
    }
    return rows;
  }

  /// <summary>Records that the event's lifecycle completed; recording it again is absorbed.</summary>
  internal static async Task RecordCompletionAsync(
      DbCommand cmd, string prefix, Guid eventId, Guid instanceId, CancellationToken cancellationToken) {
    cmd.CommandText = $"INSERT INTO {prefix}wh_lifecycle_completions (event_id, instance_id) VALUES (@e, @i) ON CONFLICT DO NOTHING";
    cmd.Parameters.Add(new NpgsqlParameter("e", eventId));
    cmd.Parameters.Add(new NpgsqlParameter("i", instanceId));
    await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
  }

  /// <summary>
  /// The envelope an orphan is replayed with: the payload as a <see cref="JsonElement"/> (the concrete type is resolved
  /// downstream), and the stored scope restored as hops so the replayed stages run under it. A scope that cannot be read
  /// is reported to <paramref name="onScopeUnreadable"/> and the event replays unscoped.
  /// </summary>
  internal static MessageEnvelope<JsonElement> ToEnvelope(
      Guid eventId, string eventData, string? scope, JsonSerializerOptions jsonOptions, Action<Exception> onScopeUnreadable) {
    JsonElement payload;
    try {
      // GetTypeInfo answers for exactly the type asked or throws (caught below), so this is the JsonElement contract.
      var typeInfo = (System.Text.Json.Serialization.Metadata.JsonTypeInfo<JsonElement>)jsonOptions.GetTypeInfo(typeof(JsonElement));
      payload = JsonSerializer.Deserialize(eventData, typeInfo);
    } catch (NotSupportedException) {
      // The chained type resolver may answer with polymorphic IEvent type info; parse directly (AOT-safe).
      payload = JsonElement.Parse(eventData);
    } catch (InvalidOperationException ex) when (ex.Message.Contains("incompatible JsonTypeInfo", StringComparison.Ordinal)) {
      payload = JsonElement.Parse(eventData);
    }
    MessageParseDiagnostics.Record(eventId, MessageParseSource.EventStoreRead, MessageParseKind.Envelope);

    var hops = new List<MessageHop>();
    try {
      StoredScope.RestoreInto(hops, StoredScope.Read(scope, jsonOptions));
    } catch (Exception ex) {
      // Best-effort for reconciliation, but a parse failure silently drops the stored scope: always reported.
      onScopeUnreadable(ex);
    }

    return new MessageEnvelope<JsonElement> {
      MessageId = new MessageId(eventId),
      Payload = payload,
      Hops = hops,
      DispatchContext = new MessageDispatchContext {
        Mode = DispatchModes.Local,
        Source = MessageSource.Local
      }
    };
  }
}
