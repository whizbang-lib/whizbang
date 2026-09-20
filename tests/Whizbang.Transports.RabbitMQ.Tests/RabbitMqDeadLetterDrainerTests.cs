using Microsoft.Extensions.Logging.Abstractions;
using RabbitMQ.Client;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Transports;
using Whizbang.Transports.RabbitMQ;

namespace Whizbang.Transports.RabbitMQ.Tests;

/// <summary>
/// Unit coverage for <see cref="RabbitMqDeadLetterDrainer"/> — construction guards, the drain
/// loop's import→ack/requeue settlement semantics, and the internal <c>TryBuildImport</c> /
/// <c>ParseXDeath</c> broker-message → custody-record mapping (raw body, no deserialization).
/// </summary>
/// <code-under-test>src/Whizbang.Transports.RabbitMQ/RabbitMqDeadLetterDrainer.cs</code-under-test>
public class RabbitMqDeadLetterDrainerTests {

  private static readonly string _id1 = "00000000-0000-0000-0000-000000000001";
  private static readonly string _id2 = "00000000-0000-0000-0000-000000000002";

  private static Func<BrokerDeadLetterImport, CancellationToken, Task<bool>> _noopImport =>
    (_, _) => Task.FromResult(true);

  // ===== Constructor =====

  [Test]
  public async Task Constructor_NullConnection_ThrowsAsync() {
    await Assert.That(() => new RabbitMqDeadLetterDrainer(
      connection: null!, dlqName: "q.dlq", importAsync: _noopImport,
      logger: NullLogger<RabbitMqDeadLetterDrainer>.Instance))
      .Throws<ArgumentNullException>();
  }

  [Test]
  public async Task Constructor_EmptyDlqName_ThrowsAsync() {
    var connection = _connectionFor(new DrainFakeChannel());
    await Assert.That(() => new RabbitMqDeadLetterDrainer(
      connection, dlqName: " ", importAsync: _noopImport,
      logger: NullLogger<RabbitMqDeadLetterDrainer>.Instance))
      .Throws<ArgumentException>();
  }

  [Test]
  public async Task Constructor_NullImporter_ThrowsAsync() {
    var connection = _connectionFor(new DrainFakeChannel());
    await Assert.That(() => new RabbitMqDeadLetterDrainer(
      connection, dlqName: "q.dlq", importAsync: null!,
      logger: NullLogger<RabbitMqDeadLetterDrainer>.Instance))
      .Throws<ArgumentNullException>();
  }

  [Test]
  public async Task TransportName_FormatsAsRmqDlqAsync() {
    var drainer = _newDrainer(_connectionFor(new DrainFakeChannel()), "orders.dlq");
    await Assert.That(drainer.TransportName).IsEqualTo("rmq:orders.dlq");
  }

  // ===== TryBuildImport / ParseXDeath =====

  [Test]
  public async Task TryBuildImport_WhizbangMessage_MapsFieldsWithoutDeserializingAsync() {
    var headers = _xDeathHeaders(reason: "rejected", count: 4L);
    headers["EnvelopeType"] = System.Text.Encoding.UTF8.GetBytes("Whizbang.Test.Envelope");
    var result = _dlqResult(deliveryTag: 7, headers: headers, messageId: _id1, body: """{"v":1}""");

    var ok = RabbitMqDeadLetterDrainer.TryBuildImport(result, "orders.dlq", out var import);

    await Assert.That(ok).IsTrue();
    await Assert.That(import.MessageId).IsEqualTo(Guid.Parse(_id1));
    await Assert.That(import.MessageType).IsEqualTo("Whizbang.Test.Envelope");
    await Assert.That(import.Destination).IsEqualTo("orders.dlq");
    await Assert.That(import.EnvelopeJson).IsEqualTo("""{"v":1}""")
      .Because("custody is the RAW wire body, verbatim — the import path never deserializes");
    await Assert.That(import.BrokerReason).IsEqualTo("rejected");
    await Assert.That(import.DeliveryCount).IsEqualTo(4);
  }

  [Test]
  public async Task TryBuildImport_EnvelopeTypeAsString_AlsoMapsAsync() {
    var headers = new Dictionary<string, object?> { ["EnvelopeType"] = "String.Envelope" };
    var result = _dlqResult(1, headers, messageId: _id1);

    var ok = RabbitMqDeadLetterDrainer.TryBuildImport(result, "q.dlq", out var import);

    await Assert.That(ok).IsTrue();
    await Assert.That(import.MessageType).IsEqualTo("String.Envelope");
  }

  [Test]
  public async Task TryBuildImport_NonGuidMessageId_ReturnsFalseAsync() {
    var result = _dlqResult(1, headers: null, messageId: "not-a-guid");

    var ok = RabbitMqDeadLetterDrainer.TryBuildImport(result, "q.dlq", out _);

    await Assert.That(ok).IsFalse()
      .Because("only Whizbang wire messages (GUID MessageId) are ours to custody");
  }

