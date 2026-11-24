// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Whizbang.Core.Observability;
using Whizbang.Core.Startup;

namespace Whizbang.Data.Postgres;

/// <summary>What the migrator election needs from the driver that runs it.</summary>
/// <param name="ConnectionFactory">Opens a connection of its own to the database being migrated (never the pass's).</param>
/// <param name="Schema">The schema being migrated, unquoted.</param>
/// <param name="LockId">The schema initialization lock key (<see cref="SchemaInitializationLockKey"/>).</param>
/// <param name="BootstrapScripts">The bootstrap closure: the core tables, then the marked migration regions, in order.</param>
/// <param name="CommandTimeoutSeconds">The schema command budget, for one bootstrap script and one lock wait.</param>
/// <param name="IsSchemaCurrentAsync">
/// Whether the schema needs nothing from this build, read on the connection given and without a lock: what a waiter
/// asks first, so a migrator that committed is never mistaken for one that died.
/// </param>
/// <param name="IncompatibilityOverride">
/// The reason the application's schema is served as it is by its own override (#2553), recorded on this instance's
/// row when it registers; null when there is none.
/// </param>
/// <docs>operations/infrastructure/migrations#which-instance-migrates</docs>
public sealed record MigratorElectionRequest(
  Func<NpgsqlConnection> ConnectionFactory,
  string Schema,
  long LockId,
  IReadOnlyList<(string Name, string Sql)> BootstrapScripts,
  int CommandTimeoutSeconds,
  Func<NpgsqlConnection, CancellationToken, Task<bool>> IsSchemaCurrentAsync,
  string? IncompatibilityOverride = null);

/// <summary>
/// The one migrator election every Postgres driver runs (#2561): the bootstrap closure, this instance's registration,
/// the vote for the migrator duty, and then, before the schema lock, the wait for whoever is migrating or the takeover
/// of a migrator that died.
/// </summary>
/// <remarks>
/// <para>
/// The EF Core driver's generated initializer and the Dapper driver's runner both call this, so the two cannot drift:
/// one instance migrates, every other instance waits on the migrator's duty and then serves, and a waiter whose
/// migrator died with the schema still behind votes for the duty and does the work once it holds it.
/// </para>
/// <para>
/// Nothing about the election is fatal (ai-docs/schema-initialization-connections.md, Trap 4): a refusal, a missing
/// function, a failed registration, no elector or no instance identity, each ends unstaged, and an unstaged instance
/// migrates under the schema lock exactly as every instance did before an election existed. The bootstrap is the one
/// exception: it fails the attempt, loudly, naming the step.
/// </para>
/// <para>
/// The caller owns the <see cref="MigratorDuty"/> (<see cref="CreateDuty"/>) and routes progress to it with
/// <see cref="StartupProgress.ReportTo"/> from its own body, so every step the pass commits keeps the role; it disposes
/// the duty when initialization returns, however it returns.
/// </para>
/// </remarks>
/// <docs>operations/infrastructure/migrations#which-instance-migrates</docs>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperMigratorElectionTests.cs</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SchemaInitializationConcurrencyTests.cs</tests>
public static partial class MigratorElection {
  /// <summary>
  /// The migrator duty a schema initialization holds, on the host's clock (else the system clock), held to the stall
  /// limit the role options and the schema command budget give it (#2165).
  /// </summary>
  /// <param name="services">The host's services; null reads the defaults.</param>
  /// <param name="schemaCommandTimeoutSeconds">The schema command budget, in seconds.</param>
  /// <param name="logger">Where the keepalive reports.</param>
  /// <returns>A duty holding nothing yet; dispose it when initialization returns.</returns>
  /// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperMigratorElectionTests.cs:CreateDuty_ReadsTheHostsClockAndRoleOptions_AndHoldsNothingAsync</tests>
  public static MigratorDuty CreateDuty(IServiceProvider? services, int schemaCommandTimeoutSeconds, ILogger? logger = null) =>
    new(
      services?.GetService<TimeProvider>() ?? TimeProvider.System,
      logger,
      MigratorDuty.StallLimitFor(services?.GetService<IOptions<RoleAssignmentOptions>>()?.Value, schemaCommandTimeoutSeconds));

