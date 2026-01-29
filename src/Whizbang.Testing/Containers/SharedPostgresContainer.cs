using System.Diagnostics;
using Npgsql;

namespace Whizbang.Testing.Containers;

/// <summary>
/// Provides a shared PostgreSQL container for all tests.
/// Uses raw Docker commands to ensure container persists across test processes.
/// </summary>
/// <remarks>
/// <para>
/// This container uses a fixed name ("whizbang-test-postgres") and checks for an existing
/// running container before creating a new one. This allows multiple test projects running
/// in parallel to share the same container without race conditions.
/// </para>
/// <para>
/// The container persists after tests complete - it will be reused on subsequent runs.
/// To remove it: <c>docker rm -f whizbang-test-postgres</c>
/// </para>
/// <para>
/// Usage:
/// <code>
/// [Before(Test)]
/// public async Task SetupAsync() {
///   await SharedPostgresContainer.InitializeAsync();
///   var connectionString = SharedPostgresContainer.ConnectionString;
/// }
/// </code>
/// </para>
/// </remarks>
public static class SharedPostgresContainer {
  private const string CONTAINER_NAME = "whizbang-test-postgres";
  private const string IMAGE_NAME = "postgres:17-alpine";
  private const string USERNAME = "whizbang_user";
  private const string PASSWORD = "whizbang_pass";
  private const string DATABASE = "whizbang_test";
  private const int CONTAINER_PORT = 5432;

  private static readonly SemaphoreSlim _initLock = new(1, 1);
  private static string? _connectionString;
  private static bool _initialized;
  private static bool _initializationFailed;
  private static Exception? _lastInitializationError;

  /// <summary>
  /// Gets the shared PostgreSQL connection string (base database).
  /// For test isolation, use <see cref="GetPerTestDatabaseConnectionString"/> instead.
  /// </summary>
  /// <exception cref="InvalidOperationException">Thrown if container is not initialized.</exception>
  public static string ConnectionString =>
    _connectionString ?? throw new InvalidOperationException("Shared PostgreSQL not initialized. Call InitializeAsync() first.");

  /// <summary>
  /// Gets whether the container has been successfully initialized.
  /// </summary>
  public static bool IsInitialized => _initialized;

