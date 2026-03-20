using System.Diagnostics;
using Azure.Messaging.ServiceBus;

namespace Whizbang.Transports.AzureServiceBus.Tests.Containers;

/// <summary>
/// Manages Azure Service Bus Emulator for integration tests.
/// Uses docker-compose to start both the emulator and required SQL Server instance.
/// </summary>
public sealed class ServiceBusEmulatorFixture : IAsyncDisposable {
  private readonly int _port;
  private readonly string _configFilePath;
  private readonly string _dockerComposeFile;
  private bool _isInitialized;
  private ServiceBusClient? _client;

  /// <summary>
  /// Known topic/subscription pairs configured in Config.json.
  /// Used for dead letter queue monitoring.
  /// </summary>
  private static readonly (string Topic, string Subscription)[] _knownSubscriptions = [
    ("topic-00", "sub-00-a"),
    ("topic-01", "sub-01-a")
  ];

  /// <summary>
  /// Creates a fixture with the default port (5672).
  /// </summary>
  public ServiceBusEmulatorFixture() : this(5672) {
  }

  /// <summary>
  /// Creates a fixture with a custom port.
  /// </summary>
  public ServiceBusEmulatorFixture(int port) {
    _port = port;
    _configFilePath = Path.Combine(AppContext.BaseDirectory, "Config.json");
    _dockerComposeFile = Path.Combine(Path.GetTempPath(), $"docker-compose-sb-test-{_port}.yml");
  }

  /// <summary>
  /// Gets the Service Bus connection string for the emulator.
  /// </summary>
  public string ConnectionString =>
    $"Endpoint=sb://localhost:{_port};SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true;";

  /// <summary>
  /// Gets the ServiceBusClient for the emulator.
  /// </summary>
  public ServiceBusClient Client =>
    _client ?? throw new InvalidOperationException("Call InitializeAsync() first.");

