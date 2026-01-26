using System.Diagnostics;
using System.Net.Http;

namespace Whizbang.Testing.Containers;

/// <summary>
/// Provides a shared RabbitMQ container for all tests.
/// Uses raw Docker commands to ensure container persists across test processes.
/// </summary>
/// <remarks>
/// <para>
/// This container uses a fixed name ("whizbang-test-rabbitmq") and checks for an existing
/// running container before creating a new one. This allows multiple test projects running
/// in parallel to share the same container without race conditions.
/// </para>
/// <para>
/// The container persists after tests complete - it will be reused on subsequent runs.
/// To remove it: <c>docker rm -f whizbang-test-rabbitmq</c>
/// </para>
/// <para>
/// Usage:
/// <code>
/// [Before(Test)]
/// public async Task SetupAsync() {
///   await SharedRabbitMqContainer.InitializeAsync();
///   var connectionString = SharedRabbitMqContainer.ConnectionString;
/// }
/// </code>
/// </para>
/// </remarks>
public static class SharedRabbitMqContainer {
  private const string CONTAINER_NAME = "whizbang-test-rabbitmq";
  private const string IMAGE_NAME = "rabbitmq:3.13-management-alpine";
  private const string USERNAME = "guest";
  private const string PASSWORD = "guest";
  private const int AMQP_PORT = 5672;
  private const int MANAGEMENT_PORT = 15672;

  private static readonly SemaphoreSlim _initLock = new(1, 1);
  private static string? _connectionString;
  private static Uri? _managementApiUri;
  private static bool _initialized;
  private static bool _initializationFailed;
  private static Exception? _lastInitializationError;

  /// <summary>
  /// Gets the shared RabbitMQ connection string.
  /// </summary>
  /// <exception cref="InvalidOperationException">Thrown if container is not initialized.</exception>
  public static string ConnectionString =>
    _connectionString ?? throw new InvalidOperationException("Shared RabbitMQ not initialized. Call InitializeAsync() first.");

  /// <summary>
  /// Gets the RabbitMQ Management API URI (port 15672).
  /// </summary>
  /// <exception cref="InvalidOperationException">Thrown if container is not initialized.</exception>
  public static Uri ManagementApiUri =>
    _managementApiUri ?? throw new InvalidOperationException("Shared RabbitMQ not initialized. Call InitializeAsync() first.");

  /// <summary>
  /// Gets whether the container has been successfully initialized.
  /// </summary>
  public static bool IsInitialized => _initialized;

