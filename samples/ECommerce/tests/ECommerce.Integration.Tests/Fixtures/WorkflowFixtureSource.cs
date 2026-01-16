namespace ECommerce.Integration.Tests.Fixtures;

/// <summary>
/// Fixture source for workflow tests (CreateProduct, UpdateProduct, RestockInventory, SeedProducts).
/// Reuses the shared ServiceBus emulator and creates a single ServiceBusIntegrationFixture
/// that's shared across all workflow tests, dramatically reducing test execution time.
/// Database is cleaned between tests instead of recreating containers.
/// </summary>
public sealed class WorkflowFixtureSource {
  private static ServiceBusIntegrationFixture? _sharedFixture;
  private static readonly SemaphoreSlim _initLock = new(1, 1);
  private static bool _initialized;

  /// <summary>
  /// Default constructor required by TUnit ClassDataSource.
  /// </summary>
  [Obsolete]
  public WorkflowFixtureSource() {
    // Synchronously wait for initialization to complete
    // This ensures fixture is ready before test classes are constructed
    _initializeAsync().GetAwaiter().GetResult();
  }

  /// <summary>
  /// Gets the initialized workflow fixture.
  /// </summary>
  public ServiceBusIntegrationFixture Fixture => _sharedFixture ?? throw new InvalidOperationException("Workflow fixture not initialized");

  private static async Task _initializeAsync() {
    if (_initialized) {
      return;
    }

    await _initLock.WaitAsync();
    try {
      if (_initialized) {
        return;
      }

      Console.WriteLine("================================================================================");
      Console.WriteLine("[WorkflowFixtureSource] Creating SHARED fixture for all workflow tests...");
      Console.WriteLine("================================================================================");

      // Reuse the emulator from SharedFixtureSource (already running on port 5672)
      var testIndex = 50; // Use unique index for workflow tests
      var (connectionString, sharedClient) = await SharedFixtureSource.GetSharedResourcesAsync(testIndex);

      // Create a single fixture that will be reused by all workflow tests
      _sharedFixture = new ServiceBusIntegrationFixture(connectionString, sharedClient, 0);
      await _sharedFixture.InitializeAsync();

      Console.WriteLine("================================================================================");
      Console.WriteLine("[WorkflowFixtureSource] ✅ Shared fixture ready! All workflow tests will reuse this fixture.");
      Console.WriteLine("================================================================================");

      _initialized = true;
    } finally {
      _initLock.Release();
    }
  }
}
