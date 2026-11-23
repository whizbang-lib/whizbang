// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Whizbang.Core.Observability;
using Whizbang.Core.Workers;

namespace Whizbang.Data.Postgres;

/// <summary>A seal this service holds for an origin it does not audit (#2544).</summary>
/// <param name="OriginServiceId">The origin.</param>
/// <param name="SealedThrough">How far the seal reached before the audit stopped reaching the origin (0: never).</param>
/// <param name="UpdatedAt">When the seal last moved.</param>
/// <param name="Known">True when a checkpoint from the origin was recorded but announced no request address.</param>
/// <docs>resilience/stream-integrity#a-seal-nothing-audits</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/CutOfUnauditedOriginSqlTests.cs:Step_WarnsNamingEachUnauditedOrigin_AndGaugesHowManyThereAreAsync</tests>
public sealed record UnauditedSeal(Guid OriginServiceId, long SealedThrough, DateTimeOffset UpdatedAt, bool Known);

/// <summary>
/// The maintenance step that names every seal this service holds for an origin it does not audit (#2544). A seal
/// advances only through this service's audit of the origin, and the audit asks only the origins whose checkpoints
/// reached this service with a request address. A seal for any other origin has no path to advance: the cut gate no
/// longer waits on it (migration 560), and this step makes the missing checkpoint route visible, with a Warning naming
/// each origin and the <c>whizbang.stream_integrity.unaudited_seals</c> gauge.
/// </summary>
/// <remarks>
/// It reads <c>wh_integrity_unaudited_seals()</c>, one row per seal (bounded by origins), each maintenance cycle.
/// </remarks>
/// <docs>resilience/stream-integrity#a-seal-nothing-audits</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/CutOfUnauditedOriginSqlTests.cs</tests>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "S2077:Formatting SQL queries is security-sensitive", Justification = "Only the schema prefix is formatted in, built by SqlText.Identifier; there are no values.")]
public sealed partial class UnauditedSealReportStep : IMaintenanceStep {
  private readonly ILogger _logger;

  /// <summary>Creates the step.</summary>
  /// <param name="logger">Logs each unaudited origin at Warning.</param>
  public UnauditedSealReportStep(ILogger logger) {
    ArgumentNullException.ThrowIfNull(logger);
    _logger = logger;
  }

  /// <summary>Builds the step from a host's services, logging to the host's logger when it has one.</summary>
  /// <param name="services">The host's services.</param>
  /// <returns>The step.</returns>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/CutOfUnauditedOriginSqlTests.cs:Step_FromServices_LogsToTheHostsLogger_AndRunsWithoutMetricsAsync</tests>
  public static UnauditedSealReportStep FromServices(IServiceProvider services) {
    ArgumentNullException.ThrowIfNull(services);
    return new UnauditedSealReportStep(
      services.GetService<ILogger<UnauditedSealReportStep>>() ?? (ILogger)NullLogger.Instance);
  }

  /// <inheritdoc />
  public string Name => "unaudited-seal-report";

  /// <inheritdoc />
  public async Task RunAsync(IServiceProvider services, CancellationToken cancellationToken) =>
    _ = await ReportAsync(services, cancellationToken).ConfigureAwait(false);

  /// <summary>Reads the seals nothing audits, warns naming each origin, and publishes their count.</summary>
  /// <param name="services">The maintenance cycle's services; the database is reached through
  /// <see cref="PostgresArchiveConnection"/>, the gauge through <see cref="StreamIntegrityMetrics"/> when registered.</param>
  /// <param name="cancellationToken">Cancellation; set at shutdown.</param>
  /// <returns>The seals nothing audits.</returns>
  public async Task<IReadOnlyList<UnauditedSeal>> ReportAsync(IServiceProvider services, CancellationToken cancellationToken) {
    ArgumentNullException.ThrowIfNull(services);
    var database = services.GetRequiredService<PostgresArchiveConnection>();
    var prefix = database.Schema is null ? string.Empty : SqlText.Identifier(database.Schema) + ".";
    var seals = new List<UnauditedSeal>();
    await using (var connection = await database.Open(cancellationToken).ConfigureAwait(false)) {
      await using var command = new NpgsqlCommand(
        $"SELECT origin_service_id, sealed_through, updated_at, known FROM {prefix}wh_integrity_unaudited_seals()", connection);
      await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
      while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
        seals.Add(new UnauditedSeal(reader.GetGuid(0), reader.GetInt64(1), reader.GetFieldValue<DateTimeOffset>(2), reader.GetBoolean(3)));
      }
    }
    foreach (var seal in seals) {
      LogUnaudited(_logger, seal.OriginServiceId, seal.SealedThrough, seal.UpdatedAt,
        seal.Known ? "its checkpoints announce no request address" : "no checkpoint from it has reached this service");
    }
    services.GetService<StreamIntegrityMetrics>()?.UpdateUnauditedSeals(seals.Count);
    return seals;
  }

  [LoggerMessage(EventId = 1, Level = LogLevel.Warning,
    Message = "Integrity seal vs origin {OriginServiceId} cannot advance: this service does not audit that origin ({Why}). " +
              "The seal stays at {SealedThrough} (last moved {UpdatedAt}) and no longer holds archiving or truncation; check " +
              "that the origin publishes its checkpoints to this service (#2544)")]
  static partial void LogUnaudited(ILogger logger, Guid originServiceId, long sealedThrough, DateTimeOffset updatedAt, string why);
}
