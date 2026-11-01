// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;

namespace Whizbang.Data.Postgres;

/// <summary>
/// Tells every registered <see cref="ISchemaInitializationObserver"/> what schema initialization reached, from code
/// that has the service provider but not the observers: the EF Core driver's generated schema pass calls this.
/// </summary>
/// <docs>data/turnkey-initialization#watching-initialization</docs>
/// <tests>tests/Whizbang.Core.Tests/Schema/SchemaInitializationObserversTests.cs</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StartupParity/StartupParityTests.cs:ConcurrentStarts_AreSerializedByTheSchemaLockAsync</tests>
public static class SchemaInitializationObservers {
  /// <summary>Another session holds the schema lock for <paramref name="schema"/>; this start is about to wait.</summary>
  /// <param name="services">Where the observers are registered; null tells none.</param>
  /// <param name="schema">The schema whose lock is held, unquoted.</param>
  /// <param name="cancellationToken">Canceled when initialization is.</param>
  public static async Task LockContendedAsync(IServiceProvider? services, string schema, CancellationToken cancellationToken) {
    foreach (var observer in services?.GetServices<ISchemaInitializationObserver>() ?? []) {
      await observer.OnSchemaLockContendedAsync(schema, cancellationToken).ConfigureAwait(false);
    }
  }

  /// <summary>One step of the schema pass for <paramref name="schema"/> committed.</summary>
  /// <param name="services">Where the observers are registered; null tells none.</param>
  /// <param name="schema">The schema being migrated, unquoted.</param>
  /// <param name="step">The step that committed.</param>
  /// <param name="cancellationToken">Canceled when initialization is.</param>
  public static async Task StepCommittedAsync(IServiceProvider? services, string schema, string step, CancellationToken cancellationToken) {
    foreach (var observer in services?.GetServices<ISchemaInitializationObserver>() ?? []) {
      await observer.OnSchemaStepCommittedAsync(schema, step, cancellationToken).ConfigureAwait(false);
    }
  }

  /// <summary>The bootstrap closure for <paramref name="schema"/> failed transiently and retries after <paramref name="delay"/>.</summary>
  /// <param name="services">Where the observers are registered; null tells none.</param>
  /// <param name="schema">The schema being bootstrapped, unquoted.</param>
  /// <param name="attempt">Which attempt failed, from 1.</param>
  /// <param name="exception">Why it failed.</param>
  /// <param name="delay">How long the bootstrap waits before its next attempt.</param>
  /// <param name="cancellationToken">Canceled when initialization is.</param>
  public static async Task BootstrapRetryScheduledAsync(
      IServiceProvider? services, string schema, int attempt, Exception exception, TimeSpan delay, CancellationToken cancellationToken) {
    foreach (var observer in services?.GetServices<ISchemaInitializationObserver>() ?? []) {
      await observer.OnBootstrapRetryScheduledAsync(schema, attempt, exception, delay, cancellationToken).ConfigureAwait(false);
    }
  }
}
