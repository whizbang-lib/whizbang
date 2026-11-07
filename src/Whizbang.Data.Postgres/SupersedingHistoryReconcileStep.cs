// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;
using Whizbang.Core;
using Whizbang.Core.Configuration;
using Whizbang.Core.Lifecycle;
using Whizbang.Core.Serialization;
using Whizbang.Core.Workers;

namespace Whizbang.Data.Postgres;

/// <summary>What one superseding-history cycle did.</summary>
/// <param name="Batches">The batches it began, across every type.</param>
/// <param name="EventsScanned">Events of the store read past the types' cursors, of any type.</param>
/// <param name="EventsStamped">Events stamped with the superseding flag and their key.</param>
/// <param name="EventsQueued">Events queued for the superseded-event reaper.</param>
/// <param name="TypesFinished">Types whose walk read to the end of the store in this cycle.</param>
/// <param name="Status">The last batch's status: <c>ok</c>, <c>finished</c>, <c>complete</c>, <c>held (...)</c> or
/// <c>skipped (...)</c>.</param>
/// <docs>fundamentals/events/superseding-events#history-stored-before-the-declaration</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SupersedingHistoryReconcileSqlTests.cs:Reconcile_RunsInBoundedBatches_AndResumesFromItsCursor_AfterACrashAsync</tests>
public readonly record struct SupersedingHistoryReconcileResult(
  int Batches, long EventsScanned, long EventsStamped, long EventsQueued, int TypesFinished, string Status);

/// <summary>
/// The maintenance step that brings the events a superseding type stored before it was declared
/// <see cref="Core.Attributes.SupersedingAttribute">[Superseding]</see> under the superseding rule (#2110). For each
/// registered superseding type it walks the event store by event id, a window of <see cref="SCAN_SIZE"/> events per batch
/// and each batch its own transaction, reads each unflagged event's key from its payload with the generated accessor the
/// write paths use, and has the SQL stamp the flag and the key and queue every event a newer one of its stream, type and
/// key supersedes. The superseded-event reaper then deletes a queued event only past every one of its gates.
/// </summary>
/// <remarks>
/// <para>
/// It runs in the maintenance cycle, so it is idle-gated (the cycle waits for the service to settle, or for its deferral
/// limit) and runs only after the maintainer turn. Within that, the SQL takes each type's cursor row with
/// <c>SKIP LOCKED</c>, so a second instance skips a type another is walking instead of doing the same work, and a crash
/// rolls back the batch, so the walk resumes from the last committed cursor. A rerun changes nothing: a stamped event is
/// never read again and a queued event is never queued twice.
/// </para>
/// <para>
/// A cycle runs at most <see cref="MAX_BATCHES_PER_CYCLE"/> batches across all types, so a large store is walked over many
/// idle cycles. A walk restarts from the first event when the type-definition registry holds a newer definition of the
/// type than the walk started under. <see cref="SupersedingOptions.ReconcileHistory"/> set to <see langword="false"/>
/// turns the step off.
/// </para>
/// </remarks>
/// <docs>fundamentals/events/superseding-events#history-stored-before-the-declaration</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SupersedingHistoryReconcileSqlTests.cs</tests>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperSupersedingKeyTests.cs:HistoryStoredBeforeTheDeclaration_IsReconciledByTheRegisteredStep_AndReapedToTheNewestAsync</tests>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "S2077:Formatting SQL queries is security-sensitive", Justification = "Only the schema prefix is formatted in, built by SqlText.Identifier; every value is a bound parameter.")]
public sealed partial class SupersedingHistoryReconcileStep : IMaintenanceStep {
  /// <summary>Events of the store one batch reads past a type's cursor: one transaction's worth.</summary>
  public const int SCAN_SIZE = 5000;

  /// <summary>Batches one cycle runs at most, across every type.</summary>
  public const int MAX_BATCHES_PER_CYCLE = 20;

