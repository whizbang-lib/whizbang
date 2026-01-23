using Npgsql;
using Testcontainers.PostgreSql;

namespace Whizbang.Testing.Containers;

/// <summary>
/// Provides a shared PostgreSQL container for all tests.
/// Tests should use this instead of creating their own containers to avoid timeout issues
/// caused by multiple container startups.
/// </summary>
/// <remarks>
/// Usage:
/// <code>
/// [Before(Test)]
/// public async Task SetupAsync() {
///   await SharedPostgresContainer.InitializeAsync();
///   // Option 1: Use base connection string
///   var connectionString = SharedPostgresContainer.ConnectionString;
///
///   // Option 2: Get per-test isolated database (recommended for integration tests)
///   var isolatedConnectionString = SharedPostgresContainer.GetPerTestDatabaseConnectionString();
/// }
/// </code>
/// </remarks>
public static class SharedPostgresContainer {
  private static readonly SemaphoreSlim _initLock = new(1, 1);
  private static PostgreSqlContainer? _container;
  private static bool _initialized;
  private static bool _initializationFailed;
  private static Exception? _lastInitializationError;

  /// <summary>
  /// Gets the shared PostgreSQL connection string (base database).
  /// For test isolation, use <see cref="GetPerTestDatabaseConnectionString"/> instead.
  /// </summary>
  /// <exception cref="InvalidOperationException">Thrown if container is not initialized.</exception>
  public static string ConnectionString =>
    _container?.GetConnectionString() ?? throw new InvalidOperationException("Shared PostgreSQL not initialized. Call InitializeAsync() first.");

  /// <summary>
  /// Gets whether the container has been successfully initialized.
  /// </summary>
  public static bool IsInitialized => _initialized;

  /// <summary>
  /// Initializes the shared PostgreSQL container.
  /// Safe to call multiple times - will only initialize once.
  /// </summary>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <exception cref="InvalidOperationException">Thrown if initialization fails or has previously failed.</exception>
  public static async Task InitializeAsync(CancellationToken cancellationToken = default) {
    // If already initialized successfully, return immediately
    if (_initialized) {
      return;
    }

    // If previous initialization failed, throw the error immediately (don't retry)
    if (_initializationFailed) {
      throw new InvalidOperationException(
        $"Shared PostgreSQL container initialization previously failed and cannot be retried. " +
        $"Original error: {_lastInitializationError?.Message}",
        _lastInitializationError
      );
    }

    // Use default timeout of 120 seconds
    using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
    using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
    var ct = linkedCts.Token;

    await _initLock.WaitAsync(ct);
    try {
      // Double-check after acquiring lock
      if (_initialized) {
        return;
      }

      if (_initializationFailed) {
        throw new InvalidOperationException(
          $"Shared PostgreSQL container initialization previously failed. Original error: {_lastInitializationError?.Message}",
          _lastInitializationError
        );
      }

      Console.WriteLine("================================================================================");
      Console.WriteLine("[SharedPostgresContainer] Initializing shared PostgreSQL container...");
      Console.WriteLine("================================================================================");

      try {
        _container = new PostgreSqlBuilder()
          .WithImage("postgres:17-alpine")
          .WithDatabase("whizbang_test")
          .WithUsername("whizbang_user")
          .WithPassword("whizbang_pass")
          // Increase max_connections for high-concurrency tests
          // Default is 100, but with parallel tests creating many databases/connections we need more
          .WithCommand("-c", "max_connections=500")
          .Build();

        Console.WriteLine("[SharedPostgresContainer] Starting container (may take 10-15 seconds)...");
        await _container.StartAsync(ct);

        Console.WriteLine("================================================================================");
        Console.WriteLine("[SharedPostgresContainer] PostgreSQL container ready!");
        Console.WriteLine($"[SharedPostgresContainer] Connection: {ConnectionString}");
        Console.WriteLine("================================================================================");

        _initialized = true;
      } catch (Exception ex) {
        // Mark initialization as failed to prevent retry loops
        _initializationFailed = true;
        _lastInitializationError = ex;

        Console.WriteLine("================================================================================");
        Console.WriteLine($"[SharedPostgresContainer] Initialization FAILED: {ex.Message}");
        Console.WriteLine("================================================================================");

        // Clean up partial initialization
        await _cleanupAfterFailureAsync();

        throw new InvalidOperationException(
          $"Failed to initialize shared PostgreSQL container. " +
          $"Error: {ex.Message}. " +
          $"This is a fatal error - remaining tests will be skipped.",
          ex
        );
      }
    } finally {
      _initLock.Release();
    }
  }

  /// <summary>
  /// Creates a unique database connection string for a specific test.
  /// Each test gets its own database for complete isolation.
  /// </summary>
  /// <returns>Connection string pointing to a unique database.</returns>
  /// <exception cref="InvalidOperationException">Thrown if container is not initialized.</exception>
  public static string GetPerTestDatabaseConnectionString() {
    if (!_initialized) {
      throw new InvalidOperationException("Container must be initialized before creating per-test databases");
    }

    // Generate unique database name using GUID
    var dbName = $"test_{Guid.NewGuid():N}";

    // Build connection string with unique database name
    // IMPORTANT: Use small pool sizes to avoid hitting PostgreSQL's max_connections limit
    // With 60 tests x 2 databases per test = 120 databases, we need to be very conservative
    var builder = new NpgsqlConnectionStringBuilder(ConnectionString) {
      Database = dbName,
      MinPoolSize = 0,    // Don't keep idle connections
      MaxPoolSize = 2,    // Limit max connections per database
      ConnectionIdleLifetime = 5,  // Close idle connections after 5 seconds
      ConnectionPruningInterval = 3  // Prune connections every 3 seconds
    };

    return builder.ConnectionString;
  }

  /// <summary>
  /// Cleans up resources after initialization failure.
  /// </summary>
  private static async Task _cleanupAfterFailureAsync() {
    try {
      if (_container != null) {
        await _container.DisposeAsync();
        _container = null;
        Console.WriteLine("[SharedPostgresContainer] Disposed container after failure");
      }
    } catch (Exception ex) {
      Console.WriteLine($"[SharedPostgresContainer] Warning: Error during cleanup: {ex.Message}");
    }
  }

  /// <summary>
  /// Final cleanup: disposes shared container when tests complete.
  /// </summary>
  public static async Task DisposeAsync() {
    if (_container != null) {
      await _container.DisposeAsync();
      _container = null;
      Console.WriteLine("[SharedPostgresContainer] Disposed shared PostgreSQL container");
    }

    // Reset state to allow reinitialization if needed
    _initialized = false;
    _initializationFailed = false;
    _lastInitializationError = null;
  }
}
