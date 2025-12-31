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
  private ServiceBusClient? _sharedServiceBusClient;  // Shared client for warmup operations

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
  /// Initializes the single emulator instance and warms up all topics.
  /// </summary>
  public async Task InitializeAsync(CancellationToken cancellationToken = default) {
    Console.WriteLine($"[Batch {_batchIndex}] Starting ServiceBus emulator on port {_basePort}...");

    // Create emulator using generic topic config
    _emulator = new DirectServiceBusEmulatorFixture(_basePort, "Config-Named.json");
    await _emulator.InitializeAsync(cancellationToken);

    // Create shared client for warmup and test operations
    _sharedServiceBusClient = new ServiceBusClient(ConnectionString);
    Console.WriteLine($"[Batch {_batchIndex}] Created shared ServiceBusClient");

    // Warmup all 20 generic topics
    await WarmupAsync(cancellationToken);

    Console.WriteLine($"[Batch {_batchIndex}] Ready! Emulator warmed up.");
  }

  /// <summary>
  /// Warms up the topics defined in Config-Named.json (products, inventory) by sending test messages.
  /// This eliminates cold-start delays during actual test execution.
  /// Uses the shared ServiceBusClient to avoid creating extra connections.
  /// </summary>
  private async Task WarmupAsync(CancellationToken cancellationToken = default) {
    var sw = Stopwatch.StartNew();

    if (_sharedServiceBusClient == null) {
      throw new InvalidOperationException("Shared ServiceBusClient not initialized");
    }

    // Send warmup messages to topics defined in config using shared client
    var warmupTasks = new[] { "products", "inventory" }.Select(async topicName => {
      await SendWarmupMessageAsync(_sharedServiceBusClient, topicName, cancellationToken);
    });

    await Task.WhenAll(warmupTasks);

    Console.WriteLine($"[Batch {_batchIndex}] Warmup completed in {sw.Elapsed.TotalSeconds:F1}s");
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
  /// Disposes the emulator instance and shared client.
  /// </summary>
  public async ValueTask DisposeAsync() {
    // Dispose shared client first
    if (_sharedServiceBusClient != null) {
      await _sharedServiceBusClient.DisposeAsync();
      _sharedServiceBusClient = null;
    }

    // Then dispose emulator
    if (_emulator != null) {
      Console.WriteLine($"[Batch {_batchIndex}] Disposing emulator instance...");
      await _emulator.DisposeAsync();
      _emulator = null;
    }
  }
}
