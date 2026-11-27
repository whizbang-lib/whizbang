// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Buffers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.Postgres;

/// <summary>
/// <see cref="IIntegrityExclusions"/> over the service's own Postgres store (#2588, migration 663), for both drivers.
/// A call naming up to <see cref="IntegrityExclusionOptions.InlineLimit"/> ids is applied at once
/// (<c>wh_integrity_exclude</c>, <c>wh_integrity_unexclude</c>); a larger one, and every selection, is queued in
/// <c>wh_integrity_exclusion_requests</c> for <see cref="IntegrityExclusionStep"/>. A dry run reads
/// <c>wh_integrity_exclusion_dry_run</c> and marks nothing.
/// </summary>
/// <remarks>
/// Received history only (decision D1): the SQL refuses this service's own events by id and a selection never picks
/// them, and a selection naming this service's id as its origin is refused here, before anything is queued.
/// </remarks>
/// <docs>resilience/integrity-exclusions#the-api</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityExclusionsApiTests.cs</tests>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "S2077:Formatting SQL queries is security-sensitive", Justification = "Only the schema prefix is formatted in, built by SqlText.Identifier; every value is a bound parameter.")]
public sealed partial class PostgresIntegrityExclusions : IIntegrityExclusions {
  /// <summary>The most events a dry run counts; a larger selection reports <see cref="IntegrityExclusionResult.Capped"/>.</summary>
  public const int DRY_RUN_CAP = 100_000;

  /// <summary>How many store rows a selection walk reads per event it may pick, so a sparse selection still advances.</summary>
  public const int SCAN_FACTOR = 10;

  internal const string EXCLUDE = "exclude";
  internal const string UNEXCLUDE = "unexclude";
  internal const string MARKED = "marked";
  internal const string UNMARKED = "unmarked";

  private readonly PostgresArchiveConnection _database;
  private readonly IntegrityExclusionOptions _options;
  private readonly TimeProvider _time;
  private readonly string? _serviceName;
  private readonly ILogger _logger;
  private readonly StreamIntegrityMetrics? _metrics;
  private readonly string _prefix;

  /// <summary>Creates the exclusions over the service's store.</summary>
  /// <param name="database">How to reach the store.</param>
  /// <param name="options">The integrity options; <see cref="StreamIntegrityOptions.Exclusions"/> is read.</param>
  /// <param name="time">The clock marks are stamped with.</param>
  /// <param name="serviceName">This service's name: an event whose first hop names it was written here and is refused.</param>
  /// <param name="logger">Logs a Warning per origin and batch.</param>
  /// <param name="metrics">Counts marks and unmarks; null for none.</param>
  public PostgresIntegrityExclusions(
      PostgresArchiveConnection database, StreamIntegrityOptions options, TimeProvider time, string? serviceName,
      ILogger logger, StreamIntegrityMetrics? metrics = null) {
    ArgumentNullException.ThrowIfNull(database);
    ArgumentNullException.ThrowIfNull(options);
    ArgumentNullException.ThrowIfNull(time);
    ArgumentNullException.ThrowIfNull(logger);
    _database = database;
    _options = options.Exclusions;
    _time = time;
    _serviceName = serviceName;
    _logger = logger;
    _metrics = metrics;
    _prefix = database.Schema is null ? string.Empty : SqlText.Identifier(database.Schema) + ".";
  }

  /// <summary>Builds the exclusions from a host's services, as both drivers register them.</summary>
  /// <param name="services">The host's services; the store connection is required.</param>
  /// <returns>The exclusions.</returns>
  public static PostgresIntegrityExclusions FromServices(IServiceProvider services) {
    ArgumentNullException.ThrowIfNull(services);
    return new PostgresIntegrityExclusions(
      services.GetRequiredService<PostgresArchiveConnection>(),
      services.GetService<IOptions<StreamIntegrityOptions>>()?.Value ?? new StreamIntegrityOptions(),
      services.GetService<TimeProvider>() ?? TimeProvider.System,
      services.GetService<IServiceInstanceProvider>()?.ServiceName,
      services.GetService<ILogger<PostgresIntegrityExclusions>>() ?? (ILogger)NullLogger.Instance,
      services.GetService<StreamIntegrityMetrics>());
  }

