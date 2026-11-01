// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Data.Postgres;

/// <summary>
/// Watches schema initialization while it runs. Register any number with
/// <c>services.AddSingleton&lt;ISchemaInitializationObserver&gt;(...)</c>; every driver's initialization reports to
/// all of them.
/// </summary>
/// <remarks>
/// <para>
/// For monitoring (a start that waits on another instance's migration says so here before it waits) and for a test
/// that has to know a stage was reached rather than guess from time passing. Every method has a default that does
/// nothing, so an observer implements only what it watches.
/// </para>
/// <para>
/// The initializer awaits each call before it carries on, so an observer that blocks holds startup. That is
/// deliberate: it is how a test fixes an interleaving. A production observer returns promptly.
/// </para>
/// </remarks>
/// <docs>data/turnkey-initialization#watching-initialization</docs>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperSchemaStartupTests.cs:HostStart_WaitsForTheSchemaLock_WhileAnotherSessionHoldsItAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StartupParity/StartupParityTests.cs</tests>
public interface ISchemaInitializationObserver {
  /// <summary>
  /// Another session holds the schema initialization lock for <paramref name="schema"/>, so this start is about to
  /// wait for it instead of migrating alongside it.
  /// </summary>
  /// <param name="schema">The schema whose lock is held, unquoted.</param>
  /// <param name="cancellationToken">Canceled when initialization is.</param>
  /// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperSchemaStartupTests.cs:HostStart_WaitsForTheSchemaLock_WhileAnotherSessionHoldsItAsync</tests>
  ValueTask OnSchemaLockContendedAsync(string schema, CancellationToken cancellationToken) => ValueTask.CompletedTask;

  /// <summary>
  /// A background initialization attempt failed (the database was unreachable, a migration threw, the migration
  /// timeout passed); the schema-ready gate stays closed and the next attempt starts after
  /// <c>SchemaInitializationOptions.InitRetryDelay</c>, once this returns.
  /// </summary>
  /// <param name="attempt">Which attempt failed, from 1.</param>
  /// <param name="exception">Why it failed.</param>
  /// <param name="cancellationToken">Canceled at host shutdown.</param>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StartupParity/StartupParityTests.cs:ADatabaseThatComesUpLate_IsWaitedFor_ThenTheGateOpensAsync</tests>
  ValueTask OnAttemptFailedAsync(int attempt, Exception exception, CancellationToken cancellationToken) => ValueTask.CompletedTask;

  /// <summary>
  /// One step of the schema pass for <paramref name="schema"/> committed: a migration file, a perspective table, or
  /// one of the core-tables, constraints, associations and registries steps, each in a transaction of its own.
  /// </summary>
  /// <param name="schema">The schema being migrated, unquoted.</param>
  /// <param name="stepName">The step's name: a migration's file name, a perspective's ledger key, or the step's own name.</param>
  /// <param name="cancellationToken">Canceled when initialization is.</param>
  /// <remarks>
  /// Called after the commit and before the next step takes the schema lock, so nothing is held while it runs: a
  /// test that blocks here has another instance start against a pass that is part way through.
  /// </remarks>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Migrations/MigrationStepTransactionTests.cs</tests>
  ValueTask OnSchemaStepCommittedAsync(string schema, string stepName, CancellationToken cancellationToken) => ValueTask.CompletedTask;

  /// <summary>
  /// The bootstrap closure for <paramref name="schema"/> failed with a transient database failure (a deadlock, a
  /// dropped connection, a timeout), and is applied again once <paramref name="delay"/> has passed.
  /// </summary>
  /// <param name="schema">The schema being bootstrapped, unquoted.</param>
  /// <param name="attempt">Which attempt failed, from 1.</param>
  /// <param name="exception">Why it failed.</param>
  /// <param name="delay">How long the bootstrap waits before its next attempt.</param>
  /// <param name="cancellationToken">Canceled when initialization is.</param>
  /// <remarks>
  /// Called once the wait has been scheduled on the initializer's clock and before it is awaited, so a test driving a
  /// fake clock can advance it by <paramref name="delay"/> as soon as this is called.
  /// </remarks>
  ValueTask OnBootstrapRetryScheduledAsync(
      string schema, int attempt, Exception exception, TimeSpan delay, CancellationToken cancellationToken) => ValueTask.CompletedTask;
}
