using Testcontainers.Azurite;

namespace Whizbang.Offloads.AzureBlob.Integration.Tests;

/// <summary>
/// Class-scoped Azurite container fixture. Spins up one Azurite container
/// per test class (TUnit's <c>[Before(Class)]</c> + <c>[After(Class)]</c>),
/// hands out the connection string for the provider to use.
/// </summary>
/// <remarks>
/// <para>
/// Lifecycle is per-class so multiple test methods share container startup
/// cost (~2-5 seconds). Each method should target a unique container name
/// to avoid cross-test contamination.
/// </para>
/// <para>
/// Requires Docker to be running locally. If Docker is unavailable, tests
/// using this fixture report a runtime exception with a clear "Docker
/// required" message rather than silently passing.
/// </para>
/// </remarks>
public static class AzuriteFixture {
  private static AzuriteContainer? _container;
  private static readonly SemaphoreSlim _initLock = new(1, 1);

  /// <summary>
  /// Starts the Azurite container if it hasn't been started yet. Idempotent.
  /// Returns the connection string the Whizbang AzureBlob provider should use.
  /// </summary>
  public static async Task<string> EnsureStartedAsync(CancellationToken cancellationToken = default) {
    await _initLock.WaitAsync(cancellationToken);
    try {
      if (_container is null) {
        _container = new AzuriteBuilder("mcr.microsoft.com/azure-storage/azurite:latest").Build();
        await _container.StartAsync(cancellationToken);
      }
      return _container.GetConnectionString();
    } finally {
      _initLock.Release();
    }
  }

  /// <summary>
  /// Stops + disposes the container. Optional — the test process exit will
  /// clean it up anyway, but explicit dispose frees the port faster on
  /// short-lived test sessions.
  /// </summary>
  public static async Task DisposeAsync() {
    await _initLock.WaitAsync();
    try {
      if (_container is not null) {
        await _container.DisposeAsync();
        _container = null;
      }
    } finally {
      _initLock.Release();
    }
  }
}
