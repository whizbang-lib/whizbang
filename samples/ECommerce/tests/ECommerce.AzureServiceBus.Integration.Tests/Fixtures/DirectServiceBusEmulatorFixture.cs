// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics;

namespace ECommerce.Integration.Tests.Fixtures;

/// <summary>
/// Fixture for managing Azure Service Bus Emulator directly via docker-compose (without Aspire).
/// This approach avoids Aspire's memory issues and provides better control over emulator configuration.
/// </summary>
public sealed class DirectServiceBusEmulatorFixture : IAsyncDisposable {
  private readonly string _configFile;
  private readonly string? _customConfigFile;
  private readonly int _port;
  private readonly string _projectName;
  private string? _activeComposeFile;  // The compose file currently in use (persisted for cleanup)
  private bool _isInitialized;

  /// <summary>
  /// Creates a fixture using the emulator's default built-in configuration.
  /// </summary>
  public DirectServiceBusEmulatorFixture() : this(5673, null) {
  }

  /// <summary>
  /// Creates a fixture with a custom port and optional custom config file.
  /// </summary>
  /// <param name="port">External port for the emulator (default: 5672)</param>
  /// <param name="configFileName">Optional config file name (e.g., "Config-Default.json"). If null, uses built-in config.</param>
  public DirectServiceBusEmulatorFixture(int port, string? configFileName) {
    _port = port;
    _projectName = $"whizbang-ecommerce-servicebus-{_port}";  // Explicit project name to avoid conflicts
    // Store paths for Config.json
    var testDirectory = AppContext.BaseDirectory;
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
    // Use a consistent file name based on port (not GUID) so we can find it for cleanup
    var dockerComposeContent = _generateDockerComposeContent();
    _activeComposeFile = Path.Combine(Path.GetTempPath(), $"docker-compose-sb-ecommerce-{_port}.yml");
    await File.WriteAllTextAsync(_activeComposeFile, dockerComposeContent, cancellationToken);

    var containerName = $"whizbang-test-servicebus-{_port}";

    try {
      // Check if emulator container is already running (reuse from previous run)
      var existingState = await _getContainerStateAsync(containerName, cancellationToken);
      if (existingState == "running") {
        Console.WriteLine($"[DirectEmulator] Reusing existing container '{containerName}'");
        _isInitialized = true;
        return;
      }

      // Not running — clean up and start fresh
      if (existingState != null) {
        await _runDockerComposeAsync($"-p {_projectName} down -v --remove-orphans", cancellationToken);
        await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
      }

      await _pullImagesAsync(cancellationToken);
      await _runDockerComposeAsync($"-p {_projectName} up -d --force-recreate", cancellationToken);

      Console.WriteLine("[DirectEmulator] Waiting for emulator to be ready (polling up to 180 seconds)...");
      const int maxWaitSeconds = 180;
      const int pollIntervalSeconds = 5;
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
    } catch {
      // Clean up compose file on failure (it's kept for successful dispose)
      if (_activeComposeFile != null && File.Exists(_activeComposeFile)) {
        File.Delete(_activeComposeFile);
        _activeComposeFile = null;
      }
      throw;
    }
  }

  /// <summary>
  /// Keeps containers running for reuse across test runs.
  /// To remove: docker rm -f whizbang-test-servicebus-{port} whizbang-test-mssql-{port}
  /// </summary>
  public ValueTask DisposeAsync() {
    Console.WriteLine($"[DirectEmulator] Keeping containers running for reuse (port {_port})");
    return ValueTask.CompletedTask;
  }