  /// <summary>
  /// Preflight's end and Elect: applies the bootstrap closure, registers this instance, votes for the migrator duty,
  /// holds what the vote decided on <paramref name="duty"/>, and reports the outcome as the Elect stage.
  /// </summary>
  /// <param name="duty">The duty this initialization holds.</param>
  /// <param name="request">The election's connection, schema, lock and closure.</param>
  /// <param name="services">The host's services: the instance identity, the elector, the stage reporter and observers.</param>
  /// <param name="logger">Optional logger.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>What the election decided.</returns>
  /// <exception cref="SchemaBootstrapException">The bootstrap failed for a reason that is not transient.</exception>
  /// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperMigratorElectionTests.cs:TwoInstancesStarting_OneMigrates_TheOtherWaitsForIt_ThenServesAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SchemaInitializationConcurrencyTests.cs:Staged_TheMigratorRegistersAndThenReleasesTheDutyAsync</tests>
  public static async Task<SchemaStaging> StageAsync(
      MigratorDuty duty, MigratorElectionRequest request, IServiceProvider? services, ILogger? logger,
      CancellationToken cancellationToken) {
    ArgumentNullException.ThrowIfNull(duty);
    ArgumentNullException.ThrowIfNull(request);
    var log = logger ?? NullLogger.Instance;

    // Phase 0: the bootstrap. Outside the catch below on purpose: a bootstrap that cannot be applied fails the attempt
    // in Preflight. It reports whether an election is possible now rather than whether the scripts appeared to work.
    var canElect = await SchemaBootstrapPhase.ApplyAsync(
      new SchemaBootstrapRequest(
        request.ConnectionFactory, request.LockId, request.BootstrapScripts, request.Schema, request.CommandTimeoutSeconds),
      services, TimeProvider.System, logger, cancellationToken).ConfigureAwait(false);

    var staging = duty.Staging;
    try {
      // Phase 1: join the registry, then vote. Registering is Preflight's last step (#1636), and it precedes the vote:
      // the capability function refuses an instance it cannot find, and that refusal says nothing about contention.
      if (canElect && services?.GetService<IServiceInstanceProvider>() is { } instance) {
        await SchemaInitializationStages.RegisteringAsync(services, cancellationToken).ConfigureAwait(false);
        var registered = await MigratorDutyStaging.RegisterAsync(
          ct => _registerAsync(request, instance, services, ct), request.Schema, logger, cancellationToken).ConfigureAwait(false);
        if (registered) {
          await SchemaInitializationStages.ElectingAsync(services, cancellationToken).ConfigureAwait(false);
          staging = await MigratorDutyStaging.ElectAsync(
            services.GetService<IDutyElector>(), request.Schema, logger, cancellationToken).ConfigureAwait(false);
          await duty.HoldAsync(staging).ConfigureAwait(false);
        } else {
          staging = new SchemaStaging(SchemaStage.Unstaged, null, "this instance could not join the registry");
        }
      }
    } catch (Exception ex) when (ex is not OperationCanceledException) {
      // Belt and braces: the election answers rather than throws; this keeps anything it could not foresee out of the
      // startup path, and this instance migrates under the schema lock alone.
      LogStagingFailed(log, ex, request.Schema);
    }

    await SchemaInitializationStages.ElectedAsync(services, staging, cancellationToken).ConfigureAwait(false);
    return staging;
  }

  /// <summary>
  /// The phases before the schema lock: the migrator (and an instance that could not be staged) runs
  /// <paramref name="workAsync"/>; a waiter waits on the migrator's duty, and votes and does the work only when the
  /// migrator is gone with the schema still behind (<see cref="MigratorPreLockPhase"/>).
  /// </summary>
  /// <param name="duty">The duty <see cref="StageAsync"/> held; replaced when this instance takes the duty over.</param>
  /// <param name="request">The election's connection, schema and lock.</param>
  /// <param name="workAsync">The driver's work before the schema lock: an owed rollback, the stored-form rewrites.</param>
  /// <param name="services">The host's services: the elector, the stage reporter and observers.</param>
  /// <param name="logger">Optional logger.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>
  /// Whether the migrator finished the schema for this instance: true for a waiter that saw it current, which has no
  /// pass of its own to run; false when this instance did the work and goes on to the pass.
  /// </returns>
  /// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperMigratorElectionTests.cs:AMigratorThatDiesMidMigration_HandsTheWorkToTheWaiterAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SchemaInitializationConcurrencyTests.cs:Staged_AKilledDutyHolderIsTakenOverAsync</tests>
  public static async Task<bool> RunPreLockAsync(
      MigratorDuty duty, MigratorElectionRequest request, Func<CancellationToken, Task> workAsync, IServiceProvider? services,
      ILogger? logger, CancellationToken cancellationToken) {
    ArgumentNullException.ThrowIfNull(duty);
    ArgumentNullException.ThrowIfNull(request);
    ArgumentNullException.ThrowIfNull(workAsync);
    var worked = false;
    await MigratorPreLockPhase.RunAsync(
      duty,
      new PreLockPhaseSteps(
        WaitAsync: (stage, ct) => _waitAsync(request, stage, services, logger, ct),
        ElectAsync: ct => MigratorDutyStaging.ElectAsync(services?.GetService<IDutyElector>(), request.Schema, logger, ct),
        // On the system clock, like the deferral's own polls: a host clock frozen by a test must not stall a vote.
        PauseAsync: (vote, ct) => Task.Delay(MigratorPreLockPhase.ReelectionDelay(vote), TimeProvider.System, ct),
        WorkAsync: async ct => {
          worked = true;
          await workAsync(ct).ConfigureAwait(false);
        }),
      request.Schema, logger, cancellationToken).ConfigureAwait(false);
    // Only a waiter returns without working, and only once it saw the schema current: the migrator finished it.
    return !worked;
  }

