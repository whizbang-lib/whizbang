using System.Diagnostics.CodeAnalysis;

namespace ECommerce.Integration.Tests.Fixtures;

/// <summary>
/// Provides a shared Service Bus emulator with deterministic topic suffix assignment for parallel test execution.
/// The emulator is initialized ONCE (60s) with 25 topic sets (00-24) in a SINGLE namespace.
/// Each topic set has products-XX and inventory-XX topics with 4 subscriptions total.
/// Each test instance gets a deterministic topic suffix based on its unique instance ID hash (modulo 25).
/// Tests may reuse topic sets, but sequential execution (NotInParallel) ensures isolation.
/// </summary>
public static class SharedFixtureSource {
  private static DirectServiceBusEmulatorFixture? _sharedEmulator;
  private static readonly SemaphoreSlim _emulatorLock = new(1, 1);
  private const int TOPIC_SET_COUNT = 25;  // Config has 25 topic sets (00-24)

  /// <summary>
  /// Gets or initializes the shared Service Bus emulator (with Config-TopicPool.json).
  /// This is called once and reused by all test instances.
  /// </summary>
  public static async Task<DirectServiceBusEmulatorFixture> GetSharedEmulatorAsync() {
    await _emulatorLock.WaitAsync();
    try {
      if (_sharedEmulator == null) {
        Console.WriteLine("[SharedFixture] Initializing Service Bus emulator with 25 topic sets (ONE TIME COST: ~60s)...");
        _sharedEmulator = new DirectServiceBusEmulatorFixture("Config-TopicPool.json");
        await _sharedEmulator.InitializeAsync();
        Console.WriteLine($"[SharedFixture] ✅ Service Bus emulator ready with 50 topics (25 sets)!");
      }
      return _sharedEmulator;
    } finally {
      _emulatorLock.Release();
    }
  }

  /// <summary>
  /// Gets a fixture instance for a test. Each test instance gets its own fixture
  /// with a deterministic topic suffix based on test instance ID, ensuring isolation.
  /// </summary>
  [RequiresUnreferencedCode("EF Core in tests may use unreferenced code")]
  [RequiresDynamicCode("EF Core in tests may use dynamic code")]
  public static async Task<AspireIntegrationFixture> GetFixtureAsync() {
    // Get or initialize shared emulator first
    var emulator = await GetSharedEmulatorAsync();

    // Generate unique test instance ID
    var testInstanceId = Guid.NewGuid();

    // Calculate deterministic topic suffix from instance ID hash (00-24)
    var suffixIndex = Math.Abs(testInstanceId.GetHashCode()) % TOPIC_SET_COUNT;
    var topicSuffix = suffixIndex.ToString("D2");

    Console.WriteLine($"[SharedFixture] Test instance {testInstanceId:N} assigned topic suffix: {topicSuffix} (modulo {TOPIC_SET_COUNT})");

    // Create new fixture with the deterministic topic suffix
    var fixture = new AspireIntegrationFixture(emulator, topicSuffix);
    await fixture.InitializeAsync();

    return fixture;
  }

  /// <summary>
  /// Disposes a fixture (no suffix tracking needed with deterministic assignment).
  /// </summary>
  public static async Task DisposeFixtureAsync(AspireIntegrationFixture fixture) {
    await fixture.DisposeAsync();
  }

  /// <summary>
  /// Final cleanup: disposes the shared emulator when all tests complete.
  /// </summary>
  public static async Task DisposeAsync() {
    if (_sharedEmulator != null) {
      Console.WriteLine("[SharedFixture] Disposing shared Service Bus emulator...");
      await _sharedEmulator.DisposeAsync();
      _sharedEmulator = null;
    }
  }
}