  private string _generateDockerComposeContent() {
    var serviceBusSection = _customConfigFile != null
      ? $"""
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
              - "{_customConfigFile}:/ServiceBus_Emulator/ConfigFiles/Config.json:ro"
            depends_on:
              - mssql
            mem_limit: 4g
        """
      : $"""
          servicebus-emulator:
            container_name: whizbang-test-servicebus-{_port}
            image: mcr.microsoft.com/azure-messaging/servicebus-emulator:latest
            ports:
              - "{_port}:5672"
            environment:
              - ACCEPT_EULA=Y
              - MSSQL_SA_PASSWORD=ServiceBus!Pass
              - SQL_SERVER=mssql
            depends_on:
              - mssql
            mem_limit: 4g
        """;

    return $"""
      services:
      {serviceBusSection}

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

  private async Task _runDockerComposeAsync(string arguments, CancellationToken cancellationToken = default) {
    var (exitCode, error) = await _runDockerComposeProcessAsync(arguments, cancellationToken);
    if (exitCode != 0) {
      throw new InvalidOperationException($"docker compose failed: {error}");
    }
  }

  /// <summary>
  /// Pulls the compose file's images before <c>up</c>, retrying a pull that a registry dropped.
  /// </summary>
  /// <remarks>
  /// The images come from mcr.microsoft.com, which the CI image mirror does not cover, so a reset connection
  /// mid-pull used to fail the fixture and every test in the suite with it. Only a failure
  /// <see cref="IsTransientPullFailure"/> recognizes is retried: a missing image or a stopped daemon fails at once.
  /// </remarks>
  private async Task _pullImagesAsync(CancellationToken cancellationToken) {
    var error = string.Empty;
    for (var attempt = 1; attempt <= MAX_PULL_ATTEMPTS; attempt++) {
      int exitCode;
      (exitCode, error) = await _runDockerComposeProcessAsync($"-p {_projectName} pull", cancellationToken);
      if (exitCode == 0) {
        return;
      }

      if (!IsTransientPullFailure(error)) {
        throw new InvalidOperationException($"docker compose pull failed: {error}");
      }

      if (attempt < MAX_PULL_ATTEMPTS) {
        Console.WriteLine($"[DirectEmulator] Image pull attempt {attempt} lost its registry connection; retrying: {error.Trim()}");
        await Task.Delay(TimeSpan.FromSeconds(5 * attempt), cancellationToken);
      }
    }

    throw new InvalidOperationException($"docker compose pull failed after {MAX_PULL_ATTEMPTS} attempts: {error}");
  }

  internal const int MAX_PULL_ATTEMPTS = 4;

  private static readonly string[] _transientPullMarkers = [
    "connection reset by peer",
    "i/o timeout",
    "TLS handshake timeout",
    "unexpected EOF",
    "Client.Timeout exceeded",
    "502 Bad Gateway",
    "503 Service Unavailable",
    "504 Gateway Timeout",
    "toomanyrequests",
  ];

  /// <summary>Whether a failed pull's error is a registry network failure worth another attempt.</summary>
  internal static bool IsTransientPullFailure(string error) =>
    _transientPullMarkers.Any(marker => error.Contains(marker, StringComparison.OrdinalIgnoreCase));

  /// <summary>
  /// Runs <c>docker compose</c> and returns its exit code and standard error.
  /// </summary>
  /// <remarks>
  /// Both streams are read while the process runs: reading them only after it exits lets a long pull log fill the
  /// pipe, and the process then blocks writing to it and never exits.
  /// </remarks>
  private async Task<(int ExitCode, string Error)> _runDockerComposeProcessAsync(string arguments, CancellationToken cancellationToken) {
    if (_activeComposeFile == null) {
      throw new InvalidOperationException("No active compose file - call InitializeAsync first");
    }

    // Use "docker compose" (v2) instead of "docker-compose" (v1)
    // GitHub Actions ubuntu-24.04 only has docker compose v2
    var psi = new ProcessStartInfo {
      FileName = "docker",
      Arguments = $"compose -f \"{_activeComposeFile}\" {arguments}",
      RedirectStandardOutput = true,
      RedirectStandardError = true,
      UseShellExecute = false,
      CreateNoWindow = true
    };

    using var process = Process.Start(psi)
      ?? throw new InvalidOperationException("Failed to start docker compose process");

    var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
    var error = process.StandardError.ReadToEndAsync(cancellationToken);
    await process.WaitForExitAsync(cancellationToken);
    await output;
    return (process.ExitCode, await error);
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

  private static async Task<string> _getDockerLogsAsync(string containerName, CancellationToken cancellationToken = default) {
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
