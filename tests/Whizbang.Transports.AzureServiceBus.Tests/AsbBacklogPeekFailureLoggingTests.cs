// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Transports.AzureServiceBus.Tests;

/// <summary>
/// A failed head peek must say so. Without a log entry, a peek that throws (permissions, a missing
/// entity, a broker error) is indistinguishable from an empty entity: both report depth with no
/// age, and nothing tells an operator why the age is missing. The warning is rate-limited per
/// entity, on the transport's clock, so a peek that fails on every duty tick does not flood the log.
/// </summary>
/// <code-under-test>src/Whizbang.Transports.AzureServiceBus/AzureServiceBusTransport.cs</code-under-test>
[Timeout(10_000)]
[Category("Transports")]
public class AsbBacklogPeekFailureLoggingTests {
  private const string TOPIC = "orders-topic";
  private const string SUBSCRIPTION = "orders-svc-sub";
  private const string OTHER_SUBSCRIPTION = "audit-svc-sub";
  private const string FAILURE_FRAGMENT = "Backlog head peek failed";

  /// <summary>
  /// A throwing peek logs one warning naming the entity, the exception type and its message, and
  /// the sample still carries the depth.
  /// </summary>
  [Test]
  public async Task PeekBacklogsAsync_HeadPeekThrows_LogsWarningWithEntityAndExceptionAsync() {
    var logger = new RecordingTransportLogger();
    var transport = _transport(logger, new FakeTimeProvider(), depth: 7);
    transport.LivenessWatchdog!.Track(TOPIC, SUBSCRIPTION);
    transport.OldestEnqueuedTimeProbe = (_, _, _) =>
      Task.FromException<DateTimeOffset?>(new InvalidOperationException("peek refused"));

    var samples = await transport.PeekBacklogsAsync(CancellationToken.None);

    await Assert.That(samples).Count().IsEqualTo(1);
    await Assert.That(samples[0].Depth).IsEqualTo(7L)
      .Because("a failed age read degrades the sample, never the depth");
    await Assert.That(samples[0].OldestAge).IsNull();
    await Assert.That(logger.Count(LogLevel.Warning, FAILURE_FRAGMENT)).IsEqualTo(1);
    await Assert.That(logger.Contains(LogLevel.Warning, $"{TOPIC}/{SUBSCRIPTION}")).IsTrue()
      .Because("the warning names the entity whose age is missing");
    await Assert.That(logger.Contains(LogLevel.Warning, nameof(InvalidOperationException))).IsTrue()
      .Because("the warning names what kind of failure it was");
    await Assert.That(logger.Contains(LogLevel.Warning, "peek refused")).IsTrue()
      .Because("the warning carries the exception's message");
  }

  /// <summary>
  /// An empty entity is not a failure: its peek returns no head message and logs nothing.
  /// </summary>
  [Test]
  public async Task PeekBacklogsAsync_EmptyEntityHead_LogsNoWarningAsync() {
    var logger = new RecordingTransportLogger();
    var transport = _transport(logger, new FakeTimeProvider(), depth: 0);
    transport.LivenessWatchdog!.Track(TOPIC, SUBSCRIPTION);
    transport.OldestEnqueuedTimeProbe = (_, _, _) => Task.FromResult<DateTimeOffset?>(null);

    var samples = await transport.PeekBacklogsAsync(CancellationToken.None);

    await Assert.That(samples).Count().IsEqualTo(1);
    await Assert.That(samples[0].OldestAge).IsNull();
    await Assert.That(logger.Count(LogLevel.Warning, FAILURE_FRAGMENT)).IsEqualTo(0)
      .Because("an empty entity is a truthful 'no age', not a failure");
  }

  /// <summary>
  /// A peek that fails on every tick logs once per interval per entity: a second failure inside the
  /// interval is quiet, and the first failure after it logs again.
  /// </summary>
  [Test]
  public async Task PeekBacklogsAsync_RepeatedFailures_LogOncePerIntervalAsync() {
    var logger = new RecordingTransportLogger();
    var clock = new FakeTimeProvider();
    var transport = _transport(logger, clock, depth: 3);
    transport.LivenessWatchdog!.Track(TOPIC, SUBSCRIPTION);
    transport.OldestEnqueuedTimeProbe = (_, _, _) =>
      Task.FromException<DateTimeOffset?>(new InvalidOperationException("peek refused"));
    var interval = AzureServiceBusTransport.BacklogPeekFailureLogInterval;

    await transport.PeekBacklogsAsync(CancellationToken.None);
    clock.Advance(interval - TimeSpan.FromTicks(1));
    await transport.PeekBacklogsAsync(CancellationToken.None);
    var withinInterval = logger.Count(LogLevel.Warning, FAILURE_FRAGMENT);
    clock.Advance(TimeSpan.FromTicks(1));
    await transport.PeekBacklogsAsync(CancellationToken.None);

    await Assert.That(withinInterval).IsEqualTo(1)
      .Because("a second failure inside the interval is rate-limited");
    await Assert.That(logger.Count(LogLevel.Warning, FAILURE_FRAGMENT)).IsEqualTo(2)
      .Because("once the interval has elapsed the next failure is logged again");
  }

  /// <summary>
  /// The rate limit is per entity: one entity's recent warning does not silence another entity's
  /// first failure.
  /// </summary>
  [Test]
  public async Task PeekBacklogsAsync_FailuresOnTwoEntities_EachEntityLogsAsync() {
    var logger = new RecordingTransportLogger();
    var transport = _transport(logger, new FakeTimeProvider(), depth: 3);
    transport.LivenessWatchdog!.Track(TOPIC, SUBSCRIPTION);
    transport.OldestEnqueuedTimeProbe = (_, _, _) =>
      Task.FromException<DateTimeOffset?>(new InvalidOperationException("peek refused"));

    await transport.PeekBacklogsAsync(CancellationToken.None);
    transport.LivenessWatchdog.Track(TOPIC, OTHER_SUBSCRIPTION);
    await transport.PeekBacklogsAsync(CancellationToken.None);

    await Assert.That(logger.Count(LogLevel.Warning, FAILURE_FRAGMENT)).IsEqualTo(2)
      .Because("the second entity's first failure logs even though the first entity's is rate-limited");
    await Assert.That(logger.Contains(LogLevel.Warning, $"{TOPIC}/{OTHER_SUBSCRIPTION}")).IsTrue();
  }

  private static AzureServiceBusTransport _transport(
      RecordingTransportLogger logger, FakeTimeProvider clock, long depth) =>
    new(new RaisableServiceBusClient(),
      AsbTransportTestData.CombinedOptions,
      new AzureServiceBusOptions { AutoProvisionInfrastructure = false, EnableSessions = false },
      logger,
      adminClient: new RecordingProvisioningAdminClient { ActiveMessageCountResult = depth },
      timeProvider: clock);
}
