// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Whizbang.Core.Startup;

namespace Whizbang.Data.Postgres;

/// <summary>
/// The startup stages schema initialization reports (#1636), from code that has the service provider but not the
/// stage reporter: the EF Core driver's generated schema pass and the shared initializer call these. Each does
/// nothing when no <see cref="IStartupStageReporter"/> is registered.
/// </summary>
/// <docs>operations/startup/startup-status#startup-stages</docs>
/// <tests>tests/Whizbang.Core.Tests/Schema/SchemaInitializationStagesTests.cs</tests>
public static class SchemaInitializationStages {
  /// <summary>This instance is about to join the registry and vote for the migrator duty.</summary>
  /// <param name="services">Where the reporter is registered; null reports nothing.</param>
  /// <param name="cancellationToken">Canceled when initialization is.</param>
  /// <returns>A task that completes once the stage is reported.</returns>
  public static Task ElectingAsync(IServiceProvider? services, CancellationToken cancellationToken) =>
    Task.CompletedTask;

  /// <summary>This instance's row now exists; the stage held until it did is written.</summary>
  /// <param name="services">Where the reporter is registered; null reports nothing.</param>
  /// <param name="cancellationToken">Canceled when initialization is.</param>
  /// <returns>A task that completes once the held stage is written or held again.</returns>
  public static Task RegisteredAsync(IServiceProvider? services, CancellationToken cancellationToken) =>
    Task.CompletedTask;

  /// <summary>
  /// The election is over: reports its outcome, or the election as skipped when it was never held.
  /// </summary>
  /// <param name="services">Where the reporter is registered; null reports nothing.</param>
  /// <param name="staging">What the election decided.</param>
  /// <param name="cancellationToken">Canceled when initialization is.</param>
  /// <returns>A task that completes once the outcome is reported.</returns>
  public static Task ElectedAsync(IServiceProvider? services, SchemaStaging staging, CancellationToken cancellationToken) =>
    Task.CompletedTask;

  /// <summary>The schema pass is under way, or this instance is waiting for the one doing it.</summary>
  /// <param name="services">Where the reporter is registered; null reports nothing.</param>
  /// <param name="message">What the pass is doing.</param>
  /// <param name="cancellationToken">Canceled when initialization is.</param>
  /// <returns>A task that completes once the stage is reported.</returns>
  public static Task MigratingAsync(IServiceProvider? services, string message, CancellationToken cancellationToken) =>
    Task.CompletedTask;

  /// <summary>This instance's schema pass begins, with how many steps it owes.</summary>
  /// <param name="services">Where the reporter is registered; null reports nothing.</param>
  /// <param name="steps">The steps the pass owes.</param>
  /// <param name="cancellationToken">Canceled when initialization is.</param>
  /// <returns>A task that completes once the stage is reported.</returns>
  public static Task ApplyingAsync(IServiceProvider? services, int steps, CancellationToken cancellationToken) =>
    Task.CompletedTask;

  /// <summary>
  /// The schema needs no pass from this instance: progress within <see cref="StartupStage.Migrate"/> when it was
  /// entered (a waiter whose migrator finished), the stage skipped otherwise.
  /// </summary>
  /// <param name="services">Where the reporter is registered; null reports nothing.</param>
  /// <param name="reason">Why no pass is needed.</param>
  /// <param name="cancellationToken">Canceled when initialization is.</param>
  /// <returns>A task that completes once the stage is reported.</returns>
  public static Task MigrationNotNeededAsync(IServiceProvider? services, string reason, CancellationToken cancellationToken) =>
    Task.CompletedTask;

  /// <summary>The pass committed; new indexes are analyzed and managed objects reconciled.</summary>
  /// <param name="services">Where the reporter is registered; null reports nothing.</param>
  /// <param name="cancellationToken">Canceled when initialization is.</param>
  /// <returns>A task that completes once the stage is reported.</returns>
  public static Task ConvergingAsync(IServiceProvider? services, CancellationToken cancellationToken) =>
    Task.CompletedTask;

  /// <summary>Every driver's schema is initialized; partitions are recomputed and the schema-ready gate opens next.</summary>
  /// <param name="services">Where the reporter is registered; null reports nothing.</param>
  /// <param name="cancellationToken">Canceled when initialization is.</param>
  /// <returns>A task that completes once the stage is reported.</returns>
  public static Task CoordinatingAsync(IServiceProvider? services, CancellationToken cancellationToken) =>
    Task.CompletedTask;
}
