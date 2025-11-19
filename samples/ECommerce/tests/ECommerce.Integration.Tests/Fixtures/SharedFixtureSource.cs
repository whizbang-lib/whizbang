namespace ECommerce.Integration.Tests.Fixtures;

/// <summary>
/// Provides a shared integration fixture for all test classes.
/// TUnit will create this once and share it across all tests.
/// </summary>
public static class SharedFixtureSource {
  private static SharedIntegrationFixture? _fixture;
  private static readonly SemaphoreSlim _lock = new(1, 1);

  public static async Task<SharedIntegrationFixture> GetFixtureAsync() {
    await _lock.WaitAsync();
    try {
      if (_fixture == null) {
        _fixture = new SharedIntegrationFixture();
        await _fixture.InitializeAsync();
      }
      return _fixture;
    } finally {
      _lock.Release();
    }
  }

  public static async Task DisposeAsync() {
    if (_fixture != null) {
      await _fixture.DisposeAsync();
      _fixture = null;
    }
  }
}
