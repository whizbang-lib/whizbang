// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NpgsqlTypes;
using Whizbang.Core;
using Whizbang.Core.Serialization;
using Whizbang.Core.Workers;

namespace Whizbang.Data.Postgres;

/// <summary>What one stored vector conversion cycle did.</summary>
/// <param name="Mode">What the cycle did: <see cref="StoredVectorConversionStep.MODE_PACK"/>,
/// <see cref="StoredVectorConversionStep.MODE_UNPACK"/>, <see cref="StoredVectorConversionStep.MODE_HELD"/> or
/// <see cref="StoredVectorConversionStep.MODE_CLOSED"/>.</param>
/// <param name="Batches">The batches it ran, of the event store walk and of the archive.</param>
/// <param name="BodiesScanned">Stored bodies read.</param>
/// <param name="BodiesChanged">Bodies packed, or unpacked.</param>
/// <param name="BodiesLeft">Event store bodies the walks have yet to read.</param>
/// <param name="Status">The last batch's status, or, when unpacking, the finish's: <c>unpacked</c> or
/// <c>unpacking (...)</c>.</param>
/// <docs>data/event-store#packing-history</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PackedVectorHistorySqlTests.cs:Pack_TheHistoryStoredBeforeTheGateOpened_InBatches_ResumesAfterOneBatch_AndPacksEachBodyOnceAsync</tests>
public readonly record struct StoredVectorConversionResult(
  string Mode, int Batches, long BodiesScanned, long BodiesChanged, long BodiesLeft, string Status);

/// <summary>
/// The maintenance step that converts the vector bodies already stored (#2627, V4 of the vector storage plan). Once the
/// packing gate is open (<see cref="VectorPackingGateStep"/>), it packs the bodies stored before it opened: for each
/// event type this service declares vector paths for (<see cref="StoredVectorPaths"/>), it walks the type's events
/// through the per-type history walker (<see cref="TypeHistoryWalker"/>, operation <c>pack-vectors</c>) and packs each
/// body at the type's paths. While an operator's unpack request is open (<c>wh_request_vector_unpack</c>, before a
/// planned downgrade), it instead writes every packed vector back as JSON numbers, in the event store (operation
/// <c>unpack-vectors</c>, every type with a recorded packed path) and in the archive, and then completes the request:
/// the record of packed paths and the journal entry go, so the build being returned to starts.
/// </summary>
/// <remarks>
/// <para>
/// Each batch is its own transaction of at most <see cref="SCAN_SIZE"/> events, a cycle runs at most
/// <see cref="MAX_BATCHES_PER_CYCLE"/> batches, a walk resumes after a crash from its last committed cursor, and one
/// instance walks a type at a time. Only a body that changes is written, so each is converted once. A walk's variant
/// is the type's paths, so a build whose paths changed walks the type again.
/// </para>
/// <para>
/// It runs after the gate in the maintenance cycle, so the cycle that opens the gate starts packing the history.
/// </para>
/// </remarks>
/// <docs>data/event-store#packing-history</docs>
/// <docs>data/event-store#unpacking-for-a-downgrade</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PackedVectorHistorySqlTests.cs</tests>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "S2077:Formatting SQL queries is security-sensitive", Justification = "Only the schema prefix is formatted in, built by SqlText.Identifier; every value is a bound parameter.")]
public sealed partial class StoredVectorConversionStep : IMaintenanceStep {
  /// <summary>Events one batch reads: one transaction's worth.</summary>
  public const int SCAN_SIZE = 1000;

  /// <summary>Batches one cycle runs at most.</summary>
  public const int MAX_BATCHES_PER_CYCLE = 20;

  /// <summary>The mode of a cycle that packs the history: the gate is open.</summary>
  public const string MODE_PACK = "pack";

  /// <summary>The mode of a cycle that unpacks: an operator's unpack request is open.</summary>
  public const string MODE_UNPACK = "unpack";

  /// <summary>The mode of a cycle that does nothing because a completed unpack holds the gate closed.</summary>
  public const string MODE_HELD = "held";

  /// <summary>The mode of a cycle that does nothing because the gate is closed.</summary>
  public const string MODE_CLOSED = "closed";

  /// <summary>The status of the cycle that completed an unpack, and of every cycle after it while the hold stays.</summary>
  public const string UNPACKED = "unpacked";

  private const string PACK_OPERATION = "pack-vectors";
  private const string UNPACK_OPERATION = "unpack-vectors";

  private readonly ILogger _logger;
  private readonly int _scanSize;
  private readonly int _maxBatches;

  /// <summary>Creates the step.</summary>
  /// <param name="logger">Logs each cycle's progress.</param>
  public StoredVectorConversionStep(ILogger<StoredVectorConversionStep> logger) : this(logger, SCAN_SIZE, MAX_BATCHES_PER_CYCLE) { }