  /// <summary>
  /// Initializes the shared PostgreSQL container.
  /// Safe to call multiple times - will only initialize once.
  /// Reuses existing container if one is already running.
  /// </summary>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <exception cref="InvalidOperationException">Thrown if initialization fails or has previously failed.</exception>
  public static async Task InitializeAsync(CancellationToken cancellationToken = default) {
    // If already initialized successfully, verify the connection is still valid
    if (_initialized) {
      try {
        // Quick health check - verify we can still connect
        await using var connection = new NpgsqlConnection(_connectionString);
        using var healthCheckCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await connection.OpenAsync(healthCheckCts.Token);
        return; // Connection still works, we're good
      } catch {
        // Connection failed - container may have been removed
        // Reset state and reinitialize
        Console.WriteLine("[SharedPostgresContainer] Existing connection failed, reinitializing...");
        _initialized = false;
        _connectionString = null;
      }
    }

    // If previous initialization failed, reset and try again (container may have been fixed)
    if (_initializationFailed) {
      Console.WriteLine("[SharedPostgresContainer] Previous initialization failed, retrying...");
      _initializationFailed = false;
      _lastInitializationError = null;
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

      Console.WriteLine("================================================================================");
      Console.WriteLine("[SharedPostgresContainer] Initializing shared PostgreSQL container...");
      Console.WriteLine("================================================================================");

      try {
        // Retry loop to handle race conditions with parallel test processes
        const int MAX_RETRIES = 3;
        for (var attempt = 1; attempt <= MAX_RETRIES; attempt++) {
          // Check if container already exists and is running
          var existingPort = await _getExistingContainerPortAsync(ct);
          if (existingPort.HasValue) {
            Console.WriteLine($"[SharedPostgresContainer] Found existing container '{CONTAINER_NAME}' on port {existingPort.Value}");
            _connectionString = $"Host=localhost;Port={existingPort.Value};Database={DATABASE};Username={USERNAME};Password={PASSWORD}";

            // Verify connection works
            await _verifyConnectionAsync(ct);

            Console.WriteLine("================================================================================");
            Console.WriteLine("[SharedPostgresContainer] Reusing existing PostgreSQL container!");
            Console.WriteLine($"[SharedPostgresContainer] Connection: {_connectionString}");
            Console.WriteLine("================================================================================");
            break;
          }

          // Try to create new container using raw Docker command
          Console.WriteLine($"[SharedPostgresContainer] No existing container found, creating '{CONTAINER_NAME}'... (attempt {attempt}/{MAX_RETRIES})");

          try {
            var hostPort = await _createContainerWithDockerAsync(ct);
            _connectionString = $"Host=localhost;Port={hostPort};Database={DATABASE};Username={USERNAME};Password={PASSWORD}";

            // Verify connection works
            await _verifyConnectionAsync(ct);

            Console.WriteLine("================================================================================");
            Console.WriteLine("[SharedPostgresContainer] PostgreSQL container ready!");
            Console.WriteLine($"[SharedPostgresContainer] Connection: {_connectionString}");
            Console.WriteLine("================================================================================");
            break;
          } catch (Exception ex) when (attempt < MAX_RETRIES && ex.Message.Contains("Conflict")) {
            // Another process created the container - wait and retry detection
            Console.WriteLine("[SharedPostgresContainer] Container was created by another process, retrying detection...");
            await Task.Delay(2000, ct);
          }
        }

        _initialized = true;
      } catch (Exception ex) {
        // Mark initialization as failed
        _initializationFailed = true;
        _lastInitializationError = ex;

        Console.WriteLine("================================================================================");
        Console.WriteLine($"[SharedPostgresContainer] Initialization FAILED: {ex.Message}");
        Console.WriteLine("================================================================================");

        throw new InvalidOperationException(
          "Failed to initialize shared PostgreSQL container. " +
          $"Error: {ex.Message}. " +
          "This is a fatal error - remaining tests will be skipped.",
          ex
        );
      }
    } finally {
      _initLock.Release();
    }
  }

