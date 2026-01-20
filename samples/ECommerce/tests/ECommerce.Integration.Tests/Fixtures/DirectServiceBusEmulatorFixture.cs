using System.Diagnostics;

namespace ECommerce.Integration.Tests.Fixtures;

/// <summary>
/// Fixture for managing Azure Service Bus Emulator directly via docker-compose (without Aspire).
/// This approach avoids Aspire's memory issues and provides better control over emulator configuration.
/// </summary>
public sealed class DirectServiceBusEmulatorFixture : IAsyncDisposable {
  private readonly string _dockerComposeFile;
  private readonly string _configFile;
  private readonly string? _customConfigFile;
  private readonly int _port;
  private bool _isInitialized;

  /// <summary>
  /// Creates a fixture using the emulator's default built-in configuration.
  /// </summary>
  public DirectServiceBusEmulatorFixture() : this(5672, null) {
  }

  /// <summary>
  /// Creates a fixture with a custom port and optional custom config file.
  /// </summary>
  /// <param name="port">External port for the emulator (default: 5672)</param>
  /// <param name="configFileName">Optional config file name (e.g., "Config-Default.json"). If null, uses built-in config.</param>
  public DirectServiceBusEmulatorFixture(int port, string? configFileName) {
    _port = port;
    // Store paths for docker-compose and Config.json
    var testDirectory = AppContext.BaseDirectory;
    _dockerComposeFile = Path.Combine(testDirectory, "docker-compose.servicebus.yml");
    _configFile = Path.Combine(testDirectory, "Config.json");

    if (configFileName != null) {
      _customConfigFile = Path.Combine(testDirectory, configFileName);
    }
  }

  /// <summary>
  /// Gets the Service Bus connection string for the emulator.
  /// IMPORTANT: Must include port number or defaults to port 80!
  /// </summary>
  public string ServiceBusConnectionString =>
    $"Endpoint=sb://localhost:{_port};SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true;";

  /// <summary>
  /// Initializes the emulator by starting docker-compose containers.
  /// </summary>
  public async Task InitializeAsync(CancellationToken cancellationToken = default) {
    if (_isInitialized) {
      return;
    }

    var configInfo = _customConfigFile != null
      ? $"with custom config: {Path.GetFileName(_customConfigFile)}"
      : "with built-in default config";
    Console.WriteLine($"[DirectEmulator] Starting Azure Service Bus Emulator {configInfo}...");

    // If using custom config, verify it exists
    if (_customConfigFile != null && !File.Exists(_customConfigFile)) {
      throw new FileNotFoundException($"Custom config not found at: {_customConfigFile}");
    }

    // Generate docker-compose file dynamically based on config choice
    var dockerComposeContent = _generateDockerComposeContent();
    var tempDockerComposeFile = Path.Combine(Path.GetTempPath(), $"docker-compose-sb-{Guid.NewGuid():N}.yml");
    await File.WriteAllTextAsync(tempDockerComposeFile, dockerComposeContent, cancellationToken);

    try {
      // Stop any existing containers
      await _runDockerComposeAsync("down", cancellationToken, tempDockerComposeFile);

      // Start containers
      await _runDockerComposeAsync("up -d", cancellationToken, tempDockerComposeFile);

      // Wait for emulator to be ready by polling logs until "Successfully Up!" appears
      // SQL Server can take 60-120 seconds to start (especially on ARM64), and the emulator
      // has built-in retries (15s each). Polling is more reliable than a fixed delay.
      Console.WriteLine("[DirectEmulator] Waiting for emulator to be ready (polling up to 180 seconds)...");
      var containerName = $"servicebus-emulator-{_port}";
      var maxWaitSeconds = 180;
      var pollIntervalSeconds = 5;
      var elapsed = 0;

      while (elapsed < maxWaitSeconds) {
        var logs = await _getDockerLogsAsync(containerName, cancellationToken);
        if (logs.Contains("Emulator Service is Successfully Up!")) {
          Console.WriteLine($"[DirectEmulator] Emulator ready after {elapsed} seconds");
          break;
        }

        if (elapsed % 30 == 0 && elapsed > 0) {
          Console.WriteLine($"[DirectEmulator] Still waiting... ({elapsed}s elapsed)");
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

      Console.WriteLine("[DirectEmulator] ✅ Emulator is ready!");
      _isInitialized = true;
    } finally {
      // Clean up temp docker-compose file
      if (File.Exists(tempDockerComposeFile)) {
        File.Delete(tempDockerComposeFile);
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

    Console.WriteLine("[DirectEmulator] Stopping emulator containers...");
    await _runDockerComposeAsync("down");
    Console.WriteLine("[DirectEmulator] ✅ Emulator stopped");
  }

  private string _generateDockerComposeContent() {
    var serviceBusSection = _customConfigFile != null
      ? $@"  servicebus-emulator:
    container_name: servicebus-emulator-{_port}
    image: mcr.microsoft.com/azure-messaging/servicebus-emulator:latest
    ports:
      - ""{_port}:5672""
    environment:
      - ACCEPT_EULA=Y
      - MSSQL_SA_PASSWORD=ServiceBus!Pass
      - SQL_SERVER=mssql
      - CONFIG_PATH=/ServiceBus_Emulator/ConfigFiles/Config.json
    volumes:
      - ""{_customConfigFile}:/ServiceBus_Emulator/ConfigFiles/Config.json:ro""
    depends_on:
      - mssql
    mem_limit: 4g"
      : $@"  servicebus-emulator:
    container_name: servicebus-emulator-{_port}
    image: mcr.microsoft.com/azure-messaging/servicebus-emulator:latest
    ports:
      - ""{_port}:5672""
    environment:
      - ACCEPT_EULA=Y
      - MSSQL_SA_PASSWORD=ServiceBus!Pass
      - SQL_SERVER=mssql
    depends_on:
      - mssql
    mem_limit: 4g";

    return $@"services:
{serviceBusSection}

  mssql:
    container_name: mssql-servicebus-{_port}
    image: mcr.microsoft.com/mssql/server:2022-latest
    ports:
      - ""{_port + 10000}:1433""
    environment:
      - ACCEPT_EULA=Y
      - MSSQL_SA_PASSWORD=ServiceBus!Pass
    mem_limit: 4g
";
  }

  private async Task _runDockerComposeAsync(string arguments, CancellationToken cancellationToken = default, string? composeFile = null) {
    var file = composeFile ?? _dockerComposeFile;
    var psi = new ProcessStartInfo {
      FileName = "docker-compose",
      Arguments = $"-f \"{file}\" {arguments}",
      RedirectStandardOutput = true,
      RedirectStandardError = true,
      UseShellExecute = false,
      CreateNoWindow = true
    };

    using var process = Process.Start(psi);
    if (process == null) {
      throw new InvalidOperationException("Failed to start docker-compose process");
    }

    await process.WaitForExitAsync(cancellationToken);

    if (process.ExitCode != 0) {
      var error = await process.StandardError.ReadToEndAsync(cancellationToken);
      throw new InvalidOperationException($"docker-compose failed: {error}");
    }
  }

  private async Task<string> _getDockerLogsAsync(string containerName, CancellationToken cancellationToken = default) {
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
}
