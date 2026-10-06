// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Routing;
using Whizbang.Core.Transports;

namespace Whizbang.Transports.RabbitMQ.Tests.BranchCoverage;

/// <summary>
/// Final-pass branch coverage for the RabbitMQ package: a recovery handler that completes
/// asynchronously, a quarantine whose nack finds the channel closed (with and without a logger),
/// repeated pause and resume under every logger shape, and the owned-command-inbox marker test in
/// every metadata shape.
/// </summary>
/// <remarks>
/// The existing recovery tests only use handlers that complete synchronously, so the transport's
/// await on the handler never suspended and the resumption path after a real asynchronous
/// completion was never taken. The tests here hold the handler on a gate the test releases, which
/// also pins the contract that recovery waits for the handler rather than firing and forgetting it.
/// </remarks>
/// <code-under-test>src/Whizbang.Transports.RabbitMQ/RabbitMQTransport.cs</code-under-test>
/// <code-under-test>src/Whizbang.Transports.RabbitMQ/RabbitMQSubscription.cs</code-under-test>
/// <code-under-test>src/Whizbang.Transports.RabbitMQ/RabbitMQInfrastructureProvisioner.cs</code-under-test>
public class RabbitMQFinalPassBranchTests {
  private const string QUEUE = "final-pass-queue";
  private const string INBOX_TOPIC = "inbox.myapp.orders.commands";

  #region Connection recovery

  [Test]
  public async Task Recovery_HandlerCompletesAsynchronously_RecoveryWaitsForItAsync() {
    var connection = new FakeConnection(() => Task.FromResult<IChannel>(new RecordingChannel()));
    var transport = await RabbitTestWire.NewInitializedTransportAsync(connection);
    var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var handlerFinished = false;
    transport.SetRecoveryHandler(async _ => {
      await gate.Task;
      handlerFinished = true;
    });

    var recovery = connection.SimulateRecoverySucceededAsync();

    await Assert.That(recovery.IsCompleted).IsFalse()
      .Because("recovery awaits the handler, so it cannot finish while the handler is still running");
    gate.SetResult();
    await recovery;
    await Assert.That(handlerFinished).IsTrue();
  }

  [Test]
  public async Task Recovery_HandlerFailsAsynchronously_FailureIsLoggedNotThrownAsync() {
    var connection = new FakeConnection(() => Task.FromResult<IChannel>(new RecordingChannel()));
    var logger = new CapturingLogger<RabbitMQTransport>();
    var transport = await RabbitTestWire.NewInitializedTransportAsync(connection, logger: logger);
    var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    transport.SetRecoveryHandler(async _ => {
      await gate.Task;
      throw new InvalidOperationException("resubscribe failed later");
    });

    var recovery = connection.SimulateRecoverySucceededAsync();
    gate.SetResult();

    await Assert.That(async () => await recovery).ThrowsNothing();
    await Assert.That(logger.Entries.Any(e =>
      e.Level == LogLevel.Error && e.Message.Contains("Error in recovery handler", StringComparison.Ordinal))).IsTrue()
      .Because("a handler that fails after yielding is swallowed like one that fails at once, and logged");
  }

  #endregion

  #region Poison quarantine with a closed channel

  [Test]
  [Arguments(true)]
  [Arguments(false)]
  public async Task Quarantine_NackFindsTheChannelClosed_StillReportsQuarantinedAsync(bool withLogger) {
    var channel = new FakeChannel { ExceptionToThrowOnNack = RabbitTestWire.NewAlreadyClosedException() };
    var logger = withLogger ? new CapturingLogger<RabbitMQTransport>() : null;
    var transport = _newTransport(channel, new QuarantiningDetector(), logger);

    var quarantined = await transport.TryQuarantinePoisonAsync(channel, _args("poison-1"), QUEUE);

    await Assert.That(quarantined).IsTrue()
      .Because("the verdict stands: the broker redelivers and the quarantine re-evaluates, so the "
             + "message must not fall through to the handler on this delivery");
    await Assert.That(channel.BasicNackAsyncCalled).IsFalse()
      .Because("the nack threw before it was recorded");
    if (logger is not null) {
      await Assert.That(logger.Entries.Any(e =>
        e.Level == LogLevel.Warning
        && e.Message.Contains("closed/disposed while quarantining", StringComparison.Ordinal)
        && e.Message.Contains("poison-1", StringComparison.Ordinal))).IsTrue();
    }
  }

  #endregion

  #region Subscription pause and resume

