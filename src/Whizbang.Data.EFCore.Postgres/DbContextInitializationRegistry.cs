// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Whizbang.Data.EFCore.Postgres;

/// <summary>
/// Static registry for DbContext initialization callbacks.
/// Consumer assemblies register their DbContext initialization via module initializer.
/// This approach is AOT-compatible (no reflection required).
/// Used by EnsureWhizbangInitializedAsync() host extension.
/// </summary>
/// <docs>data/turnkey-initialization</docs>
public static class DbContextInitializationRegistry {
  private static readonly List<DbContextInitializer> _initializers = [];
  private static readonly Lock _lock = new();

  /// <summary>
  /// The idempotence guard, keyed per service provider — i.e. per host — not per process (issue
  /// #620). A process that builds several hosts (a test suite with a host per test, a composition
  /// root hosting two services) owns several databases, and each must be initialized; a
  /// process-wide flag told every host after the first "already initialized" and left its database
  /// with no schema. The weak table lets disposed hosts fall away.
  /// </summary>
  /// <remarks>
  /// Keyed on the host's <see cref="IServiceScopeFactory"/> when it has one (#1636): the host's own provider
  /// (<c>app.Services</c>) and the root scope its singletons are built from, the hosted initializer included, are two
  /// objects with one container, and keyed on the object a host that initialized from <c>app.Services</c> while its
  /// hosted initializer ran started a second initialization beside it. A provider with no scope factory is its own key.
  /// </remarks>
  private static readonly ConditionalWeakTable<object, InitializationState> _initializedByProvider = [];

  /// <summary>
  /// The attempt a provider's initialization is in: none yet, one in progress, one that succeeded (true) or one that
  /// failed (false), behind a lock so two callers cannot both start one.
  /// </summary>
  private sealed class InitializationState {
    public readonly Lock Gate = new();
    public Task<bool>? Attempt;
  }

  /// <summary>
  /// Represents a DbContext initialization delegate.
  /// </summary>
  /// <param name="DbContextType">The DbContext type this initializer handles.</param>
  /// <param name="Callback">
  /// The callback that initializes the database schema for this DbContext.
  /// Parameters: (IServiceProvider, ILogger?, CancellationToken)
  /// </param>
  private sealed record DbContextInitializer(
      Type DbContextType,
      Func<IServiceProvider, ILogger?, CancellationToken, Task> Callback);

  /// <summary>
  /// Registers an initialization callback for a DbContext type, replacing an earlier one for the same type.
  /// Called by source-generated module initializer in the consumer assembly; running that initializer again must
  /// not make a start initialize the context twice.
  /// </summary>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/DbContextInitializationRegistryTests.cs:Register_TheSameContextTwice_KeepsOne_SoAStartInitializesItOnceAsync</tests>
  /// <typeparam name="TDbContext">The DbContext type.</typeparam>
  /// <param name="callback">
  /// Callback that initializes the database schema.
  /// Should call EnsureWhizbangDatabaseInitializedAsync on the resolved DbContext.
  /// </param>
  public static void Register<TDbContext>(Func<IServiceProvider, ILogger?, CancellationToken, Task> callback)
      where TDbContext : class {
    lock (_lock) {
      _initializers.RemoveAll(i => i.DbContextType == typeof(TDbContext));
      _initializers.Add(new DbContextInitializer(typeof(TDbContext), callback));
    }
  }

  /// <summary>
  /// Invokes all registered initialization callbacks.
  /// Called by EnsureWhizbangInitializedAsync() host extension.
  /// </summary>
  /// <remarks>
  /// A barrier per service provider (#1636): a call made while another call's initialization is in progress returns
  /// only once that one has finished, and runs an attempt of its own when that one failed. A call made after one has
  /// succeeded returns at once.
  /// </remarks>
  /// <param name="serviceProvider">The service provider to resolve DbContexts from.</param>
  /// <param name="logger">Optional logger for diagnostic messages.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <docs>data/turnkey-initialization#idempotency</docs>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/DbContextInitializationRegistryTests.cs:InitializeAllAsync_DifferentServiceProviders_EachInitializeAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/DbContextInitializationRegistryTests.cs:InitializeAllAsync_SameServiceProviderTwice_SkipsAndSaysSoAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/DbContextInitializationRegistryTests.cs:InitializeAllAsync_ASecondCallWhileTheFirstRuns_ReturnsOnlyOnceTheFirstHasFinishedAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/DbContextInitializationRegistryTests.cs:InitializeAllAsync_ASecondCallWhoseFirstFails_RunsItsOwnAttemptAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StartupPreflightHostTests.cs:ASecondInitialization_ReturnsOnlyOnceTheFirstHasFinishedAsync</tests>
  public static async Task InitializeAllAsync(
      IServiceProvider serviceProvider,
      ILogger? logger = null,
      CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(serviceProvider);

    var host = serviceProvider.GetService(typeof(IServiceScopeFactory)) ?? serviceProvider;
    var state = _initializedByProvider.GetValue(host, static _ => new InitializationState());
    var log = logger ?? NullLogger.Instance;
    while (true) {
      TaskCompletionSource<bool>? mine = null;
      Task<bool> attempt;
      lock (state.Gate) {
        // A provider with no attempt, or whose last attempt failed, starts one. A failed attempt must not latch: the
        // caller's retry loop reads this method returning as success and opens the schema-ready gate on it, so a
        // latched failure would open the gate over a database whose migration threw.
        if (state.Attempt is not { } previous || previous is { IsCompleted: true, Result: false }) {
          mine = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
          state.Attempt = mine.Task;
        }
        attempt = state.Attempt;
      }

      if (mine is not null) {
        await _runAsync(serviceProvider, logger, mine, cancellationToken);
        return;
      }

      if (attempt.IsCompleted) {
        DbContextInitializationLog.AlreadyInitialized(log);
        return;
      }

      // #1636: another call's initialization is in progress. Returning now said "initialized" while the schema was
      // still being migrated, so a host that initialized again from ApplicationStarted went on to use it; this call
      // waits for that attempt instead, and runs one of its own if that one fails.
      DbContextInitializationLog.WaitingForInitializationInProgress(log);
      if (await attempt.WaitAsync(cancellationToken)) {
        return;
      }
      DbContextInitializationLog.WaitedForInitializationFailed(log);
    }
  }

