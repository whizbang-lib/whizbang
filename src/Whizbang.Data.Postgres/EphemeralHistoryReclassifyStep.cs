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
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
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
  int Batches, long EventsScanned, long EventsReclassified, long EventsBlocked, int TypesFinished, string Status) {
  /// <summary>Events of released blocking types reclassified with the ephemeral types in their streams
  /// (<see cref="EphemeralOptions.ReleaseBlockers"/>).</summary>
  /// <docs>fundamentals/events/ephemeral-events#releasing-a-blocking-type</docs>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EphemeralBlockerReportSqlTests.cs:Release_StampsTheBlockingEvents_OnlyInStreamsHoldingTheEphemeralType_AndTheDigestsEqualARecountAsync</tests>
  public long EventsReleased { get; init; }

  /// <summary>Events of the other declared-ephemeral types reclassified with the walked type in its streams, so each
  /// stream becomes ephemeral whole (#2278).</summary>
  /// <docs>fundamentals/events/ephemeral-events#a-stream-becomes-ephemeral-whole</docs>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EphemeralStreamByStreamSqlTests.cs:OneWalk_ConvertsEveryDeclaredEphemeralTypeInTheStream_Together_AndTheDigestsEqualARecountAsync</tests>
  public long EventsReclassifiedWithStream { get; init; }
}

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
/// <para>
/// With <see cref="EphemeralBlockerReportOptions.Enabled"/> on (the default) each batch also records which durable types
/// keep the type's events Sourced (#2250); the step logs them per type when its walk finishes, summarizes the stored
/// report once per process, and publishes it as the <see cref="EphemeralBlockerMetrics"/> gauge. Types released for an
/// ephemeral type (<see cref="EphemeralOptions.ReleaseBlockers"/>) are reclassified with it in its own streams. The
/// report switch and the release list are the walk's variant, so changing either walks the type again.
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
    var report = options.BlockerReport.Enabled;
    var catalog = services.GetService<IMessageTypeCatalog>();
    // #2255: the pairs allowed to share a stream, under every stored name: their Sourced types do not block the walk.
    var allowed = MixedStreamAllowance.Stored(MixedStreamAllowance.Allowed(services.GetService<IPerspectiveRunnerRegistry>()), catalog);
    var targets = _targets(catalog, options.ReleaseBlockers, report, allowed);
    long released = 0;
    var walk = await new TypeHistoryWalker(OPERATION, _scanSize, _maxBatches)
      .WalkAsync(database, targets, async (transaction, prefix, target, window, ct) => {
        var (batch, releasedInBatch) = await _applyAsync(transaction, prefix, target, window, ct).ConfigureAwait(false);
        released += releasedInBatch;
        return batch;
      }, cancellationToken)
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
    if (report) {
      await _reportAsync(services, database, targets, walk, cancellationToken).ConfigureAwait(false);
    }
    return new EphemeralHistoryReclassifyResult(walk.Batches, walk.Scanned, walk.Changed, walk.Blocked, walk.TypesFinished, walk.Status) {
      EventsReleased = released,
    };
  }

  // The stored report: a block per type whose walk finished this cycle, the startup summary once per process, the gauge.
  private async Task _reportAsync(
      IServiceProvider services, PostgresArchiveConnection database, List<EphemeralTarget> targets, TypeHistoryWalkResult walk,
      CancellationToken cancellationToken) {
    var prefix = database.Schema is null ? string.Empty : SqlText.Identifier(database.Schema) + ".";
    var rows = await _readReportAsync(database, prefix, cancellationToken).ConfigureAwait(false);
    var registry = services.GetService<IPerspectiveRunnerRegistry>();
    var metrics = services.GetService<EphemeralBlockerMetrics>();
    var releaseIndex = new Dictionary<string, int>(StringComparer.Ordinal);
    foreach (var target in targets) {
      _ = releaseIndex.TryAdd(TypeNameFormatter.GetFullName(target.EventType), target.Release.ConfiguredCount);
    }
    string entries(IEnumerable<EphemeralBlocker> blockers) {
      var text = new System.Text.StringBuilder();
      foreach (var blocker in blockers) {
        EphemeralBlockerReportText.AppendEntry(text, blocker, releaseIndex.GetValueOrDefault(TypeNameFormatter.GetFullName(blocker.EphemeralType)),
          _readModels(registry, blocker.BlockingType), _readModels(registry, blocker.BlockingType, blocker.EphemeralType));
      }
      return text.ToString();
    }

    // The text is built only for a logger that writes it; the gauge is published either way.
    var logging = _logger.IsEnabled(LogLevel.Information);
    var startup = metrics?.ClaimStartupSummary() == true && logging;
    if (startup) {
      _logStartupSummary(rows, entries);
    }
    if (logging) {
      _logFinishedWalks(walk, rows, entries);
    }
    if (startup) {
      await _reportMixedReadModelsAsync(database, prefix, registry, targets, rows, cancellationToken).ConfigureAwait(false);
    }
    metrics?.Publish(rows.Select(r => new EphemeralBlockedCount(r.EphemeralType, r.BlockingType, r.Events)));
  }

  // The startup summary: every stored blocker, grouped by ephemeral type, once per process.
  private void _logStartupSummary(List<EphemeralBlocker> rows, Func<IEnumerable<EphemeralBlocker>, string> entries) {
    if (rows.Count == 0) {
      return;
    }
    var summary = new System.Text.StringBuilder();
    foreach (var type in rows.GroupBy(r => r.EphemeralType, StringComparer.Ordinal)) {
      summary.Append('\n').Append(type.Key).Append(type.First().WalkComplete ? " (walk complete):" : " (walk in progress):").Append(entries(type));
    }
    var types = rows.Select(r => r.EphemeralType).Distinct(StringComparer.Ordinal).Count();
    var text = summary.ToString();
    LogStartupSummary(_logger, rows.Count, types, text);
  }

  // A block per type whose walk finished this cycle. A finished walk is matched to its rows by the type's name without
  // the assembly, and the block names the type in the stored form the rows carry, whichever form the catalog gave the walk.
  private void _logFinishedWalks(TypeHistoryWalkResult walk, List<EphemeralBlocker> rows, Func<IEnumerable<EphemeralBlocker>, string> entries) {
    foreach (var name in walk.Types.Where(t => t.Finished).Select(t => TypeNameFormatter.GetFullName(t.Target.EventType))) {
      var blockers = rows.Where(r => string.Equals(TypeNameFormatter.GetFullName(r.EphemeralType), name, StringComparison.Ordinal)).ToList();
      if (blockers.Count > 0) {
        var text = entries(blockers);
        LogBlockers(_logger, blockers[0].EphemeralType, blockers.Count, text);
      }
    }
  }

  // Once per process: each read model opted into mixing ephemeral and Sourced events, with its durable inputs' stored
  // events, so the operator learns when it can drop the opt-in. An input is ephemeral when it is one of the walked types.
  private async Task _reportMixedReadModelsAsync(
      PostgresArchiveConnection database, string prefix, IPerspectiveRunnerRegistry? registry, List<EphemeralTarget> targets,
      List<EphemeralBlocker> rows, CancellationToken cancellationToken) {
    var ephemeralNames = targets.Select(t => TypeNameFormatter.GetFullName(t.EventType)).ToHashSet(StringComparer.Ordinal);
    foreach (var perspective in registry?.GetRegisteredPerspectives().Where(p => p.AllowsMixedEphemeralAndSourced) ?? []) {
      var inputs = perspective.EventTypes.Select(EventTypeMatchingHelper.NormalizeTypeName).Distinct(StringComparer.Ordinal).ToList();
      string[] ephemeral = [.. inputs.Where(i => ephemeralNames.Contains(TypeNameFormatter.GetFullName(i)))];
      string[] durable = [.. inputs.Except(ephemeral, StringComparer.Ordinal)];
      var counts = await _mixedInputsAsync(database, prefix, durable, ephemeral, cancellationToken).ConfigureAwait(false);
      var text = EphemeralBlockerReportText.MixedReadModel(counts, rows);
      LogMixedReadModel(_logger, perspective.ClrTypeName, text);
    }
  }

  private static async Task<List<EphemeralMixedInput>> _mixedInputsAsync(
      PostgresArchiveConnection database, string prefix, string[] durable, string[] ephemeral, CancellationToken cancellationToken) {
    await using var connection = await database.Open(cancellationToken).ConfigureAwait(false);
    await using var command = new NpgsqlCommand(
      $"SELECT event_type, events_stored, streams_stored, streams_mixed FROM {prefix}wh_ephemeral_mixed_inputs($1, $2)", connection);
    command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text, Value = durable });
    command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text, Value = ephemeral });
    await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
    var inputs = new List<EphemeralMixedInput>();
    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
      inputs.Add(new EphemeralMixedInput(reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3)));
    }
    return inputs;
  }

  // The read models that apply every given type: the blocking type alone names those that lose their rebuild from its
  // events once a release reaps them; with the ephemeral type too, those that must opt in for the pair to mix (#2255).
  private static List<string> _readModels(IPerspectiveRunnerRegistry? registry, params string[] types) {
    var names = types.Select(EventTypeMatchingHelper.NormalizeTypeName).ToList();
    return registry is null
      ? []
      : [.. registry.GetRegisteredPerspectives()
          .Where(p => names.TrueForAll(name => p.EventTypes.Any(e => string.Equals(EventTypeMatchingHelper.NormalizeTypeName(e), name, StringComparison.Ordinal))))
          .Select(p => p.ClrTypeName)
          .Distinct(StringComparer.Ordinal)
          .Order(StringComparer.Ordinal)];
  }

  private static async Task<List<EphemeralBlocker>> _readReportAsync(
      PostgresArchiveConnection database, string prefix, CancellationToken cancellationToken) {
    await using var connection = await database.Open(cancellationToken).ConfigureAwait(false);
    await using var command = new NpgsqlCommand(
      "SELECT ephemeral_type, blocking_type, events_blocked, streams_blocked, bytes_blocked, sample_stream_id, walk_complete, " +
      "route_service, route_topic, route_caller_class, route_caller_member " +
      $"FROM {prefix}wh_ephemeral_blockers_report()", connection);
    await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
    var rows = new List<EphemeralBlocker>();
    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
      rows.Add(new EphemeralBlocker(
        reader.GetString(0), reader.GetString(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4), reader.GetGuid(5),
        reader.GetBoolean(6), _text(reader, 7), _text(reader, 8), _text(reader, 9), _text(reader, 10)));
    }
    return rows;
  }

  private static string? _text(NpgsqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

  /// <summary>One walk per name each ephemeral event type is stored under, each judging streams by all of its names.</summary>
  /// <remarks>
  /// Every name is the stored form the append path writes to <c>event_type</c> (<see cref="TypeNameFormatter.Format"/>, and
  /// <see cref="TypeNameFormatter.FormatFormerName"/> for the ledger's former names), because the walk reads the type's
  /// events through the type's index by that name. The catalog's <see cref="MessageTypeCatalogEntry.ClrTypeName"/> is the
  /// bare CLR name: it matches no stored event, and a walk under it read nothing and completed (#2254). The bare name is
  /// kept for the one thing keyed by it, the type-definition registry.
  /// The report switch, the type's release list and the Sourced types allowed to share its streams (#2255) are the walk's
  /// variant: a change walks the type again, so a release or an allowance reaches history a completed walk passed, and the
  /// report's counts describe one walk. Off with nothing released or allowed is no variant, as before the report.
  /// </remarks>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EphemeralHistoryReclassifySqlTests.cs:TheWalk_NamesTheTypeAsTheAppendPathStoredIt_AndItsDefinitionByTheBareNameAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/HistoryWalkTargetNamingTests.cs:EveryHistoryWalkStep_NamesItsTargetsAsTheAppendPathStoresThem_AndReadsThemAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/MixedStreamAllowanceSqlTests.cs:Reclassify_AnAllowedPair_ReclassifiesTheEphemeralEvents_KeepsTheDurable_AndTheDigestsEqualARecountAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/MixedStreamAllowanceSqlTests.cs:Reclassify_WithNoAllowingReadModel_TheAllowedPairStillBlocksAsync</tests>
  private static List<EphemeralTarget> _targets(
      IMessageTypeCatalog? catalog, IReadOnlyDictionary<string, List<string>> releaseBlockers, bool report,
      IReadOnlyList<MixedStreamPair> allowed) {
    var targets = new List<EphemeralTarget>();
    if (catalog is not { IsAvailable: true }) {
      return targets;
    }
    var all = catalog.GetAll();
    foreach (var entry in all) {
      if (entry.Ephemeral is null || !string.Equals(entry.Kind, "event", StringComparison.Ordinal)) {
        continue;
      }
      string[] names = [
        TypeNameFormatter.Format(entry.Type),
        .. entry.FormerNames.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => TypeNameFormatter.FormatFormerName(entry.Type, n)),
      ];
      var release = EphemeralReleaseList.Resolve(releaseBlockers, entry, all);
      string[] mixing = [.. allowed.Where(p => names.Contains(p.EphemeralType, StringComparer.Ordinal))
        .Select(p => p.DurableType).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
      var variant = _variant(report, release.Names, mixing);
      targets.AddRange(names.Select(name => new EphemeralTarget(name, entry.ClrTypeName, names, release, report, variant) { Allowed = mixing }));
    }
    return targets;
  }

  private static string? _variant(bool report, string[] released, string[] allowed) {
    var parts = new List<string>(3);
    if (report) {
      parts.Add("blocker-report");
    }
    if (released.Length > 0) {
      parts.Add("release=" + string.Join('|', released));
    }
    // #2255: a pair newly allowed walks the type again, so history a completed walk left blocked is reclassified.
    if (allowed.Length > 0) {
      parts.Add("allow=" + string.Join('|', allowed));
    }
    return parts.Count == 0 ? null : string.Join(';', parts);
  }

  private static async Task<(TypeHistoryBatch Batch, long Released)> _applyAsync(
      NpgsqlTransaction transaction, string prefix, EphemeralTarget target, TypeHistoryWindow window, CancellationToken cancellationToken) {
    await using var command = new NpgsqlCommand(
      "SELECT status, events_reclassified, events_blocked, events_total, events_matched, events_released " +
      $"FROM {prefix}wh_ephemeral_history_apply($1, $2, $3, $4, $5, $6, $7, $8, $9)", transaction.Connection, transaction);
    command.Parameters.AddWithValue(target.EventType);
    command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text, Value = target.Names });
    command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = (object?)window.After ?? DBNull.Value });
    command.Parameters.AddWithValue(window.Through);
    command.Parameters.AddWithValue(window.ScanSize);
    command.Parameters.AddWithValue(window.Scanned);
    command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text, Value = target.Release.Names });
    command.Parameters.AddWithValue(target.Report);
    command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text, Value = target.Allowed });
    await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
    await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
    return (new TypeHistoryBatch(reader.GetString(0), reader.GetInt64(1), 0, reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4)),
      reader.GetInt64(5));
  }

  // A walk of one stored name; Names are every name the type is stored under, which the mixed-stream guard treats as one.
  // Release is the type's release list, Report whether its batches record their blockers, Allowed the Sourced types allowed
  // to share its streams (#2255); all three make up the Variant.
  private sealed record EphemeralTarget(
      string EventType, string DefinitionName, string[] Names, EphemeralReleaseList Release, bool Report, string? Variant)
    : TypeHistoryTarget(EventType, DefinitionName, Variant) {
    public string[] Allowed { get; init; } = [];
  }

  [LoggerMessage(Level = LogLevel.Information,
    Message = "Ephemeral history of {EventType}: {Reclassified} reclassified, {Blocked} left Sourced in mixed streams this cycle; {Left} of {Total} left to read")]
  static partial void LogProgress(ILogger logger, string eventType, long reclassified, long blocked, long left, long total);

  [LoggerMessage(Level = LogLevel.Information,
    Message = "Ephemeral history of {EventType} reclassified: every event stored before the declaration is ephemeral, except in streams that also hold Sourced events of another type")]
  static partial void LogFinished(ILogger logger, string eventType);

  [LoggerMessage(Level = LogLevel.Information,
    Message = "Ephemeral history of {EventType} is held back by {BlockerCount} durable event type(s):{Report}")]
  static partial void LogBlockers(ILogger logger, string eventType, int blockerCount, string report);

  [LoggerMessage(Level = LogLevel.Information,
    Message = "Ephemeral blocker report at startup: {PairCount} durable event type(s) hold back the history of {TypeCount} ephemeral type(s):{Report}")]
  static partial void LogStartupSummary(ILogger logger, int pairCount, int typeCount, string report);

  [LoggerMessage(Level = LogLevel.Information,
    Message = "Read model {ReadModel} applies ephemeral and durable events ([DangerouslyAllowMixedEphemeralAndSourcedEvents]): {Report}")]
  static partial void LogMixedReadModel(ILogger logger, string readModel, string report);

  [LoggerMessage(Level = LogLevel.Debug, Message = "Ephemeral history reclassify changed nothing ({Status})")]
  static partial void LogNothing(ILogger logger, string status);
}
