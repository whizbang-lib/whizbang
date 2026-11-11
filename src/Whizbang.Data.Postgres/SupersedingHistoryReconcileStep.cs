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
/// <param name="EventsScanned">The types' own events read past their cursors (#2205: never an event of another type).</param>
/// <param name="EventsStamped">Events stamped with the superseding flag and their key.</param>
/// <param name="EventsQueued">Events queued for the superseded-event reaper.</param>
/// <param name="TypesFinished">Types whose walk completed in this cycle: read past the type's newest event, with none of
/// its events still unstamped.</param>
/// <param name="Status">The last batch's status: <c>ok</c>, <c>finished</c>, <c>complete</c>, <c>held (...)</c>,
/// <c>waiting (...)</c> or <c>skipped (...)</c>.</param>
/// <docs>fundamentals/events/superseding-events#history-stored-before-the-declaration</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SupersedingHistoryReconcileSqlTests.cs:Reconcile_RunsInBoundedBatches_AndResumesFromItsCursor_AfterACrashAsync</tests>
public readonly record struct SupersedingHistoryReconcileResult(
  int Batches, long EventsScanned, long EventsStamped, long EventsQueued, int TypesFinished, string Status);

/// <summary>
/// The maintenance step that brings the events a superseding type stored before it was declared
/// <see cref="Core.Attributes.SupersedingAttribute">[Superseding]</see> under the superseding rule (#2110). For each
/// registered superseding type it walks the type's own stored events through the per-type history walker
/// (<see cref="TypeHistoryWalker"/>, #2205): in commit order through the type's index, a window of
/// <see cref="SCAN_SIZE"/> of them per batch and each batch its own transaction. It reads each unflagged event's key from
/// its payload with the generated accessor the write paths use, and has the SQL stamp the flag and the key and queue every
/// event a newer one of its stream, type and key supersedes. The superseded-event reaper then deletes a queued event only past every one of its gates.
/// A type's superseding group (#2124) is stamped with its key; when the group a type declares changes, its walk starts
/// again from the first event and re-stamps every stored event of the type whose group differs, so a type that joins a
/// group later has its history superseded within the group. A walk completes only once it has read past the type's newest
/// commit sequence and none of the type's events is still waiting for the commit-order stamper.
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
/// A cycle runs at most <see cref="MAX_BATCHES_PER_CYCLE"/> batches across all types, so a type with many events is walked
/// over many idle cycles; a type with N events costs ceil(N / <see cref="SCAN_SIZE"/>) batches whatever the size of the
/// store, and a complete type spends none of the budget. A walk restarts from the first event when the type-definition registry holds a newer definition of the
/// type than the walk started under. <see cref="SupersedingOptions.ReconcileHistory"/> set to <see langword="false"/>
/// turns the step off.
/// </para>
/// </remarks>
/// <docs>fundamentals/events/superseding-events#history-stored-before-the-declaration</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SupersedingHistoryReconcileSqlTests.cs</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/TypeHistoryWalkSqlTests.cs</tests>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperSupersedingKeyTests.cs:HistoryStoredBeforeTheDeclaration_IsReconciledByTheRegisteredStep_AndReapedToTheNewestAsync</tests>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperSupersedingKeyTests.cs:HistoryMissingAValueTypeKey_IsReconciledUnkeyed_AndNoneIsReapedAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SupersedingGroupSqlTests.cs:Reconcile_TypesThatJoinAGroupLater_AreReStamped_AndTheOlderEventOfTheGroupIsReapedAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SupersedingHistoryReconcileSqlTests.cs:Reconcile_AValueTypeKeyMissingFromThePayload_IsStampedUnkeyed_KeptAndLogged_APresentDefaultIsAKeyAsync</tests>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "S2077:Formatting SQL queries is security-sensitive", Justification = "Only the schema prefix is formatted in, built by SqlText.Identifier; every value is a bound parameter.")]
public sealed partial class SupersedingHistoryReconcileStep : IMaintenanceStep {
  /// <summary>Events of the type one batch reads past its cursor: one transaction's worth.</summary>
  public const int SCAN_SIZE = 5000;

  /// <summary>Batches one cycle runs at most, across every type.</summary>
  public const int MAX_BATCHES_PER_CYCLE = 20;