  /// <summary>Runs every registered callback as one attempt, and completes <paramref name="attempt"/> with its outcome.</summary>
  private static async Task _runAsync(
      IServiceProvider serviceProvider, ILogger? logger, TaskCompletionSource<bool> attempt, CancellationToken cancellationToken) {
    var succeeded = false;
    try {
      List<DbContextInitializer> initializersCopy;

      lock (_lock) {
        initializersCopy = [.. _initializers];
      }

      var count = initializersCopy.Count;
      if (logger is not null) {
        DbContextInitializationLog.StartingInitialization(logger, count);
      }

      foreach (var initializer in initializersCopy) {
        var dbContextName = initializer.DbContextType.Name;
        if (logger is not null) {
          DbContextInitializationLog.InitializingDbContext(logger, dbContextName);
        }
        await initializer.Callback(serviceProvider, logger, cancellationToken);
      }
      succeeded = true;
    } finally {
      // Completed whatever happened: a call waiting for this attempt learns whether it succeeded, and a failure
      // leaves the provider free for the next attempt to be a real one.
      attempt.TrySetResult(succeeded);
    }

    if (logger is not null) {
      DbContextInitializationLog.InitializationComplete(logger);
    }
  }

  /// <summary>
  /// Clears the registered initializers; for tests that share this process-wide registry. Disposing the returned
  /// scope puts them back: they come from module initializers, which never run again, so a reset that is not undone
  /// silently unregisters every generated schema initializer for the rest of the process.
  /// </summary>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/DbContextInitializationRegistryTests.cs:ResetForTesting_DisposingTheScope_PutsBackWhatWasRegisteredAsync</tests>
  internal static IDisposable ResetForTesting() {
    lock (_lock) {
      var saved = _initializers.ToArray();
      _initializers.Clear();
      return new InitializerRestore(saved);
    }
  }

  private sealed class InitializerRestore(DbContextInitializer[] saved) : IDisposable {
    public void Dispose() {
      lock (_lock) {
        _initializers.Clear();
        _initializers.AddRange(saved);
      }
    }
  }

  /// <summary>
  /// Gets the count of registered initializers.
  /// </summary>
  public static int Count {
    get {
      lock (_lock) {
        return _initializers.Count;
      }
    }
  }
}

/// <summary>
/// Source-generated logging methods for DbContext initialization.
/// </summary>
internal static partial class DbContextInitializationLog {
  [LoggerMessage(
      Level = LogLevel.Information,
      Message = "Initializing {Count} Whizbang DbContext(s)...")]
  public static partial void StartingInitialization(ILogger logger, int count);

  [LoggerMessage(
      Level = LogLevel.Information,
      Message = "Initializing {DbContextName}...")]
  public static partial void InitializingDbContext(ILogger logger, string dbContextName);

  [LoggerMessage(
      Level = LogLevel.Information,
      Message = "All Whizbang DbContext(s) initialized successfully")]
  public static partial void InitializationComplete(ILogger logger);

  [LoggerMessage(
      Level = LogLevel.Debug,
      Message = "Whizbang database already initialized, skipping")]
  public static partial void AlreadyInitialized(ILogger logger);

  [LoggerMessage(
      Level = LogLevel.Information,
      Message = "Whizbang database initialization is in progress; waiting for it to finish")]
  public static partial void WaitingForInitializationInProgress(ILogger logger);

  [LoggerMessage(
      Level = LogLevel.Warning,
      Message = "The Whizbang database initialization this call waited for failed; this call runs one of its own")]
  public static partial void WaitedForInitializationFailed(ILogger logger);
}
