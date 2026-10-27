// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Whizbang.Core.Capabilities;
using Whizbang.Core.Messaging;
using Whizbang.Core.Processes;

namespace Whizbang.Core.Workers;

/// <summary>
/// The <see cref="TableRewriteStrategies.PG_REPACK"/> strategy: rewrites a table online with the pg_repack client,
/// which holds an ACCESS EXCLUSIVE lock only briefly at the start and the end.
/// </summary>
/// <remarks>
/// <para>
/// In <see cref="PgRepackMode.InProcess"/> mode the client runs here, through <see cref="IExternalProcessRunner"/>, once
/// per requested table the maintenance cycle's <see cref="OnlineTableRewriteStep"/> takes within its byte budget. The work coordinator chooses the connection (the schema-initialization one,
/// which owns the table, when configured), checks the table name, and re-measures afterwards
/// (<see cref="IWorkCoordinator.RewriteTableWithAsync"/>). The connection reaches the client only as libpq
/// environment variables (<c>PGHOST</c>, <c>PGUSER</c>, <c>PGPASSWORD</c> and the rest): process arguments are visible
/// to every user on the host, and they carry only the options and the table. The client runs with
/// <c>--no-superuser-check</c> (the table owner may repack its own table, which a managed server requires) and
/// <c>--no-kill-backend</c> (when it cannot take its lock in time it gives up rather than end the application's own
/// sessions).
/// </para>
/// <para>
/// In <see cref="PgRepackMode.External"/> mode it never starts a process: <see cref="CanRun"/> is false, the step
/// reports why, and the queue is left to the operator's job. A client the capability probe found unusable holds the
/// rewriter back the same way; one the probe could not decide is tried, and a failed run is logged and left queued.
/// </para>
/// </remarks>
/// <docs>operations/infrastructure/storage-maintenance#pg-repack</docs>
/// <tests>tests/Whizbang.Core.Tests/Workers/PgRepackTests.cs</tests>
public sealed partial class PgRepackTableRewriter : ITableRewriter {
  private const string EXTERNAL = "pg_repack runs as an external job (" + PgRepackClient.MODE_KEY + " = External): "
    + "Whizbang does not start it, and requested tables stay queued for that job (wh_tables_needing_rewrite() lists them)";

  private readonly IOptions<MaintenanceWorkerOptions> _options;
  private readonly IExternalProcessRunner _runner;
  private readonly ICapabilityRegistry? _capabilities;
  private readonly ILogger<PgRepackTableRewriter> _logger;

  /// <summary>Creates the rewriter.</summary>
  /// <param name="options">The maintenance options: the run mode and the client path.</param>
  /// <param name="runner">Starts the client.</param>
  /// <param name="capabilities">The capability registry holding the client's probed state, or null when the host has none.</param>
  /// <param name="logger">Where a failed run is reported.</param>
  public PgRepackTableRewriter(
      IOptions<MaintenanceWorkerOptions> options,
      IExternalProcessRunner runner,
      ICapabilityRegistry? capabilities,
      ILogger<PgRepackTableRewriter> logger) {
    ArgumentNullException.ThrowIfNull(options);
    ArgumentNullException.ThrowIfNull(runner);
    ArgumentNullException.ThrowIfNull(logger);
    _options = options;
    _runner = runner;
    _capabilities = capabilities;
    _logger = logger;
  }

  /// <inheritdoc />
  public string Strategy => TableRewriteStrategies.PG_REPACK;

  /// <inheritdoc />
  public bool CanRun => _cannotRunReason() is null;

  /// <inheritdoc />
  public string CannotRunReason => _cannotRunReason() ?? "none: the pg_repack client can run on this host";

  /// <inheritdoc />
  public Task<double?> RewriteAsync(IWorkCoordinator coordinator, string tableName, CancellationToken cancellationToken) {
    ArgumentNullException.ThrowIfNull(coordinator);
    if (_options.Value.TableRewrite.PgRepack.Mode == PgRepackMode.External) {
      return Task.FromResult<double?>(null);
    }
    return coordinator.RewriteTableWithAsync(tableName, _runAsync, cancellationToken);
  }

  private string? _cannotRunReason() {
    if (_options.Value.TableRewrite.PgRepack.Mode == PgRepackMode.External) {
      return EXTERNAL;
    }
    var status = _capabilities?.Statuses.FirstOrDefault(s => s.Descriptor.Id == PgRepackClient.Capability);
    return status is null || status.State is CapabilityState.Ready or CapabilityState.Unknown
      ? null
      : $"the pg_repack client is not usable on this host ({status.State}): {status.Reason}; requested tables stay queued";
  }

  private async Task<bool> _runAsync(TableRewriteTarget target, CancellationToken cancellationToken) {
    var request = new ExternalProcessRequest(
      _options.Value.TableRewrite.PgRepack.ClientPath,
      ["--no-superuser-check", "--no-kill-backend", "--table=" + target.QualifiedTable],
      target.ClientEnvironment);
    var result = await _runner.RunAsync(request, cancellationToken).ConfigureAwait(false);
    if (result.Started && result.ExitCode == 0) {
      return true;
    }
    var why = result.Started
      ? $"exited {result.ExitCode}: {result.StandardError.Trim()}"
      : $"did not start: {result.StandardError}";
    LogNotRewritten(_logger, target.QualifiedTable, why);
    return false;
  }

  [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "pg_repack did not rewrite {Table}: {Reason}")]
  private static partial void LogNotRewritten(ILogger logger, string table, string reason);
}