  [Test]
  [Arguments("none")]
  [Arguments("enabled")]
  [Arguments("disabled")]
  public async Task Subscription_RepeatedPauseAndResume_AreIdempotentForEveryLoggerShapeAsync(string loggerShape) {
    await using var channel = new FakeChannel();
    var logger = _logger(loggerShape);
    using var subscription = new RabbitMQSubscription(channel, QUEUE, logger: logger);

    await subscription.PauseAsync();
    await subscription.PauseAsync();
    var activeAfterPauses = subscription.IsActive;
    await subscription.ResumeAsync();
    await subscription.ResumeAsync();

    await Assert.That(activeAfterPauses).IsFalse()
      .Because("a second pause of a paused subscription is a no-op, not a toggle");
    await Assert.That(subscription.IsActive).IsTrue()
      .Because("a second resume of an active subscription is a no-op, not a toggle");
    if (logger is CapturingLogger<RabbitMQSubscription> capturing) {
      await Assert.That(capturing.Entries.Any(e => e.Message.Contains("already paused", StringComparison.Ordinal))).IsTrue();
      await Assert.That(capturing.Entries.Any(e => e.Message.Contains("already active", StringComparison.Ordinal))).IsTrue();
    }
  }

  #endregion

  #region Owned-command-inbox marker

  /// <summary>
  /// Only a subscription whose metadata carries the owned-inbox marker set to true is probed for
  /// ownership drift; no metadata, metadata without the marker, and a false marker are all not
  /// owned.
  /// </summary>
  [Test]
  [Arguments("no-metadata", 0)]
  [Arguments("no-marker", 0)]
  [Arguments("marker-false", 0)]
  [Arguments("marker-true", 1)]
  public async Task Provisioner_OwnedInboxMarker_ProbesOnlyAnOwnedInboxAsync(string metadataShape, int expectedProbes) {
    var channel = new FakeChannel { ExistingExchanges = { INBOX_TOPIC } };
    var connection = new FakeConnection(() => Task.FromResult<IChannel>(channel));
    var pool = new RabbitMQChannelPool(connection, maxChannels: 10);
    var provisioner = new RabbitMQInfrastructureProvisioner(
      pool, NullLogger<RabbitMQInfrastructureProvisioner>.Instance, options: null, driftState: new TopologyDriftState());
    var manifest = new TopologyManifest(
      "order-service", [], [new InboxSubscription(INBOX_TOPIC, null, _metadata(metadataShape))]);

    await provisioner.ProvisionManifestAsync(manifest);

    await Assert.That(channel.PassiveExchangeDeclareCount).IsEqualTo(expectedProbes);
  }

  #endregion

  #region Helpers

  private static Dictionary<string, object>? _metadata(string shape) => shape switch {
    "no-metadata" => null,
    "no-marker" => new Dictionary<string, object> { ["unrelated"] = "value" },
    "marker-false" => new Dictionary<string, object> { [NamespaceInboxStrategy.OwnedCommandInboxMetadataKey] = false },
    _ => new Dictionary<string, object> { [NamespaceInboxStrategy.OwnedCommandInboxMetadataKey] = true },
  };

  private static ILogger? _logger(string shape) => shape switch {
    "enabled" => new CapturingLogger<RabbitMQSubscription>(),
    "disabled" => new DisabledLogger(),
    _ => null,
  };

  private static RabbitMQTransport _newTransport(
      IChannel channel, IPoisonMessageDetector detector, ILogger<RabbitMQTransport>? logger) {
    var connection = new FakeConnection(() => Task.FromResult(channel));
    var pool = new RabbitMQChannelPool(connection, maxChannels: 5);
    return new RabbitMQTransport(
      connection, RabbitTestWire.JsonOptions, pool, new RabbitMQOptions(), logger, poisonDetector: detector);
  }

  private static BasicDeliverEventArgs _args(string messageId) =>
    new(
      consumerTag: "test-consumer",
      deliveryTag: 7,
      redelivered: false,
      exchange: "test-exchange",
      routingKey: "#",
      properties: new BasicProperties { MessageId = messageId, Timestamp = new AmqpTimestamp(0) },
      body: ReadOnlyMemory<byte>.Empty,
      cancellationToken: CancellationToken.None);

  private sealed class QuarantiningDetector : IPoisonMessageDetector {
    public PoisonVerdict Evaluate(PoisonEvaluationContext context) =>
      PoisonVerdict.Quarantine(PoisonQuarantineReason.MessageAgeExceeded, "aged out");

    public void RecordQuarantine(
        PoisonQuarantineGate gate,
        PoisonVerdict verdict,
        PoisonEvaluationContext context,
        IReadOnlyDictionary<string, object?>? additionalTags = null) { }

    public void ReportAgeCapability(string transport, string entity, bool canSupplyTrustworthyAge) { }
  }

  private sealed class DisabledLogger : ILogger {
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => false;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) {
      // Disabled: every IsEnabled-gated diagnostic is skipped before reaching here.
    }
  }

  #endregion
}
