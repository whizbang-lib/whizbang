using System.Diagnostics.CodeAnalysis;
using Azure.Messaging.ServiceBus;

namespace ECommerce.Integration.Tests.Fixtures;

/// <summary>
/// Provides a shared ServiceBusIntegrationFixture for all ServiceBus integration test classes.
/// Creates the fixture once (PostgreSQL database, hosts, schema init) and reuses across tests.
/// Tests call CleanupDatabaseAsync between runs for isolation.
/// Follows the same pattern as SharedInMemoryFixtureSource.
/// </summary>
public static class SharedServiceBusFixtureSource {
  private static ServiceBusIntegrationFixture? _fixture;
  private static readonly SemaphoreSlim _lock = new(1, 1);

  [RequiresUnreferencedCode("Test code - reflection allowed")]
  [RequiresDynamicCode("Test code - reflection allowed")]
  public static async Task<ServiceBusIntegrationFixture> GetFixtureAsync() {
    await _lock.WaitAsync();
    try {
      if (_fixture == null) {
        var (connectionString, sharedClient) = await SharedFixtureSource.GetSharedResourcesAsync(0);
        _fixture = new ServiceBusIntegrationFixture(connectionString, sharedClient, 0);
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
