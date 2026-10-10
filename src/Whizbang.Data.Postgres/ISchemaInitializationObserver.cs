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
}
