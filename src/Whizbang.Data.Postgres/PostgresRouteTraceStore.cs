// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using NpgsqlTypes;
using Whizbang.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Serialization;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.Postgres;

/// <summary>
/// The Postgres <see cref="IRouteStore"/> and <see cref="ITraceStore"/> over the service's own database (#1726): routes
/// from <c>wh_routes</c>; traces rebuilt by <c>wh_trace_metadata</c> from the staged full form while it exists, then
/// from the event row, its trace and its route, then from the row alone.
/// </summary>
/// <remarks>
/// <para>
/// The trace store is filled where events are stored (the emit chains and the direct appends stage every event's full
/// form under the Linked write form), not by <see cref="StoreAsync"/>, which does nothing: most stored events never pass
/// through the dispatcher's store call, and an event the write paths stage needs no second write. The events it holds
/// are those with a staged form or a trace; <see cref="GetByMessageIdAsync"/> also answers from an event's row alone once
/// its trace has aged out. Envelopes come back as <c>MessageEnvelope&lt;JsonElement&gt;</c> with the stored payload.
/// </para>
/// <para>
/// The correlation, causal-chain and time-range queries read the traces through their indexes and the staged rows not
/// yet distilled by scanning them: the staging table has one index, its key, so the hot path writes one index entry.
/// </para>
/// </remarks>
/// <docs>fundamentals/persistence/observability#the-postgres-stores</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RouteTraceStoreTests.cs</tests>
public sealed class PostgresRouteTraceStore : IRouteStore, ITraceStore {
  private const string STAGED_HOPS = "COALESCE(s.metadata -> 'Hops', s.metadata -> 'hops')";
  private static readonly JsonSerializerOptions _options = JsonContextRegistry.CreateCombinedOptions();

  private readonly PostgresArchiveConnection _database;
  private readonly string _prefix;

  /// <summary>Creates the store over the service's database.</summary>
  /// <param name="database">How the service's database is reached, as both Postgres drivers register it.</param>
  public PostgresRouteTraceStore(PostgresArchiveConnection database) {
    ArgumentNullException.ThrowIfNull(database);
    _database = database;
    _prefix = database.Schema is null ? string.Empty : SqlText.Identifier(database.Schema) + ".";
  }

  // ===== IRouteStore =====

  /// <inheritdoc />
  public async Task<RouteInfo?> GetRouteAsync(Guid routeId, CancellationToken cancellationToken = default) =>
    (await _routesAsync("r.route_id = $1", routeId, cancellationToken).ConfigureAwait(false)).FirstOrDefault();

  /// <inheritdoc />
  public async Task<IReadOnlyList<RouteInfo>> GetRoutesAsync(string eventType, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(eventType);
    return await _routesAsync("r.event_type = $1", RouteNormalizer.TypeNamePart(eventType), cancellationToken).ConfigureAwait(false);
  }

  /// <inheritdoc />
  public async Task<IReadOnlyList<RouteInfo>> GetRoutesForApplyPathAsync(IReadOnlyList<string> applyPath, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(applyPath);
    var types = applyPath.Select(step => RouteNormalizer.TypeNamePart(step.TrimEnd('+'))).Distinct(StringComparer.Ordinal).ToList();
    var routes = await _routesAsync("r.event_type = ANY($1)", types.ToArray(), cancellationToken).ConfigureAwait(false);
    return [.. routes.OrderBy(r => types.IndexOf(r.EventType))];
  }

