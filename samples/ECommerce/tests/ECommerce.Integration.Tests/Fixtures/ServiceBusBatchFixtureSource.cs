namespace ECommerce.Integration.Tests.Fixtures;

/// <summary>
/// TUnit ClassDataSource for shared ServiceBus emulator fixture.
/// Only ServiceBus emulator is pre-created BEFORE tests run.
/// PostgreSQL and service hosts are created per-test (during test timeout).
/// TUnit manages the lifecycle: initialization happens BEFORE tests run, not during test execution.
/// </summary>
public sealed class ServiceBusBatchFixtureSource {
  private static ServiceBusBatchFixture? _serviceBusFixture;
  private static readonly SemaphoreSlim _initLock = new(1, 1);
  private static bool _initialized = false;

  /// <summary>
  /// Default constructor required by TUnit ClassDataSource.
  /// </summary>
  [Obsolete]
  public ServiceBusBatchFixtureSource() {
    // Synchronously wait for initialization to complete
    // This ensures fixtures are ready before test classes are constructed
    InitializeAsync().GetAwaiter().GetResult();
  }

  /// <summary>
  /// Gets the initialized ServiceBus fixture.
  /// </summary>
  public ServiceBusBatchFixture ServiceBusFixture => _serviceBusFixture ?? throw new InvalidOperationException("ServiceBus fixture not initialized");

  /// <summary>
  /// Initializes the shared ServiceBus emulator fixture once per assembly.
  /// Reuses the emulator from SharedFixtureSource instead of starting a new one.
  /// </summary>
  [Obsolete]
  private async Task InitializeAsync() {
    if (_initialized) {
      return;
    }

    await _initLock.WaitAsync();
    try {
      if (_initialized) {
        return;
      }

      Console.WriteLine("================================================================================");
      Console.WriteLine("[BATCH FIXTURE SOURCE] Reusing shared ServiceBus emulator...");
      Console.WriteLine("================================================================================");

      // Reuse the emulator from SharedFixtureSource (already running on port 5672)
      // This avoids Docker container name conflicts
      var (connectionString, sharedClient) = await SharedFixtureSource.GetSharedResourcesAsync(0);

      // Create a lightweight fixture wrapper that doesn't start its own emulator
      _serviceBusFixture = new ServiceBusBatchFixture(0);
      await _serviceBusFixture.InitializeWithSharedEmulatorAsync(connectionString, sharedClient);

      Console.WriteLine("================================================================================");
      Console.WriteLine("[BATCH FIXTURE SOURCE] ✅ Shared emulator ready!");
      Console.WriteLine("================================================================================");

      _initialized = true;
    } finally {
      _initLock.Release();
    }
  }
}
