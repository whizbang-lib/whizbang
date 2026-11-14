// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;
using Whizbang.Core;
using Whizbang.Core.SystemEvents;
using Whizbang.Core.Workers;

namespace Whizbang.Data.Postgres;

/// <summary>What one audit body conversion cycle did.</summary>
/// <param name="Batches">The batches it ran.</param>
/// <param name="RecordsScanned">Audit records read past the walk's cursor.</param>
/// <param name="RecordsConverted">Records whose copy of the audited body was removed, leaving the pointer form.</param>
/// <param name="RecordsKept">Records left with their body because their type keeps it.</param>
/// <param name="RecordsLeft">Records the walk has yet to read.</param>
/// <param name="Status">The last batch's status: <c>ok</c>, <c>finished</c>, <c>complete</c>, <c>waiting (...)</c> or
/// <c>skipped (...)</c>.</param>
/// <docs>fundamentals/events/system-events#audit-body</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/AuditBodyConversionSqlTests.cs:Step_ConvertsInBoundedBatches_Resumes_AndReportsWhatIsLeftAsync</tests>
public readonly record struct AuditBodyConversionResult(
  int Batches, long RecordsScanned, long RecordsConverted, long RecordsKept, long RecordsLeft, string Status);

/// <summary>
/// The maintenance step that converts the audit records written before the pointer default (#1588) to the pointer form
/// (#2326): each <see cref="EventAudited"/> record that still carries <see cref="EventAudited.OriginalBody"/>, a full copy
/// of the audited event, loses it unless its type keeps bodies (<c>[AuditEvent(IncludeBody = true)]</c>). The record's
/// <see cref="EventAudited.OriginalEventId"/> still finds the audited event, so readers see the same audit entry.
/// </summary>
/// <remarks>
/// <para>
/// It walks the audit records through the per-type history walker (<see cref="TypeHistoryWalker"/>, operation
/// <c>audit-body</c>): the records in commit order, <see cref="SCAN_SIZE"/> per batch and each batch its own transaction,
/// at most <see cref="MAX_BATCHES_PER_CYCLE"/> batches a cycle, resumable after a crash, one instance at a time. The set of
/// types that keep their bodies is the walk's variant, so declaring one more type keeps-body walks the records again.
/// </para>
/// <para>
/// Nothing is converted while <see cref="SystemEventOptions.AuditBodyDefault"/> is <see cref="AuditBodyDefault.Include"/>:
/// every record keeps its body then. The records the walk has left are reported as
/// <c>whizbang.maintenance.pending_rows{task=convert_audit_record_bodies}</c>.
/// </para>
/// </remarks>
/// <docs>fundamentals/events/system-events#audit-body</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/AuditBodyConversionSqlTests.cs</tests>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "S2077:Formatting SQL queries is security-sensitive", Justification = "Only the schema prefix is formatted in, built by SqlText.Identifier; every value is a bound parameter.")]
public sealed partial class AuditBodyConversionStep : IMaintenanceStep {
  /// <summary>Audit records one batch reads: one transaction's worth.</summary>
  public const int SCAN_SIZE = 5000;

  /// <summary>Batches one cycle runs at most.</summary>
  public const int MAX_BATCHES_PER_CYCLE = 20;

  /// <summary>The status of a cycle that converts nothing because every record keeps its body.</summary>
  public const string SKIPPED_INCLUDE = "skipped (AuditBodyDefault=Include)";

  // The walker's name for this operation: its rows of wh_type_history_walks.
  private const string OPERATION = "audit-body";

  private readonly ILogger _logger;
  private readonly int _scanSize;
  private readonly int _maxBatches;

  /// <summary>Creates the step.</summary>
  /// <param name="logger">Logs each cycle's progress.</param>
  public AuditBodyConversionStep(ILogger<AuditBodyConversionStep> logger) : this(logger, SCAN_SIZE, MAX_BATCHES_PER_CYCLE) { }

  /// <summary>Creates the step with its own limits, for tests.</summary>
  internal AuditBodyConversionStep(ILogger logger, int scanSize, int maxBatches) {
    ArgumentNullException.ThrowIfNull(logger);
    _logger = logger;
    _scanSize = scanSize;
    _maxBatches = maxBatches;
  }

  /// <summary>Builds the step from a host's services, logging to the host's logger when it has one.</summary>
  /// <param name="services">The host's services.</param>
  /// <returns>The step.</returns>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/AuditBodyConversionSqlTests.cs:FromServices_AndItsArguments_AreCheckedAsync</tests>
  public static AuditBodyConversionStep FromServices(IServiceProvider services) {
    ArgumentNullException.ThrowIfNull(services);
    return new AuditBodyConversionStep(
      services.GetService<ILogger<AuditBodyConversionStep>>() ?? NullLogger<AuditBodyConversionStep>.Instance);
  }

  /// <inheritdoc />
  public string Name => "audit-body-conversion";

  /// <inheritdoc />
  public async Task RunAsync(IServiceProvider services, CancellationToken cancellationToken) =>
    await ConvertAsync(services, cancellationToken).ConfigureAwait(false);

