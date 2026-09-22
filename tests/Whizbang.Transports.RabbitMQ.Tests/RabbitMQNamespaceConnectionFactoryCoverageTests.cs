using Microsoft.Extensions.Logging.Abstractions;
using RabbitMQ.Client.Exceptions;
using TUnit.Assertions;
using TUnit.Core;

#pragma warning disable CA1707 // Identifiers should not contain underscores (test method names use underscores by convention)

namespace Whizbang.Transports.RabbitMQ.Tests;

/// <summary>
/// Coverage-focused tests for the internal <see cref="RabbitMQNamespaceConnectionFactory"/> —
/// the default <see cref="IRabbitMQNamespaceConnectionFactory"/> that opens a class namespace's
/// connection with the same connection-factory settings and retry policy as the default
/// namespace. It builds a real <c>RabbitMQ.Client.ConnectionFactory</c> internally and hands the
/// resulting connection attempt to <see cref="RabbitMQConnectionRetry"/>, so there is no seam to
/// substitute a fake connection here — this suite exercises it the same way
/// <c>RabbitMQConnectionRetryTests</c> exercises the retry handler itself: a syntactically valid
/// but unreachable AMQP URI (<c>invalid-host</c>, an established convention in this test suite),
/// which fails fast without a broker or any real namespace.
/// </summary>
public class RabbitMQNamespaceConnectionFactoryCoverageTests {

  // A class namespace's connection factory that can't reach its broker must fail loudly and
  // fast, naming the failure back to the caller — if it silently swallowed the failure or
  // returned a broken IConnection instead of throwing, every publish routed to that traffic
  // class would fail one at a time with no diagnosis of which namespace's connection string
  // is bad, instead of failing once, clearly, at connection time.
  [Test]
  public async Task CreateConnection_WithUnreachableNamespace_ThrowsBrokerUnreachableExceptionAsync() {
    // Arrange
    var options = new RabbitMQOptions {
      InitialRetryAttempts = 0,
      RetryIndefinitely = false
    };
    var factory = new RabbitMQNamespaceConnectionFactory(NullLogger<RabbitMQConnectionRetry>.Instance);

    // Act & Assert
    await Assert.That(() => factory.CreateConnection(
        "bulk-traffic-class",
        "amqp://invalid-host:5672/coverage-namespace",
        options))
      .Throws<BrokerUnreachableException>();
  }

  // Same failure path with no retry logger supplied (the constructor accepts a nullable
  // logger) — the factory must still build the connection factory from the namespace's own
  // options (retry delay, channel ceiling) and fail the same way, proving the logger is
  // optional plumbing, not a required collaborator for the connection attempt itself.
  [Test]
  public async Task CreateConnection_WithNoRetryLogger_StillThrowsBrokerUnreachableExceptionAsync() {
    // Arrange
    var options = new RabbitMQOptions {
      InitialRetryAttempts = 0,
      RetryIndefinitely = false,
      InitialRetryDelay = TimeSpan.FromMilliseconds(5)
    };
    var factory = new RabbitMQNamespaceConnectionFactory(retryLogger: null);

    // Act & Assert
    await Assert.That(() => factory.CreateConnection(
        "priority-traffic-class",
        "amqp://invalid-host:5672/coverage-namespace-2",
        options))
      .Throws<BrokerUnreachableException>();
  }
}
