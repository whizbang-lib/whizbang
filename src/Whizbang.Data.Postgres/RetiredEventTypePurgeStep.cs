// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;
using Whizbang.Core.Messaging;
using Whizbang.Core.Workers;

namespace Whizbang.Data.Postgres;

/// <summary>What one retired-type purge cycle did (#2329).</summary>
/// <param name="Batches">The purge batches it ran.</param>
/// <param name="EventsRetired">Stored events of the retired types it removed.</param>
/// <param name="StreamsRetired">Streams it removed them from.</param>
/// <param name="StreamsWaiting">Streams its last batch left waiting: a gate or a cursor holds one of their retired events.</param>
/// <param name="UnknownNames">Retired names nothing is stored under, reported this cycle (the first of the process).</param>
/// <param name="StillDeclaredNames">Retired names this service still declares as an event type: skipped, since its
/// readers here still apply them, and warned about every cycle (owner decision, 2026-11-20).</param>
/// <param name="Status">The last batch's status: <c>ok</c>, <c>ok (n streams waiting)</c>, <c>waiting (...)</c> or
/// <c>skipped (...)</c>.</param>
/// <docs>fundamentals/events/retired-event-types#what-the-purge-does</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RetiredEventTypePurgeStepTests.cs</tests>
public readonly record struct RetiredEventTypePurgeResult(
  int Batches, long EventsRetired, int StreamsRetired, int StreamsWaiting, IReadOnlyList<string> UnknownNames,
  IReadOnlyList<string> StillDeclaredNames, string Status);

/// <summary>
/// The maintenance step that purges the stored events of the retired event types (<see cref="RetiredEventTypesOptions"/>,
/// #2329), a batch of <see cref="RetiredEventTypesOptions.BatchStreams"/> streams at a time, at most
/// <see cref="RetiredEventTypesOptions.MaxBatchesPerCycle"/> batches a cycle, each its own transaction
/// (<c>wh_retire_event_types</c>). It runs in the maintenance cycle, idle-gated and under the maintainer turn, so
/// startup never waits on it, and it does nothing while no type is retired.
/// </summary>
/// <remarks>
/// <para>
/// On the first cycle of a process it reports each retired name nothing is stored under, once: the name is misspelled,
/// or the type's history is already purged here.
/// </para>
/// <para>
/// A retired name this service still declares as an event type is skipped and warned about on every cycle, the first
/// one after the host starts included: a running service never purges events its own readers still apply.
/// </para>
/// </remarks>
/// <docs>fundamentals/events/retired-event-types</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RetiredEventTypePurgeStepTests.cs</tests>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "S2077:Formatting SQL queries is security-sensitive", Justification = "Only the schema prefix is formatted in, built by SqlText.Identifier; every value is a bound parameter.")]
public sealed partial class RetiredEventTypePurgeStep : IMaintenanceStep {
  // The names already reported in this process, by schema: the report is a startup signal, not a per-cycle one.
  private static readonly ConcurrentDictionary<(string Schema, string Name), byte> _reported = new();

  private readonly ILogger _logger;

  /// <summary>Creates the step.</summary>
  /// <param name="logger">Logs each cycle's progress and each unknown name.</param>
  public RetiredEventTypePurgeStep(ILogger<RetiredEventTypePurgeStep> logger) : this((ILogger)logger) { }

  /// <summary>Creates the step over any logger, for tests.</summary>
  internal RetiredEventTypePurgeStep(ILogger logger) {
    ArgumentNullException.ThrowIfNull(logger);
    _logger = logger;
  }

  /// <summary>Builds the step from a host's services, logging to the host's logger when it has one.</summary>
  /// <param name="services">The host's services.</param>
  /// <returns>The step.</returns>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RetiredEventTypePurgeStepTests.cs:FromServices_AndItsArguments_AreCheckedAsync</tests>
  public static RetiredEventTypePurgeStep FromServices(IServiceProvider services) {
    ArgumentNullException.ThrowIfNull(services);
    return new RetiredEventTypePurgeStep(
      services.GetService<ILogger<RetiredEventTypePurgeStep>>() ?? NullLogger<RetiredEventTypePurgeStep>.Instance);
  }

  /// <inheritdoc />
  public string Name => "retired-event-type-purge";

  /// <inheritdoc />
  public async Task RunAsync(IServiceProvider services, CancellationToken cancellationToken) =>
    await PurgeAsync(services, cancellationToken).ConfigureAwait(false);

