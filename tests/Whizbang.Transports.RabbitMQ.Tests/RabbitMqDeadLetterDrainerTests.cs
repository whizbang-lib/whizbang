using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Transports.RabbitMQ;

namespace Whizbang.Transports.RabbitMQ.Tests;

/// <summary>
/// v0.502 slice C.9 — unit regression locks for <see cref="RabbitMqDeadLetterDrainer"/>.
/// Covers constructor argument-validation against a null connection (the one path that
/// doesn't need a real broker), and the internal <c>x-death</c> header parser which is
/// pure logic and where the bulk of the coverage value lives. The receive/republish
/// broker interaction is covered by the integration suite.
/// </summary>
public class RabbitMqDeadLetterDrainerTests {

  // ===== Constructor =====

  [Test]
  public async Task Constructor_NullConnection_ThrowsArgumentNullExceptionAsync() {
    await Assert.That(() => new RabbitMqDeadLetterDrainer(
      connection: null!,
      dlqName: "q",
      fallbackExchange: "ex",
      logger: NullLogger<RabbitMqDeadLetterDrainer>.Instance))
      .Throws<ArgumentNullException>();
  }

  // ===== ResolveOriginalDestination — null/empty header paths =====

  [Test]
  public async Task ResolveOriginalDestination_NullHeaders_ReturnsFallbackAsync() {
    var result = RabbitMqDeadLetterDrainer.ResolveOriginalDestination(
      headers: null,
      fallbackRoutingKey: "rk-fallback",
      fallbackExchange: "ex-fallback");
    await Assert.That(result.Exchange).IsEqualTo("ex-fallback");
    await Assert.That(result.RoutingKey).IsEqualTo("rk-fallback");
  }

  [Test]
  public async Task ResolveOriginalDestination_HeadersWithoutXDeath_ReturnsFallbackAsync() {
    var headers = new Dictionary<string, object?> {
      ["some-other-header"] = "value",
    };
    var result = RabbitMqDeadLetterDrainer.ResolveOriginalDestination(
      headers, fallbackRoutingKey: "rk", fallbackExchange: "ex");
    await Assert.That(result.Exchange).IsEqualTo("ex");
    await Assert.That(result.RoutingKey).IsEqualTo("rk");
  }

  [Test]
  public async Task ResolveOriginalDestination_XDeathWrongType_ReturnsFallbackAsync() {
    var headers = new Dictionary<string, object?> {
      ["x-death"] = "not-a-list",
    };
    var result = RabbitMqDeadLetterDrainer.ResolveOriginalDestination(
      headers, fallbackRoutingKey: "rk", fallbackExchange: "ex");
    await Assert.That(result.Exchange).IsEqualTo("ex");
  }

  [Test]
  public async Task ResolveOriginalDestination_XDeathEmptyList_ReturnsFallbackAsync() {
    var headers = new Dictionary<string, object?> {
      ["x-death"] = new List<object?>(),
    };
    var result = RabbitMqDeadLetterDrainer.ResolveOriginalDestination(
      headers, fallbackRoutingKey: "rk", fallbackExchange: "ex");
    await Assert.That(result.Exchange).IsEqualTo("ex");
  }

  [Test]
  public async Task ResolveOriginalDestination_XDeathFirstElementWrongType_ReturnsFallbackAsync() {
    var headers = new Dictionary<string, object?> {
      ["x-death"] = new List<object?> { "wrong-shape" },
    };
    var result = RabbitMqDeadLetterDrainer.ResolveOriginalDestination(
      headers, fallbackRoutingKey: "rk", fallbackExchange: "ex");
    await Assert.That(result.Exchange).IsEqualTo("ex");
  }

  // ===== ResolveOriginalDestination — happy path =====

  [Test]
  public async Task ResolveOriginalDestination_XDeathPresent_ExtractsExchangeAndRoutingKeyAsync() {
    var headers = new Dictionary<string, object?> {
      ["x-death"] = new List<object?> {
        new Dictionary<string, object?> {
          ["exchange"] = System.Text.Encoding.UTF8.GetBytes("orders.exchange"),
          ["routing-keys"] = new List<object?> {
            System.Text.Encoding.UTF8.GetBytes("orders.created"),
          },
        },
      },
    };
    var result = RabbitMqDeadLetterDrainer.ResolveOriginalDestination(
      headers, fallbackRoutingKey: "rk-fallback", fallbackExchange: "ex-fallback");
    await Assert.That(result.Exchange).IsEqualTo("orders.exchange");
    await Assert.That(result.RoutingKey).IsEqualTo("orders.created");
  }