  /// <summary>Joins the registry, records an application override on the row, and writes the stage held until then.</summary>
  private static async Task _registerAsync(
      MigratorElectionRequest request, IServiceInstanceProvider instance, IServiceProvider services, CancellationToken ct) {
    await SchemaBootstrapPhase.RegisterInstanceAsync(
      request.ConnectionFactory, request.Schema, instance,
      liveness: InstanceRegistrationLiveness.Of(services), cancellationToken: ct).ConfigureAwait(false);
    // #2553: the application's schema served as it is by its own override, while the W! migrations still run.
    if (request.IncompatibilityOverride is { } overridden) {
      await SchemaBootstrapPhase.RecordIncompatibilityOverrideAsync(
        request.ConnectionFactory, request.Schema, instance.InstanceId, overridden, ct).ConfigureAwait(false);
    }
    // The row exists now: the stage held until it did is written.
    await SchemaInitializationStages.RegisteredAsync(services, ct).ConfigureAwait(false);
  }

  /// <summary>
  /// Waits for whoever is doing the schema work now. A waiter watches the migrator's duty: its assignment row, or the
  /// holding an older release records (judged by the one liveness rule, #2044). Anyone else watches the schema lock (a
  /// sibling's bootstrap or DDL), and has nothing to wait for when the lock is free.
  /// </summary>
  /// <returns>Whether work remains for this instance (<see cref="PreLockPhaseSteps.WaitAsync"/>).</returns>
  private static async Task<bool> _waitAsync(
      MigratorElectionRequest request, SchemaStage stage, IServiceProvider? services, ILogger? logger, CancellationToken ct) {
    await using var connection = request.ConnectionFactory();
    await connection.OpenAsync(ct).ConfigureAwait(false);
    var isWaiter = stage == SchemaStage.Waiter;
    if (!isWaiter && !await AdvisoryLockProbe.IsHeldElsewhereAsync(connection, request.LockId, ct).ConfigureAwait(false)) {
      return true;
    }
    Func<CancellationToken, Task<bool>> isMigrating = isWaiter
      ? c => MigratorWatch.IsMigratingElsewhereAsync(connection, request.Schema, c)
      : c => AdvisoryLockProbe.IsHeldElsewhereAsync(connection, request.LockId, c);
    // Another instance is doing the schema work; this one waits for it, and says so first.
    await SchemaInitializationStages.MigratingAsync(services, "waiting for the instance migrating the schema", ct).ConfigureAwait(false);
    await SchemaInitializationObservers.LockContendedAsync(services, request.Schema, ct).ConfigureAwait(false);
    var outcome = await SchemaMigrationDeferral.DeferAsync(
      c => request.IsSchemaCurrentAsync(connection, c), isMigrating, TimeProvider.System, request.Schema, logger, ct)
      .ConfigureAwait(false);
    return !isWaiter || outcome == SchemaDeferralOutcome.MigratingInstanceGone;
  }

  [LoggerMessage(EventId = 1, Level = LogLevel.Warning,
    Message = "Staged startup could not be arranged for schema {Schema}; this instance will migrate under the advisory lock alone")]
  static partial void LogStagingFailed(ILogger logger, Exception exception, string schema);
}