  private async Task<List<RouteInfo>> _routesAsync(string where, object argument, CancellationToken cancellationToken) {
    await using var connection = await _database.Open(cancellationToken).ConfigureAwait(false);
#pragma warning disable S2077 // the filter is one of three constants in this class; the value is a parameter
    await using var command = new NpgsqlCommand(
      "SELECT r.route_id, r.event_type, r.hops::text, r.occurrences, r.first_seen, r.last_seen, r.duration_count, " +
      $"r.duration_sum_ms, r.duration_min_ms, r.duration_max_ms, r.duration_buckets FROM {_prefix}wh_routes r " +
      $"WHERE {where} ORDER BY r.occurrences DESC, r.route_id", connection);
#pragma warning restore S2077
    command.Parameters.AddWithValue(argument);
    var routes = new List<RouteInfo>();
    await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
      routes.Add(new RouteInfo(
        reader.GetGuid(0),
        reader.GetString(1),
        _routeHops(reader.GetString(2)),
        reader.GetInt64(3),
        reader.GetFieldValue<DateTimeOffset>(4),
        reader.GetFieldValue<DateTimeOffset>(5),
        new RouteDurationStatistics(
          reader.GetInt64(6),
          reader.GetDouble(7),
          reader.IsDBNull(8) ? null : reader.GetDouble(8),
          reader.IsDBNull(9) ? null : reader.GetDouble(9),
          reader.GetFieldValue<long[]>(10))));
    }
    return routes;
  }

  private static List<RouteHop> _routeHops(string json) {
    using var document = JsonDocument.Parse(json);
    return [.. document.RootElement.EnumerateArray().Select(hop => new RouteHop(
      hop.TryGetProperty("ty", out var type) && type.ValueKind == JsonValueKind.Number ? (HopType)type.GetInt32() : HopType.Current,
      _text(hop, "sn"), _text(hop, "to"), _text(hop, "es"), _text(hop, "cl"), _text(hop, "cm"),
      [.. hop.GetProperty("pd").EnumerateArray().Select(d => new RoutePolicyDecision(_text(d, "pn"), _text(d, "r"), d.GetProperty("m").ValueKind == JsonValueKind.True))]))];
  }

  private static string _text(JsonElement element, string name) =>
    element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : string.Empty;

  // ===== ITraceStore =====

  /// <summary>Does nothing: the write paths stage every stored event's full form, which is what this store reads.</summary>
  /// <param name="envelope">The envelope, not stored.</param>
  /// <param name="ct">Cancellation.</param>
  /// <returns>A completed task.</returns>
  public Task StoreAsync(IMessageEnvelope envelope, CancellationToken ct = default) {
    ArgumentNullException.ThrowIfNull(envelope);
    return Task.CompletedTask;
  }

  /// <inheritdoc />
  public async Task<IMessageEnvelope?> GetByMessageIdAsync(MessageId messageId, CancellationToken ct = default) =>
    (await _envelopesAsync([messageId.Value], ct).ConfigureAwait(false)).FirstOrDefault();

  /// <inheritdoc />
  public async Task<Guid?> GetRouteIdAsync(MessageId messageId, CancellationToken ct = default) {
    await using var connection = await _database.Open(ct).ConfigureAwait(false);
    await using var command = new NpgsqlCommand($"SELECT route_id FROM {_prefix}wh_trace_metadata($1)", connection);
    command.Parameters.AddWithValue(NpgsqlDbType.Array | NpgsqlDbType.Uuid, new[] { messageId.Value });
    var value = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
    return value is Guid routeId ? routeId : null;
  }

  /// <inheritdoc />
  public async Task<List<IMessageEnvelope>> GetByCorrelationAsync(CorrelationId correlationId, CancellationToken ct = default) {
    var ids = await _idsAsync(
      "SELECT t.event_id FROM {0}wh_traces t WHERE t.correlation_id = $1 " +
      "UNION SELECT s.event_id FROM {0}wh_event_metadata_staging s WHERE " + STAGED_HOPS + " -> 0 ->> 'co' = $1::text",
      [correlationId.Value], ct).ConfigureAwait(false);
    var envelopes = await _envelopesAsync(ids, ct).ConfigureAwait(false);
    return [.. envelopes.OrderBy(e => e.GetMessageTimestamp())];
  }

  /// <inheritdoc />
  public async Task<List<IMessageEnvelope>> GetByTimeRangeAsync(DateTimeOffset from, DateTimeOffset toTime, CancellationToken ct = default) {
    var ids = await _idsAsync(
      "SELECT t.event_id FROM {0}wh_traces t WHERE t.occurred_at BETWEEN $1 AND $2 " +
      "UNION SELECT s.event_id FROM {0}wh_event_metadata_staging s WHERE {0}_wh_first_current_hop_at(" + STAGED_HOPS + ") BETWEEN $1 AND $2",
      [from.ToUniversalTime(), toTime.ToUniversalTime()], ct).ConfigureAwait(false);
    var envelopes = await _envelopesAsync(ids, ct).ConfigureAwait(false);
    return [.. envelopes.OrderBy(_firstCurrentTimestamp)];
  }

  /// <inheritdoc />
  public async Task<List<IMessageEnvelope>> GetCausalChainAsync(MessageId messageId, CancellationToken ct = default) {
    var self = await GetByMessageIdAsync(messageId, ct).ConfigureAwait(false);
    if (self is null) {
      return [];
    }
    var chain = new HashSet<MessageId> { self.MessageId };
    var results = new List<IMessageEnvelope> { self };

    // Up the causation chain, while each parent is held and not already in the chain. (A message id is never empty: a
    // MessageId is a version 7 uuid, so the in-memory store's empty-cause check has nothing to stop here.)
    var cause = self.GetCausationId();
    while (cause is { } parentId && chain.Add(parentId)) {
      var parent = await GetByMessageIdAsync(parentId, ct).ConfigureAwait(false);
      if (parent is null) {
        break;
      }
      results.Add(parent);
      cause = parent.GetCausationId();
    }

    // Down: every held event caused by one already in the chain, generation by generation.
    var frontier = new List<MessageId> { self.MessageId };
    while (frontier.Count > 0) {
      var children = await _childrenAsync(frontier, ct).ConfigureAwait(false);
      frontier = [];
      foreach (var child in children.Where(c => chain.Add(c.MessageId))) {
        results.Add(child);
        frontier.Add(child.MessageId);
      }
    }

    return [.. results.OrderBy(_firstCurrentTimestamp)];
  }

  private async Task<List<IMessageEnvelope>> _childrenAsync(List<MessageId> parents, CancellationToken ct) {
    var parentIds = parents.Select(p => p.Value).ToArray();
    var ids = await _idsAsync(
      "SELECT t.event_id FROM {0}wh_traces t WHERE t.causation_id = ANY($1) " +
      "UNION SELECT s.event_id FROM {0}wh_event_metadata_staging s WHERE {0}_wh_try_uuid(" + STAGED_HOPS + " -> 0 ->> 'ca') = ANY($1)",
      [parentIds], ct).ConfigureAwait(false);
    return await _envelopesAsync(ids, ct).ConfigureAwait(false);
  }

  private static DateTimeOffset _firstCurrentTimestamp(IMessageEnvelope envelope) =>
    envelope.Hops.FirstOrDefault(h => h.Type == HopType.Current)?.Timestamp ?? DateTimeOffset.MinValue;

  private async Task<Guid[]> _idsAsync(string sqlFormat, object[] arguments, CancellationToken ct) {
    await using var connection = await _database.Open(ct).ConfigureAwait(false);
#pragma warning disable S2077 // the statements are constants in this class; only the schema identifier is formatted in
    await using var command = new NpgsqlCommand(string.Format(System.Globalization.CultureInfo.InvariantCulture, sqlFormat, _prefix), connection);
#pragma warning restore S2077
    foreach (var argument in arguments) {
      command.Parameters.AddWithValue(argument);
    }
    var ids = new List<Guid>();
    await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
    while (await reader.ReadAsync(ct).ConfigureAwait(false)) {
      ids.Add(reader.GetGuid(0));
    }
    return [.. ids];
  }

  private async Task<List<IMessageEnvelope>> _envelopesAsync(Guid[] ids, CancellationToken ct) {
    if (ids.Length == 0) {
      return [];
    }
    await using var connection = await _database.Open(ct).ConfigureAwait(false);
    await using var command = new NpgsqlCommand($"SELECT event_id, metadata::text, event_data::text FROM {_prefix}wh_trace_metadata($1)", connection);
    command.Parameters.AddWithValue(NpgsqlDbType.Array | NpgsqlDbType.Uuid, ids);
    var envelopes = new List<IMessageEnvelope>();
    await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
    while (await reader.ReadAsync(ct).ConfigureAwait(false)) {
      // The metadata is never null (wh_trace_metadata answers {} at worst); the payload is null once the event is gone.
      envelopes.Add(_envelope(reader.GetGuid(0), reader.GetString(1), reader.IsDBNull(2) ? "null" : reader.GetString(2)));
    }
    return envelopes;
  }

  private static MessageEnvelope<JsonElement> _envelope(Guid eventId, string metadataJson, string payloadJson) {
    using var metadata = JsonDocument.Parse(metadataJson);
    var root = metadata.RootElement;
    List<MessageHop> hops = [];
    if (root.TryGetProperty("Hops", out var hopList) || root.TryGetProperty("hops", out hopList)) {
      hops = hopList.Deserialize((JsonTypeInfo<List<MessageHop>>)_options.GetTypeInfo(typeof(List<MessageHop>))) ?? [];
    }
    using var payload = JsonDocument.Parse(payloadJson);
    return new MessageEnvelope<JsonElement> {
      MessageId = MessageId.From(eventId),
      Payload = payload.RootElement.Clone(),
      Hops = hops,
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local },
    };
  }
}

