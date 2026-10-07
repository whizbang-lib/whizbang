// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Routing;
using Whizbang.Core.Transports;

namespace Whizbang.Transports.RabbitMQ.Tests.BranchCoverage;

/// <summary>
/// Branch coverage for <see cref="RabbitMQTransport.TryQuarantinePoisonAsync"/> (the "unknown"
/// MessageId fallback, the report-on-change capability cache, the quarantine warning) and for the
/// routing-pattern resolution a subscription binds with (a non-array <c>RoutingPatterns</c>
/// value, and a destination with no metadata at all).
/// </summary>
/// <code-under-test>src/Whizbang.Transports.RabbitMQ/RabbitMQTransport.cs</code-under-test>
public class RabbitMQTransportPoisonAndRoutingBranchTests {

  #region Poison quarantine

  [Test]
  public async Task Quarantine_MessageWithoutIdWithLogger_EvaluatesAsUnknownAndLogsTheReasonAsync() {
    var detector = new RecordingDetector(quarantine: true);
    var channel = new FakeChannel();
    var logger = new CapturingLogger<RabbitMQTransport>();
    var transport = _newTransport(channel, detector, logger);

    var quarantined = await transport.TryQuarantinePoisonAsync(channel, _args(messageId: null, unixSeconds: 0), "q");

    await Assert.That(quarantined).IsTrue();
    await Assert.That(detector.Evaluated.Single().MessageId).IsEqualTo("unknown")
      .Because("a foreign message with no AMQP MessageId must still be identifiable in the verdict");
    await Assert.That(channel.LastNackRequeue).IsFalse();
    await Assert.That(logger.Entries.Any(e =>
      e.Level == LogLevel.Warning
      && e.Message.Contains("Poison quarantine", StringComparison.Ordinal)
      && e.Message.Contains("unknown", StringComparison.Ordinal))).IsTrue();
  }

  [Test]
  public async Task Quarantine_RepeatedDeliveriesOnOneQueue_ReportCapabilityOnlyWhenItChangesAsync() {
    // The capability report runs on the receive hot path, so it is written only on CHANGE: two
    // unstamped deliveries report once, and a stamped one afterwards reports the flip.
    var detector = new RecordingDetector(quarantine: false);
    var channel = new FakeChannel();
    var transport = _newTransport(channel, detector, logger: null);

    await transport.TryQuarantinePoisonAsync(channel, _args("m1", unixSeconds: 0), "q");
    await transport.TryQuarantinePoisonAsync(channel, _args("m2", unixSeconds: 0), "q");
    await transport.TryQuarantinePoisonAsync(channel, _args("m3", unixSeconds: 1_700_000_000), "q");

    await Assert.That(detector.CapabilityReports).Count().IsEqualTo(2)
      .Because("unchanged capability must not be re-reported; a change must be");
    await Assert.That(detector.CapabilityReports[0]).IsFalse();
    await Assert.That(detector.CapabilityReports[1]).IsTrue();
    await Assert.That(detector.Evaluated).Count().IsEqualTo(3);
  }

  #endregion

  #region Routing patterns

  [Test]
  public async Task Subscribe_RoutingPatternsNotAnArray_FallsBackToTheSingularPatternAsync() {
    var channel = new RecordingChannel();
    var transport = await _initializedAsync(channel);
    var metadata = new Dictionary<string, JsonElement> {
      ["RoutingPatterns"] = JsonDocument.Parse("\"orders.*\"").RootElement.Clone(),
      ["RoutingPattern"] = JsonDocument.Parse("\"payments.created\"").RootElement.Clone()
    };

    await transport.SubscribeAsync(
      (_, _, _) => Task.CompletedTask, RabbitTestWire.Destination(extraMetadata: metadata));

    var routingKeys = channel.QueueBindings
      .Where(b => b.Queue == "test-subscriber-test-exchange")
      .Select(b => b.RoutingKey)
      .ToList();
    List<string> expected = ["payments.created"];
    await Assert.That(routingKeys).IsEquivalentTo(expected)
      .Because("a RoutingPatterns value that is not a JSON array is not a pattern list");
  }

  [Test]
  public async Task Subscribe_DestinationWithoutMetadata_BindsMatchAllOnTheDefaultQueueAsync() {
    var channel = new RecordingChannel();
    var transport = await _initializedAsync(channel, new RabbitMQOptions { DefaultQueueName = "fixed-queue" });

    await transport.SubscribeAsync((_, _, _) => Task.CompletedTask, new TransportDestination("plain-exchange"));

    var routingKeys = channel.QueueBindings
      .Where(b => b.Queue == "fixed-queue")
      .Select(b => b.RoutingKey)
      .ToList();
    List<string> expected = ["#"];
    await Assert.That(routingKeys).IsEquivalentTo(expected);
  }

  #endregion

  #region Helpers

  private static async Task<RabbitMQTransport> _initializedAsync(RecordingChannel channel, RabbitMQOptions? options = null) {
    var connection = new FakeConnection(() => Task.FromResult<IChannel>(channel));
    return await RabbitTestWire.NewInitializedTransportAsync(connection, options);
  }

  private static RabbitMQTransport _newTransport(
      IChannel channel, IPoisonMessageDetector detector, ILogger<RabbitMQTransport>? logger) {
    var connection = new FakeConnection(() => Task.FromResult(channel));
    var pool = new RabbitMQChannelPool(connection, maxChannels: 5);
    return new RabbitMQTransport(
      connection, RabbitTestWire.JsonOptions, pool, new RabbitMQOptions(), logger, poisonDetector: detector);
  }

  private static BasicDeliverEventArgs _args(string? messageId, long unixSeconds) =>
    new(
      consumerTag: "test-consumer",
      deliveryTag: 1,
      redelivered: false,
      exchange: "test-exchange",
      routingKey: "#",
      properties: new BasicProperties { MessageId = messageId, Timestamp = new AmqpTimestamp(unixSeconds) },
      body: ReadOnlyMemory<byte>.Empty,
      cancellationToken: CancellationToken.None);

  private sealed class RecordingDetector(bool quarantine) : IPoisonMessageDetector {
    public List<PoisonEvaluationContext> Evaluated { get; } = [];
    public List<bool> CapabilityReports { get; } = [];

    public PoisonVerdict Evaluate(PoisonEvaluationContext context) {
      Evaluated.Add(context);
      return quarantine
        ? PoisonVerdict.Quarantine(PoisonQuarantineReason.MessageAgeExceeded, "aged out")
        : PoisonVerdict.Proceed();
    }

    public void RecordQuarantine(
        PoisonQuarantineGate gate,
        PoisonVerdict verdict,
        PoisonEvaluationContext context,
        IReadOnlyDictionary<string, object?>? additionalTags = null) { }

    public void ReportAgeCapability(string transport, string entity, bool canSupplyTrustworthyAge) =>
      CapabilityReports.Add(canSupplyTrustworthyAge);
  }

  #endregion
}
