using System.Diagnostics;
using Azure.Messaging.ServiceBus;

namespace ECommerce.Integration.Tests.Fixtures;

/// <summary>
/// Manages a single ServiceBus emulator instance for test execution.
/// All tests share the same 2 topics (topic-00 and topic-01).
/// Message draining provides isolation between tests and test runs.
/// The emulator is warmed up before tests run to eliminate cold-start delays.
/// </summary>
public sealed class ServiceBusBatchFixture : IAsyncDisposable {
  private DirectServiceBusEmulatorFixture? _emulator;
  private readonly int _batchIndex;
  private readonly int _basePort;
  private ServiceBusClient? _externalSharedClient;  // Client provided externally (not owned by this fixture)

  /// <summary>
  /// Creates a fixture for the single ServiceBus emulator.
  /// </summary>
  /// <param name="batchIndex">Zero-based batch index (always 0 for single emulator)</param>
  public ServiceBusBatchFixture(int batchIndex) {
    _batchIndex = batchIndex;
    _basePort = 5672;  // Always port 5672 - single emulator
  }

  /// <summary>
  /// Gets the Service Bus connection string for this batch's emulator instance.
  /// </summary>
  public string ConnectionString => _emulator?.ServiceBusConnectionString
    ?? throw new InvalidOperationException("Emulator not initialized. Call InitializeAsync() first.");

  /// <summary>
  /// Initializes ONLY the emulator instance (no warmup).
  /// Call WarmupWithClientAsync() after creating a shared ServiceBusClient.
  /// </summary>
  /// <param name="cancellationToken">Cancellation token.</param>
  public async Task InitializeEmulatorAsync(CancellationToken cancellationToken = default) {
    Console.WriteLine($"[Batch {_batchIndex}] Starting ServiceBus emulator on port {_basePort}...");

    // Create emulator using generic topic config
    _emulator = new DirectServiceBusEmulatorFixture(_basePort, "Config-Named.json");
    await _emulator.InitializeAsync(cancellationToken);

    Console.WriteLine($"[Batch {_batchIndex}] Emulator started (warmup not yet performed).");
  }

  /// <summary>
  /// Warms up the emulator using the provided shared ServiceBusClient.
  /// Must be called after InitializeEmulatorAsync().
  /// </summary>
  /// <param name="sharedClient">The shared ServiceBusClient to use for warmup.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  public async Task WarmupWithClientAsync(ServiceBusClient sharedClient, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(sharedClient);

    if (_emulator == null) {
      throw new InvalidOperationException("Emulator not initialized. Call InitializeEmulatorAsync() first.");
    }

    _externalSharedClient = sharedClient;
    Console.WriteLine($"[Batch {_batchIndex}] Using provided shared ServiceBusClient for warmup");

    await WarmupAsync(cancellationToken);

    Console.WriteLine($"[Batch {_batchIndex}] Ready! Emulator warmed up.");
  }

  /// <summary>
  /// Initializes the emulator and warms it up (backwards compatibility).
  /// Creates a temporary ServiceBusClient for warmup if none provided.
  /// </summary>
  /// <param name="sharedClient">Optional shared ServiceBusClient to use for warmup.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  [Obsolete("Use InitializeEmulatorAsync() followed by WarmupWithClientAsync() instead.")]
  public async Task InitializeAsync(ServiceBusClient? sharedClient = null, CancellationToken cancellationToken = default) {
    await InitializeEmulatorAsync(cancellationToken);

    if (sharedClient != null) {
      await WarmupWithClientAsync(sharedClient, cancellationToken);
    } else {
      // Create temporary client for warmup
      Console.WriteLine($"[Batch {_batchIndex}] No shared client provided, creating temporary one for warmup");
      await WarmupAsync(cancellationToken);
    }
  }

  /// <summary>
  /// Warms up the topics defined in Config-Named.json (products, inventory) by sending test messages.
  /// This eliminates cold-start delays during actual test execution.
  /// Uses the external shared ServiceBusClient if provided, otherwise creates a temporary one.
  /// </summary>
  private async Task WarmupAsync(CancellationToken cancellationToken = default) {
    var sw = Stopwatch.StartNew();

    ServiceBusClient clientToUse;
    ServiceBusClient? temporaryClient = null;
    bool usingTemporaryClient = false;

    if (_externalSharedClient != null) {
      clientToUse = _externalSharedClient;
    } else {
      // Create temporary client for warmup (will be disposed after warmup)
      temporaryClient = new ServiceBusClient(ConnectionString);
      clientToUse = temporaryClient;
      usingTemporaryClient = true;
    }

    try {
      // Send warmup messages to topics defined in config
      var warmupTasks = new[] { "products", "inventory" }.Select(async topicName => {
        await SendWarmupMessageAsync(clientToUse, topicName, cancellationToken);
      });

      await Task.WhenAll(warmupTasks);

      Console.WriteLine($"[Batch {_batchIndex}] Warmup completed in {sw.Elapsed.TotalSeconds:F1}s" +
                       (usingTemporaryClient ? " (using temporary client)" : " (using shared client)"));
    } finally {
      // Only dispose if we created a temporary client
      if (temporaryClient != null) {
        await temporaryClient.DisposeAsync();
      }
    }
  }

  /// <summary>
  /// Sends a single warmup message to a topic to initialize it.
  /// </summary>
  private static async Task SendWarmupMessageAsync(
    ServiceBusClient client,
    string topicName,
    CancellationToken cancellationToken = default
  ) {
    var sender = client.CreateSender(topicName);

    try {
      var message = new ServiceBusMessage($"{{\"warmup\":true,\"topic\":\"{topicName}\"}}") {
        MessageId = Guid.NewGuid().ToString(),
        ContentType = "application/json"
      };

      await sender.SendMessageAsync(message, cancellationToken);
      Console.WriteLine($"  [Warmup] ✓ {topicName}");
    } catch (Exception ex) {
      Console.WriteLine($"  [Warmup] ✗ {topicName}: {ex.Message}");
      throw;
    } finally {
      await sender.DisposeAsync();
    }
  }

  /// <summary>
  /// Disposes the emulator instance. Does NOT dispose the external shared client (not owned by this fixture).
  /// </summary>
  public async ValueTask DisposeAsync() {
    // Note: We don't dispose _externalSharedClient because we don't own it
    // It will be disposed by SharedFixtureSource

    // Dispose emulator
    if (_emulator != null) {
      Console.WriteLine($"[Batch {_batchIndex}] Disposing emulator instance...");
      await _emulator.DisposeAsync();
      _emulator = null;
    }
  }
}
