using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Azure.Messaging.ServiceBus;
using TUnit.Core;

namespace ECommerce.Integration.Tests.Fixtures;

/// <summary>
/// Provides shared ServiceBus emulator and single static ServiceBusClient for all tests.
/// Tests run SEQUENTIALLY with per-test PostgreSQL and hosts.
/// All hosts reuse the same ServiceBusClient to stay under connection quota.
/// </summary>
public static class SharedFixtureSource {
  private static readonly SemaphoreSlim _initLock = new(1, 1);
  private static ServiceBusBatchFixture? _sharedEmulator;
  private static ServiceBusClient? _sharedServiceBusClient;
  private static bool _initialized = false;
  private static bool _initializationFailed = false;
  private static Exception? _lastInitializationError;

  /// <summary>
  /// Gets the single shared ServiceBusClient that all tests and hosts will reuse.
  /// This keeps us under the emulator's connection quota (~25 connections).
  /// </summary>
  public static ServiceBusClient SharedServiceBusClient =>
    _sharedServiceBusClient ?? throw new InvalidOperationException("Shared ServiceBusClient not initialized. Call GetSharedResourcesAsync() first.");

  /// <summary>
  /// Gets the shared ServiceBus emulator connection string.
  /// </summary>
  public static string ConnectionString =>
    _sharedEmulator?.ConnectionString ?? throw new InvalidOperationException("Shared emulator not initialized. Call GetSharedResourcesAsync() first.");

  /// <summary>
  /// Initializes shared ServiceBus emulator and creates single static ServiceBusClient.
  /// Called once before any tests run.
  /// </summary>
  /// <param name="testIndex">The test index (not used, kept for compatibility).</param>
  /// <param name="cancellationToken">Cancellation token with default timeout of 120 seconds.</param>
  public static async Task<(string ConnectionString, ServiceBusClient Client)> GetSharedResourcesAsync(
      int testIndex,
      CancellationToken cancellationToken = default) {

    // If already initialized successfully, return immediately
    if (_initialized) {
      return (ConnectionString, SharedServiceBusClient);
    }

    // If previous initialization failed, throw the error immediately (don't retry)
    if (_initializationFailed) {
      throw new InvalidOperationException(
        $"Shared ServiceBus emulator initialization previously failed and cannot be retried. " +
        $"Original error: {_lastInitializationError?.Message}",
        _lastInitializationError
      );
    }

    // Use default timeout of 240 seconds if no cancellation token provided
    // (SQL Server can take 60-120 seconds to start, plus emulator init + warmup)
    using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(240));
    using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
    var ct = linkedCts.Token;

    await _initLock.WaitAsync(ct);
    try {
      // Double-check after acquiring lock
      if (_initialized) {
        return (ConnectionString, SharedServiceBusClient);
      }

      if (_initializationFailed) {
        throw new InvalidOperationException(
          $"Shared ServiceBus emulator initialization previously failed. Original error: {_lastInitializationError?.Message}",
          _lastInitializationError
        );
      }

      Console.WriteLine("================================================================================");
      Console.WriteLine("[SharedFixture] Initializing shared ServiceBus emulator and client...");
      Console.WriteLine("================================================================================");

      try {
        // Step 1: Create and start emulator
        _sharedEmulator = new ServiceBusBatchFixture(0);
        await _sharedEmulator.InitializeEmulatorAsync(ct);
        Console.WriteLine("[SharedFixture] ✓ Emulator started");

        // Step 2: Create single static ServiceBusClient
        _sharedServiceBusClient = new ServiceBusClient(ConnectionString);
        Console.WriteLine("[SharedFixture] ✓ Created SINGLE shared ServiceBusClient (will be reused by all hosts)");

        // Step 3: Warmup emulator with timeout protection
        Console.WriteLine("[SharedFixture] Warming up emulator (may take 30-60 seconds)...");
        await _sharedEmulator.WarmupWithClientAsync(_sharedServiceBusClient, ct);
        Console.WriteLine("[SharedFixture] ✓ Emulator warmed up successfully");

        Console.WriteLine("================================================================================");
        Console.WriteLine("[SharedFixture] ✅ Shared resources ready!");
        Console.WriteLine("[SharedFixture] All tests and warmup use the SAME ServiceBusClient");
        Console.WriteLine("================================================================================");

        _initialized = true;
        return (ConnectionString, SharedServiceBusClient);
      } catch (Exception ex) {
        // Mark initialization as failed to prevent retry loops
        _initializationFailed = true;
        _lastInitializationError = ex;

        Console.WriteLine("================================================================================");
        Console.WriteLine($"[SharedFixture] ❌ Initialization FAILED: {ex.Message}");
        Console.WriteLine("================================================================================");

        // Clean up partial initialization
        await _cleanupAfterFailureAsync();

        throw new InvalidOperationException(
          $"Failed to initialize shared ServiceBus emulator. " +
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
      if (_sharedServiceBusClient != null) {
        await _sharedServiceBusClient.DisposeAsync();
        _sharedServiceBusClient = null;
        Console.WriteLine("[SharedFixture] Disposed ServiceBusClient after failure");
      }

      if (_sharedEmulator != null) {
        await _sharedEmulator.DisposeAsync();
        _sharedEmulator = null;
        Console.WriteLine("[SharedFixture] Disposed emulator after failure");
      }
    } catch (Exception ex) {
      Console.WriteLine($"[SharedFixture] Warning: Error during cleanup: {ex.Message}");
    }
  }

  /// <summary>
  /// Final cleanup: disposes shared ServiceBus emulator and client when tests complete.
  /// Also resets failure state to allow retry if needed.
  /// </summary>
  public static async Task DisposeAsync() {
    if (_sharedServiceBusClient != null) {
      await _sharedServiceBusClient.DisposeAsync();
      _sharedServiceBusClient = null;
      Console.WriteLine("[SharedFixture] Disposed shared ServiceBusClient");
    }

    if (_sharedEmulator != null) {
      await _sharedEmulator.DisposeAsync();
      _sharedEmulator = null;
      Console.WriteLine("[SharedFixture] Disposed shared emulator");
    }

    // Reset all state flags
    _initialized = false;
    _initializationFailed = false;
    _lastInitializationError = null;

    Console.WriteLine("[SharedFixture] All shared resources disposed and state reset.");
  }
}