  /// <summary>
  /// Initializes the shared RabbitMQ container.
  /// Safe to call multiple times - will only initialize once.
  /// Reuses existing container if one is already running.
  /// </summary>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <exception cref="InvalidOperationException">Thrown if initialization fails or has previously failed.</exception>
  public static async Task InitializeAsync(CancellationToken cancellationToken = default) {
    // If already initialized successfully, verify the connection is still valid
    if (_initialized) {
      try {
        // Quick health check - verify we can still connect to management API
        using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var authBytes = System.Text.Encoding.ASCII.GetBytes($"{USERNAME}:{PASSWORD}");
        httpClient.DefaultRequestHeaders.Authorization =
          new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", Convert.ToBase64String(authBytes));
        var response = await httpClient.GetAsync($"{_managementApiUri}api/overview", cancellationToken);
        if (response.IsSuccessStatusCode) {
          return; // Connection still works, we're good
        }
      } catch {
        // Connection failed - container may have been removed
      }
      // Reset state and reinitialize
      Console.WriteLine("[SharedRabbitMqContainer] Existing connection failed, reinitializing...");
      _initialized = false;
      _connectionString = null;
      _managementApiUri = null;
    }

    // If previous initialization failed, reset and try again (container may have been fixed)
    if (_initializationFailed) {
      Console.WriteLine("[SharedRabbitMqContainer] Previous initialization failed, retrying...");
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
      Console.WriteLine("[SharedRabbitMqContainer] Initializing shared RabbitMQ container...");
      Console.WriteLine("================================================================================");

      try {
        // Retry loop to handle race conditions with parallel test processes
        const int MAX_RETRIES = 3;
        for (var attempt = 1; attempt <= MAX_RETRIES; attempt++) {
          // Check if container already exists and is running
          var existingPorts = await _getExistingContainerPortsAsync(ct);
          if (existingPorts.HasValue) {
            var (amqpPort, mgmtPort) = existingPorts.Value;
            Console.WriteLine($"[SharedRabbitMqContainer] Found existing container '{CONTAINER_NAME}' on ports {amqpPort}/{mgmtPort}");
            _connectionString = $"amqp://{USERNAME}:{PASSWORD}@localhost:{amqpPort}";
            _managementApiUri = new Uri($"http://localhost:{mgmtPort}");

            // Verify connection works
            await _verifyConnectionAsync(ct);

            Console.WriteLine("================================================================================");
            Console.WriteLine("[SharedRabbitMqContainer] Reusing existing RabbitMQ container!");
            Console.WriteLine($"[SharedRabbitMqContainer] Connection: {_connectionString}");
            Console.WriteLine($"[SharedRabbitMqContainer] Management API: {_managementApiUri}");
            Console.WriteLine("================================================================================");
            break;
          }

          // Try to create new container using raw Docker command
          Console.WriteLine($"[SharedRabbitMqContainer] No existing container found, creating '{CONTAINER_NAME}'... (attempt {attempt}/{MAX_RETRIES})");

          try {
            var (amqpPort, mgmtPort) = await _createContainerWithDockerAsync(ct);
            _connectionString = $"amqp://{USERNAME}:{PASSWORD}@localhost:{amqpPort}";
            _managementApiUri = new Uri($"http://localhost:{mgmtPort}");

            // Verify connection works
            await _verifyConnectionAsync(ct);

            Console.WriteLine("================================================================================");
            Console.WriteLine("[SharedRabbitMqContainer] RabbitMQ container ready!");
            Console.WriteLine($"[SharedRabbitMqContainer] Connection: {_connectionString}");
            Console.WriteLine($"[SharedRabbitMqContainer] Management API: {_managementApiUri}");
            Console.WriteLine("================================================================================");
            break;
          } catch (Exception ex) when (attempt < MAX_RETRIES && ex.Message.Contains("Conflict")) {
            // Another process created the container - wait and retry detection
            Console.WriteLine($"[SharedRabbitMqContainer] Container was created by another process, retrying detection...");
            await Task.Delay(3000, ct);
          }
        }

        _initialized = true;
      } catch (Exception ex) {
        // Mark initialization as failed
        _initializationFailed = true;
        _lastInitializationError = ex;

        Console.WriteLine("================================================================================");
        Console.WriteLine($"[SharedRabbitMqContainer] Initialization FAILED: {ex.Message}");
        Console.WriteLine("================================================================================");

        throw new InvalidOperationException(
          $"Failed to initialize shared RabbitMQ container. " +
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
  /// Creates the container using raw Docker CLI command (not Testcontainers library).
  /// This ensures the container persists across test processes.
  /// </summary>
  private static async Task<(int AmqpPort, int MgmtPort)> _createContainerWithDockerAsync(CancellationToken ct) {
    // Use docker run with --detach and --publish to create a persistent container
    var psi = new ProcessStartInfo {
      FileName = "docker",
      Arguments = $"run --detach --name {CONTAINER_NAME} " +
                  $"-e RABBITMQ_DEFAULT_USER={USERNAME} " +
                  $"-e RABBITMQ_DEFAULT_PASS={PASSWORD} " +
                  $"--publish 0:{AMQP_PORT} " +
                  $"--publish 0:{MANAGEMENT_PORT} " +
                  $"--restart no " +
                  $"{IMAGE_NAME}",
      RedirectStandardOutput = true,
      RedirectStandardError = true,
      UseShellExecute = false,
      CreateNoWindow = true
    };

    using var process = Process.Start(psi);
    if (process == null) {
      throw new InvalidOperationException("Failed to start docker process");
    }

    var stdOut = await process.StandardOutput.ReadToEndAsync(ct);
    var stdErr = await process.StandardError.ReadToEndAsync(ct);
    await process.WaitForExitAsync(ct);

    if (process.ExitCode != 0) {
      throw new InvalidOperationException($"Docker run failed: {stdErr}");
    }

    Console.WriteLine($"[SharedRabbitMqContainer] Container created with ID: {stdOut.Trim()[..12]}");

    // Wait for container to be ready and get the ports
    await Task.Delay(3000, ct);

    var amqpPort = await _getPortAsync(AMQP_PORT, ct);
    var mgmtPort = await _getPortAsync(MANAGEMENT_PORT, ct);

    if (!amqpPort.HasValue || !mgmtPort.HasValue) {
      throw new InvalidOperationException("Failed to get container ports after creation");
    }

    return (amqpPort.Value, mgmtPort.Value);
  }

  /// <summary>
  /// Checks if a container with the expected name exists and is running.
  /// If it exists but is stopped, starts it first.
  /// Returns the mapped host ports (AMQP, Management) if found, null otherwise.
  /// </summary>
  private static async Task<(int AmqpPort, int ManagementPort)?> _getExistingContainerPortsAsync(CancellationToken ct) {
    try {
      // First check if container exists (running or stopped)
      var state = await _getContainerStateAsync(ct);
      if (state == null) {
        return null; // Container doesn't exist
      }

      // If container exists but isn't running, start it
      if (state != "running") {
        Console.WriteLine($"[SharedRabbitMqContainer] Found stopped container '{CONTAINER_NAME}' (state: {state}), starting it...");
        await _startContainerAsync(ct);
        // Wait a moment for container to be ready
        await Task.Delay(3000, ct);
      }

      // Get AMQP port
      var amqpPort = await _getPortAsync(AMQP_PORT, ct);
      if (!amqpPort.HasValue) {
        return null;
      }

      // Get Management port
      var mgmtPort = await _getPortAsync(MANAGEMENT_PORT, ct);
      if (!mgmtPort.HasValue) {
        return null;
      }

      return (amqpPort.Value, mgmtPort.Value);
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

  private static async Task<int?> _getPortAsync(int containerPort, CancellationToken ct) {
    var psi = new ProcessStartInfo {
      FileName = "docker",
      Arguments = $"port {CONTAINER_NAME} {containerPort}",
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
  /// Verifies that the RabbitMQ container is accepting connections via management API.
  /// </summary>
  private static async Task _verifyConnectionAsync(CancellationToken ct) {
    using var retryTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
    using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, retryTimeout.Token);
    using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };

    // RabbitMQ management API requires authentication
    var authBytes = System.Text.Encoding.ASCII.GetBytes($"{USERNAME}:{PASSWORD}");
    httpClient.DefaultRequestHeaders.Authorization =
      new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", Convert.ToBase64String(authBytes));

    string? lastError = null;
    for (var attempt = 1; attempt <= 15; attempt++) {
      try {
        // Check management API overview endpoint (simpler than health check)
        var url = $"{_managementApiUri}api/overview";
        var response = await httpClient.GetAsync(url, linked.Token);
        if (response.IsSuccessStatusCode) {
          Console.WriteLine($"[SharedRabbitMqContainer] Connection verified on attempt {attempt}");
          return; // Success
        }
        lastError = $"HTTP {(int)response.StatusCode} from {url}";
        Console.WriteLine($"[SharedRabbitMqContainer] Connection attempt {attempt}: {lastError}");
      } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
        throw; // Propagate if main token cancelled
      } catch (Exception ex) {
        lastError = ex.Message;
        Console.WriteLine($"[SharedRabbitMqContainer] Connection attempt {attempt} failed: {lastError}");
      }

      if (attempt < 15) {
        await Task.Delay(2000, linked.Token);
      }
    }

    throw new InvalidOperationException(
      $"Could not verify connection to RabbitMQ container after 15 attempts. Last error: {lastError}");
  }

  /// <summary>
  /// Resets local state for testing purposes.
  /// Note: Container is NEVER disposed - it persists for reuse across test processes.
  /// To stop the container: docker rm -f whizbang-test-rabbitmq
  /// </summary>
  public static Task DisposeAsync() {
    // NEVER dispose the container - we want it to persist across test processes
    // The container persists until explicitly removed: docker rm -f whizbang-test-rabbitmq
    Console.WriteLine("[SharedRabbitMqContainer] Keeping container running for reuse");

    // Reset state to allow reinitialization if needed
    _connectionString = null;
    _managementApiUri = null;
    _initialized = false;
    _initializationFailed = false;
    _lastInitializationError = null;

    return Task.CompletedTask;
  }
}