  private const string OK = "ok";
  private const string FINISHED = "finished";

  private readonly ILogger _logger;
  private readonly int _scanSize;
  private readonly int _maxBatches;
  private readonly Func<IReadOnlyList<Type>> _types;

  /// <summary>Creates the step over every registered superseding type.</summary>
  /// <param name="logger">Logs each cycle's progress and each type's completion.</param>
  public SupersedingHistoryReconcileStep(ILogger<SupersedingHistoryReconcileStep> logger)
    : this(logger, SCAN_SIZE, MAX_BATCHES_PER_CYCLE, SupersedingEvents.Types) { }

  /// <summary>Creates the step with its own limits and types, for tests.</summary>
  internal SupersedingHistoryReconcileStep(ILogger logger, int scanSize, int maxBatches, Func<IReadOnlyList<Type>> types) {
    ArgumentNullException.ThrowIfNull(logger);
    ArgumentNullException.ThrowIfNull(types);
    _logger = logger;
    _scanSize = scanSize;
    _maxBatches = maxBatches;
    _types = types;
  }

  /// <summary>Builds the step from a host's services, logging to the host's logger when it has one.</summary>
  /// <param name="services">The host's services.</param>
  /// <returns>The step.</returns>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SupersedingHistoryReconcileSqlTests.cs:FromServices_AndItsArguments_AreCheckedAsync</tests>
  public static SupersedingHistoryReconcileStep FromServices(IServiceProvider services) {
    ArgumentNullException.ThrowIfNull(services);
    return new SupersedingHistoryReconcileStep(
      services.GetService<ILogger<SupersedingHistoryReconcileStep>>() ?? NullLogger<SupersedingHistoryReconcileStep>.Instance);
  }

  /// <inheritdoc />
  public string Name => "superseding-history-reconcile";

  /// <inheritdoc />
  public async Task RunAsync(IServiceProvider services, CancellationToken cancellationToken) =>
    await ReconcileAsync(services, cancellationToken).ConfigureAwait(false);

  /// <summary>Runs one cycle and says what it did.</summary>
  /// <param name="services">The maintenance cycle's services; the database is reached through
  /// <see cref="PostgresArchiveConnection"/>, payloads are read with the registered <see cref="JsonSerializerOptions"/>
  /// (or the combined registered contexts), and <see cref="SupersedingOptions"/> can turn the step off.</param>
  /// <param name="cancellationToken">Cancellation; set at shutdown.</param>
  /// <returns>What the cycle did.</returns>
  public async Task<SupersedingHistoryReconcileResult> ReconcileAsync(IServiceProvider services, CancellationToken cancellationToken) {
    ArgumentNullException.ThrowIfNull(services);
    var options = services.GetService<IOptions<SupersedingOptions>>()?.Value ?? new SupersedingOptions();
    if (!options.ReconcileHistory) {
      const string OFF = "skipped (Whizbang:Superseding:ReconcileHistory=false)";
      LogNothing(_logger, OFF);
      return new SupersedingHistoryReconcileResult(0, 0, 0, 0, 0, OFF);
    }
    var database = services.GetRequiredService<PostgresArchiveConnection>();
    var json = services.GetService<JsonSerializerOptions>() ?? JsonContextRegistry.CreateCombinedOptions();
    var prefix = database.Schema is null ? string.Empty : SqlText.Identifier(database.Schema) + ".";
    var total = new SupersedingHistoryReconcileResult(0, 0, 0, 0, 0, OK);
    foreach (var type in _types()) {
      if (total.Batches >= _maxBatches) {
        break;
      }
      var walk = new TypeWalk(TypeNameFormatter.Format(type), TypeNameFormatter.FormatClrTypeName(type));
      total = await _walkAsync(database, prefix, walk, json, total, cancellationToken).ConfigureAwait(false);
    }
    if (total.EventsStamped == 0) {
      LogNothing(_logger, total.Status);
    }
    return total;
  }

