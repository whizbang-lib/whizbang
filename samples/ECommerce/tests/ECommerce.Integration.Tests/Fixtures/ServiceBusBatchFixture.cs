using System.Diagnostics;
using Azure.Messaging.ServiceBus;

namespace ECommerce.Integration.Tests.Fixtures;

/// <summary>
/// Manages ServiceBus emulator instances for batched test execution.
/// Each batch of 25 tests gets its own emulator instance on a unique port.
/// The emulator is warmed up before tests run to eliminate cold-start delays.
/// </summary>
public sealed class ServiceBusBatchFixture : IAsyncDisposable {
  private DirectServiceBusEmulatorFixture? _emulator;
  private readonly int _batchIndex;
  private readonly int _basePort;

  /// <summary>
  /// Creates a fixture for a specific batch of tests.
  /// </summary>
  /// <param name="batchIndex">Zero-based batch index (0 = tests 0-24, 1 = tests 25-49, etc.)</param>
  public ServiceBusBatchFixture(int batchIndex) {
    _batchIndex = batchIndex;
    _basePort = 5672 + batchIndex; // Port 5672, 5673, 5674, etc.
  }

  /// <summary>
  /// Gets the Service Bus connection string for this batch's emulator instance.
  /// </summary>
  public string ConnectionString => _emulator?.ServiceBusConnectionString
    ?? throw new InvalidOperationException("Emulator not initialized. Call InitializeAsync() first.");

  /// <summary>
  /// Initializes the emulator instance and warms up all topics.
  /// </summary>
  public async Task InitializeAsync(CancellationToken cancellationToken = default) {
    Console.WriteLine($"[Batch {_batchIndex}] Starting ServiceBus emulator on port {_basePort}...");

    // Create emulator with unique port
    _emulator = new DirectServiceBusEmulatorFixture(_basePort, "Config-TopicPool.json");
    await _emulator.InitializeAsync(cancellationToken);

    // Warmup all 50 topics
    await WarmupAsync(cancellationToken);

    Console.WriteLine($"[Batch {_batchIndex}] Ready! Emulator warmed up.");
  }

  /// <summary>
  /// Warms up all 50 topics (25 sets × 2 topics) by sending test messages.
  /// This eliminates cold-start delays during actual test execution.
  /// </summary>
  private async Task WarmupAsync(CancellationToken cancellationToken = default) {
    var sw = Stopwatch.StartNew();
    var client = new ServiceBusClient(ConnectionString);

    try {
      // Send warmup messages to all 50 topics (2 per test × 25 tests)
      var warmupTasks = Enumerable.Range(0, 25).Select(async i => {
        var suffix = i.ToString("D2");
        await SendWarmupMessageAsync(client, $"products-{suffix}", cancellationToken);
        await SendWarmupMessageAsync(client, $"inventory-{suffix}", cancellationToken);
      });

      await Task.WhenAll(warmupTasks);

      Console.WriteLine($"[Batch {_batchIndex}] Warmup completed in {sw.Elapsed.TotalSeconds:F1}s");
    } finally {
      await client.DisposeAsync();
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
    } finally {
      await sender.DisposeAsync();
    }
  }

  /// <summary>
  /// Disposes the emulator instance for this batch.
  /// </summary>
  public async ValueTask DisposeAsync() {
    if (_emulator != null) {
      Console.WriteLine($"[Batch {_batchIndex}] Disposing emulator instance...");
      await _emulator.DisposeAsync();
      _emulator = null;
    }
  }
}