  /// <summary>
  /// Creates the container using raw Docker CLI command (not Testcontainers library).
  /// This ensures the container persists across test processes.
  /// </summary>
  private static async Task<int> _createContainerWithDockerAsync(CancellationToken ct) {
    // Use docker run with --detach and --publish to create a persistent container
    var psi = new ProcessStartInfo {
      FileName = "docker",
      Arguments = $"run --detach --name {CONTAINER_NAME} " +
                  $"-e POSTGRES_USER={USERNAME} " +
                  $"-e POSTGRES_PASSWORD={PASSWORD} " +
                  $"-e POSTGRES_DB={DATABASE} " +
                  $"--publish 0:{CONTAINER_PORT} " +
                  "--restart no " +
                  $"{IMAGE_NAME} " +
                  "-c max_connections=500",
      RedirectStandardOutput = true,
      RedirectStandardError = true,
      UseShellExecute = false,
      CreateNoWindow = true
    };

    using var process = Process.Start(psi)
      ?? throw new InvalidOperationException("Failed to start docker process");

    var stdOut = await process.StandardOutput.ReadToEndAsync(ct);
    var stdErr = await process.StandardError.ReadToEndAsync(ct);
    await process.WaitForExitAsync(ct);

    if (process.ExitCode != 0) {
      throw new InvalidOperationException($"Docker run failed: {stdErr}");
    }

    Console.WriteLine($"[SharedPostgresContainer] Container created with ID: {stdOut.Trim()[..12]}");

    // Wait for container to be ready and get the port
    await Task.Delay(2000, ct);

    var port = await _getPortAsync(ct);
    if (!port.HasValue) {
      throw new InvalidOperationException("Failed to get container port after creation");
    }

    return port.Value;
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
  /// Checks if a container with the expected name exists and is running.
  /// If it exists but is stopped, starts it first.
  /// Returns the mapped host port if found, null otherwise.
  /// </summary>
  private static async Task<int?> _getExistingContainerPortAsync(CancellationToken ct) {
    try {
      // First check if container exists (running or stopped)
      var state = await _getContainerStateAsync(ct);
      if (state == null) {
        return null; // Container doesn't exist
      }

      // If container exists but isn't running, start it
      if (state != "running") {
        Console.WriteLine($"[SharedPostgresContainer] Found stopped container '{CONTAINER_NAME}' (state: {state}), starting it...");
        await _startContainerAsync(ct);
        // Wait a moment for container to be ready
        await Task.Delay(2000, ct);
      }

      // Now get the port
      return await _getPortAsync(ct);
    } catch {
      return null;
    }
  }

  private static async Task<string?> _getContainerStateAsync(CancellationToken ct) {
    var psi = new ProcessStartInfo {
      FileName = "docker",
      Arguments = $"inspect --format={{{{.State.Status}}}} {CONTAINER_NAME}",
      RedirectStandardOutput = true,
      RedirectStandardError = true,
      UseShellExecute = false,
      CreateNoWindow = true
    };

    using var process = Process.Start(psi);
    if (process == null) {
      return null;
    }

    var output = await process.StandardOutput.ReadToEndAsync(ct);
    await process.WaitForExitAsync(ct);

    if (process.ExitCode != 0) {
      return null; // Container doesn't exist
    }

    return output.Trim();
  }

  private static async Task _startContainerAsync(CancellationToken ct) {
    var psi = new ProcessStartInfo {
      FileName = "docker",
      Arguments = $"start {CONTAINER_NAME}",
      RedirectStandardOutput = true,
      RedirectStandardError = true,
      UseShellExecute = false,
      CreateNoWindow = true
    };

    using var process = Process.Start(psi);
    if (process != null) {
      await process.WaitForExitAsync(ct);
    }
  }

  private static async Task<int?> _getPortAsync(CancellationToken ct) {
    var psi = new ProcessStartInfo {
      FileName = "docker",
      Arguments = $"port {CONTAINER_NAME} {CONTAINER_PORT}",
      RedirectStandardOutput = true,
      RedirectStandardError = true,
      UseShellExecute = false,
      CreateNoWindow = true
    };

    using var process = Process.Start(psi);
    if (process == null) {
      return null;
    }

    var output = await process.StandardOutput.ReadToEndAsync(ct);
    await process.WaitForExitAsync(ct);

    if (process.ExitCode != 0) {
      return null;
    }

    // Output is like "0.0.0.0:54321" or "[::]:54321"
    var parts = output.Trim().Split(':');
    if (parts.Length >= 2 && int.TryParse(parts[^1], out var port)) {
      return port;
    }

    return null;
  }

  /// <summary>
  /// Verifies that the connection string works by attempting to connect.
  /// </summary>
  private static async Task _verifyConnectionAsync(CancellationToken ct) {
    using var retryTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
    using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, retryTimeout.Token);

    Exception? lastException = null;
    for (var attempt = 1; attempt <= 15; attempt++) {
      try {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(linked.Token);
        Console.WriteLine($"[SharedPostgresContainer] Connection verified on attempt {attempt}");
        return; // Success
      } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
        throw; // Propagate if main token cancelled
      } catch (Exception ex) {
        lastException = ex;
        Console.WriteLine($"[SharedPostgresContainer] Connection attempt {attempt} failed: {ex.Message}");
      }

      if (attempt < 15) {
        await Task.Delay(2000, linked.Token);
      }
    }

    throw new InvalidOperationException(
      $"Could not verify connection to PostgreSQL container after 15 attempts. Last error: {lastException?.Message}",
      lastException);
  }

  /// <summary>
  /// Resets local state for testing purposes.
  /// Note: Container is NEVER disposed - it persists for reuse across test processes.
  /// To stop the container: docker rm -f whizbang-test-postgres
  /// </summary>
  public static Task DisposeAsync() {
    // NEVER dispose the container - we want it to persist across test processes
    // The container persists until explicitly removed: docker rm -f whizbang-test-postgres
    Console.WriteLine("[SharedPostgresContainer] Keeping container running for reuse");

    // Reset state to allow reinitialization if needed
    _connectionString = null;
    _initialized = false;
    _initializationFailed = false;
    _lastInitializationError = null;

    return Task.CompletedTask;
  }
}
