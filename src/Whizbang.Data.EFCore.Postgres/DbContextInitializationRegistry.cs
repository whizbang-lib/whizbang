using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;

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
  private static readonly ConditionalWeakTable<IServiceProvider, InitializationState> _initializedByProvider = new();

  private sealed class InitializationState {
    public int Done;
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
  /// Registers an initialization callback for a DbContext type.
  /// Called by source-generated module initializer in the consumer assembly.
  /// </summary>
  /// <typeparam name="TDbContext">The DbContext type.</typeparam>
  /// <param name="callback">
  /// Callback that initializes the database schema.
  /// Should call EnsureWhizbangDatabaseInitializedAsync on the resolved DbContext.
  /// </param>
  public static void Register<TDbContext>(Func<IServiceProvider, ILogger?, CancellationToken, Task> callback)
      where TDbContext : class {
    lock (_lock) {
      _initializers.Add(new DbContextInitializer(typeof(TDbContext), callback));
    }
  }

  /// <summary>
  /// Invokes all registered initialization callbacks.
  /// Called by EnsureWhizbangInitializedAsync() host extension.
  /// </summary>
  /// <param name="serviceProvider">The service provider to resolve DbContexts from.</param>
  /// <param name="logger">Optional logger for diagnostic messages.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <docs>data/turnkey-initialization#idempotency</docs>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/DbContextInitializationRegistryTests.cs:InitializeAllAsync_DifferentServiceProviders_EachInitializeAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/DbContextInitializationRegistryTests.cs:InitializeAllAsync_SameServiceProviderTwice_SkipsAndSaysSoAsync</tests>
  public static async Task InitializeAllAsync(
      IServiceProvider serviceProvider,
      ILogger? logger = null,
      CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(serviceProvider);

    var state = _initializedByProvider.GetValue(serviceProvider, static _ => new InitializationState());
    if (Interlocked.CompareExchange(ref state.Done, 1, 0) == 1) {
      if (logger is not null) {
        DbContextInitializationLog.AlreadyInitialized(logger);
      }
      return;
    }

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

    if (logger is not null) {
      DbContextInitializationLog.InitializationComplete(logger);
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
}
