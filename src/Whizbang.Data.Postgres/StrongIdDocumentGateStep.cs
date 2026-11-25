// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Whizbang.Core.Workers;

namespace Whizbang.Data.Postgres;

/// <summary>
/// The maintenance step that asks the schema's strong-id document gate each cycle (#2518), for a driver that has no
/// stored-form rewrite of its own to ask it: the Dapper driver. Asking opens this process's strong-id write form once
/// every live instance reads the UUID string, and the heartbeat then says this instance writes it, so the rewrite an
/// Entity Framework instance runs in the same schema is not held for this one. It also reports a live instance of an
/// earlier release that joined after the switch, as the gate does on every check.
/// </summary>
/// <remarks>
/// The database is reached through <see cref="PostgresArchiveConnection"/>; a null schema is the connection's search path,
/// which for the Dapper driver is <c>public</c>.
/// </remarks>
/// <docs>fundamentals/identity/whizbang-ids#stored-form-switch</docs>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperStrongIdDocumentGateTests.cs</tests>
public sealed partial class StrongIdDocumentGateStep : IMaintenanceStep {
  private readonly ILogger _logger;

  /// <summary>Creates the step.</summary>
  /// <param name="logger">Logs each answer at debug level; the gate reports an unreadable instance as a Warning.</param>
  public StrongIdDocumentGateStep(ILogger<StrongIdDocumentGateStep> logger) {
    ArgumentNullException.ThrowIfNull(logger);
    _logger = logger;
  }

  /// <summary>Builds the step from a host's services, logging to the host's logger when it has one.</summary>
  /// <param name="services">The host's services.</param>
  /// <returns>The step.</returns>
  public static StrongIdDocumentGateStep FromServices(IServiceProvider services) {
    ArgumentNullException.ThrowIfNull(services);
    return new StrongIdDocumentGateStep(
      services.GetService<ILogger<StrongIdDocumentGateStep>>() ?? NullLogger<StrongIdDocumentGateStep>.Instance);
  }

  /// <inheritdoc />
  public string Name => "strong-id-document-gate";

  /// <inheritdoc />
  public async Task RunAsync(IServiceProvider services, CancellationToken cancellationToken) =>
    _ = await CheckAsync(services, cancellationToken).ConfigureAwait(false);

  /// <summary>Asks the gate and records its answer in this process's write form.</summary>
  /// <param name="services">The maintenance cycle's services.</param>
  /// <param name="cancellationToken">Cancellation; set at shutdown.</param>
  /// <returns>The gate's answer.</returns>
  public async Task<StrongIdDocumentGateAnswer> CheckAsync(IServiceProvider services, CancellationToken cancellationToken) {
    ArgumentNullException.ThrowIfNull(services);
    var database = services.GetRequiredService<PostgresArchiveConnection>();
    await using var connection = await database.Open(cancellationToken).ConfigureAwait(false);
    var answer = await StrongIdDocumentGate.CheckAsync(connection, database.Schema, _logger, cancellationToken).ConfigureAwait(false);
    LogStatus(_logger, database.Schema ?? "public", answer.Status);
    return answer;
  }

  [LoggerMessage(Level = LogLevel.Debug, Message = "Strong-id document gate in {Schema}: {Status}")]
  static partial void LogStatus(ILogger logger, string schema, string status);
}
