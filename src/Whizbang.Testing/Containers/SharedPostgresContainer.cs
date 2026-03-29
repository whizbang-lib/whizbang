using System.Collections.Concurrent;
using System.Diagnostics;

using Npgsql;
using TUnit.Core.Exceptions;

namespace Whizbang.Testing.Containers;

/// <summary>
/// Provides per-project PostgreSQL containers for test isolation.
/// Each test assembly gets its own container, eliminating connection contention
/// when test projects run in parallel.
/// </summary>
/// <remarks>
/// <para>
/// Container names are derived from the calling assembly:
/// <c>whizbang-test-postgres-{assembly-suffix}</c>. Each container uses a
/// random host port via <c>--publish 0:5432</c>.
/// </para>
/// <para>
/// Containers persist after tests complete for reuse on subsequent runs.
/// To remove all: <c>docker rm -f $(docker ps -a --filter "name=whizbang-test-postgres-" -q)</c>
/// </para>
/// </remarks>
public static class SharedPostgresContainer {
  private const string CONTAINER_PREFIX = "whizbang-test-postgres";
  private const string IMAGE_NAME = "pgvector/pgvector:pg17";
  private const string USERNAME = "whizbang_user";
  private const string PASSWORD = "whizbang_pass";
  private const string DATABASE = "whizbang_test";
  private const int CONTAINER_PORT = 5432;

  /// <summary>
  /// Per-container state, keyed by container name.
  /// Each test assembly gets its own entry.
  /// </summary>
  private static readonly ConcurrentDictionary<string, ContainerState> _containers = new();

#pragma warning disable CA1001 // ContainerState lives for process lifetime in static dictionary; disposal unnecessary
  private sealed class ContainerState {
#pragma warning restore CA1001
    public readonly SemaphoreSlim InitLock = new(1, 1);
    public string? ConnectionString;
    public bool Initialized;
    public bool InitializationFailed;
#pragma warning disable S4487 // Written for diagnostics; available in debugger during test failures
    public Exception? LastInitializationError;
#pragma warning restore S4487
  }

  /// <summary>
  /// Gets the container name for the calling assembly.
  /// </summary>
  private static string _getContainerName() {
    // Walk the call stack to find the test assembly (not Whizbang.Testing itself)
    var testingAssemblyName = typeof(SharedPostgresContainer).Assembly.GetName().Name;
    var callingAssembly = System.Reflection.Assembly.GetCallingAssembly();

    // If the caller is this assembly, walk further up the stack
    if (callingAssembly.GetName().Name == testingAssemblyName) {
      callingAssembly = System.Reflection.Assembly.GetEntryAssembly() ?? callingAssembly;
    }

    var assemblyName = callingAssembly.GetName().Name ?? "unknown";
    // Create a short, Docker-safe suffix from the assembly name
    var suffix = assemblyName
      .Replace("Whizbang.", "")
      .Replace(".Tests", "")
      .Replace(".", "-")
      .ToLowerInvariant();

    return $"{CONTAINER_PREFIX}-{suffix}";
  }

  /// <summary>
  /// Gets the container name for a specific assembly (used by callers who know their assembly).
  /// </summary>
  public static string GetContainerName(System.Reflection.Assembly assembly) {
    var assemblyName = assembly.GetName().Name ?? "unknown";
    var suffix = assemblyName
      .Replace("Whizbang.", "")
      .Replace(".Tests", "")
      .Replace(".", "-")
      .ToLowerInvariant();

    return $"{CONTAINER_PREFIX}-{suffix}";
  }

  /// <summary>
  /// Gets the shared PostgreSQL connection string (base database).
  /// Uses the entry assembly to determine which container to connect to.
  /// </summary>
  /// <exception cref="InvalidOperationException">Thrown if container is not initialized.</exception>
  public static string ConnectionString {
    get {
      var containerName = _resolveContainerName();
      var state = _containers.GetValueOrDefault(containerName);
      return state?.ConnectionString
        ?? throw new InvalidOperationException(
          $"Shared PostgreSQL not initialized for '{containerName}'. Call InitializeAsync() first.");
    }
  }

  /// <summary>
  /// Gets whether the container has been successfully initialized.
  /// </summary>
  public static bool IsInitialized {
    get {
      var containerName = _resolveContainerName();
      return _containers.TryGetValue(containerName, out var state) && state.Initialized;
    }
  }

  /// <summary>
  /// Resolves the container name from the entry assembly (most reliable for static property access).
  /// </summary>
  private static string _resolveContainerName() {
    var assembly = System.Reflection.Assembly.GetEntryAssembly() ?? System.Reflection.Assembly.GetCallingAssembly();
    return GetContainerName(assembly);
  }

