namespace Whizbang.Transports.AzureServiceBus.Tests.Containers;

/// <summary>
/// TUnit ClassDataSource for ServiceBus emulator fixture.
/// Provides a single shared emulator instance for all tests in the assembly.
/// </summary>
public sealed class ServiceBusEmulatorFixtureSource {
  private static ServiceBusEmulatorFixture? _fixture;
  private static readonly SemaphoreSlim _initLock = new(1, 1);
  private static bool _initialized;

  /// <summary>
  /// Default constructor required by TUnit ClassDataSource.
  /// Synchronously initializes the emulator before tests run.
  /// </summary>
  public ServiceBusEmulatorFixtureSource() {
    // Synchronously wait for initialization to complete
    // This ensures the emulator is ready before test classes are constructed
    _initializeAsync().GetAwaiter().GetResult();
  }

  /// <summary>
  /// Gets the initialized ServiceBus emulator fixture.
  /// </summary>
  // Instance property because TUnit ClassDataSource requires instance access
#pragma warning disable CA1822 // Member does not access instance data
  public ServiceBusEmulatorFixture Fixture =>
    _fixture ?? throw new InvalidOperationException("Emulator fixture not initialized");
#pragma warning restore CA1822

  /// <summary>
  /// Initializes the emulator once per assembly.
  /// </summary>
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
      Console.WriteLine("[EMULATOR FIXTURE SOURCE] Initializing Azure Service Bus Emulator...");
      Console.WriteLine("================================================================================");

      _fixture = new ServiceBusEmulatorFixture();
      await _fixture.InitializeAsync();

      Console.WriteLine("================================================================================");
      Console.WriteLine("[EMULATOR FIXTURE SOURCE] ✅ Emulator ready!");
      Console.WriteLine("================================================================================");

      _initialized = true;
    } finally {
      _initLock.Release();
    }
  }
}
