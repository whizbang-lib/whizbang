using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;

namespace ECommerce.RabbitMQ.Integration.Tests.Fixtures;

/// <summary>
/// Provides shared RabbitMQ and PostgreSQL containers for all tests.
/// Tests run SEQUENTIALLY with per-test databases and separate hosts.
/// Ensures reliable test execution with predictable timing.
/// </summary>
public static class SharedRabbitMqFixtureSource {
  private static readonly SemaphoreSlim _initLock = new(1, 1);
  private static RabbitMqContainer? _sharedRabbitMq;
  private static PostgreSqlContainer? _sharedPostgres;
  private static bool _initialized = false;
  private static bool _initializationFailed = false;
  private static Exception? _lastInitializationError;

  /// <summary>
  /// Gets the shared RabbitMQ connection string.
  /// </summary>
  public static string RabbitMqConnectionString =>
    _sharedRabbitMq?.GetConnectionString() ?? throw new InvalidOperationException("Shared RabbitMQ not initialized. Call InitializeAsync() first.");

  /// <summary>
  /// Gets the shared PostgreSQL connection string (base - tests create unique databases).
  /// </summary>
  public static string PostgresConnectionString =>
    _sharedPostgres?.GetConnectionString() ?? throw new InvalidOperationException("Shared PostgreSQL not initialized. Call InitializeAsync() first.");

  /// <summary>
  /// Gets the RabbitMQ Management API URI.
  /// </summary>
  public static Uri ManagementApiUri {
    get {
      if (_sharedRabbitMq == null) {
        throw new InvalidOperationException("Shared RabbitMQ not initialized. Call InitializeAsync() first.");
      }
      return new Uri($"http://localhost:{_sharedRabbitMq.GetMappedPublicPort(15672)}");
    }
  }

  /// <summary>
  /// Initializes shared RabbitMQ and PostgreSQL containers.
  /// Called once before any tests run.
  /// </summary>
  public static async Task InitializeAsync(CancellationToken cancellationToken = default) {
    // If already initialized successfully, return immediately
    if (_initialized) {
      return;
    }

    // If previous initialization failed, throw the error immediately (don't retry)
    if (_initializationFailed) {
      throw new InvalidOperationException(
        $"Shared container initialization previously failed and cannot be retried. " +
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
          $"Shared container initialization previously failed. Original error: {_lastInitializationError?.Message}",
          _lastInitializationError
        );
      }

      Console.WriteLine("================================================================================");
      Console.WriteLine("[SharedRabbitMqFixture] Initializing shared RabbitMQ and PostgreSQL containers...");
      Console.WriteLine("================================================================================");

      try {
        // Step 1: Create RabbitMQ container
        _sharedRabbitMq = new RabbitMqBuilder()
          .WithImage("rabbitmq:3.13-management-alpine")
          .WithUsername("guest")
          .WithPassword("guest")
          .WithPortBinding(15672, true)  // Expose Management API port
          .Build();
        Console.WriteLine("[SharedRabbitMqFixture] Created RabbitMQ container");

        // Step 2: Create PostgreSQL container
        _sharedPostgres = new PostgreSqlBuilder()
          .WithImage("postgres:17-alpine")
          .WithDatabase("whizbang_test")
          .WithUsername("whizbang_user")
          .WithPassword("whizbang_pass")
          .Build();
        Console.WriteLine("[SharedRabbitMqFixture] Created PostgreSQL container");

        // Step 3: Start containers in parallel
        Console.WriteLine("[SharedRabbitMqFixture] Starting containers (may take 10-15 seconds)...");
        await Task.WhenAll(
          _sharedRabbitMq.StartAsync(ct),
          _sharedPostgres.StartAsync(ct)
        );
        Console.WriteLine("[SharedRabbitMqFixture] ✓ Containers started");

        Console.WriteLine("================================================================================");
        Console.WriteLine("[SharedRabbitMqFixture] ✅ Shared resources ready!");
        Console.WriteLine($"[SharedRabbitMqFixture] RabbitMQ: {RabbitMqConnectionString}");
        Console.WriteLine($"[SharedRabbitMqFixture] Management API: {ManagementApiUri}");
        Console.WriteLine("================================================================================");

        _initialized = true;
      } catch (Exception ex) {
        // Mark initialization as failed to prevent retry loops
        _initializationFailed = true;
        _lastInitializationError = ex;

        Console.WriteLine("================================================================================");
        Console.WriteLine($"[SharedRabbitMqFixture] ❌ Initialization FAILED: {ex.Message}");
        Console.WriteLine("================================================================================");

        // Clean up partial initialization
        await CleanupAfterFailureAsync();

        throw new InvalidOperationException(
          $"Failed to initialize shared containers. " +
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
  public static string GetPerTestDatabaseConnectionString() {
    if (!_initialized) {
      throw new InvalidOperationException("Fixture must be initialized before creating per-test databases");
    }

    // Generate unique database name using GUID
    var dbName = $"test_{Guid.NewGuid():N}";

    // Build connection string with unique database name
    // IMPORTANT: Use small pool sizes to avoid hitting PostgreSQL's max_connections limit
    // With 60 tests × 2 databases per test = 120 databases, we need to be very conservative
    // Reducing to MaxPoolSize=2 to prevent resource exhaustion when running all tests together
    var builder = new Npgsql.NpgsqlConnectionStringBuilder(PostgresConnectionString) {
      Database = dbName,
      MinPoolSize = 0,    // Don't keep idle connections
      MaxPoolSize = 2,    // Limit max connections per database (reduced from 5 to prevent exhaustion)
      ConnectionIdleLifetime = 5,  // Close idle connections after 5 seconds (reduced from 10)
      ConnectionPruningInterval = 3  // Prune connections every 3 seconds (reduced from 5)
    };

    return builder.ConnectionString;
  }

  /// <summary>
  /// Cleans up resources after initialization failure.
  /// </summary>
  private static async Task CleanupAfterFailureAsync() {
    try {
      if (_sharedRabbitMq != null) {
        await _sharedRabbitMq.DisposeAsync();
        _sharedRabbitMq = null;
        Console.WriteLine("[SharedRabbitMqFixture] Disposed RabbitMQ after failure");
      }

      if (_sharedPostgres != null) {
        await _sharedPostgres.DisposeAsync();
        _sharedPostgres = null;
        Console.WriteLine("[SharedRabbitMqFixture] Disposed PostgreSQL after failure");
      }
    } catch (Exception ex) {
      Console.WriteLine($"[SharedRabbitMqFixture] Warning: Error during cleanup: {ex.Message}");
    }
  }

  /// <summary>
  /// Final cleanup: disposes shared containers when tests complete.
  /// </summary>
  public static async Task DisposeAsync() {
    if (_sharedRabbitMq != null) {
      await _sharedRabbitMq.DisposeAsync();
      _sharedRabbitMq = null;
      Console.WriteLine("[SharedRabbitMqFixture] Disposed shared RabbitMQ container");
    }

    if (_sharedPostgres != null) {
      await _sharedPostgres.DisposeAsync();
      _sharedPostgres = null;
      Console.WriteLine("[SharedRabbitMqFixture] Disposed shared PostgreSQL container");
    }

    // Reset state to allow reinitialization if needed
    _initialized = false;
    _initializationFailed = false;
    _lastInitializationError = null;
  }
}