  /// <summary>
  /// Attempts to initialize the shared PostgreSQL container.
  /// Returns true if initialization succeeds, false if Docker or PostgreSQL is not available.
  /// </summary>
  public static async Task<bool> TryInitializeAsync(CancellationToken cancellationToken = default) {
    var containerName = _resolveContainerName();
    var state = _containers.GetOrAdd(containerName, _ => new ContainerState());

    if (state.Initialized) {
      return true;
    }

    if (!await SharedRabbitMqContainer.IsDockerAvailableAsync(cancellationToken)) {
      Console.WriteLine($"[SharedPostgresContainer] Docker is not available - skipping container initialization ({containerName})");
      return false;
    }

    try {
      await _initializeCoreAsync(containerName, state, cancellationToken);
      return true;
    } catch (Exception ex) when (ex is not OperationCanceledException) {
      Console.WriteLine($"[SharedPostgresContainer] Container initialization failed ({containerName}): {ex.Message}");
      return false;
    }
  }

  /// <summary>
  /// Initializes the shared PostgreSQL container, or skips the test if Docker/PostgreSQL is not available.
  /// </summary>
  public static async Task InitializeOrSkipAsync(CancellationToken cancellationToken = default) {
    if (!await TryInitializeAsync(cancellationToken)) {
      throw new SkipTestException("PostgreSQL container is not available (Docker may not be running)");
    }
  }

  /// <summary>
  /// Initializes the shared PostgreSQL container.
  /// Safe to call multiple times - will only initialize once per container.
  /// </summary>
  public static async Task InitializeAsync(CancellationToken cancellationToken = default) {
    var containerName = _resolveContainerName();
    var state = _containers.GetOrAdd(containerName, _ => new ContainerState());
    await _initializeCoreAsync(containerName, state, cancellationToken);
  }

  private static async Task _initializeCoreAsync(string containerName, ContainerState state, CancellationToken cancellationToken) {
    // If already initialized successfully, verify the connection is still valid
    if (state.Initialized) {
      try {
        await using var connection = new NpgsqlConnection(state.ConnectionString);
        using var healthCheckCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await connection.OpenAsync(healthCheckCts.Token);
        return;
      } catch {
        Console.WriteLine($"[SharedPostgresContainer] Existing connection failed for '{containerName}', reinitializing...");
        state.Initialized = false;
        state.ConnectionString = null;
      }
    }

    if (state.InitializationFailed) {
      Console.WriteLine($"[SharedPostgresContainer] Previous initialization failed for '{containerName}', retrying...");
      state.InitializationFailed = false;
      state.LastInitializationError = null;
    }

    using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
    using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
    var ct = linkedCts.Token;

    await state.InitLock.WaitAsync(ct);
    try {
      if (state.Initialized) {
        return;
      }

      Console.WriteLine("================================================================================");
      Console.WriteLine($"[SharedPostgresContainer] Initializing container '{containerName}'...");
      Console.WriteLine("================================================================================");

      try {
        const int MAX_RETRIES = 3;
        for (var attempt = 1; attempt <= MAX_RETRIES; attempt++) {
          var existingPort = await _getExistingContainerPortAsync(containerName, ct);
          if (existingPort.HasValue) {
            Console.WriteLine($"[SharedPostgresContainer] Found existing container '{containerName}' on port {existingPort.Value}");
            state.ConnectionString = $"Host=localhost;Port={existingPort.Value};Database={DATABASE};Username={USERNAME};Password={PASSWORD}";
            await _verifyConnectionAsync(containerName, state.ConnectionString, ct);

            Console.WriteLine("================================================================================");
            Console.WriteLine($"[SharedPostgresContainer] Reusing existing container '{containerName}'!");
            Console.WriteLine($"[SharedPostgresContainer] Connection: {state.ConnectionString}");
            Console.WriteLine("================================================================================");
            break;
          }

          Console.WriteLine($"[SharedPostgresContainer] Creating '{containerName}'... (attempt {attempt}/{MAX_RETRIES})");

          try {
            var hostPort = await _createContainerWithDockerAsync(containerName, ct);
            state.ConnectionString = $"Host=localhost;Port={hostPort};Database={DATABASE};Username={USERNAME};Password={PASSWORD}";
            await _verifyConnectionAsync(containerName, state.ConnectionString, ct);

            Console.WriteLine("================================================================================");
            Console.WriteLine($"[SharedPostgresContainer] Container '{containerName}' ready!");
            Console.WriteLine($"[SharedPostgresContainer] Connection: {state.ConnectionString}");
            Console.WriteLine("================================================================================");
            break;
          } catch (Exception ex) when (attempt < MAX_RETRIES && ex.Message.Contains("Conflict")) {
            Console.WriteLine($"[SharedPostgresContainer] Container '{containerName}' was created by another process, retrying...");
            await Task.Delay(2000, ct);
          }
        }

        state.Initialized = true;
      } catch (Exception ex) {
        state.InitializationFailed = true;
        state.LastInitializationError = ex;

        Console.WriteLine("================================================================================");
        Console.WriteLine($"[SharedPostgresContainer] Initialization FAILED for '{containerName}': {ex.Message}");
        Console.WriteLine("================================================================================");

        throw new InvalidOperationException(
          $"Failed to initialize PostgreSQL container '{containerName}'. Error: {ex.Message}.",
          ex);
      }
    } finally {
      state.InitLock.Release();
    }
  }