  /// <summary>Creates the step with its own limits, for tests.</summary>
  internal StoredVectorConversionStep(ILogger logger, int scanSize, int maxBatches) {
    ArgumentNullException.ThrowIfNull(logger);
    _logger = logger;
    _scanSize = scanSize;
    _maxBatches = maxBatches;
  }

  /// <summary>Builds the step from a host's services, logging to the host's logger when it has one.</summary>
  /// <param name="services">The host's services.</param>
  /// <returns>The step.</returns>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PackedVectorHistorySqlTests.cs:Step_IsNamed_BuildsFromServices_AndChecksItsArgumentsAsync</tests>
  public static StoredVectorConversionStep FromServices(IServiceProvider services) {
    ArgumentNullException.ThrowIfNull(services);
    return new StoredVectorConversionStep(
      services.GetService<ILogger<StoredVectorConversionStep>>() ?? NullLogger<StoredVectorConversionStep>.Instance);
  }

  /// <inheritdoc />
  public string Name => "stored-vector-conversion";

  /// <inheritdoc />
  public async Task RunAsync(IServiceProvider services, CancellationToken cancellationToken) =>
    await ConvertAsync(services, cancellationToken).ConfigureAwait(false);

  /// <summary>Runs one cycle and says what it did.</summary>
  /// <param name="services">The maintenance cycle's services: the database through <see cref="PostgresArchiveConnection"/>.</param>
  /// <param name="cancellationToken">Cancellation; set at shutdown.</param>
  /// <returns>What the cycle did.</returns>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PackedVectorHistorySqlTests.cs:Pack_WhileTheGateIsClosed_DoesNothing_AndStartsNoWalkAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PackedVectorHistorySqlTests.cs:Unpack_WritesEveryBodyBack_ByteEqualToItsJsonNumbers_AndClearsTheRecordAndTheLatchAsync</tests>
  public async Task<StoredVectorConversionResult> ConvertAsync(IServiceProvider services, CancellationToken cancellationToken) {
    ArgumentNullException.ThrowIfNull(services);
    var database = services.GetRequiredService<PostgresArchiveConnection>();
    var prefix = database.Schema is null ? string.Empty : SqlText.Identifier(database.Schema) + ".";
    var mode = await _scalarAsync(database, $"SELECT {prefix}wh_vector_conversion_mode()", cancellationToken).ConfigureAwait(false);
    var result = mode switch {
      MODE_PACK => await _packAsync(database, cancellationToken).ConfigureAwait(false),
      MODE_UNPACK => await _unpackAsync(database, prefix, cancellationToken).ConfigureAwait(false),
      _ => new StoredVectorConversionResult(mode, 0, 0, 0, 0, mode == MODE_HELD ? UNPACKED : MODE_CLOSED),
    };
    if (result.BodiesChanged > 0) {
      LogConverted(_logger, result.Mode, result.BodiesChanged, result.BodiesScanned, result.BodiesLeft, result.Status);
    } else {
      LogNothing(_logger, result.Mode, result.Status);
    }
    return result;
  }

  // The history pass: this service's vector types, each walked under its own paths.
  private async Task<StoredVectorConversionResult> _packAsync(PostgresArchiveConnection database, CancellationToken cancellationToken) {
    var targets = StoredVectorPaths.EventTypes()
      .Select(t => new TypeHistoryTarget(
        TypeNameFormatter.Format(t), TypeNameFormatter.FormatClrTypeName(t), PathsJson(StoredVectorPaths.For(t))))
      .OrderBy(t => t.EventType, StringComparer.Ordinal)
      .ToList();
    var walk = await new TypeHistoryWalker(PACK_OPERATION, _scanSize, _maxBatches)
      .WalkAsync(database, targets, _packBatchAsync, cancellationToken).ConfigureAwait(false);
    return new StoredVectorConversionResult(MODE_PACK, walk.Batches, walk.Scanned, walk.Changed, walk.Types.Sum(c => c.Left), walk.Status);
  }

  // The unpack: every type with a recorded packed path, then the archive, then the finish.
  private async Task<StoredVectorConversionResult> _unpackAsync(
      PostgresArchiveConnection database, string prefix, CancellationToken cancellationToken) {
    var targets = new List<TypeHistoryTarget>();
    await using (var connection = await database.Open(cancellationToken).ConfigureAwait(false)) {
      await using var command = new NpgsqlCommand(
        $"SELECT DISTINCT event_type FROM {prefix}wh_packed_vector_paths ORDER BY event_type", connection);
      await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
      while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
        var type = reader.GetString(0);
        targets.Add(new TypeHistoryTarget(type, type, null));
      }
    }
    var walk = await new TypeHistoryWalker(UNPACK_OPERATION, _scanSize, _maxBatches)
      .WalkAsync(database, targets, _unpackBatchAsync, cancellationToken).ConfigureAwait(false);

