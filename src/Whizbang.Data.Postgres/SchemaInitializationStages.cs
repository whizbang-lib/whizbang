// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Whizbang.Core.Startup;

namespace Whizbang.Data.Postgres;

/// <summary>
/// The startup stages schema initialization reports (#1636), from code that has the service provider but not the
/// stage reporter: the EF Core driver's generated schema pass and the shared initializer call these. Each does
/// nothing when no <see cref="IStartupStageReporter"/> is registered.
/// </summary>
/// <remarks>
/// The messages are framework-authored text only. The election's own detail can carry a server's words, and the stage
/// message reaches the health surface, which is usually the least protected surface a pod has; the detail stays in
/// the election's own log line.
/// </remarks>
/// <docs>operations/startup/startup-status#startup-stages</docs>
/// <tests>tests/Whizbang.Core.Tests/Schema/SchemaInitializationStagesTests.cs</tests>
public static class SchemaInitializationStages {
  private const string UNSTAGED = "this instance migrates under the schema lock";

  /// <summary>The bootstrap closure found the schema lock held and waits for it, within Preflight.</summary>
  /// <param name="services">Where the reporter is registered; null reports nothing.</param>
  /// <param name="cancellationToken">Canceled when initialization is.</param>
  /// <returns>A task that completes once the progress is reported.</returns>
  /// <tests>tests/Whizbang.Core.Tests/Schema/SchemaInitializationStagesTests.cs:TheBootstrapsWaitsAndFailure_AreProgressWithinPreflightAsync</tests>
  public static Task WaitingForTheBootstrapLockAsync(IServiceProvider? services, CancellationToken cancellationToken) =>
    _preflightAsync(services, "waiting for the schema lock to apply the bootstrap closure", cancellationToken);

  /// <summary>The bootstrap closure met a transient database failure and is retried, within Preflight.</summary>
  /// <param name="services">Where the reporter is registered; null reports nothing.</param>
  /// <param name="cancellationToken">Canceled when initialization is.</param>
  /// <returns>A task that completes once the progress is reported.</returns>
  /// <tests>tests/Whizbang.Core.Tests/Schema/SchemaInitializationStagesTests.cs:TheBootstrapsWaitsAndFailure_AreProgressWithinPreflightAsync</tests>
  public static Task BootstrapRetryingAsync(IServiceProvider? services, CancellationToken cancellationToken) =>
    _preflightAsync(services, "the bootstrap closure met a transient database failure; retrying", cancellationToken);

  /// <summary>The bootstrap closure failed with an error that is not transient; Preflight does not finish.</summary>
  /// <param name="services">Where the reporter is registered; null reports nothing.</param>
  /// <param name="cancellationToken">Canceled when initialization is.</param>
  /// <returns>A task that completes once the progress is reported.</returns>
  /// <tests>tests/Whizbang.Core.Tests/Schema/SchemaInitializationStagesTests.cs:TheBootstrapsWaitsAndFailure_AreProgressWithinPreflightAsync</tests>
  public static Task BootstrapFailedAsync(IServiceProvider? services, CancellationToken cancellationToken) =>
    _preflightAsync(services, "the bootstrap closure failed with an error that is not transient", cancellationToken);

  /// <summary>This instance is about to join the registry and vote for the migrator duty.</summary>
  /// <param name="services">Where the reporter is registered; null reports nothing.</param>
  /// <param name="cancellationToken">Canceled when initialization is.</param>
  /// <returns>A task that completes once the stage is reported.</returns>
  public static async Task ElectingAsync(IServiceProvider? services, CancellationToken cancellationToken) {
    if (_reporter(services) is { } stages) {
      await stages.EnterAsync(StartupStage.Elect, "joining the registry and voting for the migrator duty", cancellationToken)
        .ConfigureAwait(false);
    }
  }

  /// <summary>This instance's row now exists; the stage held until it did is written.</summary>
  /// <param name="services">Where the reporter is registered; null reports nothing.</param>
  /// <param name="cancellationToken">Canceled when initialization is.</param>
  /// <returns>A task that completes once the held stage is written or held again.</returns>
  public static async Task RegisteredAsync(IServiceProvider? services, CancellationToken cancellationToken) {
    if (_reporter(services) is { } stages) {
      await stages.InstanceRegisteredAsync(cancellationToken).ConfigureAwait(false);
    }
  }

