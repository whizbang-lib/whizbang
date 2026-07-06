using Microsoft.Extensions.Logging.Abstractions;
using RabbitMQ.Client;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Transports.RabbitMQ;

namespace Whizbang.Transports.RabbitMQ.Tests;

/// <summary>
/// v0.502 slice C.9 — unit regression locks for <see cref="RabbitMqDeadLetterDrainer"/>.
/// Covers the constructor argument-validation surface, the internal <c>x-death</c> header
/// parser (pure logic), and the poll/republish/settle drain loop — the loop is exercised
/// against fake <see cref="IConnection"/>/<see cref="IChannel"/> doubles (RabbitMQ.Client
/// exposes interfaces, so no broker is required). End-to-end broker behavior is covered by
/// the integration suite.
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

  [Test]
  public async Task Constructor_EmptyDlqName_ThrowsArgumentExceptionAsync() {
    var connection = _connectionFor(new DrainFakeChannel());
    await Assert.That(() => new RabbitMqDeadLetterDrainer(
      connection,
      dlqName: "",
      fallbackExchange: "ex",
      logger: NullLogger<RabbitMqDeadLetterDrainer>.Instance))
      .Throws<ArgumentException>();
  }

  [Test]
  public async Task Constructor_WhitespaceDlqName_ThrowsArgumentExceptionAsync() {
    var connection = _connectionFor(new DrainFakeChannel());
    await Assert.That(() => new RabbitMqDeadLetterDrainer(
      connection,
      dlqName: "   ",
      fallbackExchange: "ex",
      logger: NullLogger<RabbitMqDeadLetterDrainer>.Instance))
      .Throws<ArgumentException>();
  }

  [Test]
  public async Task Constructor_NullLogger_AcceptedFallsBackToNullLoggerAsync() {
    // Logger is documented as optional; constructor accepts null and substitutes NullLogger.
    var channel = new DrainFakeChannel();
    var drainer = new RabbitMqDeadLetterDrainer(
      _connectionFor(channel),
      dlqName: "orders.dlq",
      fallbackExchange: "ex",
      logger: null!);

    var drained = await drainer.DrainDeadLetterQueueAsync(maxCount: 1);

    await Assert.That(drained).IsEqualTo(0);
  }

  // ===== TransportName =====

  [Test]
  public async Task TransportName_FormatsAsRmqDlqAsync() {
    var drainer = _newDrainer(_connectionFor(new DrainFakeChannel()), dlqName: "orders.dlq");
    // The format contract is "rmq:{dlq}" — this is the OTEL metric dimension that
    // dashboards key on. Locking it.
    await Assert.That(drainer.TransportName).IsEqualTo("rmq:orders.dlq");
  }

  // ===== DrainDeadLetterQueueAsync — guards =====

  [Test]
  public async Task DrainDeadLetterQueueAsync_MaxCountZero_ReturnsZeroWithoutCreatingChannelAsync() {
    var channelCreations = 0;
    var connection = new FakeConnection(() => {
      channelCreations++;
      return Task.FromResult<IChannel>(new DrainFakeChannel());
    });
    var drainer = _newDrainer(connection);

    var drained = await drainer.DrainDeadLetterQueueAsync(maxCount: 0);

    await Assert.That(drained).IsEqualTo(0);
    await Assert.That(channelCreations).IsEqualTo(0);
  }

  [Test]
  public async Task DrainDeadLetterQueueAsync_NegativeMaxCount_ReturnsZeroAsync() {
    var channelCreations = 0;
    var connection = new FakeConnection(() => {
      channelCreations++;
      return Task.FromResult<IChannel>(new DrainFakeChannel());
    });
    var drainer = _newDrainer(connection);

    var drained = await drainer.DrainDeadLetterQueueAsync(maxCount: -5);

    await Assert.That(drained).IsEqualTo(0);
    await Assert.That(channelCreations).IsEqualTo(0);
  }

  // ===== DrainDeadLetterQueueAsync — drain loop =====

  /// <summary>
  /// Empty DLQ: BasicGet returns null on the first poll and the loop exits with zero.
  /// Also locks the polling contract — manual-ack against the configured DLQ — and that
  /// the per-drain channel is disposed.
  /// </summary>
  [Test]
  public async Task DrainDeadLetterQueueAsync_EmptyQueue_ReturnsZeroAndDisposesChannelAsync() {
    var channel = new DrainFakeChannel();
    var drainer = _newDrainer(_connectionFor(channel), dlqName: "orders.dlq");

    var drained = await drainer.DrainDeadLetterQueueAsync(maxCount: 10);

    await Assert.That(drained).IsEqualTo(0);
    await Assert.That(channel.BasicGetCalls).IsEqualTo(1);
    await Assert.That(channel.LastGetQueue).IsEqualTo("orders.dlq");
    await Assert.That(channel.LastGetAutoAck).IsEqualTo(false);
    await Assert.That(channel.IsDisposed).IsTrue();
  }

  /// <summary>
  /// Happy path: each message is republished to the exchange/routing-key recorded in its
  /// x-death header (body and headers preserved) and then acked off the DLQ.
  /// </summary>
  [Test]
  public async Task DrainDeadLetterQueueAsync_MessagesWithXDeath_RepublishesToOriginalDestinationAsync() {
    var channel = new DrainFakeChannel();
    channel.GetResults.Enqueue(_dlqResult(1, headers: _xDeathHeaders("orders.exchange", "orders.created"), body: "payload-1"));
    channel.GetResults.Enqueue(_dlqResult(2, headers: _xDeathHeaders("orders.exchange", "orders.created"), body: "payload-2"));
    var drainer = _newDrainer(_connectionFor(channel));

    var drained = await drainer.DrainDeadLetterQueueAsync(maxCount: 10);

    await Assert.That(drained).IsEqualTo(2);
    await Assert.That(channel.Published).Count().IsEqualTo(2);
    await Assert.That(channel.Published[0].Exchange).IsEqualTo("orders.exchange");
    await Assert.That(channel.Published[0].RoutingKey).IsEqualTo("orders.created");
    await Assert.That(System.Text.Encoding.UTF8.GetString(channel.Published[0].Body)).IsEqualTo("payload-1");
    await Assert.That(System.Text.Encoding.UTF8.GetString(channel.Published[1].Body)).IsEqualTo("payload-2");
    await Assert.That(channel.AckedTags).Count().IsEqualTo(2);
    await Assert.That(channel.AckedTags[0]).IsEqualTo(1UL);
    await Assert.That(channel.AckedTags[1]).IsEqualTo(2UL);
    await Assert.That(channel.NackedTags).Count().IsEqualTo(0);
    var headers = channel.PublishedProperties[0].Headers;
    await Assert.That(headers).IsNotNull();
    await Assert.That(headers!.ContainsKey("x-death")).IsTrue()
      .Because("republish must preserve the original headers");
  }

  /// <summary>
  /// A message whose x-death header was stripped falls back to the configured exchange and
  /// the message's current routing key.
  /// </summary>
  [Test]
  public async Task DrainDeadLetterQueueAsync_MessageWithoutXDeath_UsesFallbackExchangeAsync() {
    var channel = new DrainFakeChannel();
    channel.GetResults.Enqueue(_dlqResult(1, headers: null, routingKey: "current-rk"));
    var drainer = _newDrainer(_connectionFor(channel), fallbackExchange: "fallback.exchange");

    var drained = await drainer.DrainDeadLetterQueueAsync(maxCount: 10);

    await Assert.That(drained).IsEqualTo(1);
    await Assert.That(channel.Published).Count().IsEqualTo(1);
    await Assert.That(channel.Published[0].Exchange).IsEqualTo("fallback.exchange");
    await Assert.That(channel.Published[0].RoutingKey).IsEqualTo("current-rk");
  }

  /// <summary>A null fallback exchange is coalesced to the default ("") exchange.</summary>
  [Test]
  public async Task DrainDeadLetterQueueAsync_NullFallbackExchange_CoalescesToEmptyStringAsync() {
    var channel = new DrainFakeChannel();
    channel.GetResults.Enqueue(_dlqResult(1, headers: null));
    // Null fallback intentionally exercises the constructor's null-coalescing arm.
    var drainer = new RabbitMqDeadLetterDrainer(
      _connectionFor(channel),
      dlqName: "orders.dlq",
      fallbackExchange: null!,
      logger: NullLogger<RabbitMqDeadLetterDrainer>.Instance);

    var drained = await drainer.DrainDeadLetterQueueAsync(maxCount: 10);

    await Assert.That(drained).IsEqualTo(1);
    await Assert.That(channel.Published[0].Exchange).IsEqualTo("");
  }

  /// <summary>The loop stops polling exactly at maxCount even if the DLQ has more messages.</summary>
  [Test]
  public async Task DrainDeadLetterQueueAsync_MaxCountReached_StopsPollingAsync() {
    var channel = new DrainFakeChannel();
    channel.GetResults.Enqueue(_dlqResult(1, headers: null));
    channel.GetResults.Enqueue(_dlqResult(2, headers: null));
    channel.GetResults.Enqueue(_dlqResult(3, headers: null));
    var drainer = _newDrainer(_connectionFor(channel));

    var drained = await drainer.DrainDeadLetterQueueAsync(maxCount: 2);

    await Assert.That(drained).IsEqualTo(2);
    await Assert.That(channel.BasicGetCalls).IsEqualTo(2);
    await Assert.That(channel.GetResults).Count().IsEqualTo(1)
      .Because("the third message must stay in the DLQ for the next sweep");
  }

  /// <summary>
  /// A publish failure nacks the message back onto the DLQ (requeue=true) and the loop
  /// continues with the next message.
  /// </summary>
  [Test]
  public async Task DrainDeadLetterQueueAsync_PublishFails_NacksWithRequeueAndContinuesAsync() {
    var channel = new DrainFakeChannel();
    channel.PublishOutcomes.Enqueue(new InvalidOperationException("publish failed"));
    channel.PublishOutcomes.Enqueue(null);
    channel.GetResults.Enqueue(_dlqResult(1, headers: null));
    channel.GetResults.Enqueue(_dlqResult(2, headers: null));
    var drainer = _newDrainer(_connectionFor(channel));

    var drained = await drainer.DrainDeadLetterQueueAsync(maxCount: 10);

    await Assert.That(drained).IsEqualTo(1);
    await Assert.That(channel.NackedTags).Count().IsEqualTo(1);
    await Assert.That(channel.NackedTags[0].DeliveryTag).IsEqualTo(1UL);
    await Assert.That(channel.NackedTags[0].Requeue).IsTrue()
      .Because("requeue=true keeps the message in the DLQ instead of dropping it");
    await Assert.That(channel.AckedTags).Count().IsEqualTo(1);
    await Assert.That(channel.AckedTags[0]).IsEqualTo(2UL);
  }

  /// <summary>
  /// A nack failure on the cleanup path is swallowed (best-effort) and the loop continues.
  /// </summary>
  [Test]
  public async Task DrainDeadLetterQueueAsync_PublishAndNackBothFail_SwallowsAndContinuesAsync() {
    var channel = new DrainFakeChannel();
    channel.PublishOutcomes.Enqueue(new InvalidOperationException("publish failed"));
    channel.PublishOutcomes.Enqueue(null);
    channel.NackException = new InvalidOperationException("nack failed");
    channel.GetResults.Enqueue(_dlqResult(1, headers: null));
    channel.GetResults.Enqueue(_dlqResult(2, headers: null));
    var drainer = _newDrainer(_connectionFor(channel));

    var drained = await drainer.DrainDeadLetterQueueAsync(maxCount: 10);

    await Assert.That(drained).IsEqualTo(1)
      .Because("the nack failure is best-effort cleanup and must not abort the sweep");
    await Assert.That(channel.NackedTags).Count().IsEqualTo(1);
    await Assert.That(channel.AckedTags).Count().IsEqualTo(1);
  }

  /// <summary>
  /// Cancellation raised after a message settles is honored at the loop condition: the
  /// drainer returns the partial count instead of polling again.
  /// </summary>
  [Test]
  public async Task DrainDeadLetterQueueAsync_CancelledAfterFirstMessage_ReturnsPartialCountAsync() {
    using var cts = new CancellationTokenSource();
    var channel = new DrainFakeChannel {
      OnAck = cts.Cancel,
    };
    channel.GetResults.Enqueue(_dlqResult(1, headers: null));
    channel.GetResults.Enqueue(_dlqResult(2, headers: null));
    var drainer = _newDrainer(_connectionFor(channel));

    var drained = await drainer.DrainDeadLetterQueueAsync(maxCount: 10, cts.Token);

    await Assert.That(drained).IsEqualTo(1);
    await Assert.That(channel.BasicGetCalls).IsEqualTo(1)
      .Because("the cancelled token must stop the loop before a second poll");
  }

  /// <summary>A pre-cancelled token exits before the first poll.</summary>
  [Test]
  public async Task DrainDeadLetterQueueAsync_PreCancelledToken_ReturnsZeroWithoutPollingAsync() {
    using var cts = new CancellationTokenSource();
    await cts.CancelAsync();
    var channel = new DrainFakeChannel();
    channel.GetResults.Enqueue(_dlqResult(1, headers: null));
    var drainer = _newDrainer(_connectionFor(channel));

    var drained = await drainer.DrainDeadLetterQueueAsync(maxCount: 10, cts.Token);

    await Assert.That(drained).IsEqualTo(0);
    await Assert.That(channel.BasicGetCalls).IsEqualTo(0);
    await Assert.That(channel.IsDisposed).IsTrue();
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

  // ===== Helpers =====

  private static RabbitMqDeadLetterDrainer _newDrainer(
    IConnection connection,
    string dlqName = "orders.dlq",
    string fallbackExchange = "fallback.exchange") =>
    new(connection, dlqName, fallbackExchange, NullLogger<RabbitMqDeadLetterDrainer>.Instance);

  private static FakeConnection _connectionFor(DrainFakeChannel channel) =>
    new(() => Task.FromResult<IChannel>(channel));

  /// <summary>Builds a broker-shaped BasicGetResult carrying the given headers and body.</summary>
  private static BasicGetResult _dlqResult(
    ulong deliveryTag,
    IDictionary<string, object?>? headers,
    string routingKey = "current-rk",
    string body = "dlq-body") {
    var properties = new BasicProperties {
      Headers = headers,
    };
    return new BasicGetResult(
      deliveryTag: deliveryTag,
      redelivered: false,
      exchange: "",
      routingKey: routingKey,
      messageCount: 0,
      basicProperties: properties,
      body: System.Text.Encoding.UTF8.GetBytes(body));
  }

  /// <summary>Builds the x-death header shape RabbitMQ adds when a message enters DLQ.</summary>
  private static Dictionary<string, object?> _xDeathHeaders(string exchange, string routingKey) => new() {
    ["x-death"] = new List<object?> {
      new Dictionary<string, object?> {
        ["exchange"] = System.Text.Encoding.UTF8.GetBytes(exchange),
        ["routing-keys"] = new List<object?> {
          System.Text.Encoding.UTF8.GetBytes(routingKey),
        },
      },
    },
  };

  // ===== Drain-loop test double =====

  /// <summary>
  /// Channel fake for the drain loop. Derives from the shared <see cref="FakeChannel"/> and
  /// re-implements <see cref="IChannel"/> so the members the drainer touches (BasicGet /
  /// BasicPublish / BasicAck / BasicNack) are replaced with recording versions that support
  /// queued results and per-call failure injection.
  /// </summary>
  private sealed class DrainFakeChannel : FakeChannel, IChannel {
    public Queue<BasicGetResult?> GetResults { get; } = new();
    public Queue<Exception?> PublishOutcomes { get; } = new();
    public List<(string Exchange, string RoutingKey, byte[] Body)> Published { get; } = [];
    public List<IReadOnlyBasicProperties> PublishedProperties { get; } = [];
    public List<ulong> AckedTags { get; } = [];
    public List<(ulong DeliveryTag, bool Requeue)> NackedTags { get; } = [];
    public Exception? NackException { get; set; }
    public Action? OnAck { get; set; }
    public int BasicGetCalls { get; private set; }
    public string? LastGetQueue { get; private set; }
    public bool? LastGetAutoAck { get; private set; }

    public new Task<BasicGetResult?> BasicGetAsync(string queue, bool autoAck, CancellationToken cancellationToken = default) {
      BasicGetCalls++;
      LastGetQueue = queue;
      LastGetAutoAck = autoAck;
      var result = GetResults.Count > 0 ? GetResults.Dequeue() : null;
      return Task.FromResult(result);
    }

    public new ValueTask BasicPublishAsync<TProperties>(
      string exchange,
      string routingKey,
      bool mandatory,
      TProperties basicProperties,
      ReadOnlyMemory<byte> body = default,
      CancellationToken cancellationToken = default) where TProperties : IReadOnlyBasicProperties, IAmqpHeader {
      var outcome = PublishOutcomes.Count > 0 ? PublishOutcomes.Dequeue() : null;
      if (outcome is not null) {
        throw outcome;
      }
      Published.Add((exchange, routingKey, body.ToArray()));
      PublishedProperties.Add(basicProperties);
      return ValueTask.CompletedTask;
    }

    public new ValueTask BasicAckAsync(ulong deliveryTag, bool multiple, CancellationToken cancellationToken = default) {
      AckedTags.Add(deliveryTag);
      OnAck?.Invoke();
      return ValueTask.CompletedTask;
    }

    public new ValueTask BasicNackAsync(ulong deliveryTag, bool multiple, bool requeue, CancellationToken cancellationToken = default) {
      // The attempt is recorded before the injected failure is thrown so swallow behavior
      // stays observable.
      NackedTags.Add((deliveryTag, requeue));
      if (NackException is not null) {
        throw NackException;
      }
      return ValueTask.CompletedTask;
    }
  }
}
