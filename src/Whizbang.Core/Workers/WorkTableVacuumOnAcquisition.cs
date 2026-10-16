// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Startup;

namespace Whizbang.Core.Workers;

/// <summary>
/// Vacuums the work tables when this instance acquires the maintainer duty (#1351), instead of on every
/// instance at every start. A first assignment vacuums once per process; a reassignment, where another
/// instance held the duty before, vacuums again unless <see cref="MaintenanceWorkerOptions.MaintainOnReassignment"/>
/// is off. An instance that joins a fleet with a live holder never acquires the duty, so it never vacuums.
/// </summary>
/// <remarks>
/// <para>
/// Every start used to purge and vacuum the work tables from schema initialization, on every instance.
/// A service that scales out on its backlog starts instances at the peak of a bulk load, so each one
/// vacuumed at the worst moment, and on a least-privilege application role Postgres skipped the
/// vacuum with a warning while the call read as success. The purge belongs to the
/// <see cref="MaintenanceWorker"/> cycle, behind its busy check; the vacuum belongs here.
/// </para>
/// <para>
/// The coordinator runs it with the table owner's rights (the schema-initialization connection when
/// configured). A table it cannot vacuum comes back with the reason, which is logged as a warning naming
/// the table and the role. Without role assignment nothing holds the duty, so nothing vacuums at start;
/// autovacuum and the maintenance cycle carry the tables.
/// </para>
/// </remarks>
/// <docs>operations/startup/capabilities-and-duties#vacuum-on-acquiring-the-maintainer-duty</docs>
/// <tests>tests/Whizbang.Core.Tests/Workers/WorkTableVacuumOnAcquisitionTests.cs</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Chaos/MaintainerAcquisitionVacuumTests.cs</tests>
public sealed partial class WorkTableVacuumOnAcquisition : IDutyAcquisitionHandler {
  private readonly IServiceScopeFactory _scopeFactory;
  private readonly MaintenanceWorkerOptions _options;
  private readonly IServiceInstanceProvider _instance;
  private readonly ILogger<WorkTableVacuumOnAcquisition> _logger;
  private bool _firstAssignmentVacuumed;

  /// <summary>Creates the handler over the scope factory the coordinator resolves from.</summary>
  /// <param name="scopeFactory">Resolves the work coordinator.</param>
  /// <param name="options">Maintenance options; <see cref="MaintenanceWorkerOptions.MaintainOnReassignment"/>.</param>
  /// <param name="instance">This instance, to tell its own earlier tenure from another instance's.</param>
  /// <param name="logger">Logger.</param>
  public WorkTableVacuumOnAcquisition(
      IServiceScopeFactory scopeFactory,
      IOptions<MaintenanceWorkerOptions> options,
      IServiceInstanceProvider instance,
      ILogger<WorkTableVacuumOnAcquisition> logger) {
    ArgumentNullException.ThrowIfNull(scopeFactory);
    ArgumentNullException.ThrowIfNull(options);
    ArgumentNullException.ThrowIfNull(instance);
    ArgumentNullException.ThrowIfNull(logger);
    _scopeFactory = scopeFactory;
    _options = options.Value;
    _instance = instance;
    _logger = logger;
  }

  /// <inheritdoc />
  public string Role => StartupDuties.MAINTAINER;

  /// <inheritdoc />
  public async ValueTask OnAcquiredAsync(IDutyGrant grant, CancellationToken cancellationToken) {
    ArgumentNullException.ThrowIfNull(grant);
    var reassignedFrom = grant.PreviousHolderInstanceId is { } previous && previous != _instance.InstanceId
      ? previous
      : (Guid?)null;
    if (reassignedFrom is { } from) {
      if (!_options.MaintainOnReassignment) {
        LogReassignmentNotVacuumed(_logger, from);
        return;
      }
    } else if (_firstAssignmentVacuumed) {
      // This instance's own lapse and recovery, or a duty it released and won back: not a new holder.
      return;
    }
    if (!await grant.VerifyStillHeldAsync(cancellationToken).ConfigureAwait(false)) {
      return;   // lost before it started; the next acquisition runs it
    }

    var scope = _scopeFactory.CreateAsyncScope();
    await using (scope.ConfigureAwait(false)) {
      if (scope.ServiceProvider.GetService<IWorkCoordinator>() is not { } coordinator) {
        return;
      }
      var outcomes = await coordinator.VacuumWorkTablesAsync(cancellationToken).ConfigureAwait(false);
      var vacuumed = 0;
      foreach (var outcome in outcomes) {
        if (outcome.Vacuumed) {
          vacuumed++;
        } else {
          LogTableNotVacuumed(_logger, outcome.TableName, outcome.SkippedReason ?? "no reason given");
        }
      }
      _firstAssignmentVacuumed = true;
      LogVacuumed(_logger, vacuumed, outcomes.Count, reassignedFrom is null ? "a first assignment" : $"a reassignment from instance {reassignedFrom}");
    }
  }

  [LoggerMessage(EventId = 1, Level = LogLevel.Information,
    Message = "Maintainer duty acquired ({Assignment}): vacuumed {Vacuumed} of {Tables} work table(s)")]
  static partial void LogVacuumed(ILogger logger, int vacuumed, int tables, string assignment);

  [LoggerMessage(EventId = 2, Level = LogLevel.Warning,
    Message = "Maintainer duty acquired: {Table} was not vacuumed: {Reason}")]
  static partial void LogTableNotVacuumed(ILogger logger, string table, string reason);

  [LoggerMessage(EventId = 3, Level = LogLevel.Information,
    Message = "Maintainer duty taken over from instance {PreviousInstanceId}: work tables not vacuumed, because MaintenanceWorkerOptions.MaintainOnReassignment is off")]
  static partial void LogReassignmentNotVacuumed(ILogger logger, Guid previousInstanceId);
}