  /// <summary>Runs one cycle and says what it did.</summary>
  /// <param name="services">The maintenance cycle's services: the database through <see cref="PostgresArchiveConnection"/>,
  /// the types from <see cref="IMessageTypeCatalog"/>, and <see cref="SystemEventOptions"/> for the body default.</param>
  /// <param name="cancellationToken">Cancellation; set at shutdown.</param>
  /// <returns>What the cycle did.</returns>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/AuditBodyConversionSqlTests.cs:Step_ConvertsAHistoricalRecordToThePointerForm_AndKeepsIncludeBodyTypesAsync</tests>
  public async Task<AuditBodyConversionResult> ConvertAsync(IServiceProvider services, CancellationToken cancellationToken) {
    ArgumentNullException.ThrowIfNull(services);
    var options = services.GetService<IOptions<SystemEventOptions>>()?.Value ?? new SystemEventOptions();
    if (options.AuditBodyDefault == AuditBodyDefault.Include) {
      LogNothing(_logger, SKIPPED_INCLUDE);
      return new AuditBodyConversionResult(0, 0, 0, 0, 0, SKIPPED_INCLUDE);
    }
    var database = services.GetRequiredService<PostgresArchiveConnection>();
    var kept = KeptBodyNames(services.GetService<IMessageTypeCatalog>());
    var target = new AuditTarget(
      TypeNameFormatter.Format(typeof(EventAudited)), TypeNameFormatter.FormatClrTypeName(typeof(EventAudited)), kept,
      kept.Length == 0 ? null : string.Join(';', kept));
    var walk = await new TypeHistoryWalker(OPERATION, _scanSize, _maxBatches)
      .WalkAsync(database, [target], _applyAsync, cancellationToken)
      .ConfigureAwait(false);
    // One target, so at most one cycle: what it has left is the walk's.
    long left = 0;
    long total = 0;
    foreach (var cycle in walk.Types) {
      left = cycle.Left;
      total = cycle.Total;
    }
    if (walk.Changed > 0) {
      LogConverted(_logger, walk.Changed, walk.Blocked, left, total, walk.Status);
    } else {
      LogNothing(_logger, walk.Status);
    }
    return new AuditBodyConversionResult(walk.Batches, walk.Scanned, walk.Changed, walk.Blocked, left, walk.Status);
  }

  /// <summary>
  /// The names audit records give the types that keep their bodies: each catalog type declared
  /// <c>[AuditEvent(IncludeBody = true)]</c>, by its simple name (the live writer's form) and its full name (the ledger
  /// rebuild's form), sorted so the walk's variant is stable.
  /// </summary>
  /// <param name="catalog">The message-type catalog, or null when the host has none.</param>
  /// <returns>The names, sorted and distinct.</returns>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/AuditBodyConversionSqlTests.cs:KeptBodyNames_AreBothFormsOfEachIncludeBodyType_SortedAsync</tests>
  public static string[] KeptBodyNames(IMessageTypeCatalog? catalog) {
    if (catalog is null) {
      return [];
    }
    return [.. catalog.GetAll()
      .Where(e => AuditEventRegistry.Find(e.Type)?.IncludeBody == true)
      .SelectMany(e => new[] { TypeNameFormatter.SimpleName(e.Type), TypeNameFormatter.GetFullName(TypeNameFormatter.Format(e.Type)) })
      .Distinct(StringComparer.Ordinal)
      .Order(StringComparer.Ordinal)];
  }

  private static async Task<TypeHistoryBatch> _applyAsync(
      NpgsqlTransaction transaction, string prefix, AuditTarget target, TypeHistoryWindow window, CancellationToken cancellationToken) {
    await using var command = new NpgsqlCommand(
      "SELECT status, records_converted, records_kept, events_total, events_matched " +
      $"FROM {prefix}wh_audit_body_apply($1, $2, $3, $4, $5, $6)", transaction.Connection, transaction);
    command.Parameters.AddWithValue(target.EventType);
    command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = (object?)window.After ?? DBNull.Value });
    command.Parameters.AddWithValue(window.Through);
    command.Parameters.AddWithValue(window.ScanSize);
    command.Parameters.AddWithValue(window.Scanned);
    command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text, Value = target.Kept });
    await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
    await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
    return new TypeHistoryBatch(reader.GetString(0), reader.GetInt64(1), 0, reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4));
  }

  // The walk of the audit record type; Kept are the names of the types that keep their bodies, which make up the Variant.
  private sealed record AuditTarget(string EventType, string DefinitionName, string[] Kept, string? Variant)
    : TypeHistoryTarget(EventType, DefinitionName, Variant);

  [LoggerMessage(Level = LogLevel.Information,
    Message = "Audit records converted to the pointer form: {Converted} this cycle, {Kept} kept their body; {Left} of {Total} left to read ({Status})")]
  static partial void LogConverted(ILogger logger, long converted, long kept, long left, long total, string status);

  [LoggerMessage(Level = LogLevel.Debug, Message = "Audit body conversion converted nothing ({Status})")]
  static partial void LogNothing(ILogger logger, string status);
}
