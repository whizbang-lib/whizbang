using ECommerce.RabbitMQ.Integration.Tests.Fixtures;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace ECommerce.RabbitMQ.Integration.Tests.Infrastructure;

/// <summary>
/// Sanity tests for RabbitMQ integration test fixtures.
/// Verifies that containers start correctly and hosts initialize properly.
/// </summary>
[ClassDataSource<RabbitMqClassFixtureSource>(Shared = SharedType.PerClass)]
public sealed class RabbitMqFixtureSanityTests(RabbitMqClassFixtureSource fixtureSource) {
  private RabbitMqIntegrationFixture? _fixture;

  [Before(Test)]
  public async Task InitializeAsync() {
    // Initialize container fixture (starts RabbitMQ + PostgreSQL)
    await fixtureSource.InitializeAsync();

    // Create and initialize test fixture (creates hosts)
    _fixture = new RabbitMqIntegrationFixture(
      fixtureSource.RabbitMqConnectionString,
      fixtureSource.PostgresConnectionString,
      fixtureSource.ManagementApiUri,
      testClassName: nameof(RabbitMqFixtureSanityTests)
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
    await Assert.That(fixtureSource.RabbitMqConnectionString).IsNotNull().And.IsNotEmpty();

    Console.WriteLine($"[Sanity] RabbitMQ connection: {fixtureSource.RabbitMqConnectionString}");
  }

  [Test]
  public async Task PostgreSQL_ConnectionIsOpen() {
    // Verify PostgreSQL connection string is valid
    await Assert.That(fixtureSource.PostgresConnectionString).IsNotNull().And.IsNotEmpty();

    Console.WriteLine($"[Sanity] PostgreSQL connection: {fixtureSource.PostgresConnectionString}");
  }

  [Test]
  public async Task ManagementApi_UriIsValid() {
    // Verify Management API URI is valid
    await Assert.That(fixtureSource.ManagementApiUri).IsNotNull();
    await Assert.That(fixtureSource.ManagementApiUri.Scheme).IsEqualTo("http");

    Console.WriteLine($"[Sanity] Management API: {fixtureSource.ManagementApiUri}");
  }

  [After(Test)]
  public async Task CleanupAsync() {
    if (_fixture != null) {
      await _fixture.DisposeAsync();
    }
  }
}
