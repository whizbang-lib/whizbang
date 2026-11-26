// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Workers;

namespace Whizbang.Data.Postgres;

/// <summary>What one unregistered-perspective cleanup cycle did (#2601).</summary>
/// <param name="Batches">The batches it ran.</param>
/// <param name="CursorsRemoved">Cursors it removed.</param>
/// <param name="RowsRemoved">Queued rows it removed with them.</param>
/// <param name="SnapshotsRemoved">Snapshots it removed with them.</param>
/// <param name="Perspectives">The perspectives it removed them for, each once, in ordinal order.</param>
/// <param name="Status"><c>ok</c>, <c>budget</c> (the cycle's batches were spent) or <c>skipped (...)</c>.</param>
/// <docs>fundamentals/perspectives/rewind#perspectives-no-longer-registered</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/UnregisteredPerspectiveCleanupStepTests.cs</tests>
public readonly record struct UnregisteredPerspectiveCleanupResult(
  int Batches, int CursorsRemoved, int RowsRemoved, int SnapshotsRemoved, IReadOnlyList<string> Perspectives, string Status);

/// <summary>
/// The maintenance step that removes what is left of a perspective no service runs any more (#2601): its cursors, its
/// queued rows and its snapshots, a batch of <see cref="UnregisteredPerspectiveCleanupOptions.BatchCursors"/> cursors at
/// a time, at most <see cref="UnregisteredPerspectiveCleanupOptions.MaxBatchesPerCycle"/> batches a cycle
/// (<c>wh_remove_unregistered_perspective_cursors</c>). It runs in the maintenance cycle, after startup, so the service's
/// own registration has already written its association rows.
/// </summary>
/// <remarks>
/// <para>
/// A perspective counts as no longer run only when this service's registry does not list it and no service's association
/// rows name it. Each service writes its perspectives' association rows at its startup and deletes the rest, so a name
/// with none is one no running service in the schema registers. That is not the rolling-deploy hedge for an unknown
/// event type, which a newer instance may read: a newer instance that runs a perspective has written its rows before it
/// processes anything, so its cursors are never taken.
/// </para>
/// <para>
/// A service with no perspective registry runs no perspectives and cannot say which ones it would run: it is skipped.
/// <see cref="UnregisteredPerspectiveCleanupOptions.Enabled"/> set to <c>false</c> keeps every leftover cursor.
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/rewind#perspectives-no-longer-registered</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/UnregisteredPerspectiveCleanupStepTests.cs</tests>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "S2077:Formatting SQL queries is security-sensitive", Justification = "Only the schema prefix is formatted in, built by SqlText.Identifier; every value is a bound parameter.")]
public sealed partial class UnregisteredPerspectiveCleanupStep : IMaintenanceStep {
  private const string STATUS_OK = "ok";
  private const string STATUS_BUDGET = "budget";

  private readonly ILogger _logger;

  /// <summary>Creates the step.</summary>
  /// <param name="logger">Logs what each cycle removed.</param>
  public UnregisteredPerspectiveCleanupStep(ILogger<UnregisteredPerspectiveCleanupStep> logger) : this((ILogger)logger) { }

  /// <summary>Creates the step over any logger, for tests.</summary>
  internal UnregisteredPerspectiveCleanupStep(ILogger logger) {
    ArgumentNullException.ThrowIfNull(logger);
    _logger = logger;
  }

  /// <summary>Builds the step from a host's services, logging to the host's logger when it has one.</summary>
  /// <param name="services">The host's services.</param>
  /// <returns>The step.</returns>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/UnregisteredPerspectiveCleanupStepTests.cs:FromServices_AndItsArguments_AreCheckedAsync</tests>
  public static UnregisteredPerspectiveCleanupStep FromServices(IServiceProvider services) {
    ArgumentNullException.ThrowIfNull(services);
    return new UnregisteredPerspectiveCleanupStep(
      services.GetService<ILogger<UnregisteredPerspectiveCleanupStep>>() ?? NullLogger<UnregisteredPerspectiveCleanupStep>.Instance);
  }

  /// <inheritdoc />
  public string Name => "unregistered-perspective-cleanup";

  /// <inheritdoc />
  public async Task RunAsync(IServiceProvider services, CancellationToken cancellationToken) =>
    await CleanAsync(services, cancellationToken).ConfigureAwait(false);

