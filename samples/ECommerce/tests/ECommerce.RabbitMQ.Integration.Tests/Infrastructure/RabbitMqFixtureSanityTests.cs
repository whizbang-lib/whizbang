using ECommerce.RabbitMQ.Integration.Tests.Fixtures;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace ECommerce.RabbitMQ.Integration.Tests.Infrastructure;

/// <summary>
/// Sanity tests for RabbitMQ integration test fixtures.
/// Verifies that containers start correctly and hosts initialize properly.
/// Each test gets its own PostgreSQL databases + hosts. RabbitMQ container is shared via SharedRabbitMqFixtureSource.
/// Tests run sequentially for reliable timing.
/// </summary>
[NotInParallel("RabbitMQ")]
public sealed class RabbitMqFixtureSanityTests {
  private static RabbitMqIntegrationFixture? _fixture;

  [Before(Test)]
  public async Task SetupAsync() {
    _fixture = await SharedRabbitMqFixtureSource.GetFixtureAsync();
    await _fixture.CleanupDatabaseAsync();
  }

  [Test]
  public async Task Fixture_InitializesSuccessfullyAsync() {
    // Verify fixture is initialized
    await Assert.That(_fixture).IsNotNull();

    // Verify hosts are created
    await Assert.That(_fixture!.InventoryHost).IsNotNull();
    await Assert.That(_fixture!.BffHost).IsNotNull();

    Console.WriteLine("[Sanity] Fixture initialized successfully!");
  }

  [Test]
  public async Task RabbitMQ_ConnectionIsOpenAsync() {
    // Verify RabbitMQ connection string is valid
    await Assert.That(SharedRabbitMqFixtureSource.RabbitMqConnectionString).IsNotNull().And.IsNotEmpty();

    Console.WriteLine($"[Sanity] RabbitMQ connection: {SharedRabbitMqFixtureSource.RabbitMqConnectionString}");
  }

  [Test]
  public async Task PostgreSQL_ConnectionIsOpenAsync() {
    // Verify PostgreSQL connection string is valid
    await Assert.That(SharedRabbitMqFixtureSource.PostgresConnectionString).IsNotNull().And.IsNotEmpty();

    Console.WriteLine($"[Sanity] PostgreSQL connection: {SharedRabbitMqFixtureSource.PostgresConnectionString}");
  }

  [Test]
  public async Task ManagementApi_UriIsValidAsync() {
    // Verify Management API URI is valid
    await Assert.That(SharedRabbitMqFixtureSource.ManagementApiUri).IsNotNull();
    await Assert.That(SharedRabbitMqFixtureSource.ManagementApiUri.Scheme).IsEqualTo("http");

    Console.WriteLine($"[Sanity] Management API: {SharedRabbitMqFixtureSource.ManagementApiUri}");
  }

  [After(Test)]
  public Task CleanupAsync() {
    // Shared fixture is reused across tests — don't dispose
    return Task.CompletedTask;
  }
}