  /// <inheritdoc />
  public async Task<IntegrityExclusionResult> ExcludeAsync(IntegrityExclusionRequest request, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(request);
    _requireWhoAndWhy(request.Reason, request.RequestedBy);
    var hasIds = request.EventIds is { Count: > 0 };
    if (hasIds == (request.Selection is not null)) {
      throw new ArgumentException("Name the events to exclude either by EventIds or by Selection, not both and not neither.", nameof(request));
    }
    var selection = request.Selection is null ? null : SelectionJson(request.Selection);
    if (request.Selection?.OriginServiceId is Guid origin && origin == await _localServiceIdAsync(cancellationToken).ConfigureAwait(false)) {
      throw new ArgumentException(
        "A selection may not name this service as the origin: an exclusion applies to received history only, because this "
        + "service's consumers compare against its own events (#2588, decision D1).", nameof(request));
    }
    if (request.DryRun) {
      return await _dryRunAsync(request.EventIds, selection, cancellationToken).ConfigureAwait(false);
    }
    if (hasIds && request.EventIds!.Count <= _options.InlineLimit) {
      var outcomes = await _applyAsync(
        $"SELECT event_id, outcome, origin_service_id FROM {_prefix}wh_integrity_exclude($1, $2, $3, NULL, $4, $5)",
        [_uuids(request.EventIds), request.Reason, request.RequestedBy, (object?)_serviceName ?? DBNull.Value, _time.GetUtcNow()],
        cancellationToken).ConfigureAwait(false);
      Report(EXCLUDE, outcomes, request.Reason, request.RequestedBy, null);
      return new IntegrityExclusionResult { Outcomes = outcomes };
    }
    var id = await _queueAsync(EXCLUDE, request.EventIds, selection, null, request.Reason, request.RequestedBy, cancellationToken)
      .ConfigureAwait(false);
    return new IntegrityExclusionResult { RequestId = id, Queued = true };
  }

  /// <inheritdoc />
  public async Task<IntegrityExclusionResult> UnexcludeAsync(IntegrityUnexclusionRequest request, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(request);
    _requireWhoAndWhy(request.Reason, request.RequestedBy);
    var ways = (request.EventIds is { Count: > 0 } ? 1 : 0) + (request.RequestId is null ? 0 : 1) + (request.OriginServiceId is null ? 0 : 1);
    if (ways != 1 || (request.EventTypes is not null && request.OriginServiceId is null)) {
      throw new ArgumentException(
        "Name the exclusions to reverse in exactly one way: EventIds, RequestId, or OriginServiceId (with EventTypes).", nameof(request));
    }
    if (request.EventIds is { Count: > 0 } ids && ids.Count <= _options.InlineLimit) {
      var outcomes = await _applyAsync(
        $"SELECT event_id, outcome, origin_service_id FROM {_prefix}wh_integrity_unexclude($1, $2, $3, NULL, $4)",
        [_uuids(ids), request.Reason, request.RequestedBy, _time.GetUtcNow()], cancellationToken).ConfigureAwait(false);
      Report(UNEXCLUDE, outcomes, request.Reason, request.RequestedBy, null);
      return new IntegrityExclusionResult { Outcomes = outcomes };
    }
    var selection = request.OriginServiceId is null
      ? "{}"
      : SelectionJson(new IntegrityExclusionSelection { OriginServiceId = request.OriginServiceId, EventTypes = request.EventTypes });
    var id = await _queueAsync(UNEXCLUDE, request.EventIds, selection, request.RequestId, request.Reason, request.RequestedBy,
      cancellationToken).ConfigureAwait(false);
    return new IntegrityExclusionResult { RequestId = id, Queued = true };
  }

