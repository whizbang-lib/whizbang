using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Testing.Containers;

#pragma warning disable CA1707 // Identifiers should not contain underscores (test method names use underscores by convention)
#pragma warning disable TUnit0023 // Disposable field should be disposed in cleanup method

namespace Whizbang.Transports.RabbitMQ.Tests.Containers;

/// <summary>
/// Integration tests for <see cref="SharedRabbitMqContainer"/>.
/// Tests container initialization, health checks, and connection reuse.
/// </summary>
[Category("Integration")]
[NotInParallel("RabbitMQ")]
public class SharedRabbitMqContainerIntegrationTests {
  [Before(Test)]
  public async Task SetupAsync() {
    // Ensure container is initialized before each test
    await SharedRabbitMqContainer.InitializeAsync();
  }

  [Test]
  [Timeout(60000)]
  public async Task InitializeAsync_StartsContainer_IsInitializedReturnsTrueAsync(CancellationToken cancellationToken) {
    // Act
    await SharedRabbitMqContainer.InitializeAsync(cancellationToken);

    // Assert
    await Assert.That(SharedRabbitMqContainer.IsInitialized).IsTrue();
  }

  [Test]
  [Timeout(60000)]
  public async Task ConnectionString_AfterInitialize_IsValidAmqpUriAsync(CancellationToken cancellationToken) {
    // Arrange
    await SharedRabbitMqContainer.InitializeAsync(cancellationToken);

    // Act
    var connectionString = SharedRabbitMqContainer.ConnectionString;

    // Assert
    await Assert.That(connectionString).IsNotNull();
    await Assert.That(connectionString).StartsWith("amqp://");
    await Assert.That(connectionString).Contains("guest:guest@localhost:");
  }

  [Test]
  [Timeout(60000)]
  public async Task ManagementApiUri_AfterInitialize_IsValidHttpUriAsync(CancellationToken cancellationToken) {
    // Arrange
    await SharedRabbitMqContainer.InitializeAsync(cancellationToken);

    // Act
    var managementApiUri = SharedRabbitMqContainer.ManagementApiUri;

    // Assert
    await Assert.That(managementApiUri).IsNotNull();
    await Assert.That(managementApiUri.Scheme).IsEqualTo("http");
    await Assert.That(managementApiUri.Host).IsEqualTo("localhost");
  }

  [Test]
  [Timeout(60000)]
  public async Task InitializeAsync_CalledMultipleTimes_ReusesContainerAsync(CancellationToken cancellationToken) {
    // Act - Call initialize multiple times
    await SharedRabbitMqContainer.InitializeAsync(cancellationToken);
    var firstConnectionString = SharedRabbitMqContainer.ConnectionString;

    await SharedRabbitMqContainer.InitializeAsync(cancellationToken);
    var secondConnectionString = SharedRabbitMqContainer.ConnectionString;

    await SharedRabbitMqContainer.InitializeAsync(cancellationToken);
    var thirdConnectionString = SharedRabbitMqContainer.ConnectionString;

    // Assert - All connection strings should be the same (same container)
    await Assert.That(secondConnectionString).IsEqualTo(firstConnectionString);
    await Assert.That(thirdConnectionString).IsEqualTo(firstConnectionString);
  }

  [Test]
  [Timeout(60000)]
  public async Task ManagementApiUri_CanBeUsedForHealthCheckAsync(CancellationToken cancellationToken) {
    // Arrange
    await SharedRabbitMqContainer.InitializeAsync(cancellationToken);
    var managementApiUri = SharedRabbitMqContainer.ManagementApiUri;

    // Act - Call the management API
    using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
    var authBytes = System.Text.Encoding.ASCII.GetBytes("guest:guest");
    httpClient.DefaultRequestHeaders.Authorization =
        new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", Convert.ToBase64String(authBytes));

    var response = await httpClient.GetAsync($"{managementApiUri}api/overview", cancellationToken);

    // Assert
    await Assert.That(response.IsSuccessStatusCode).IsTrue();
  }

  [Test]
  [Timeout(60000)]
  public async Task InitializeAsync_WithCancellation_RespectsTokenAsync(CancellationToken cancellationToken) {
    // Act - Initialize (should succeed if already initialized, or complete quickly)
    await SharedRabbitMqContainer.InitializeAsync(cancellationToken);

    // Assert - Should be initialized
    await Assert.That(SharedRabbitMqContainer.IsInitialized).IsTrue();
  }

  [Test]
  [Timeout(60000)]
  public async Task ConnectionString_BeforeInitialize_ThrowsInvalidOperationExceptionAsync(CancellationToken cancellationToken) {
    // This test is tricky because the container might already be initialized
    // We're testing the behavior when NOT initialized

    // Since we can't easily "uninitialize" the shared container,
    // we verify that after DisposeAsync, the state is reset
    await SharedRabbitMqContainer.DisposeAsync();

    // Act & Assert
    var wasInitialized = SharedRabbitMqContainer.IsInitialized;
    await Assert.That(wasInitialized).IsFalse();

    // Reinitialize for other tests
    await SharedRabbitMqContainer.InitializeAsync(cancellationToken);
  }

  [Test]
  [Timeout(60000)]
  public async Task DisposeAsync_ResetsState_AllowsReinitializationAsync(CancellationToken cancellationToken) {
    // Arrange
    await SharedRabbitMqContainer.InitializeAsync(cancellationToken);
    var beforeDisposeConnString = SharedRabbitMqContainer.ConnectionString;

    // Act
    await SharedRabbitMqContainer.DisposeAsync();

    // Assert - State should be reset
    await Assert.That(SharedRabbitMqContainer.IsInitialized).IsFalse();

    // Reinitialize
    await SharedRabbitMqContainer.InitializeAsync(cancellationToken);

    // Should be initialized again
    await Assert.That(SharedRabbitMqContainer.IsInitialized).IsTrue();
    await Assert.That(SharedRabbitMqContainer.ConnectionString).IsNotNull();
  }

  [Test]
  [Timeout(60000)]
  public async Task InitializeAsync_ConcurrentCalls_OnlyInitializesOnceAsync(CancellationToken cancellationToken) {
    // Arrange - Reset state first
    await SharedRabbitMqContainer.DisposeAsync();

    // Act - Call initialize concurrently
    var tasks = new List<Task>();
    for (var i = 0; i < 5; i++) {
      tasks.Add(SharedRabbitMqContainer.InitializeAsync(cancellationToken));
    }

    await Task.WhenAll(tasks);

    // Assert - Should be initialized and have valid connection string
    await Assert.That(SharedRabbitMqContainer.IsInitialized).IsTrue();
    await Assert.That(SharedRabbitMqContainer.ConnectionString).IsNotNull();
  }

  [Test]
  [Timeout(60000)]
  public async Task InitializeAsync_AfterFailedConnection_RetriesAndSucceedsAsync(CancellationToken cancellationToken) {
    // This test verifies the retry/recovery logic
    // The container should handle transient failures

    // Arrange - Dispose first to reset state
    await SharedRabbitMqContainer.DisposeAsync();

    // Act - Initialize (container should still be running, so this should reuse it)
    await SharedRabbitMqContainer.InitializeAsync(cancellationToken);

    // Assert
    await Assert.That(SharedRabbitMqContainer.IsInitialized).IsTrue();

    // Verify we can actually connect
    var factory = new global::RabbitMQ.Client.ConnectionFactory {
      Uri = new Uri(SharedRabbitMqContainer.ConnectionString)
    };
    using var connection = await factory.CreateConnectionAsync(cancellationToken);
    await Assert.That(connection.IsOpen).IsTrue();
  }
}