  /// <summary>
  /// The election is over: reports its outcome, or the election as skipped when it was never held.
  /// </summary>
  /// <param name="services">Where the reporter is registered; null reports nothing.</param>
  /// <param name="staging">What the election decided.</param>
  /// <param name="cancellationToken">Canceled when initialization is.</param>
  /// <returns>A task that completes once the outcome is reported.</returns>
  public static async Task ElectedAsync(IServiceProvider? services, SchemaStaging staging, CancellationToken cancellationToken) {
    if (_reporter(services) is not { } stages) {
      return;
    }
    if (stages.Current is not { Stage: >= StartupStage.Elect }) {
      // Elect was never entered: the bootstrap could not make an election possible, or there was no identity to elect.
      await stages.SkipAsync(StartupStage.Elect, "no election could be held, so " + UNSTAGED, cancellationToken)
        .ConfigureAwait(false);
      return;
    }
    var outcome = staging.Stage switch {
      SchemaStage.Migrator => "this instance migrates the schema",
      SchemaStage.Waiter => "another instance migrates the schema",
      _ => "no migrator was elected, so " + UNSTAGED,
    };
    await stages.EnterAsync(StartupStage.Elect, outcome, cancellationToken).ConfigureAwait(false);
  }

  /// <summary>The schema pass is under way, or this instance is waiting for the one doing it.</summary>
  /// <param name="services">Where the reporter is registered; null reports nothing.</param>
  /// <param name="message">What the pass is doing.</param>
  /// <param name="cancellationToken">Canceled when initialization is.</param>
  /// <returns>A task that completes once the stage is reported.</returns>
  public static async Task MigratingAsync(IServiceProvider? services, string message, CancellationToken cancellationToken) {
    if (_reporter(services) is { } stages) {
      await stages.EnterAsync(StartupStage.Migrate, message, cancellationToken).ConfigureAwait(false);
    }
  }

  /// <summary>This instance's schema pass begins, with how many steps it owes.</summary>
  /// <param name="services">Where the reporter is registered; null reports nothing.</param>
  /// <param name="steps">The steps the pass owes.</param>
  /// <param name="cancellationToken">Canceled when initialization is.</param>
  /// <returns>A task that completes once the stage is reported.</returns>
  public static Task ApplyingAsync(IServiceProvider? services, int steps, CancellationToken cancellationToken) =>
    MigratingAsync(services, steps == 1
      ? "1 migration step to apply"
      : string.Create(CultureInfo.InvariantCulture, $"{steps} migration steps to apply"), cancellationToken);

  /// <summary>
  /// The schema needs no pass from this instance: progress within <see cref="StartupStage.Migrate"/> when it was
  /// entered (a waiter whose migrator finished), the stage skipped otherwise.
  /// </summary>
  /// <param name="services">Where the reporter is registered; null reports nothing.</param>
  /// <param name="reason">Why no pass is needed.</param>
  /// <param name="cancellationToken">Canceled when initialization is.</param>
  /// <returns>A task that completes once the stage is reported.</returns>
  public static async Task MigrationNotNeededAsync(IServiceProvider? services, string reason, CancellationToken cancellationToken) {
    if (_reporter(services) is not { } stages) {
      return;
    }
    if (stages.Current?.Stage == StartupStage.Migrate) {
      await stages.EnterAsync(StartupStage.Migrate, reason, cancellationToken).ConfigureAwait(false);
    } else {
      await stages.SkipAsync(StartupStage.Migrate, reason, cancellationToken).ConfigureAwait(false);
    }
  }

  /// <summary>The pass committed; new indexes are analyzed and managed objects reconciled.</summary>
  /// <param name="services">Where the reporter is registered; null reports nothing.</param>
  /// <param name="cancellationToken">Canceled when initialization is.</param>
  /// <returns>A task that completes once the stage is reported.</returns>
  public static async Task ConvergingAsync(IServiceProvider? services, CancellationToken cancellationToken) {
    if (_reporter(services) is { } stages) {
      await stages.EnterAsync(StartupStage.Converge, "analyzing new indexes and reconciling managed objects", cancellationToken)
        .ConfigureAwait(false);
    }
  }

  /// <summary>Every driver's schema is initialized; partitions are recomputed and the schema-ready gate opens next.</summary>
  /// <param name="services">Where the reporter is registered; null reports nothing.</param>
  /// <param name="cancellationToken">Canceled when initialization is.</param>
  /// <returns>A task that completes once the stage is reported.</returns>
  public static async Task CoordinatingAsync(IServiceProvider? services, CancellationToken cancellationToken) {
    if (_reporter(services) is { } stages) {
      await stages.EnterAsync(StartupStage.Coordinate, "recomputing partitions before the schema-ready gate opens", cancellationToken)
        .ConfigureAwait(false);
    }
  }

  /// <summary>Progress within Preflight, in framework-authored words.</summary>
  private static async Task _preflightAsync(IServiceProvider? services, string message, CancellationToken cancellationToken) {
    if (_reporter(services) is { } stages) {
      await stages.EnterAsync(StartupStage.Preflight, message, cancellationToken).ConfigureAwait(false);
    }
  }

  private static IStartupStageReporter? _reporter(IServiceProvider? services) =>
    services?.GetService<IStartupStageReporter>();
}