  private static async Task<int> _createContainerWithDockerAsync(string containerName, CancellationToken ct) {
    var psi = new ProcessStartInfo {
      FileName = "docker",
      Arguments = $"run --detach --name {containerName} " +
                  $"-e POSTGRES_USER={USERNAME} " +
                  $"-e POSTGRES_PASSWORD={PASSWORD} " +
                  $"-e POSTGRES_DB={DATABASE} " +
                  $"--publish 0:{CONTAINER_PORT} " +
                  "--restart no " +
                  $"{IMAGE_NAME} " +
                  "-c max_connections=200",
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
      throw new InvalidOperationException($"Docker run failed for '{containerName}': {stdErr}");
    }

    Console.WriteLine($"[SharedPostgresContainer] Container '{containerName}' created with ID: {stdOut.Trim()[..12]}");

    await Task.Delay(2000, ct);

    var port = await _getPortAsync(containerName, ct);
    if (!port.HasValue) {
      throw new InvalidOperationException($"Failed to get port for container '{containerName}' after creation");
    }

    return port.Value;
  }

  /// <summary>
  /// Creates a unique database connection string for a specific test.
  /// Each test gets its own database for complete isolation.
  /// </summary>
  public static string GetPerTestDatabaseConnectionString() {
    var connectionString = ConnectionString; // Throws if not initialized

    var dbName = $"test_{Guid.NewGuid():N}";
    var builder = new NpgsqlConnectionStringBuilder(connectionString) {
      Database = dbName,
      MinPoolSize = 0,
      MaxPoolSize = 2,
      ConnectionIdleLifetime = 5,
      ConnectionPruningInterval = 3
    };

    return builder.ConnectionString;
  }

  private static async Task<int?> _getExistingContainerPortAsync(string containerName, CancellationToken ct) {
    try {
      var state = await _getContainerStateAsync(containerName, ct);
      if (state == null) {
        return null;
      }

      if (state != "running") {
        Console.WriteLine($"[SharedPostgresContainer] Found stopped container '{containerName}' (state: {state}), starting it...");
        await _startContainerAsync(containerName, ct);
        await Task.Delay(2000, ct);
      }

      return await _getPortAsync(containerName, ct);
    } catch {
      return null;
    }
  }

  private static async Task<string?> _getContainerStateAsync(string containerName, CancellationToken ct) {
    var psi = new ProcessStartInfo {
      FileName = "docker",
      Arguments = $"inspect --format={{{{.State.Status}}}} {containerName}",
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

    return process.ExitCode != 0 ? null : output.Trim();
  }

  private static async Task _startContainerAsync(string containerName, CancellationToken ct) {
    var psi = new ProcessStartInfo {
      FileName = "docker",
      Arguments = $"start {containerName}",
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

  private static async Task<int?> _getPortAsync(string containerName, CancellationToken ct) {
    var psi = new ProcessStartInfo {
      FileName = "docker",
      Arguments = $"port {containerName} {CONTAINER_PORT}",
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

    var parts = output.Trim().Split(':');
    if (parts.Length >= 2 && int.TryParse(parts[^1], out var port)) {
      return port;
    }

    return null;
  }

  private static async Task _verifyConnectionAsync(string containerName, string connectionString, CancellationToken ct) {
    using var retryTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
    using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, retryTimeout.Token);

    Exception? lastException = null;
    for (var attempt = 1; attempt <= 15; attempt++) {
      try {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(linked.Token);
        Console.WriteLine($"[SharedPostgresContainer] Connection verified for '{containerName}' on attempt {attempt}");
        return;
      } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
        throw;
      } catch (Exception ex) {
        lastException = ex;
        Console.WriteLine($"[SharedPostgresContainer] Connection attempt {attempt} failed for '{containerName}': {ex.Message}");
      }

      if (attempt < 15) {
        await Task.Delay(2000, linked.Token);
      }
    }

    throw new InvalidOperationException(
      $"Could not verify connection to PostgreSQL container '{containerName}' after 15 attempts. Last error: {lastException?.Message}",
      lastException);
  }

  /// <summary>
  /// Resets local state for testing purposes.
  /// Containers persist for reuse — clean up with:
  /// <c>docker rm -f $(docker ps -a --filter "name=whizbang-test-postgres-" -q)</c>
  /// </summary>
  public static Task DisposeAsync() {
    var containerName = _resolveContainerName();
    Console.WriteLine($"[SharedPostgresContainer] Keeping container '{containerName}' running for reuse");

    if (_containers.TryGetValue(containerName, out var state)) {
      state.ConnectionString = null;
      state.Initialized = false;
      state.InitializationFailed = false;
      state.LastInitializationError = null;
    }

    return Task.CompletedTask;
  }
}