    var batches = walk.Batches;
    var scanned = walk.Scanned;
    var changed = walk.Changed;
    while (batches < _maxBatches) {
      await using var connection = await database.Open(cancellationToken).ConfigureAwait(false);
      await using var command = new NpgsqlCommand(
        $"SELECT status, rows_scanned, rows_changed FROM {prefix}wh_vector_archive_unpack_batch($1)", connection);
      command.Parameters.AddWithValue(_scanSize);
      await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
      await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
      var status = reader.GetString(0);
      batches++;
      scanned += reader.GetInt64(1);
      changed += reader.GetInt64(2);
      if (status != TypeHistoryWalker.OK) {
        break;
      }
    }

    var finish = await _scalarAsync(database, $"SELECT {prefix}wh_vector_unpack_finish()", cancellationToken).ConfigureAwait(false);
    if (finish == UNPACKED) {
      LogUnpacked(_logger);
    }
    return new StoredVectorConversionResult(MODE_UNPACK, batches, scanned, changed, walk.Types.Sum(c => c.Left), finish);
  }

  /// <summary>One type's vector paths as the SQL reads them: <c>[[segment, ...], ...]</c>.</summary>
  /// <param name="paths">The paths.</param>
  /// <returns>The JSON text.</returns>
  internal static string PathsJson(IReadOnlyList<IReadOnlyList<string>> paths) {
    using var buffer = new MemoryStream();
    using (var writer = new Utf8JsonWriter(buffer)) {
      writer.WriteStartArray();
      foreach (var path in paths) {
        writer.WriteStartArray();
        foreach (var segment in path) {
          writer.WriteStringValue(segment);
        }
        writer.WriteEndArray();
      }
      writer.WriteEndArray();
    }
    return Encoding.UTF8.GetString(buffer.ToArray());
  }

  private static async Task<string> _scalarAsync(PostgresArchiveConnection database, string sql, CancellationToken cancellationToken) {
    await using var connection = await database.Open(cancellationToken).ConfigureAwait(false);
    await using var command = new NpgsqlCommand(sql, connection);
    return (string)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
  }

  private static async Task<TypeHistoryBatch> _packBatchAsync(
      NpgsqlTransaction transaction, string prefix, TypeHistoryTarget target, TypeHistoryWindow window, CancellationToken cancellationToken) {
    await using var command = new NpgsqlCommand(
      "SELECT status, bodies_changed, events_total, events_matched " +
      $"FROM {prefix}wh_vector_history_pack_apply($1, $2, $3, $4, $5, $6::jsonb)", transaction.Connection, transaction);
    _window(command, target.EventType, window);
    // The walk's variant is the type's paths, so a changed path set walks the type again.
    command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = target.Variant! });
    return await _batchAsync(command, cancellationToken).ConfigureAwait(false);
  }

  private static async Task<TypeHistoryBatch> _unpackBatchAsync(
      NpgsqlTransaction transaction, string prefix, TypeHistoryTarget target, TypeHistoryWindow window, CancellationToken cancellationToken) {
    await using var command = new NpgsqlCommand(
      "SELECT status, bodies_changed, events_total, events_matched " +
      $"FROM {prefix}wh_vector_history_unpack_apply($1, $2, $3, $4, $5)", transaction.Connection, transaction);
    _window(command, target.EventType, window);
    return await _batchAsync(command, cancellationToken).ConfigureAwait(false);
  }

  private static void _window(NpgsqlCommand command, string eventType, TypeHistoryWindow window) {
    command.Parameters.AddWithValue(eventType);
    command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = (object?)window.After ?? DBNull.Value });
    command.Parameters.AddWithValue(window.Through);
    command.Parameters.AddWithValue(window.ScanSize);
    command.Parameters.AddWithValue(window.Scanned);
  }

  private static async Task<TypeHistoryBatch> _batchAsync(NpgsqlCommand command, CancellationToken cancellationToken) {
    await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
    await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
    return new TypeHistoryBatch(reader.GetString(0), reader.GetInt64(1), 0, 0, reader.GetInt64(2), reader.GetInt64(3));
  }

  [LoggerMessage(Level = LogLevel.Information,
    Message = "Stored vector conversion ({Mode}): {Changed} body(ies) converted of {Scanned} read; {Left} left to read ({Status})")]
  static partial void LogConverted(ILogger logger, string mode, long changed, long scanned, long left, string status);

  [LoggerMessage(Level = LogLevel.Debug, Message = "Stored vector conversion ({Mode}) converted nothing ({Status})")]
  static partial void LogNothing(ILogger logger, string mode, string status);

  [LoggerMessage(Level = LogLevel.Warning,
    Message = "Every stored vector body is unpacked: the record of packed paths and the packed-vectors journal entry are cleared, so an older build can start. Packing stays held until wh_resume_vector_packing is run")]
  static partial void LogUnpacked(ILogger logger);
}