  [Test]
  public async Task ResolveOriginalDestination_OnlyExchangeInXDeath_FallsBackForRoutingKeyAsync() {
    var headers = new Dictionary<string, object?> {
      ["x-death"] = new List<object?> {
        new Dictionary<string, object?> {
          ["exchange"] = System.Text.Encoding.UTF8.GetBytes("orders.exchange"),
          // routing-keys missing
        },
      },
    };
    var result = RabbitMqDeadLetterDrainer.ResolveOriginalDestination(
      headers, fallbackRoutingKey: "rk-fallback", fallbackExchange: "ex-fallback");
    await Assert.That(result.Exchange).IsEqualTo("orders.exchange");
    await Assert.That(result.RoutingKey).IsEqualTo("rk-fallback");
  }

  [Test]
  public async Task ResolveOriginalDestination_OnlyRoutingKeyInXDeath_FallsBackForExchangeAsync() {
    var headers = new Dictionary<string, object?> {
      ["x-death"] = new List<object?> {
        new Dictionary<string, object?> {
          // exchange missing
          ["routing-keys"] = new List<object?> {
            System.Text.Encoding.UTF8.GetBytes("orders.created"),
          },
        },
      },
    };
    var result = RabbitMqDeadLetterDrainer.ResolveOriginalDestination(
      headers, fallbackRoutingKey: "rk-fallback", fallbackExchange: "ex-fallback");
    await Assert.That(result.Exchange).IsEqualTo("ex-fallback");
    await Assert.That(result.RoutingKey).IsEqualTo("orders.created");
  }

  [Test]
  public async Task ResolveOriginalDestination_XDeathExchangeNonByteArray_FallsBackForExchangeAsync() {
    var headers = new Dictionary<string, object?> {
      ["x-death"] = new List<object?> {
        new Dictionary<string, object?> {
          ["exchange"] = "string-not-bytes",  // wrong type
          ["routing-keys"] = new List<object?> {
            System.Text.Encoding.UTF8.GetBytes("orders.created"),
          },
        },
      },
    };
    var result = RabbitMqDeadLetterDrainer.ResolveOriginalDestination(
      headers, fallbackRoutingKey: "rk", fallbackExchange: "ex-fallback");
    await Assert.That(result.Exchange).IsEqualTo("ex-fallback");
    await Assert.That(result.RoutingKey).IsEqualTo("orders.created");
  }

  [Test]
  public async Task ResolveOriginalDestination_RoutingKeysFirstElementNonByteArray_FallsBackAsync() {
    var headers = new Dictionary<string, object?> {
      ["x-death"] = new List<object?> {
        new Dictionary<string, object?> {
          ["exchange"] = System.Text.Encoding.UTF8.GetBytes("orders.exchange"),
          ["routing-keys"] = new List<object?> {
            "string-not-bytes",  // wrong type
          },
        },
      },
    };
    var result = RabbitMqDeadLetterDrainer.ResolveOriginalDestination(
      headers, fallbackRoutingKey: "rk-fallback", fallbackExchange: "ex");
    await Assert.That(result.Exchange).IsEqualTo("orders.exchange");
    await Assert.That(result.RoutingKey).IsEqualTo("rk-fallback");
  }

  [Test]
  public async Task ResolveOriginalDestination_RoutingKeysEmptyList_FallsBackAsync() {
    var headers = new Dictionary<string, object?> {
      ["x-death"] = new List<object?> {
        new Dictionary<string, object?> {
          ["exchange"] = System.Text.Encoding.UTF8.GetBytes("orders.exchange"),
          ["routing-keys"] = new List<object?>(),
        },
      },
    };
    var result = RabbitMqDeadLetterDrainer.ResolveOriginalDestination(
      headers, fallbackRoutingKey: "rk-fallback", fallbackExchange: "ex");
    await Assert.That(result.Exchange).IsEqualTo("orders.exchange");
    await Assert.That(result.RoutingKey).IsEqualTo("rk-fallback");
  }
}
