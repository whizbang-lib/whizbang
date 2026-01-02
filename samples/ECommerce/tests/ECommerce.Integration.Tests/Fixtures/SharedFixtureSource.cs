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
  public static async Task<(string ConnectionString, ServiceBusClient Client)> GetSharedResourcesAsync(int testIndex) {
    if (_initialized) {
      return (ConnectionString, SharedServiceBusClient);
    }

    await _initLock.WaitAsync();
    try {
      if (_initialized) {
        return (ConnectionString, SharedServiceBusClient);
      }

      Console.WriteLine("================================================================================");
      Console.WriteLine("[SharedFixture] Initializing shared ServiceBus emulator and client...");
      Console.WriteLine("================================================================================");

      // Step 1: Create and start emulator (no warmup yet)
      _sharedEmulator = new ServiceBusBatchFixture(0);
      await _sharedEmulator.InitializeEmulatorAsync();

      // Step 2: Create single static ServiceBusClient using the emulator's connection string
      _sharedServiceBusClient = new ServiceBusClient(ConnectionString);
      Console.WriteLine("[SharedFixture] Created SINGLE shared ServiceBusClient (will be reused by all hosts AND warmup)");

      // Step 3: Warmup emulator using the shared client
      await _sharedEmulator.WarmupWithClientAsync(_sharedServiceBusClient);

      Console.WriteLine("================================================================================");
      Console.WriteLine("[SharedFixture] ✅ Shared resources ready!");
      Console.WriteLine("[SharedFixture] All tests and warmup use the SAME ServiceBusClient");
      Console.WriteLine("================================================================================");

      _initialized = true;
      return (ConnectionString, SharedServiceBusClient);
    } finally {
      _initLock.Release();
    }
  }

  /// <summary>
  /// Final cleanup: disposes shared ServiceBus emulator and client when tests complete.
  /// </summary>
  public static async Task DisposeAsync() {
    if (_sharedServiceBusClient != null) {
      await _sharedServiceBusClient.DisposeAsync();
      Console.WriteLine("[SharedFixture] Disposed shared ServiceBusClient");
    }

    if (_sharedEmulator != null) {
      await _sharedEmulator.DisposeAsync();
      Console.WriteLine("[SharedFixture] Disposed shared emulator");
    }

    _initialized = false;
    Console.WriteLine("[SharedFixture] All shared resources disposed.");
  }
}
