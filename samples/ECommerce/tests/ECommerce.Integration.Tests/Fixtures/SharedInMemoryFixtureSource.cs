using System.Diagnostics.CodeAnalysis;

namespace ECommerce.Integration.Tests.Fixtures;

/// <summary>
/// Provides a shared InMemoryIntegrationFixture for all in-memory integration test classes.
/// TUnit will create this once and share it across all in-memory tests.
/// Uses InProcessTransport for fast, deterministic testing without Service Bus infrastructure.
/// </summary>
public static class SharedInMemoryFixtureSource {
  private static InMemoryIntegrationFixture? _fixture;
  private static readonly SemaphoreSlim _lock = new(1, 1);

  [RequiresUnreferencedCode("EF Core in tests may use unreferenced code")]
  [RequiresDynamicCode("EF Core in tests may use dynamic code")]
  public static async Task<InMemoryIntegrationFixture> GetFixtureAsync() {
    await _lock.WaitAsync();
    try {
      if (_fixture == null) {
        _fixture = new InMemoryIntegrationFixture();
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
