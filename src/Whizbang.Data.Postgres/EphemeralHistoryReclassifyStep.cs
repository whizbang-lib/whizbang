// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;
using Whizbang.Core;
using Whizbang.Core.Configuration;
using Whizbang.Core.Workers;

namespace Whizbang.Data.Postgres;

/// <summary>What one ephemeral-history cycle did.</summary>
/// <param name="Batches">The batches it ran, across every type.</param>
/// <param name="EventsScanned">The types' own events read past their cursors.</param>
/// <param name="EventsReclassified">Events stamped ephemeral (and taken out of the stream digests).</param>
/// <param name="EventsBlocked">Events left Sourced because their stream holds a Sourced event of another type.</param>
/// <param name="TypesFinished">Types whose walk completed in this cycle.</param>
/// <param name="Status">The last batch's status: <c>ok</c>, <c>finished</c>, <c>complete</c>, <c>waiting (...)</c> or
/// <c>skipped (...)</c>.</param>
/// <docs>fundamentals/events/ephemeral-events#making-an-existing-type-ephemeral</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EphemeralHistoryReclassifySqlTests.cs:Reclassify_RunsInBoundedBatches_AndResumesFromItsCursor_AfterACrashAsync</tests>
public readonly record struct EphemeralHistoryReclassifyResult(
  int Batches, long EventsScanned, long EventsReclassified, long EventsBlocked, int TypesFinished, string Status);

/// <summary>
/// The maintenance step that reclassifies the events a type stored before it was declared
/// <see cref="Core.Attributes.EphemeralAttribute">[Ephemeral]</see> as ephemeral, so the ephemeral reaper can reclaim them
/// (#2206). It is the opt-in <see cref="EphemeralOptions.ReconcileHistoricalOnStartup"/>: off by default, because
/// reclassifying stored history is a deliberate, potentially lossy step.
/// </summary>
/// <remarks>
/// <para>
/// Each ephemeral event type in the message-type catalog, under its current name and every former name, is walked through
/// the per-type history walker (<see cref="TypeHistoryWalker"/>): the type's own stamped events in commit order, a window of
/// <see cref="SCAN_SIZE"/> per batch and each batch its own short transaction, at most <see cref="MAX_BATCHES_PER_CYCLE"/>
/// batches a cycle across all types, one instance per type, resumable after a crash. Each batch keeps the mixed-stream
/// guard (an event whose stream holds a Sourced event of another type is left Sourced and counted as blocked) and takes the
/// events out of the stream digests through the one digest fold.
/// </para>
/// <para>
/// It used to run inside the startup reconciler as one statement over every event of the type; it now runs in the
/// maintenance cycle, idle-gated and after the maintainer turn, so startup never waits on it. A walk restarts from the
/// first event when the type-definition registry holds a newer definition of the type.
/// </para>
/// </remarks>
/// <docs>fundamentals/events/ephemeral-events#making-an-existing-type-ephemeral</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EphemeralHistoryReclassifySqlTests.cs</tests>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "S2077:Formatting SQL queries is security-sensitive", Justification = "Only the schema prefix is formatted in, built by SqlText.Identifier; every value is a bound parameter.")]
public sealed partial class EphemeralHistoryReclassifyStep : IMaintenanceStep {
  /// <summary>Events of the type one batch reads past its cursor: one transaction's worth.</summary>
  public const int SCAN_SIZE = 5000;

  /// <summary>Batches one cycle runs at most, across every type.</summary>
  public const int MAX_BATCHES_PER_CYCLE = 20;

  // The walker's name for this operation: its rows of wh_type_history_walks.
  private const string OPERATION = "ephemeral";

  private readonly ILogger _logger;
  private readonly int _scanSize;
  private readonly int _maxBatches;

  /// <summary>Creates the step.</summary>
  /// <param name="logger">Logs each cycle's progress and each type's completion.</param>
  public EphemeralHistoryReclassifyStep(ILogger<EphemeralHistoryReclassifyStep> logger)
    : this(logger, SCAN_SIZE, MAX_BATCHES_PER_CYCLE) { }

  /// <summary>Creates the step with its own limits, for tests.</summary>
  internal EphemeralHistoryReclassifyStep(ILogger logger, int scanSize, int maxBatches) {
    ArgumentNullException.ThrowIfNull(logger);
    _logger = logger;
    _scanSize = scanSize;
    _maxBatches = maxBatches;
  }

  /// <summary>Builds the step from a host's services, logging to the host's logger when it has one.</summary>
  /// <param name="services">The host's services.</param>
  /// <returns>The step.</returns>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EphemeralHistoryReclassifySqlTests.cs:FromServices_AndItsArguments_AreCheckedAsync</tests>
  public static EphemeralHistoryReclassifyStep FromServices(IServiceProvider services) {
    ArgumentNullException.ThrowIfNull(services);
    return new EphemeralHistoryReclassifyStep(
      services.GetService<ILogger<EphemeralHistoryReclassifyStep>>() ?? NullLogger<EphemeralHistoryReclassifyStep>.Instance);
  }

  /// <inheritdoc />
  public string Name => "ephemeral-history-reclassify";

  /// <inheritdoc />
  public async Task RunAsync(IServiceProvider services, CancellationToken cancellationToken) =>
    await ReclassifyAsync(services, cancellationToken).ConfigureAwait(false);