/// <summary>
/// Registers the Postgres route and trace stores (#1726). Each is registered on its own, so a host can use either alone:
/// Postgres routes with the in-memory trace store, or the Postgres trace store with no routes. Both read the database
/// through the <see cref="PostgresArchiveConnection"/> both Postgres drivers register.
/// </summary>
/// <docs>fundamentals/persistence/observability#the-postgres-stores</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RouteTraceStoreTests.cs:Registration_EitherStoreAlone_NeedsNotTheOtherAsync</tests>
public static class PostgresRouteTraceStoreRegistration {
  /// <summary>Registers <see cref="PostgresRouteTraceStore"/> as the host's <see cref="IRouteStore"/>.</summary>
  /// <param name="services">The host's services.</param>
  /// <returns>The services.</returns>
  public static IServiceCollection AddWhizbangPostgresRouteStore(this IServiceCollection services) {
    ArgumentNullException.ThrowIfNull(services);
    services.TryAddSingleton(sp => new PostgresRouteTraceStore(sp.GetRequiredService<PostgresArchiveConnection>()));
    services.RemoveAll<IRouteStore>();
    services.AddSingleton<IRouteStore>(sp => sp.GetRequiredService<PostgresRouteTraceStore>());
    return services;
  }

  /// <summary>
  /// Registers <see cref="PostgresRouteTraceStore"/> as the host's only <see cref="ITraceStore"/>, replacing one
  /// registered earlier (the in-memory store, for example).
  /// </summary>
  /// <param name="services">The host's services.</param>
  /// <returns>The services.</returns>
  public static IServiceCollection AddWhizbangPostgresTraceStore(this IServiceCollection services) {
    ArgumentNullException.ThrowIfNull(services);
    services.TryAddSingleton(sp => new PostgresRouteTraceStore(sp.GetRequiredService<PostgresArchiveConnection>()));
    services.RemoveAll<ITraceStore>();
    services.AddSingleton<ITraceStore>(sp => sp.GetRequiredService<PostgresRouteTraceStore>());
    return services;
  }
}