  /// <summary>Runs one cycle and says what it did.</summary>
  /// <param name="services">The maintenance cycle's services: <see cref="RetiredEventTypesOptions"/> and, when a type is
  /// retired, the database through <see cref="PostgresArchiveConnection"/>.</param>
  /// <param name="cancellationToken">Cancellation; set at shutdown.</param>
  /// <returns>What the cycle did.</returns>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RetiredEventTypePurgeStepTests.cs:Purge_RunsBatchesWithinTheCycleBudget_AndTheNextCycleTakesTheRestAsync</tests>
  public async Task<RetiredEventTypePurgeResult> PurgeAsync(IServiceProvider services, CancellationToken cancellationToken) {
    ArgumentNullException.ThrowIfNull(services);
    var options = services.GetService<IOptions<RetiredEventTypesOptions>>()?.Value ?? new RetiredEventTypesOptions();
    var catalog = services.GetService<Whizbang.Core.IMessageTypeCatalog>();
    var stillDeclared = options.StillDeclared(catalog);
    foreach (var name in stillDeclared) {
      LogStillDeclared(_logger, name, RetiredEventTypesOptions.SECTION);
    }
    string[] names = [.. options.EffectiveNames(catalog)];
    if (names.Length == 0) {
      return new RetiredEventTypePurgeResult(0, 0, 0, 0, [], stillDeclared, "skipped (nothing retired)");
    }
    var types = string.Join(", ", names);
    var database = services.GetRequiredService<PostgresArchiveConnection>();
    var prefix = database.Schema is null ? string.Empty : SqlText.Identifier(database.Schema) + ".";
    var unknown = await _reportUnknownNamesAsync(database, prefix, names, cancellationToken).ConfigureAwait(false);

    var batches = 0;
    long events = 0;
    var streams = 0;
    var waiting = 0;
    var status = "ok";
    while (batches < options.MaxBatchesPerCycle) {
      var (batchEvents, batchStreams, batchWaiting, batchStatus) =
        await _batchAsync(database, prefix, names, options.BatchStreams, cancellationToken).ConfigureAwait(false);
      batches++;
      events += batchEvents;
      streams += batchStreams;
      waiting = batchWaiting;
      status = batchStatus;
      if (batchStreams == 0) {
        break;
      }
    }
    if (events > 0 || waiting > 0) {
      LogProgress(_logger, events, streams, waiting, types);
    }
    return new RetiredEventTypePurgeResult(batches, events, streams, waiting, unknown, stillDeclared, status);
  }

  // The names nothing is stored under, reported once per process and schema.
  private async Task<List<string>> _reportUnknownNamesAsync(
      PostgresArchiveConnection database, string prefix, string[] names, CancellationToken cancellationToken) {
    var unreported = names.Where(n => !_reported.ContainsKey((prefix, n))).ToArray();
    if (unreported.Length == 0) {
      return [];
    }
    await using var connection = await database.Open(cancellationToken).ConfigureAwait(false);
    await using var command = new NpgsqlCommand(
      $"SELECT retired_name FROM {prefix}wh_retired_event_type_names($1) WHERE stored_name IS NULL", connection);
    command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text, Value = unreported });
    var unknown = new List<string>();
    await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false)) {
      while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
        unknown.Add(reader.GetString(0));
      }
    }
    foreach (var name in unreported) {
      _reported.TryAdd((prefix, name), 0);
    }
    foreach (var name in unknown) {
      LogUnknownName(_logger, name, RetiredEventTypesOptions.SECTION);
    }
    return unknown;
  }

  private static async Task<(long Events, int Streams, int Waiting, string Status)> _batchAsync(
      PostgresArchiveConnection database, string prefix, string[] names, int batchStreams, CancellationToken cancellationToken) {
    await using var connection = await database.Open(cancellationToken).ConfigureAwait(false);
    await using var command = new NpgsqlCommand(
      $"SELECT events_retired, streams_retired, streams_waiting, status FROM {prefix}wh_retire_event_types($1, $2)", connection);
    command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text, Value = names });
    command.Parameters.AddWithValue(batchStreams);
    await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
    await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
    return (reader.GetInt64(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetString(3));
  }

  [LoggerMessage(EventId = 1, Level = LogLevel.Information,
    Message = "Retired event types: removed {Events} stored events from {Streams} streams this cycle; {Waiting} streams wait "
            + "for a destruction hold, a snapshot, the commit-order stamper or a perspective cursor. Types: {Types}")]
  private static partial void LogProgress(ILogger logger, long events, int streams, int waiting, string types);

  [LoggerMessage(EventId = 2, Level = LogLevel.Warning,
    Message = "Retired event type '{Name}' ({Section}) has no stored events here: the name is misspelled, or the type's "
            + "history is already purged. Names are matched without their assembly")]
  private static partial void LogUnknownName(ILogger logger, string name, string section);

  [LoggerMessage(EventId = 3, Level = LogLevel.Warning,
    Message = "Retired event type '{Name}' ({Section}) is skipped: this service still declares it as an event type, so its "
            + "readers here still apply its events. Delete the type here, or remove the name, to stop this warning")]
  private static partial void LogStillDeclared(ILogger logger, string name, string section);
}
