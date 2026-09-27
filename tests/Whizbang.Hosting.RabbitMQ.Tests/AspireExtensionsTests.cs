using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Hosting.RabbitMQ.Tests;

/// <summary>
/// Tests for <see cref="AspireExtensions"/>: the declarative topology helpers record each
/// exchange and queue binding as an annotation on the RabbitMQ resource, in declaration order,
/// and hand the same builder back so an AppHost can chain its whole topology in one expression.
/// The distributed application is never built or run, so no container starts.
/// </summary>
public class AspireExtensionsTests {
  private static readonly string[] _noArgs = [];

  // --- WithExchange ---

  [Test]
  public async Task WithExchange_NameAndType_RecordsAnExchangeAnnotationAsync() {
    var rabbit = _createRabbitMq();

    rabbit.WithExchange("orders", "direct");

    var exchanges = _annotations<RabbitMQExchangeAnnotation>(rabbit);
    await Assert.That(exchanges).IsEquivalentTo([new RabbitMQExchangeAnnotation("orders", "direct")]);
  }

  [Test]
  public async Task WithExchange_TypeOmitted_DefaultsToTopicAsync() {
    // Topic is what the Whizbang RabbitMQ transport publishes to, so a bare declaration must
    // produce a topic exchange rather than an untyped one.
    var rabbit = _createRabbitMq();

    rabbit.WithExchange("orders");

    var exchanges = _annotations<RabbitMQExchangeAnnotation>(rabbit);
    await Assert.That(exchanges).IsEquivalentTo([new RabbitMQExchangeAnnotation("orders", "topic")]);
  }

  [Test]
  public async Task WithExchange_ReturnsTheSameBuilderForChainingAsync() {
    var rabbit = _createRabbitMq();

    var returned = rabbit.WithExchange("orders", "topic");

    await Assert.That(returned).IsSameReferenceAs(rabbit);
  }

  // --- WithQueueBinding ---

  [Test]
  public async Task WithQueueBinding_RecordsQueueExchangeAndRoutingKeyAsync() {
    var rabbit = _createRabbitMq();

    rabbit.WithQueueBinding("payment-worker-queue", "orders", "order.#");

    var bindings = _annotations<RabbitMQBindingAnnotation>(rabbit);
    await Assert.That(bindings.Count).IsEqualTo(1);
    await Assert.That(bindings[0].Queue).IsEqualTo("payment-worker-queue");
    await Assert.That(bindings[0].Exchange).IsEqualTo("orders");
    await Assert.That(bindings[0].RoutingKey).IsEqualTo("order.#");
  }

  [Test]
  public async Task WithQueueBinding_ReturnsTheSameBuilderForChainingAsync() {
    var rabbit = _createRabbitMq();

    var returned = rabbit.WithQueueBinding("payment-worker-queue", "orders", "#");

    await Assert.That(returned).IsSameReferenceAs(rabbit);
  }

  // --- Chained topology ---

  [Test]
  public async Task ChainedTopology_RecordsEveryDeclarationInOrderAsync() {
    var rabbit = _createRabbitMq();

    rabbit
      .WithExchange("orders")
      .WithExchange("audit", "fanout")
      .WithQueueBinding("payment-worker-queue", "orders", "order.#")
      .WithQueueBinding("audit-queue", "audit", "");

    await Assert.That(_annotations<RabbitMQExchangeAnnotation>(rabbit)).IsEquivalentTo(
      [new RabbitMQExchangeAnnotation("orders", "topic"), new RabbitMQExchangeAnnotation("audit", "fanout")],
      TUnit.Assertions.Enums.CollectionOrdering.Matching);
    await Assert.That(_annotations<RabbitMQBindingAnnotation>(rabbit)).IsEquivalentTo(
      [
        new RabbitMQBindingAnnotation("payment-worker-queue", "orders", "order.#"),
        new RabbitMQBindingAnnotation("audit-queue", "audit", "")
      ],
      TUnit.Assertions.Enums.CollectionOrdering.Matching);
  }

  // --- helpers ---

  /// <summary>
  /// Adds a RabbitMQ server resource to an Aspire application model that is never built or run.
  /// </summary>
  private static IResourceBuilder<RabbitMQServerResource> _createRabbitMq() {
    var builder = DistributedApplication.CreateBuilder(_noArgs);
    return builder.AddRabbitMQ("rabbitmq");
  }

  private static List<T> _annotations<T>(IResourceBuilder<RabbitMQServerResource> rabbit)
    where T : IResourceAnnotation =>
    [.. rabbit.Resource.Annotations.OfType<T>()];
}
