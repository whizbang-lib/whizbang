using Microsoft.Extensions.Logging.Abstractions;
using RabbitMQ.Client;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Transports;
using Whizbang.Transports.RabbitMQ;

#pragma warning disable CA1707 // Identifiers should not contain underscores (test method names use underscores by convention)

namespace Whizbang.Transports.RabbitMQ.Tests;

/// <summary>
/// Round-23 coverage additions for <see cref="RabbitMqDeadLetterDrainer"/>: cancellation must
/// propagate out of the import step rather than being folded into the generic requeue-and-end
/// recovery arm, an unrecognized <c>EnvelopeType</c> header shape must map to a null message
/// type (never a guess), and a channel-closed race during the guarded nack must be swallowed
/// with a trace rather than crashing the recurring drain job.
/// </summary>
/// <code-under-test>src/Whizbang.Transports.RabbitMQ/RabbitMqDeadLetterDrainer.cs</code-under-test>
public class RabbitMqDeadLetterDrainerCoverageTests {

  private static readonly string _id1 = "00000000-0000-0000-0000-000000000101";

  private static Func<BrokerDeadLetterImport, CancellationToken, Task<bool>> _noopImport =>
    (_, _) => Task.FromResult(true);

  // The drain loop's catch(Exception) recovery arm (nack + end pass) exists for RECOVERABLE
  // import failures. A canceled import (e.g. host shutdown mid-drain) must rethrow immediately
  // instead of being folded into that arm — otherwise an orderly shutdown would look, from the
  // caller's perspective, like an ordinary import failure, and would nack a message that was
  // never actually rejected.
  [Test]
  public async Task Drain_ImportThrowsOperationCanceled_PropagatesWithoutNackingAsync() {
    var channel = new DrainCoverageChannel();
    channel.GetResults.Enqueue(_dlqResult(1, _withEnvelopeType(), messageId: _id1));
    var connection = new FakeConnection(() => Task.FromResult<IChannel>(channel));
    var drainer = new RabbitMqDeadLetterDrainer(
      connection, "orders.dlq",
      (_, _) => throw new OperationCanceledException("import canceled"),
      NullLogger<RabbitMqDeadLetterDrainer>.Instance);

    await Assert.That(async () => await drainer.DrainDeadLetterQueueAsync(10))
      .Throws<OperationCanceledException>();

    await Assert.That(channel.NackedTags).IsEmpty()
      .Because("a canceled import must rethrow immediately, not fall into the requeue-and-end "
             + "recovery arm");
  }

  // If an EnvelopeType header carried a shape that is neither the wire's byte[] encoding nor a
  // plain string, mapping it to anything other than null would risk importing a garbage or
  // truncated MessageType into wh_dead_letters, corrupting the operator's view of what actually
  // failed instead of just recording "unknown type".
  [Test]
  public async Task TryBuildImport_EnvelopeTypeHeaderIsUnrecognizedShape_MapsNullMessageTypeAsync() {
    var headers = new Dictionary<string, object?> { ["EnvelopeType"] = 12345 };
    var result = _dlqResult(1, headers, messageId: _id1);

    var ok = RabbitMqDeadLetterDrainer.TryBuildImport(result, "q.dlq", out var import);

    await Assert.That(ok).IsTrue();
    await Assert.That(import.MessageType).IsNull();
  }

  // This drainer runs as a recurring background job. If a channel-closed race during the
  // "requeue a foreign message" nack escaped instead of being swallowed, one ordinary broker
  // hiccup would crash the whole recurring job instead of letting the broker's own redelivery
  // handle the message on its next pass.
  [Test]
  public async Task Drain_NonWhizbangMessage_NackThrowsAlreadyClosed_SwallowsAndLogsAsync() {
    var channel = new DrainCoverageChannel { NackException = RabbitTestWire.NewAlreadyClosedException() };
    channel.GetResults.Enqueue(_dlqResult(1, headers: null, messageId: "not-a-guid"));
    var connection = new FakeConnection(() => Task.FromResult<IChannel>(channel));
    var logger = new CapturingLogger<RabbitMqDeadLetterDrainer>();
    var drainer = new RabbitMqDeadLetterDrainer(connection, "orders.dlq", _noopImport, logger);

    var drained = await drainer.DrainDeadLetterQueueAsync(10);

    await Assert.That(drained).IsEqualTo(0);
    await Assert.That(channel.NackedTags).Count().IsEqualTo(1)
      .Because("the nack itself must still be ATTEMPTED before the broker's channel-closed "
             + "exception is swallowed");
    await Assert.That(logger.Entries.Any(e => e.Message.Contains("nack failed", StringComparison.OrdinalIgnoreCase)))
      .IsTrue()
      .Because("a swallowed channel-closed nack must still leave a trace an operator can find");
  }

  // ===== Helpers =====

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

  private static Dictionary<string, object?> _withEnvelopeType() => new() {
    ["EnvelopeType"] = System.Text.Encoding.UTF8.GetBytes("Whizbang.Test.Envelope"),
  };

  /// <summary>
  /// Channel fake for the drain loop. Derives from the shared <see cref="FakeChannel"/> and
  /// re-implements <see cref="IChannel"/> so BasicGet/BasicNack are replaced with recording
  /// versions that support queued results and an injectable nack failure.
  /// </summary>
  private sealed class DrainCoverageChannel : FakeChannel, IChannel {
    public Queue<BasicGetResult?> GetResults { get; } = new();
    public List<(ulong DeliveryTag, bool Requeue)> NackedTags { get; } = [];
    public Exception? NackException { get; set; }

    public new Task<BasicGetResult?> BasicGetAsync(string queue, bool autoAck, CancellationToken cancellationToken = default) {
      var result = GetResults.Count > 0 ? GetResults.Dequeue() : null;
      return Task.FromResult(result);
    }

    public new ValueTask BasicNackAsync(ulong deliveryTag, bool multiple, bool requeue, CancellationToken cancellationToken = default) {
      NackedTags.Add((deliveryTag, requeue));
      if (NackException != null) {
        throw NackException;
      }
      return ValueTask.CompletedTask;
    }
  }
}