  /// <inheritdoc />
  public async Task<IntegrityExclusionSummary> SummarizeAsync(Guid? originServiceId = null, CancellationToken cancellationToken = default) {
    await using var connection = await _database.Open(cancellationToken).ConfigureAwait(false);
    await using var command = new NpgsqlCommand(
      $"SELECT origin_service_id, excluded_count, pending_count, unknown_count, last_marked_at, open_requests FROM {_prefix}wh_integrity_exclusion_summary($1)",
      connection);
    command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = (object?)originServiceId ?? DBNull.Value });
    var origins = new List<IntegrityExclusionOriginSummary>();
    long open = 0;
    await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false)) {
      while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
        origins.Add(new IntegrityExclusionOriginSummary(reader.GetGuid(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3),
          reader.GetFieldValue<DateTimeOffset>(4)));
        open = reader.GetInt64(5);
      }
    }
    if (origins.Count == 0) {
      await using var count = new NpgsqlCommand(
        $"SELECT count(*) FROM {_prefix}wh_integrity_exclusion_requests WHERE status = 'Pending'", connection);
      open = (long)(await count.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }
    return new IntegrityExclusionSummary { Origins = origins, OpenRequests = open };
  }

  /// <summary>
  /// The selection as the SQL reads it: <c>originServiceId</c>, <c>eventTypes</c>, <c>from</c>, <c>until</c> and
  /// <c>unresolved</c> (the bit set), each only when given. Written without reflection.
  /// </summary>
  /// <param name="selection">The selection.</param>
  /// <returns>Its JSON.</returns>
  public static string SelectionJson(IntegrityExclusionSelection selection) {
    ArgumentNullException.ThrowIfNull(selection);
    var buffer = new ArrayBufferWriter<byte>();
    using (var writer = new Utf8JsonWriter(buffer)) {
      writer.WriteStartObject();
      if (selection.OriginServiceId is Guid origin) {
        writer.WriteString("originServiceId", origin);
      }
      if (selection.EventTypes is { } types) {
        writer.WriteStartArray("eventTypes");
        foreach (var type in types) {
          writer.WriteStringValue(type);
        }
        writer.WriteEndArray();
      }
      if (selection.From is DateTimeOffset from) {
        writer.WriteString("from", from);
      }
      if (selection.Until is DateTimeOffset until) {
        writer.WriteString("until", until);
      }
      if (selection.Unresolved != IntegrityUnresolved.None) {
        writer.WriteNumber("unresolved", (int)selection.Unresolved);
      }
      writer.WriteEndObject();
    }
    return Encoding.UTF8.GetString(buffer.WrittenSpan);
  }

  /// <summary>Logs a Warning per origin and counts what a batch marked or unmarked (#2588).</summary>
  /// <param name="action"><c>exclude</c> or <c>unexclude</c>.</param>
  /// <param name="outcomes">What the batch did, per origin and outcome.</param>
  /// <param name="reason">Why.</param>
  /// <param name="requestedBy">Who asked.</param>
  /// <param name="requestId">The queued request, or null for an inline call.</param>
  internal void Report(string action, IReadOnlyList<IntegrityExclusionOutcome> outcomes, string reason, string requestedBy, Guid? requestId) {
    var applied = action == EXCLUDE ? MARKED : UNMARKED;
    foreach (var outcome in outcomes.Where(o => o.Outcome == applied && o.OriginServiceId is not null)) {
      var tag = new KeyValuePair<string, object?>("origin", outcome.OriginServiceId!.Value.ToString());
      if (action == EXCLUDE) {
        _metrics?.ExclusionsMarked.Add(outcome.Count, tag);
        LogExcluded(_logger, outcome.Count, outcome.OriginServiceId.Value, requestedBy, reason, requestId);
      } else {
        _metrics?.ExclusionsUnmarked.Add(outcome.Count, tag);
        LogUnexcluded(_logger, outcome.Count, outcome.OriginServiceId.Value, requestedBy, reason, requestId);
      }
    }
    var refused = outcomes.Where(o => o.Outcome != applied && o.Outcome is not ("already_excluded" or "not_excluded")).Sum(o => o.Count);
    if (refused > 0) {
      LogRefused(_logger, refused, action, string.Join(", ", outcomes.Where(o => o.Outcome != applied).Select(o => $"{o.Outcome}: {o.Count}")));
    }
  }

  private static void _requireWhoAndWhy(string reason, string requestedBy) {
    if (string.IsNullOrWhiteSpace(reason)) {
      throw new ArgumentException("A reason is required: it is recorded on every mark and in the audit log.", nameof(reason));
    }
    if (string.IsNullOrWhiteSpace(requestedBy)) {
      throw new ArgumentException("Who asked is required: it is recorded on every mark and in the audit log.", nameof(requestedBy));
    }
  }

  private static NpgsqlParameter _uuids(IReadOnlyList<Guid>? ids) =>
    new() { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Uuid, Value = (object?)ids?.ToArray() ?? DBNull.Value };

  private async Task<IReadOnlyList<IntegrityExclusionOutcome>> _applyAsync(string sql, object[] arguments, CancellationToken cancellationToken) {
    await using var connection = await _database.Open(cancellationToken).ConfigureAwait(false);
    await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
    await using var command = new NpgsqlCommand(sql, connection, transaction);
    foreach (var argument in arguments) {
      command.Parameters.Add(argument as NpgsqlParameter ?? new NpgsqlParameter { Value = argument });
    }
    var counts = new Dictionary<(Guid? Origin, string Outcome), int>();
    await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false)) {
      while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
        var key = (reader.IsDBNull(2) ? (Guid?)null : reader.GetGuid(2), reader.GetString(1));
        counts[key] = counts.GetValueOrDefault(key) + 1;
      }
    }
    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    return [.. counts.Select(c => new IntegrityExclusionOutcome(c.Key.Origin, c.Key.Outcome, c.Value))
      .OrderBy(o => o.OriginServiceId).ThenBy(o => o.Outcome, StringComparer.Ordinal)];
  }

  private async Task<IntegrityExclusionResult> _dryRunAsync(IReadOnlyList<Guid>? ids, string? selection, CancellationToken cancellationToken) {
    await using var connection = await _database.Open(cancellationToken).ConfigureAwait(false);
    await using var command = new NpgsqlCommand(
      $"SELECT origin_service_id, outcome, event_count, capped FROM {_prefix}wh_integrity_exclusion_dry_run($1, $2::jsonb, $3, $4, $5)",
      connection);
    command.Parameters.Add(_uuids(ids));
    command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = (object?)selection ?? DBNull.Value });
    command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = (object?)_serviceName ?? DBNull.Value });
    command.Parameters.AddWithValue(DRY_RUN_CAP);
    command.Parameters.AddWithValue(_options.BatchSize * SCAN_FACTOR);
    var outcomes = new List<IntegrityExclusionOutcome>();
    var capped = false;
    await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
      outcomes.Add(new IntegrityExclusionOutcome(reader.IsDBNull(0) ? null : reader.GetGuid(0), reader.GetString(1), reader.GetInt32(2)));
      capped |= reader.GetBoolean(3);
    }
    return new IntegrityExclusionResult { DryRun = true, Capped = capped, Outcomes = outcomes };
  }

  private async Task<Guid> _queueAsync(string action, IReadOnlyList<Guid>? ids, string? selection, Guid? ofRequest, string reason,
      string requestedBy, CancellationToken cancellationToken) {
    var id = TrackedGuid.New().Value;
    await using var connection = await _database.Open(cancellationToken).ConfigureAwait(false);
    await using var command = new NpgsqlCommand($"""
      INSERT INTO {_prefix}wh_integrity_exclusion_requests
        (request_id, action, event_ids, selection, of_selection_id, reason, requested_by, requested_at)
      VALUES ($1, $2, $3, $4::jsonb, $5, $6, $7, $8)
      """, connection);
    command.Parameters.AddWithValue(id);
    command.Parameters.AddWithValue(action);
    command.Parameters.Add(_uuids(ids is { Count: > 0 } ? ids : null));
    command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = (object?)selection ?? DBNull.Value });
    command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = (object?)ofRequest ?? DBNull.Value });
    command.Parameters.AddWithValue(reason.Trim());
    command.Parameters.AddWithValue(requestedBy.Trim());
    command.Parameters.AddWithValue(_time.GetUtcNow());
    _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    LogQueued(_logger, action, id, requestedBy, reason);
    return id;
  }

  private async Task<Guid?> _localServiceIdAsync(CancellationToken cancellationToken) {
    await using var connection = await _database.Open(cancellationToken).ConfigureAwait(false);
    await using var command = new NpgsqlCommand($"SELECT service_id FROM {_prefix}wh_service_config LIMIT 1", connection);
    return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as Guid?;
  }

  [LoggerMessage(EventId = 1, Level = LogLevel.Warning,
    Message = "Excluded {Count} received event(s) from origin {OriginServiceId} from integrity checks, asked by '{RequestedBy}': " +
              "{Reason} (request {RequestId}). The integrity engine no longer compares, asks about, holds or heals them until they are unexcluded (#2588)")]
  static partial void LogExcluded(ILogger logger, int count, Guid originServiceId, string requestedBy, string reason, Guid? requestId);

  [LoggerMessage(EventId = 2, Level = LogLevel.Warning,
    Message = "Unexcluded {Count} received event(s) from origin {OriginServiceId}, asked by '{RequestedBy}': {Reason} " +
              "(request {RequestId}). The integrity engine resolves them again from scratch (#2588)")]
  static partial void LogUnexcluded(ILogger logger, int count, Guid originServiceId, string requestedBy, string reason, Guid? requestId);

  [LoggerMessage(EventId = 3, Level = LogLevel.Warning,
    Message = "{Count} event(s) asked to {Action} were refused ({Outcomes}): only received history this service stores can be " +
              "excluded, never its own events or events written here (#2588)")]
  static partial void LogRefused(ILogger logger, int count, string action, string outcomes);

  [LoggerMessage(EventId = 4, Level = LogLevel.Information,
    Message = "Queued integrity {Action} request {RequestId}, asked by '{RequestedBy}': {Reason}; the maintenance step applies it in bounded batches (#2588)")]
  static partial void LogQueued(ILogger logger, string action, Guid requestId, string requestedBy, string reason);
}
