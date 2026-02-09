using ECommerce.Integration.Tests.Fixtures;

namespace ECommerce.Integration.Tests;

/// <summary>
/// Global test cleanup that disposes shared resources when the test process exits.
/// This ensures Docker containers (ServiceBus emulator) are properly cleaned up.
/// Registered via ModuleInitializer to run before any tests.
/// </summary>
public static class GlobalTestCleanup {
  /// <summary>
  /// Registers process exit handler to dispose shared resources.
  /// Called automatically before tests run via [ModuleInitializer].
  /// </summary>
  [System.Runtime.CompilerServices.ModuleInitializer]
  public static void Initialize() {
    Console.WriteLine("[GlobalTestCleanup] Registering process exit handler for shared resource cleanup...");

    // Register async cleanup handler for graceful shutdown
    AppDomain.CurrentDomain.ProcessExit += (sender, args) => {
      Console.WriteLine("[GlobalTestCleanup] Process exiting, disposing shared resources...");
      try {
        SharedFixtureSource.DisposeAsync().GetAwaiter().GetResult();
        Console.WriteLine("[GlobalTestCleanup] Shared resources disposed successfully.");
      } catch (Exception ex) {
        Console.WriteLine($"[GlobalTestCleanup] Error during cleanup: {ex.Message}");
      }
    };

    Console.WriteLine("[GlobalTestCleanup] Process exit handler registered.");
  }
}
