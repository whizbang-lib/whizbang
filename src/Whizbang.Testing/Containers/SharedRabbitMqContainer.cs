using Testcontainers.RabbitMq;

namespace Whizbang.Testing.Containers;

/// <summary>
/// Provides a shared RabbitMQ container for all tests.
/// Tests should use this instead of creating their own containers to avoid timeout issues
/// caused by multiple container startups.
/// </summary>
/// <remarks>
/// Usage:
/// <code>
/// [Before(Test)]
/// public async Task SetupAsync() {
///   await SharedRabbitMqContainer.InitializeAsync();
///   var connectionString = SharedRabbitMqContainer.ConnectionString;
///   // ... use connectionString to create connections
/// }
/// </code>
/// </remarks>
public static class SharedRabbitMqContainer {
  private static readonly SemaphoreSlim _initLock = new(1, 1);
  private static RabbitMqContainer? _container;
  private static bool _initialized;
  private static bool _initializationFailed;
  private static Exception? _lastInitializationError;

  /// <summary>
  /// Gets the shared RabbitMQ connection string.
  /// </summary>
  /// <exception cref="InvalidOperationException">Thrown if container is not initialized.</exception>
  public static string ConnectionString =>
    _container?.GetConnectionString() ?? throw new InvalidOperationException("Shared RabbitMQ not initialized. Call InitializeAsync() first.");

  /// <summary>
  /// Gets the RabbitMQ Management API URI (port 15672).
  /// </summary>
  /// <exception cref="InvalidOperationException">Thrown if container is not initialized.</exception>
  public static Uri ManagementApiUri {
    get {
      if (_container == null) {
        throw new InvalidOperationException("Shared RabbitMQ not initialized. Call InitializeAsync() first.");
      }
      return new Uri($"http://localhost:{_container.GetMappedPublicPort(15672)}");
    }
  }

  /// <summary>
  /// Gets whether the container has been successfully initialized.
  /// </summary>
  public static bool IsInitialized => _initialized;

  /// <summary>
  /// Initializes the shared RabbitMQ container.
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
        $"Shared RabbitMQ container initialization previously failed and cannot be retried. " +
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
          $"Shared RabbitMQ container initialization previously failed. Original error: {_lastInitializationError?.Message}",
          _lastInitializationError
        );
      }

      Console.WriteLine("================================================================================");
      Console.WriteLine("[SharedRabbitMqContainer] Initializing shared RabbitMQ container...");
      Console.WriteLine("================================================================================");

      try {
        _container = new RabbitMqBuilder()
          .WithImage("rabbitmq:3.13-management-alpine")
          .WithUsername("guest")
          .WithPassword("guest")
          .WithPortBinding(15672, true)  // Expose Management API port
          .Build();

        Console.WriteLine("[SharedRabbitMqContainer] Starting container (may take 10-15 seconds)...");
        await _container.StartAsync(ct);

        Console.WriteLine("================================================================================");
        Console.WriteLine("[SharedRabbitMqContainer] RabbitMQ container ready!");
        Console.WriteLine($"[SharedRabbitMqContainer] Connection: {ConnectionString}");
        Console.WriteLine($"[SharedRabbitMqContainer] Management API: {ManagementApiUri}");
        Console.WriteLine("================================================================================");

        _initialized = true;
      } catch (Exception ex) {
        // Mark initialization as failed to prevent retry loops
        _initializationFailed = true;
        _lastInitializationError = ex;

        Console.WriteLine("================================================================================");
        Console.WriteLine($"[SharedRabbitMqContainer] Initialization FAILED: {ex.Message}");
        Console.WriteLine("================================================================================");

        // Clean up partial initialization
        await _cleanupAfterFailureAsync();

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
  /// Cleans up resources after initialization failure.
  /// </summary>
  private static async Task _cleanupAfterFailureAsync() {
    try {
      if (_container != null) {
        await _container.DisposeAsync();
        _container = null;
        Console.WriteLine("[SharedRabbitMqContainer] Disposed container after failure");
      }
    } catch (Exception ex) {
      Console.WriteLine($"[SharedRabbitMqContainer] Warning: Error during cleanup: {ex.Message}");
    }
  }

  /// <summary>
  /// Final cleanup: disposes shared container when tests complete.
  /// </summary>
  public static async Task DisposeAsync() {
    if (_container != null) {
      await _container.DisposeAsync();
      _container = null;
      Console.WriteLine("[SharedRabbitMqContainer] Disposed shared RabbitMQ container");
    }

    // Reset state to allow reinitialization if needed
    _initialized = false;
    _initializationFailed = false;
    _lastInitializationError = null;
  }
}
