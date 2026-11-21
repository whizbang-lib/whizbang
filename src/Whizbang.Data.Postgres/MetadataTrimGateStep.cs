// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Whizbang.Core.Workers;

namespace Whizbang.Data.Postgres;

/// <summary>
/// The maintenance step that opens the stored-metadata trim gate (#2323): each cycle it calls
/// <c>wh_open_metadata_trim_gate</c>, which latches <c>wh_settings</c> <c>metadata_trim_row_values</c> once at least one
/// instance is live and every live instance's heartbeat says it reads trimmed rows
/// (<see cref="Whizbang.Core.Messaging.HeartbeatRequest.STORED_FORM_LEVEL"/>). Until then the write paths store the slim
/// form an earlier release reads; from then on they leave out what a row holds. The latch is one-way.
/// </summary>
/// <remarks>
/// The fleet is read here, once a cycle, and never on a write path: the write paths read the one flag. Registered ahead
/// of the history conversion step, so the cycle that opens the gate also starts trimming history.
/// </remarks>
/// <docs>data/event-store#trim-gate</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StoredMetadataTrimGateSqlTests.cs:Step_IsNamed_AndRunsTheGateAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StoredMetadataTrimGateSqlTests.cs:Gate_AFreshService_OpensOnlyOnceThisInstanceHasRegisteredWithItsMarkerAsync</tests>
public sealed partial class MetadataTrimGateStep : IMaintenanceStep {
  /// <summary>The status the gate returns from the call that latched it.</summary>
  public const string OPENED = "opened";

  private readonly ILogger _logger;

  /// <summary>Creates the step.</summary>
  /// <param name="logger">Logs the cycle that opens the gate, and a closed gate at debug level.</param>
  public MetadataTrimGateStep(ILogger<MetadataTrimGateStep> logger) {
    ArgumentNullException.ThrowIfNull(logger);
    _logger = logger;
  }

  /// <summary>Builds the step from a host's services, logging to the host's logger when it has one.</summary>
  /// <param name="services">The host's services.</param>
  /// <returns>The step.</returns>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StoredMetadataTrimGateSqlTests.cs:Step_IsNamed_AndRunsTheGateAsync</tests>
  public static MetadataTrimGateStep FromServices(IServiceProvider services) {
    ArgumentNullException.ThrowIfNull(services);
    return new MetadataTrimGateStep(
      services.GetService<ILogger<MetadataTrimGateStep>>() ?? NullLogger<MetadataTrimGateStep>.Instance);
  }

  /// <inheritdoc />
  public string Name => "metadata-trim-gate";

  /// <inheritdoc />
  public async Task RunAsync(IServiceProvider services, CancellationToken cancellationToken) =>
    _ = await OpenAsync(services, cancellationToken).ConfigureAwait(false);

  /// <summary>Asks the gate to open and says what it answered.</summary>
  /// <param name="services">The maintenance cycle's services; the database is reached through
  /// <see cref="PostgresArchiveConnection"/>.</param>
  /// <param name="cancellationToken">Cancellation; set at shutdown.</param>
  /// <returns><c>open</c>, <c>opened</c>, or <c>closed (n of m live instances read trimmed rows)</c>.</returns>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StoredMetadataTrimGateSqlTests.cs:Gate_StaysClosedWhileALiveInstanceLacksTheMarker_AndOpensOnceItIsGoneOrStaleAsync</tests>
  public async Task<string> OpenAsync(IServiceProvider services, CancellationToken cancellationToken) {
    ArgumentNullException.ThrowIfNull(services);
    var database = services.GetRequiredService<PostgresArchiveConnection>();
    var prefix = database.Schema is null ? string.Empty : SqlText.Identifier(database.Schema) + ".";
    await using var connection = await database.Open(cancellationToken).ConfigureAwait(false);
    await using var command = new NpgsqlCommand($"SELECT {prefix}wh_open_metadata_trim_gate()", connection);
    var status = (string)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    if (status == OPENED) {
      LogOpened(_logger);
    } else {
      LogStatus(_logger, status);
    }
    return status;
  }

  [LoggerMessage(Level = LogLevel.Information,
    Message = "Every live instance reads trimmed stored metadata: the write paths now leave out the message id and hop scope a row holds, and history conversion starts over to trim earlier rows. This is one-way: a release before this one can no longer read new rows")]
  static partial void LogOpened(ILogger logger);

  [LoggerMessage(Level = LogLevel.Debug, Message = "Stored metadata trim gate: {Status}")]
  static partial void LogStatus(ILogger logger, string status);
}