  // The walker's name for this operation: its rows of wh_type_history_walks.
  private const string OPERATION = "superseding";

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
    var targets = _types()
      .Select(t => new TypeHistoryTarget(TypeNameFormatter.Format(t), TypeNameFormatter.FormatClrTypeName(t), SupersedingEvents.GroupOf(t)))
      .ToList();
    var walk = await new TypeHistoryWalker(OPERATION, _scanSize, _maxBatches)
      .WalkAsync(database, targets, (transaction, prefix, target, window, ct) => _applyAsync(transaction, prefix, target, window, json, ct),
        cancellationToken)
      .ConfigureAwait(false);
    foreach (var type in walk.Types) {
      // Nothing is queued without a stamp in the same batch, so a cycle that stamped nothing has nothing to report.
      if (type.Changed > 0) {
        LogProgress(_logger, type.Target.EventType, type.Changed, type.Queued, type.Left, type.Total);
      }
      if (type.Finished) {
        LogFinished(_logger, type.Target.EventType);
      }
    }
    if (walk.Changed == 0) {
      LogNothing(_logger, walk.Status);
    }
    return new SupersedingHistoryReconcileResult(walk.Batches, walk.Scanned, walk.Changed, walk.Queued, walk.TypesFinished, walk.Status);
  }

  // One window: read its unflagged events and their keys, then stamp and queue, in the walker's transaction.
  private async Task<TypeHistoryBatch> _applyAsync(
      NpgsqlTransaction transaction, string prefix, TypeHistoryTarget target, TypeHistoryWindow window,
      JsonSerializerOptions json, CancellationToken cancellationToken) {
    var (ids, keys) = await _keysAsync(transaction, prefix, target.EventType, window, json, cancellationToken).ConfigureAwait(false);
    await using var command = new NpgsqlCommand(
      "SELECT status, events_stamped, events_queued, events_total, events_matched " +
      $"FROM {prefix}wh_superseding_history_apply($1, $2, $3, $4, $5, $6, $7)", transaction.Connection, transaction);
    command.Parameters.AddWithValue(target.EventType);
    command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = (object?)window.After ?? DBNull.Value });
    command.Parameters.AddWithValue(window.Through);
    command.Parameters.AddWithValue(window.ScanSize);
    command.Parameters.AddWithValue(window.Scanned);
    command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Uuid, Value = ids });
    command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text, Value = keys });
    await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
    await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
    return new TypeHistoryBatch(reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2), 0, reader.GetInt64(3), reader.GetInt64(4));
  }

  private async Task<(Guid[] Ids, string?[] Keys)> _keysAsync(
      NpgsqlTransaction transaction, string prefix, string eventType, TypeHistoryWindow window,
      JsonSerializerOptions json, CancellationToken cancellationToken) {
    await using var command = new NpgsqlCommand(
      $"SELECT event_id, event_data::text FROM {prefix}wh_superseding_history_rows($1, $2, $3)", transaction.Connection, transaction);
    command.Parameters.AddWithValue(eventType);
    command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = (object?)window.After ?? DBNull.Value });
    command.Parameters.AddWithValue(window.Through);
    var ids = new List<Guid>();
    var keys = new List<string?>();
    await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
      var eventId = reader.GetGuid(0);
      ids.Add(eventId);
      keys.Add(_keyOf(eventId, eventType, reader.GetString(1), json));
    }
    return ([.. ids], [.. keys]);
  }

  // The key a write path would have stored for this payload: the generated accessor over the payload read as its type. A
  // payload the accessor cannot read keeps no key, so the event is stamped but never superseded, and is named in the log.
  // So does a payload that lacks its key property or holds it as null (#2123): stored before the key existed, its key is
  // unknown, never the key type's default.
  private string? _keyOf(Guid eventId, string eventType, string payload, JsonSerializerOptions json) {
    try {
      using var document = JsonDocument.Parse(payload);
      if (SupersedingEvents.IsKeyMissing(document.RootElement, eventType, json)) {
        LogMissingKey(_logger, eventId, eventType);
        return null;
      }
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

  [LoggerMessage(Level = LogLevel.Information,
    Message = "Superseding history of {EventType}: {Stamped} stamped, {Queued} queued for the reaper this cycle; {Left} of {Total} left to read")]
  static partial void LogProgress(ILogger logger, string eventType, long stamped, long queued, long left, long total);

  [LoggerMessage(Level = LogLevel.Information,
    Message = "Superseding history of {EventType} reconciled: every event stored before the declaration is stamped, and the superseded ones are queued for the reaper")]
  static partial void LogFinished(ILogger logger, string eventType);

  [LoggerMessage(Level = LogLevel.Warning,
    Message = "The superseding key of event {EventId} ({EventType}) could not be read from its payload; it is stamped without a key and kept")]
  static partial void LogUnreadable(ILogger logger, Exception exception, Guid eventId, string eventType);

  [LoggerMessage(Level = LogLevel.Information,
    Message = "Event {EventId} ({EventType}) has no superseding key in its payload (the key property is missing or null); it is stamped without a key and kept")]
  static partial void LogMissingKey(ILogger logger, Guid eventId, string eventType);

  [LoggerMessage(Level = LogLevel.Debug, Message = "Superseding history reconcile stamped and queued nothing ({Status})")]
  static partial void LogNothing(ILogger logger, string status);
}