  /// <summary>
  /// Initializes the emulator by starting docker containers.
  /// </summary>
  public async Task InitializeAsync(CancellationToken cancellationToken = default) {
    if (_isInitialized) {
      return;
    }

    // Verify config file exists
    if (!File.Exists(_configFilePath)) {
      throw new FileNotFoundException(
        $"Config.json not found at: {_configFilePath}. Ensure the file is copied to output directory.",
        _configFilePath
      );
    }

    Console.WriteLine($"[ServiceBusEmulator] Starting Azure Service Bus Emulator with config: {_configFilePath}");

    // Generate docker-compose content (use consistent file path)
    var dockerComposeContent = _generateDockerComposeContent();
    await File.WriteAllTextAsync(_dockerComposeFile, dockerComposeContent, cancellationToken);

    try {
      // CRITICAL: Force cleanup any stale containers from previous test runs
      // This handles the case where tests were aborted without proper cleanup
      Console.WriteLine("[ServiceBusEmulator] Cleaning up any stale containers...");
      await _forceCleanupContainersAsync(cancellationToken);

      // Stop any existing containers on this port (via docker-compose with full cleanup)
      // Using -v to remove volumes and --remove-orphans to clean orphaned containers
      // Use explicit project name to ensure consistent network naming
      var projectName = $"sbtest{_port}";
      await _runDockerComposeAsyncIgnoreErrors($"-p {projectName} down -v --remove-orphans", _dockerComposeFile, cancellationToken);

      // Also remove the network explicitly in case docker-compose didn't
      await _removeNetworkAsync($"{projectName}_default", cancellationToken);

      // Give Docker a moment to fully release resources
      await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);

      // Start containers with explicit project name and --force-recreate
      await _runDockerComposeAsync($"-p {projectName} up -d --force-recreate", _dockerComposeFile, cancellationToken);

      // Wait for emulator to be ready
      Console.WriteLine("[ServiceBusEmulator] Waiting for emulator to be ready (up to 180 seconds)...");
      var containerName = $"whizbang-test-servicebus-{_port}";
      const int maxWaitSeconds = 180;
      const int pollIntervalSeconds = 5;
      var elapsed = 0;

      while (elapsed < maxWaitSeconds) {
        var logs = await _getDockerLogsAsync(containerName, cancellationToken);
        if (logs.Contains("Emulator Service is Successfully Up!")) {
          Console.WriteLine($"[ServiceBusEmulator] Emulator ready after {elapsed} seconds");
          break;
        }

        if (elapsed % 30 == 0 && elapsed > 0) {
          Console.WriteLine($"[ServiceBusEmulator] Still waiting... ({elapsed}s elapsed)");
        }

        await Task.Delay(TimeSpan.FromSeconds(pollIntervalSeconds), cancellationToken);
        elapsed += pollIntervalSeconds;
      }

      // Final verification
      var finalLogs = await _getDockerLogsAsync(containerName, cancellationToken);
      if (!finalLogs.Contains("Emulator Service is Successfully Up!")) {
        throw new InvalidOperationException(
          $"Service Bus Emulator failed to start within {maxWaitSeconds} seconds. Check logs:\n{finalLogs}"
        );
      }

      // Wait for AMQP connections to be fully ready
      // The emulator reports "Successfully Up" but AMQP connections may not be ready yet
      Console.WriteLine("[ServiceBusEmulator] Waiting 40 seconds for AMQP connections to stabilize...");
      await Task.Delay(TimeSpan.FromSeconds(40), cancellationToken);

      // Create client and verify connectivity
      _client = new ServiceBusClient(ConnectionString);

      // Warmup: Send and receive a test message
      await _warmupAsync(cancellationToken);

      Console.WriteLine("[ServiceBusEmulator] ✅ Emulator is ready!");
      _isInitialized = true;
    } finally {
      // Clean up on failure (keep compose file for dispose)
      if (!_isInitialized) {
        try {
          await _runDockerComposeAsync("down", _dockerComposeFile, cancellationToken);
        } catch {
          // Ignore cleanup errors
        }
        if (File.Exists(_dockerComposeFile)) {
          File.Delete(_dockerComposeFile);
        }
      }
    }
  }

  /// <summary>
  /// Stops the emulator containers.
  /// </summary>
  public async ValueTask DisposeAsync() {
    if (!_isInitialized) {
      return;
    }

    Console.WriteLine("[ServiceBusEmulator] Stopping emulator...");

    // Check dead letter queues before shutdown for diagnostics
    if (_client != null) {
      await CheckDeadLetterQueuesAsync();
      await _client.DisposeAsync();
    }

    if (File.Exists(_dockerComposeFile)) {
      var projectName = $"sbtest{_port}";
      await _runDockerComposeAsyncIgnoreErrors($"-p {projectName} down -v --remove-orphans", _dockerComposeFile);
      await _removeNetworkAsync($"{projectName}_default");
      File.Delete(_dockerComposeFile);
    }

    Console.WriteLine("[ServiceBusEmulator] ✅ Emulator stopped");
  }

  /// <summary>
  /// Checks all known dead letter queues and prints diagnostics if any messages are found.
  /// Call this to diagnose why messages might not be reaching handlers.
  /// </summary>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>Total count of dead-lettered messages across all subscriptions.</returns>
  public async Task<int> CheckDeadLetterQueuesAsync(CancellationToken cancellationToken = default) {
    if (_client == null) {
      return 0;
    }

    var totalDeadLettered = 0;
    Console.WriteLine("[ServiceBusEmulator] Checking dead letter queues...");

    foreach (var (topic, subscription) in _knownSubscriptions) {
      try {
        // Create receiver for the dead letter sub-queue
        // The dead letter queue path is: {topic}/Subscriptions/{subscription}/$DeadLetterQueue
        var dlqReceiver = _client.CreateReceiver(
          topic,
          subscription,
          new ServiceBusReceiverOptions {
            SubQueue = SubQueue.DeadLetter,
            ReceiveMode = ServiceBusReceiveMode.PeekLock
          }
        );

        try {
          // Peek messages (don't consume them) to see what's in the DLQ
          var messages = await dlqReceiver.PeekMessagesAsync(
            maxMessages: 100,
            cancellationToken: cancellationToken
          );

          if (messages.Count > 0) {
            totalDeadLettered += messages.Count;
            Console.WriteLine($"[ServiceBusEmulator] ⚠️ DEAD LETTER QUEUE: {topic}/{subscription} has {messages.Count} message(s):");

            foreach (var msg in messages) {
              var deadLetterReason = msg.DeadLetterReason ?? "Unknown";
              var deadLetterDescription = msg.DeadLetterErrorDescription ?? "No description";
              var envelopeType = msg.ApplicationProperties.TryGetValue("EnvelopeType", out var et) ? et?.ToString() : "Unknown";

              Console.WriteLine($"  - MessageId: {msg.MessageId}");
              Console.WriteLine($"    EnvelopeType: {envelopeType}");
              Console.WriteLine($"    DeadLetterReason: {deadLetterReason}");
              Console.WriteLine($"    DeadLetterDescription: {deadLetterDescription}");
              Console.WriteLine($"    DeliveryCount: {msg.DeliveryCount}");
              Console.WriteLine($"    EnqueuedTime: {msg.EnqueuedTime}");

              // Try to show body preview (first 200 chars)
              try {
                var bodyText = msg.Body.ToString();
                if (bodyText.Length > 200) {
                  bodyText = bodyText[..200] + "...";
                }
                Console.WriteLine($"    Body: {bodyText}");
              } catch {
                Console.WriteLine("    Body: [Unable to read]");
              }
            }
          }
        } finally {
          await dlqReceiver.DisposeAsync();
        }
      } catch (Exception ex) {
        Console.WriteLine($"[ServiceBusEmulator] Warning: Could not check DLQ for {topic}/{subscription}: {ex.Message}");
      }
    }

    if (totalDeadLettered == 0) {
      Console.WriteLine("[ServiceBusEmulator] ✅ No dead-lettered messages found");
    } else {
      Console.WriteLine($"[ServiceBusEmulator] ⚠️ Total dead-lettered messages: {totalDeadLettered}");
    }

    return totalDeadLettered;
  }

  private string _generateDockerComposeContent() {
    // Mounts Config.json which defines:
    // - topic-00 with sub-00-a subscription
    // - topic-01 with sub-01-a subscription
    return $"""
services:
  servicebus-emulator:
    container_name: whizbang-test-servicebus-{_port}
    image: mcr.microsoft.com/azure-messaging/servicebus-emulator:latest
    ports:
      - "{_port}:5672"
    environment:
      - ACCEPT_EULA=Y
      - MSSQL_SA_PASSWORD=ServiceBus!Pass
      - SQL_SERVER=mssql
      - CONFIG_PATH=/ServiceBus_Emulator/ConfigFiles/Config.json
    volumes:
      - "{_configFilePath}:/ServiceBus_Emulator/ConfigFiles/Config.json:ro"
    depends_on:
      - mssql
    mem_limit: 4g

  mssql:
    container_name: whizbang-test-mssql-{_port}
    image: mcr.microsoft.com/mssql/server:2022-latest
    ports:
      - "{_port + 10000}:1433"
    environment:
      - ACCEPT_EULA=Y
      - MSSQL_SA_PASSWORD=ServiceBus!Pass
    mem_limit: 4g

""";
  }

  private async Task _warmupAsync(CancellationToken cancellationToken = default) {
    if (_client == null) {
      return;
    }

    Console.WriteLine("[ServiceBusEmulator] Warming up...");

    // Warmup topic-00
    const string topicName = "topic-00";
    const string subscriptionName = "sub-00-a";

    var sender = _client.CreateSender(topicName);
    var receiver = _client.CreateReceiver(topicName, subscriptionName);

    try {
      var message = new ServiceBusMessage("{\"warmup\":true}") {
        MessageId = Guid.NewGuid().ToString(),
        ContentType = "application/json"
      };

      await sender.SendMessageAsync(message, cancellationToken);

      // Wait for message with retries
      ServiceBusReceivedMessage? received = null;
      for (int attempt = 0; attempt < 20; attempt++) {
        received = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(5), cancellationToken);
        if (received != null) {
          await receiver.CompleteMessageAsync(received, cancellationToken);
          break;
        }

        var delayMs = Math.Min(500 * (1 << attempt), 8000);
        await Task.Delay(delayMs, cancellationToken);
      }

      if (received == null) {
        throw new TimeoutException($"Warmup message to {topicName} never received - emulator may not be fully ready");
      }

      Console.WriteLine("[ServiceBusEmulator] ✓ Warmup complete");
    } finally {
      await sender.DisposeAsync();
      await receiver.DisposeAsync();
    }
  }

  private static async Task _runDockerComposeAsync(
    string arguments,
    string composeFile,
    CancellationToken cancellationToken = default
  ) {
    var psi = new ProcessStartInfo {
      FileName = "docker",
      Arguments = $"compose -f \"{composeFile}\" {arguments}",
      RedirectStandardOutput = true,
      RedirectStandardError = true,
      UseShellExecute = false,
      CreateNoWindow = true
    };

    using var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start docker compose process");
    await process.WaitForExitAsync(cancellationToken);

    if (process.ExitCode != 0) {
      var error = await process.StandardError.ReadToEndAsync(cancellationToken);
      throw new InvalidOperationException($"docker compose failed: {error}");
    }
  }

  /// <summary>
  /// Run docker compose command ignoring errors (for cleanup operations).
  /// </summary>
  private static async Task _runDockerComposeAsyncIgnoreErrors(
    string arguments,
    string composeFile,
    CancellationToken cancellationToken = default
  ) {
    try {
      var psi = new ProcessStartInfo {
        FileName = "docker",
        Arguments = $"compose -f \"{composeFile}\" {arguments}",
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true
      };

      using var process = Process.Start(psi);
      if (process != null) {
        await process.WaitForExitAsync(cancellationToken);
        // Ignore exit code - cleanup may fail if containers don't exist
      }
    } catch {
      // Ignore cleanup errors
    }
  }

  private static async Task<string> _getDockerLogsAsync(
    string containerName,
    CancellationToken cancellationToken = default
  ) {
    var psi = new ProcessStartInfo {
      FileName = "docker",
      Arguments = $"logs {containerName}",
      RedirectStandardOutput = true,
      RedirectStandardError = true,
      UseShellExecute = false,
      CreateNoWindow = true
    };

    using var process = Process.Start(psi);
    if (process == null) {
      return string.Empty;
    }

    await process.WaitForExitAsync(cancellationToken);

    var stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken);
    var stderr = await process.StandardError.ReadToEndAsync(cancellationToken);

    return stdout + stderr;
  }

  /// <summary>
  /// Remove a Docker network by name (ignores errors if network doesn't exist).
  /// </summary>
  private static async Task _removeNetworkAsync(string networkName, CancellationToken cancellationToken = default) {
    try {
      var psi = new ProcessStartInfo {
        FileName = "docker",
        Arguments = $"network rm {networkName}",
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true
      };

      using var process = Process.Start(psi);
      if (process != null) {
        await process.WaitForExitAsync(cancellationToken);
        // Ignore exit code - network may not exist
      }
    } catch {
      // Ignore cleanup errors
    }
  }

  /// <summary>
  /// Force cleanup any stale containers from previous test runs.
  /// This handles the case where tests were aborted without proper cleanup.
  /// </summary>
  private async Task _forceCleanupContainersAsync(CancellationToken cancellationToken = default) {
    var containerNames = new[] {
      $"whizbang-test-servicebus-{_port}",
      $"whizbang-test-mssql-{_port}"
    };

    foreach (var containerName in containerNames) {
      try {
        var psi = new ProcessStartInfo {
          FileName = "docker",
          Arguments = $"rm -f {containerName}",
          RedirectStandardOutput = true,
          RedirectStandardError = true,
          UseShellExecute = false,
          CreateNoWindow = true
        };

        using var process = Process.Start(psi);
        if (process != null) {
          await process.WaitForExitAsync(cancellationToken);
          // Ignore exit code - container may not exist
        }
      } catch {
        // Ignore cleanup errors
      }
    }

    // Also prune any dangling networks
    try {
      var psi = new ProcessStartInfo {
        FileName = "docker",
        Arguments = "network prune -f",
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true
      };

      using var process = Process.Start(psi);
      if (process != null) {
        await process.WaitForExitAsync(cancellationToken);
      }
    } catch {
      // Ignore cleanup errors
    }
  }
}