  /// <summary>Runs one cycle and says what it did.</summary>
  /// <param name="services">The maintenance cycle's services: the database through <see cref="PostgresArchiveConnection"/>,
  /// the ephemeral types from <see cref="IMessageTypeCatalog"/>, and <see cref="EphemeralOptions"/> for the opt-in.</param>
  /// <param name="cancellationToken">Cancellation; set at shutdown.</param>
  /// <returns>What the cycle did.</returns>
  public async Task<EphemeralHistoryReclassifyResult> ReclassifyAsync(IServiceProvider services, CancellationToken cancellationToken) {
    ArgumentNullException.ThrowIfNull(services);
    var options = services.GetService<IOptions<EphemeralOptions>>()?.Value ?? new EphemeralOptions();
    if (!options.ReconcileHistoricalOnStartup) {
      const string OFF = "skipped (Whizbang:Ephemeral:ReconcileHistoricalOnStartup=false)";
      LogNothing(_logger, OFF);
      return new EphemeralHistoryReclassifyResult(0, 0, 0, 0, 0, OFF);
    }
    var database = services.GetRequiredService<PostgresArchiveConnection>();
    var walk = await new TypeHistoryWalker(OPERATION, _scanSize, _maxBatches)
      .WalkAsync(database, _targets(services.GetService<IMessageTypeCatalog>()), _applyAsync, cancellationToken)
      .ConfigureAwait(false);
    foreach (var type in walk.Types) {
      if (type.Changed > 0 || type.Blocked > 0) {
        LogProgress(_logger, type.Target.EventType, type.Changed, type.Blocked, type.Left, type.Total);
      }
      if (type.Finished) {
        LogFinished(_logger, type.Target.EventType);
      }
    }
    if (walk.Changed == 0 && walk.Blocked == 0) {
      LogNothing(_logger, walk.Status);
    }
    return new EphemeralHistoryReclassifyResult(walk.Batches, walk.Scanned, walk.Changed, walk.Blocked, walk.TypesFinished, walk.Status);
  }

  /// <summary>One walk per name each ephemeral event type is stored under, each judging streams by all of its names.</summary>
  /// <remarks>
  /// Every name is the stored form the append path writes to <c>event_type</c> (<see cref="TypeNameFormatter.Format"/>, and
  /// <see cref="TypeNameFormatter.FormatFormerName"/> for the ledger's former names), because the walk reads the type's
  /// events through the type's index by that name. The catalog's <see cref="MessageTypeCatalogEntry.ClrTypeName"/> is the
  /// bare CLR name: it matches no stored event, and a walk under it read nothing and completed (#2254). The bare name is
  /// kept for the one thing keyed by it, the type-definition registry.
  /// </remarks>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EphemeralHistoryReclassifySqlTests.cs:TheWalk_NamesTheTypeAsTheAppendPathStoredIt_AndItsDefinitionByTheBareNameAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/HistoryWalkTargetNamingTests.cs:EveryHistoryWalkStep_NamesItsTargetsAsTheAppendPathStoresThem_AndReadsThemAsync</tests>
  private static List<EphemeralTarget> _targets(IMessageTypeCatalog? catalog) {
    var targets = new List<EphemeralTarget>();
    if (catalog is not { IsAvailable: true }) {
      return targets;
    }
    foreach (var entry in catalog.GetAll()) {
      if (entry.Ephemeral is null || !string.Equals(entry.Kind, "event", StringComparison.Ordinal)) {
        continue;
      }
      string[] names = [
        TypeNameFormatter.Format(entry.Type),
        .. entry.FormerNames.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => TypeNameFormatter.FormatFormerName(entry.Type, n)),
      ];
      targets.AddRange(names.Select(name => new EphemeralTarget(name, entry.ClrTypeName, names)));
    }
    return targets;
  }

  private static async Task<TypeHistoryBatch> _applyAsync(
      NpgsqlTransaction transaction, string prefix, EphemeralTarget target, TypeHistoryWindow window, CancellationToken cancellationToken) {
    await using var command = new NpgsqlCommand(
      "SELECT status, events_reclassified, events_blocked, events_total, events_matched " +
      $"FROM {prefix}wh_ephemeral_history_apply($1, $2, $3, $4, $5, $6)", transaction.Connection, transaction);
    command.Parameters.AddWithValue(target.EventType);
    command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text, Value = target.Names });
    command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = (object?)window.After ?? DBNull.Value });
    command.Parameters.AddWithValue(window.Through);
    command.Parameters.AddWithValue(window.ScanSize);
    command.Parameters.AddWithValue(window.Scanned);
    await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
    await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
    return new TypeHistoryBatch(reader.GetString(0), reader.GetInt64(1), 0, reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4));
  }

  // A walk of one stored name; Names are every name the type is stored under, which the mixed-stream guard treats as one.
  private sealed record EphemeralTarget(string EventType, string DefinitionName, string[] Names)
    : TypeHistoryTarget(EventType, DefinitionName, null);

  [LoggerMessage(Level = LogLevel.Information,
    Message = "Ephemeral history of {EventType}: {Reclassified} reclassified, {Blocked} left Sourced in mixed streams this cycle; {Left} of {Total} left to read")]
  static partial void LogProgress(ILogger logger, string eventType, long reclassified, long blocked, long left, long total);

  [LoggerMessage(Level = LogLevel.Information,
    Message = "Ephemeral history of {EventType} reclassified: every event stored before the declaration is ephemeral, except in streams that also hold Sourced events of another type")]
  static partial void LogFinished(ILogger logger, string eventType);

  [LoggerMessage(Level = LogLevel.Debug, Message = "Ephemeral history reclassify changed nothing ({Status})")]
  static partial void LogNothing(ILogger logger, string status);
}