  [Test]
  public async Task ParseXDeath_MissingHeader_ReturnsNullsAsync() {
    var (reason, count) = RabbitMqDeadLetterDrainer.ParseXDeath(null);
    await Assert.That(reason).IsNull();
    await Assert.That(count).IsNull();
  }

  [Test]
  public async Task ParseXDeath_WrongShapes_FallBackToNullsAsync() {
    var (reason, count) = RabbitMqDeadLetterDrainer.ParseXDeath(new Dictionary<string, object?> {
      ["x-death"] = new List<object?> {
        new Dictionary<string, object?> {
          ["reason"] = "string-not-bytes",
          ["count"] = 5,   // int, not long
        },
      },
    });
    await Assert.That(reason).IsNull();
    await Assert.That(count).IsNull();
  }

  // ===== Drain loop =====

  [Test]
  public async Task Drain_MaxCountZero_ReturnsZeroAsync() {
    var channel = new DrainFakeChannel();
    var drainer = _newDrainer(_connectionFor(channel));

    var drained = await drainer.DrainDeadLetterQueueAsync(0);

    await Assert.That(drained).IsEqualTo(0);
    await Assert.That(channel.BasicGetCalls).IsEqualTo(0);
  }

  [Test]
  public async Task Drain_EmptyDlq_ReturnsZeroAndPollsWithManualAckAsync() {
    var channel = new DrainFakeChannel();
    var drainer = _newDrainer(_connectionFor(channel), "orders.dlq");

    var drained = await drainer.DrainDeadLetterQueueAsync(10);

    await Assert.That(drained).IsEqualTo(0);
    await Assert.That(channel.BasicGetCalls).IsEqualTo(1);
    await Assert.That(channel.LastGetQueue).IsEqualTo("orders.dlq");
    await Assert.That(channel.LastGetAutoAck).IsFalse()
      .Because("manual settlement is the custody contract — nothing is acked before import");
  }

  [Test]
  public async Task Drain_MessagesAvailable_ImportsAndAcksEachAsync() {
    var channel = new DrainFakeChannel();
    var imports = new List<BrokerDeadLetterImport>();
    channel.GetResults.Enqueue(_dlqResult(1, _withEnvelopeType(), messageId: _id1, body: "payload-1"));
    channel.GetResults.Enqueue(_dlqResult(2, _withEnvelopeType(), messageId: _id2, body: "payload-2"));
    var drainer = new RabbitMqDeadLetterDrainer(
      _connectionFor(channel), "orders.dlq",
      (import, _) => { imports.Add(import); return Task.FromResult(true); },
      NullLogger<RabbitMqDeadLetterDrainer>.Instance);

    var drained = await drainer.DrainDeadLetterQueueAsync(10);

    await Assert.That(drained).IsEqualTo(2);
    await Assert.That(imports).Count().IsEqualTo(2);
    await Assert.That(imports[0].MessageId).IsEqualTo(Guid.Parse(_id1));
    await Assert.That(imports[0].EnvelopeJson).IsEqualTo("payload-1");
    await Assert.That(channel.AckedTags).Count().IsEqualTo(2);
    await Assert.That(channel.NackedTags).IsEmpty();
  }

  [Test]
  public async Task Drain_DuplicateImport_StillAcksAndCountsAsync() {
    var channel = new DrainFakeChannel();
    channel.GetResults.Enqueue(_dlqResult(1, _withEnvelopeType(), messageId: _id1));
    var drainer = new RabbitMqDeadLetterDrainer(
      _connectionFor(channel), "orders.dlq",
      (_, _) => Task.FromResult(false),   // duplicate — custody already exists
      NullLogger<RabbitMqDeadLetterDrainer>.Instance);

    var drained = await drainer.DrainDeadLetterQueueAsync(10);

    await Assert.That(drained).IsEqualTo(1);
    await Assert.That(channel.AckedTags).Count().IsEqualTo(1)
      .Because("duplicate custody still acks — the broker copy must leave the DLQ");
  }

  [Test]
  public async Task Drain_ImportFails_RequeuesAndEndsPassAsync() {
    var channel = new DrainFakeChannel();
    channel.GetResults.Enqueue(_dlqResult(1, _withEnvelopeType(), messageId: _id1));
    channel.GetResults.Enqueue(_dlqResult(2, _withEnvelopeType(), messageId: _id2));
    var drainer = new RabbitMqDeadLetterDrainer(
      _connectionFor(channel), "orders.dlq",
      (_, _) => Task.FromException<bool>(new InvalidOperationException("import failed")),
      NullLogger<RabbitMqDeadLetterDrainer>.Instance);

    var drained = await drainer.DrainDeadLetterQueueAsync(10);

    await Assert.That(drained).IsEqualTo(0);
    await Assert.That(channel.NackedTags).Count().IsEqualTo(1);
    await Assert.That(channel.NackedTags[0].Requeue).IsTrue()
      .Because("a failed import must NOT settle the broker copy");
    await Assert.That(channel.BasicGetCalls).IsEqualTo(1)
      .Because("the requeued head would be re-fetched immediately — the pass ends instead of "
             + "spinning on the same failing message");
  }