  /// <summary>Runs one cycle and says what it did.</summary>
  /// <param name="services">The maintenance cycle's services: <see cref="PerspectiveWorkerOptions"/>, the perspective
  /// registry and, when the step runs, the database through <see cref="PostgresArchiveConnection"/>.</param>
  /// <param name="cancellationToken">Cancellation; set at shutdown.</param>
  /// <returns>What the cycle did.</returns>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/UnregisteredPerspectiveCleanupStepTests.cs:Clean_RemovesTheCursorsRowsAndSnapshotsOfAPerspectiveNothingRuns_AndLeavesTheRestAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/UnregisteredPerspectiveCleanupStepTests.cs:Clean_TakesBatchesWithinTheCycleBudget_AndTheNextCycleTakesTheRestAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/UnregisteredPerspectiveCleanupStepTests.cs:Clean_IsSkipped_WhenTurnedOff_OrWithoutAPerspectiveRegistryAsync</tests>
  public async Task<UnregisteredPerspectiveCleanupResult> CleanAsync(IServiceProvider services, CancellationToken cancellationToken) {
    ArgumentNullException.ThrowIfNull(services);
    var options = (services.GetService<IOptions<PerspectiveWorkerOptions>>()?.Value ?? new PerspectiveWorkerOptions())
      .UnregisteredPerspectiveCleanup;
    if (!options.Enabled) {
      return new UnregisteredPerspectiveCleanupResult(0, 0, 0, 0, [], "skipped (turned off)");
    }
    if (services.GetService<IPerspectiveRunnerRegistry>() is not { } registry) {
      return new UnregisteredPerspectiveCleanupResult(0, 0, 0, 0, [], "skipped (no perspective registry)");
    }
    string[] registered = [.. registry.GetRegisteredPerspectives().Select(p => p.ClrTypeName)];
    var database = services.GetRequiredService<PostgresArchiveConnection>();
    var prefix = database.Schema is null ? string.Empty : SqlText.Identifier(database.Schema) + ".";

    var (batches, cursors, rows, snapshots) = (0, 0, 0, 0);
    var perspectives = new SortedSet<string>(StringComparer.Ordinal);
    var status = STATUS_OK;
    while (true) {
      var batch = await _batchAsync(database, prefix, registered, options.BatchCursors, cancellationToken).ConfigureAwait(false);
      batches++;
      foreach (var (perspective, batchCursors, batchRows, batchSnapshots) in batch) {
        perspectives.Add(perspective);
        cursors += batchCursors;
        rows += batchRows;
        snapshots += batchSnapshots;
      }
      if (batch.Count == 0) {
        break;
      }
      if (batches >= options.MaxBatchesPerCycle) {
        status = STATUS_BUDGET;
        break;
      }
    }
    if (cursors > 0) {
      services.GetService<PerspectiveMetrics>()?.UnregisteredCursorsRemoved.Add(cursors);
      LogRemoved(_logger, cursors, rows, snapshots, string.Join(", ", perspectives));
    }
    return new UnregisteredPerspectiveCleanupResult(batches, cursors, rows, snapshots, [.. perspectives], status);
  }

  private static async Task<List<(string Perspective, int Cursors, int Rows, int Snapshots)>> _batchAsync(
      PostgresArchiveConnection database, string prefix, string[] registered, int batchCursors, CancellationToken cancellationToken) {
    await using var connection = await database.Open(cancellationToken).ConfigureAwait(false);
    await using var command = new NpgsqlCommand(
      $"SELECT perspective_name, cursors_removed, rows_removed, snapshots_removed FROM {prefix}wh_remove_unregistered_perspective_cursors($1, $2)",
      connection);
    command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text, Value = registered });
    command.Parameters.AddWithValue(batchCursors);
    var removed = new List<(string, int, int, int)>();
    await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
      removed.Add((reader.GetString(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3)));
    }
    return removed;
  }

  [LoggerMessage(EventId = 1, Level = LogLevel.Warning,
    Message = "Removed {Cursors} cursors, {Rows} queued rows and {Snapshots} snapshots of perspectives this service does not "
            + "register and no service's association rows name: {Perspectives}. Set "
            + "Whizbang:Workers:Perspective:UnregisteredPerspectiveCleanup:Enabled to false to keep them")]
  private static partial void LogRemoved(ILogger logger, int cursors, int rows, int snapshots, string perspectives);
}