  private readonly record struct TypeWalk(string EventType, string DefinitionName);

  private readonly record struct Begun(string Status, Guid? After, Guid? Through, long Scanned, long Total, long Matched);

  private readonly record struct Applied(string Status, long Stamped, long Queued, long Total, long Matched);

  // Walks one type until a batch is not ok or the cycle's budget is spent, and logs what this cycle did for it.
  private async Task<SupersedingHistoryReconcileResult> _walkAsync(
      PostgresArchiveConnection database, string prefix, TypeWalk walk, JsonSerializerOptions json,
      SupersedingHistoryReconcileResult total, CancellationToken cancellationToken) {
    await using (var connection = await database.Open(cancellationToken).ConfigureAwait(false)) {
      await using var ensure = new NpgsqlCommand($"SELECT {prefix}wh_superseding_history_ensure($1, $2)", connection);
      ensure.Parameters.AddWithValue(walk.EventType);
      ensure.Parameters.AddWithValue(walk.DefinitionName);
      _ = await ensure.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
    long stamped = 0, queued = 0, left = 0, of = 0;
    var finished = false;
    while (total.Batches < _maxBatches) {
      var (status, scanned, batchStamped, batchQueued, batchTotal, batchMatched) =
        await _batchAsync(database, prefix, walk, json, cancellationToken).ConfigureAwait(false);
      stamped += batchStamped;
      queued += batchQueued;
      of = batchTotal;
      left = Math.Max(batchTotal - batchMatched, 0);
      finished = status == FINISHED;
      total = new SupersedingHistoryReconcileResult(total.Batches + 1, total.EventsScanned + scanned, total.EventsStamped + batchStamped,
        total.EventsQueued + batchQueued, total.TypesFinished + (finished ? 1 : 0), status);
      if (status != OK) {
        break;
      }
    }
    // Nothing is queued without a stamp in the same batch, so a cycle that stamped nothing has nothing to report.
    if (stamped > 0) {
      LogProgress(_logger, walk.EventType, stamped, queued, left, of);
    }
    if (finished) {
      LogFinished(_logger, walk.EventType);
    }
    return total;
  }

  // One batch, one transaction: take the type's walk and its window, read the window's unflagged events, read their keys,
  // stamp and queue. A crash anywhere rolls all of it back.
  private async Task<(string Status, long Scanned, long Stamped, long Queued, long Total, long Matched)> _batchAsync(
      PostgresArchiveConnection database, string prefix, TypeWalk walk, JsonSerializerOptions json, CancellationToken cancellationToken) {
    await using var connection = await database.Open(cancellationToken).ConfigureAwait(false);
    await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
    var begun = await _beginAsync(connection, transaction, prefix, walk, cancellationToken).ConfigureAwait(false);
    if (begun.Status != OK) {
      await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
      return (begun.Status, 0, 0, 0, begun.Total, begun.Matched);
    }
    var (ids, keys) = await _keysAsync(connection, transaction, prefix, walk, begun, json, cancellationToken).ConfigureAwait(false);
    var applied = await _applyAsync(transaction, prefix, walk, begun, ids, keys, cancellationToken).ConfigureAwait(false);
    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    return (applied.Status, begun.Scanned, applied.Stamped, applied.Queued, applied.Total, applied.Matched);
  }

  private async Task<Begun> _beginAsync(
      NpgsqlConnection connection, NpgsqlTransaction transaction, string prefix, TypeWalk walk, CancellationToken cancellationToken) {
    await using var command = new NpgsqlCommand(
      "SELECT status, after_id, through_id, rows_scanned, events_total, events_matched " +
      $"FROM {prefix}wh_superseding_history_begin($1, $2, $3)", connection, transaction);
    command.Parameters.AddWithValue(walk.EventType);
    command.Parameters.AddWithValue(walk.DefinitionName);
    command.Parameters.AddWithValue(_scanSize);
    await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
    await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
    return new Begun(reader.GetString(0), _guidOrNull(reader, 1), _guidOrNull(reader, 2), reader.GetInt64(3),
      reader.GetInt64(4), reader.GetInt64(5));
  }

  private async Task<(Guid[] Ids, string?[] Keys)> _keysAsync(
      NpgsqlConnection connection, NpgsqlTransaction transaction, string prefix, TypeWalk walk, Begun begun,
      JsonSerializerOptions json, CancellationToken cancellationToken) {
    await using var command = new NpgsqlCommand(
      $"SELECT event_id, event_data::text FROM {prefix}wh_superseding_history_rows($1, $2, $3)", connection, transaction);
    command.Parameters.AddWithValue(walk.EventType);
    command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = (object?)begun.After ?? DBNull.Value });
    command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = begun.Through.GetValueOrDefault() });
    var ids = new List<Guid>();
    var keys = new List<string?>();
    await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
      var eventId = reader.GetGuid(0);
      ids.Add(eventId);
      keys.Add(_keyOf(eventId, walk.EventType, reader.GetString(1), json));
    }
    return ([.. ids], [.. keys]);
  }

  private async Task<Applied> _applyAsync(
      NpgsqlTransaction transaction, string prefix, TypeWalk walk, Begun begun,
      Guid[] ids, string?[] keys, CancellationToken cancellationToken) {
    await using var command = new NpgsqlCommand(
      "SELECT status, events_stamped, events_queued, events_total, events_matched " +
      $"FROM {prefix}wh_superseding_history_apply($1, $2, $3, $4, $5, $6, $7)", transaction.Connection, transaction);
    command.Parameters.AddWithValue(walk.EventType);
    command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = (object?)begun.After ?? DBNull.Value });
    command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = begun.Through.GetValueOrDefault() });
    command.Parameters.AddWithValue(_scanSize);
    command.Parameters.AddWithValue(begun.Scanned);
    command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Uuid, Value = ids });
    command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text, Value = keys });
    await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
    await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
    return new Applied(reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4));
  }

  // The key a write path would have stored for this payload: the generated accessor over the payload read as its type. A
  // payload the accessor cannot read keeps no key, so the event is stamped but never superseded, and is named in the log.
  private string? _keyOf(Guid eventId, string eventType, string payload, JsonSerializerOptions json) {
    try {
      using var document = JsonDocument.Parse(payload);
      return SupersedingEvents.KeyOf(document.RootElement, eventType, json);
    } catch (JsonException ex) {
      LogUnreadable(_logger, ex, eventId, eventType);
      return null;
    } catch (NotSupportedException ex) {
      // The registered options cannot read the type at all.
      LogUnreadable(_logger, ex, eventId, eventType);
      return null;
    }
  }

  private static Guid? _guidOrNull(NpgsqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetGuid(ordinal);

  [LoggerMessage(Level = LogLevel.Information,
    Message = "Superseding history of {EventType}: {Stamped} stamped, {Queued} queued for the reaper this cycle; {Left} of {Total} left to read")]
  static partial void LogProgress(ILogger logger, string eventType, long stamped, long queued, long left, long total);

  [LoggerMessage(Level = LogLevel.Information,
    Message = "Superseding history of {EventType} reconciled: every event stored before the declaration is stamped, and the superseded ones are queued for the reaper")]
  static partial void LogFinished(ILogger logger, string eventType);

  [LoggerMessage(Level = LogLevel.Warning,
    Message = "The superseding key of event {EventId} ({EventType}) could not be read from its payload; it is stamped without a key and kept")]
  static partial void LogUnreadable(ILogger logger, Exception exception, Guid eventId, string eventType);

  [LoggerMessage(Level = LogLevel.Debug, Message = "Superseding history reconcile stamped and queued nothing ({Status})")]
  static partial void LogNothing(ILogger logger, string status);
}