  [Test]
  public async Task Drain_NonWhizbangMessage_RequeuesAndEndsPassAsync() {
    var channel = new DrainFakeChannel();
    channel.GetResults.Enqueue(_dlqResult(1, headers: null, messageId: "not-a-guid"));
    var drainer = _newDrainer(_connectionFor(channel));

    var drained = await drainer.DrainDeadLetterQueueAsync(10);

    await Assert.That(drained).IsEqualTo(0);
    await Assert.That(channel.NackedTags).Count().IsEqualTo(1);
    await Assert.That(channel.NackedTags[0].Requeue).IsTrue();
  }

  [Test]
  public async Task Drain_MaxCountReached_StopsPollingAsync() {
    var channel = new DrainFakeChannel();
    channel.GetResults.Enqueue(_dlqResult(1, _withEnvelopeType(), messageId: _id1));
    channel.GetResults.Enqueue(_dlqResult(2, _withEnvelopeType(), messageId: _id2));
    var drainer = _newDrainer(_connectionFor(channel));

    var drained = await drainer.DrainDeadLetterQueueAsync(1);

    await Assert.That(drained).IsEqualTo(1);
    await Assert.That(channel.BasicGetCalls).IsEqualTo(1);
  }

  [Test]
  public async Task Drain_CanceledAfterAck_ReturnsPartialCountAsync() {
    using var cts = new CancellationTokenSource();
    var channel = new DrainFakeChannel { OnAck = () => cts.Cancel() };
    channel.GetResults.Enqueue(_dlqResult(1, _withEnvelopeType(), messageId: _id1));
    channel.GetResults.Enqueue(_dlqResult(2, _withEnvelopeType(), messageId: _id2));
    var drainer = _newDrainer(_connectionFor(channel));

    var drained = await drainer.DrainDeadLetterQueueAsync(10, cts.Token);

    await Assert.That(drained).IsEqualTo(1);
    await Assert.That(channel.BasicGetCalls).IsEqualTo(1);
  }

  // ===== Helpers =====

  private static RabbitMqDeadLetterDrainer _newDrainer(IConnection connection, string dlqName = "orders.dlq") =>
    new(connection, dlqName, _noopImport, NullLogger<RabbitMqDeadLetterDrainer>.Instance);

  private static FakeConnection _connectionFor(DrainFakeChannel channel) =>
    new(() => Task.FromResult<IChannel>(channel));

  private static Dictionary<string, object?> _withEnvelopeType() => new() {
    ["EnvelopeType"] = System.Text.Encoding.UTF8.GetBytes("Whizbang.Test.Envelope"),
  };

  private static BasicGetResult _dlqResult(
    ulong deliveryTag,
    IDictionary<string, object?>? headers,
    string messageId,
    string body = "dlq-body") {
    var properties = new BasicProperties {
      MessageId = messageId,
      Headers = headers,
    };
    return new BasicGetResult(
      deliveryTag: deliveryTag,
      redelivered: false,
      exchange: "",
      routingKey: "rk",
      messageCount: 0,
      basicProperties: properties,
      body: System.Text.Encoding.UTF8.GetBytes(body));
  }

  /// <summary>Builds the x-death header shape RabbitMQ adds when a message enters DLQ.</summary>
  private static Dictionary<string, object?> _xDeathHeaders(string reason, long count) => new() {
    ["x-death"] = new List<object?> {
      new Dictionary<string, object?> {
        ["reason"] = System.Text.Encoding.UTF8.GetBytes(reason),
        ["count"] = count,
      },
    },
  };

  // ===== Drain-loop test double =====

  /// <summary>
  /// Channel fake for the drain loop. Derives from the shared <see cref="FakeChannel"/> and
  /// re-implements <see cref="IChannel"/> so the members the drainer touches (BasicGet /
  /// BasicAck / BasicNack) are replaced with recording versions that support queued results.
  /// </summary>
  private sealed class DrainFakeChannel : FakeChannel, IChannel {
    public Queue<BasicGetResult?> GetResults { get; } = new();
    public List<ulong> AckedTags { get; } = [];
    public List<(ulong DeliveryTag, bool Requeue)> NackedTags { get; } = [];
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

    public new ValueTask BasicAckAsync(ulong deliveryTag, bool multiple, CancellationToken cancellationToken = default) {
      AckedTags.Add(deliveryTag);
      OnAck?.Invoke();
      return ValueTask.CompletedTask;
    }

    public new ValueTask BasicNackAsync(ulong deliveryTag, bool multiple, bool requeue, CancellationToken cancellationToken = default) {
      NackedTags.Add((deliveryTag, requeue));
      return ValueTask.CompletedTask;
    }
  }
}
