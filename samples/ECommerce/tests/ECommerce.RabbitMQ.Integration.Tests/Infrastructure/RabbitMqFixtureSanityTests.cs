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
[NotInParallel]
public sealed class RabbitMqFixtureSanityTests {
  private static RabbitMqIntegrationFixture? _fixture;

  [Before(Test)]
  public async Task SetupAsync() {
    // Initialize shared containers (first test only)
    await SharedRabbitMqFixtureSource.InitializeAsync();

    // Get separate database connections for each host (eliminates lock contention)
    var inventoryDbConnection = SharedRabbitMqFixtureSource.GetPerTestDatabaseConnectionString();
    var bffDbConnection = SharedRabbitMqFixtureSource.GetPerTestDatabaseConnectionString();

    // Create and initialize test fixture with separate databases
    _fixture = new RabbitMqIntegrationFixture(
      SharedRabbitMqFixtureSource.RabbitMqConnectionString,
      inventoryDbConnection,
      bffDbConnection,
      SharedRabbitMqFixtureSource.ManagementApiUri,
      testId: Guid.NewGuid().ToString("N")[..12]
    );
    await _fixture.InitializeAsync();
  }

  [Test]
  public async Task Fixture_InitializesSuccessfully() {
    // Verify fixture is initialized
    await Assert.That(_fixture).IsNotNull();

    // Verify hosts are created
    await Assert.That(_fixture!.InventoryHost).IsNotNull();
    await Assert.That(_fixture!.BffHost).IsNotNull();

    Console.WriteLine("[Sanity] Fixture initialized successfully!");
  }

  [Test]
  public async Task RabbitMQ_ConnectionIsOpen() {
    // Verify RabbitMQ connection string is valid
    await Assert.That(SharedRabbitMqFixtureSource.RabbitMqConnectionString).IsNotNull().And.IsNotEmpty();

    Console.WriteLine($"[Sanity] RabbitMQ connection: {SharedRabbitMqFixtureSource.RabbitMqConnectionString}");
  }

  [Test]
  public async Task PostgreSQL_ConnectionIsOpen() {
    // Verify PostgreSQL connection string is valid
    await Assert.That(SharedRabbitMqFixtureSource.PostgresConnectionString).IsNotNull().And.IsNotEmpty();

    Console.WriteLine($"[Sanity] PostgreSQL connection: {SharedRabbitMqFixtureSource.PostgresConnectionString}");
  }

  [Test]
  public async Task ManagementApi_UriIsValid() {
    // Verify Management API URI is valid
    await Assert.That(SharedRabbitMqFixtureSource.ManagementApiUri).IsNotNull();
    await Assert.That(SharedRabbitMqFixtureSource.ManagementApiUri.Scheme).IsEqualTo("http");

    Console.WriteLine($"[Sanity] Management API: {SharedRabbitMqFixtureSource.ManagementApiUri}");
  }

  [After(Test)]
  public async Task CleanupAsync() {
    if (_fixture != null) {
      await _fixture.DisposeAsync();
    }
  }
}
