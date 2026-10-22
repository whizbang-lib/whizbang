// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Transports.AzureServiceBus.Integration.Tests.Containers;

namespace Whizbang.Transports.AzureServiceBus.Integration.Tests;

/// <summary>
/// Covers <c>PeekBacklogsAsync</c> — the backlog-age sample that distinguishes a deep-but-young
/// queue draining normally from a shallow-but-ancient one whose consumer has stopped.
/// </summary>
/// <remarks>
/// Depth comes from a management call the emulator does not serve, so the admin client is faked
/// for that one number. The oldest-enqueued peek is a data-plane operation the emulator does
/// serve, and that is the half worth exercising for real — a peek that silently returned nothing
/// would make every backlog look brand new, which is exactly the signal this exists to provide.
/// <para>
/// The two broker peeks use entities of their own, topic-backlog-peek-held and
/// topic-backlog-peek-empty, which no other class publishes to or consumes from, so each knows
/// exactly what its head is. This class once peeked the shared topic-00 straight after a send;
/// the emulator does not always show a just-sent message to a peek, and the check could not see a
/// missing age, so a run could pass without the peek ever finding a message and leave that
/// outcome of the peek untaken.
/// </para>
/// </remarks>
[Category("Integration")]
[Timeout(240_000)]
[NotInParallel("ServiceBus")]
[ClassDataSource<ServiceBusEmulatorFixtureSource>(Shared = SharedType.PerAssembly)]
public class AzureServiceBusBacklogPeekIntegrationTests(ServiceBusEmulatorFixtureSource fixtureSource) {
  private const string HELD_TOPIC = "topic-backlog-peek-held";
  private const string EMPTY_TOPIC = "topic-backlog-peek-empty";
  private const string SUBSCRIPTION = "sub-backlog-peek";

  private readonly ServiceBusEmulatorFixture _fixture = fixtureSource.Emulator;
  private readonly List<IAsyncDisposable> _disposables = [];

  [After(Test)]
  public async Task DisposeAsync() {
    foreach (var d in _disposables) {
      try { await d.DisposeAsync(); } catch { /* best-effort cleanup */ }
    }
    _disposables.Clear();
  }

  /// <summary>Serves only the depth call; every other admin member throws so a stray use shows up.</summary>
  private sealed class DepthOnlyAdminClient(long depth, Exception? depthThrows = null) : IServiceBusAdminClient {
    public Task<long> GetSubscriptionActiveMessageCountAsync(
        string topicName, string subscriptionName, CancellationToken cancellationToken = default)
      => depthThrows is not null
        ? Task.FromException<long>(depthThrows)
        : Task.FromResult(depth);

    public Task<NamespaceProperties> GetNamespacePropertiesAsync(CancellationToken cancellationToken = default)
      => throw new NotImplementedException();
    public Task<bool> TopicExistsAsync(string topicName, CancellationToken cancellationToken = default)
      => Task.FromResult(true);
    public Task CreateTopicAsync(string topicName, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<bool> SubscriptionExistsAsync(string topicName, string subscriptionName, CancellationToken cancellationToken = default)
      => Task.FromResult(true);
    public Task CreateSubscriptionAsync(string topicName, string subscriptionName, int maxDeliveryCount, TimeSpan lockDuration, CancellationToken cancellationToken = default)
      => Task.CompletedTask;
    public Task CreateSubscriptionAsync(string topicName, string subscriptionName, bool requiresSession, int maxDeliveryCount, TimeSpan lockDuration, CancellationToken cancellationToken = default)
      => Task.CompletedTask;
    public Task UpdateSubscriptionLockDurationAsync(string topicName, string subscriptionName, TimeSpan lockDuration, CancellationToken cancellationToken = default)
      => Task.CompletedTask;
    public Task<SubscriptionProperties> GetSubscriptionAsync(string topicName, string subscriptionName, CancellationToken cancellationToken = default)
      => throw new NotImplementedException();
    public Task DeleteSubscriptionAsync(string topicName, string subscriptionName, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public IAsyncEnumerable<RuleProperties> GetRulesAsync(string topicName, string subscriptionName, CancellationToken cancellationToken = default)
      => throw new NotImplementedException();
    public Task DeleteRuleAsync(string topicName, string subscriptionName, string ruleName, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task CreateRuleAsync(string topicName, string subscriptionName, CreateRuleOptions options, CancellationToken cancellationToken = default)
      => Task.CompletedTask;
  }

  /// <summary>The entity's head message from a peek of its own, from the first sequence number.</summary>
  private async Task<ServiceBusReceivedMessage?> _peekHeadAsync(string topic) {
    var receiver = _fixture.Client.CreateReceiver(topic, SUBSCRIPTION);
    await using (receiver) {
      return await receiver.PeekMessageAsync(fromSequenceNumber: 0);
    }
  }

  /// <summary>
  /// The entity's head once it is the given message. The emulator acknowledges a send to a topic
  /// before the message is visible to a peek of its subscription, so the first peek straight after
  /// a send can find nothing; this waits for that one transition, the probe becoming the head,
  /// before the sample is taken. That race, with a check that could not see a missing age, is what
  /// left the found-a-message outcome of the transport's peek untaken in some runs.
  /// </summary>
  private async Task<ServiceBusReceivedMessage> _peekUntilHeadIsAsync(string topic, string messageId) {
    for (var attempt = 0; attempt < 100; attempt++) {
      var head = await _peekHeadAsync(topic);
      if (head?.MessageId == messageId) {
        return head;
      }
      await Task.Delay(50);
    }
    throw new InvalidOperationException(
      $"The probe {messageId} never became the head of {topic}/{SUBSCRIPTION}; the drain should have left it the only message.");
  }

  /// <summary>
  /// Deletes whatever an earlier run left on the entity, so its head is what this test put there.
  /// A long-lived local emulator keeps messages between runs; a fresh one has none.
  /// </summary>
  private async Task _drainAsync(string topic) {
    var receiver = _fixture.Client.CreateReceiver(topic, SUBSCRIPTION,
      new ServiceBusReceiverOptions { ReceiveMode = ServiceBusReceiveMode.ReceiveAndDelete });
    await using (receiver) {
      while (await receiver.PeekMessageAsync(fromSequenceNumber: 0) is not null) {
        await receiver.ReceiveMessagesAsync(maxMessages: 100);
      }
    }
  }

  private AzureServiceBusTransport _transport(IServiceBusAdminClient? admin) {
    var transport = new AzureServiceBusTransport(
      _fixture.Client,
      new JsonSerializerOptions(),
      adminClient: admin);
    _disposables.Add(transport);
    return transport;
  }

  [Test]
  public async Task PeekBacklogs_WithoutAnAdminClient_ReportsNothingAsync() {
    // No admin client means no watchdog and no depth source; the sample set is empty rather
    // than a list of entities with unknown depth.
    var transport = _transport(admin: null);

    var samples = await transport.PeekBacklogsAsync(CancellationToken.None);

    await Assert.That(samples).IsEmpty();
  }

  [Test]
  public async Task PeekBacklogs_WithNoTrackedEntities_ReportsNothingAsync() {
    var transport = _transport(new DepthOnlyAdminClient(depth: 5));

    var samples = await transport.PeekBacklogsAsync(CancellationToken.None);

    await Assert.That(samples).IsEmpty();
  }

  [Test]
  public async Task PeekBacklogs_ReadsTheOldestEnqueuedTimeFromTheBrokerAsync() {
    // The peek is real: a message is published, then its enqueued time comes back through
    // the broker rather than through a stub.
    await _drainAsync(HELD_TOPIC);
    var transport = _transport(new DepthOnlyAdminClient(depth: 1));
    transport.LivenessWatchdog!.Track(HELD_TOPIC, SUBSCRIPTION);

    var probe = new ServiceBusMessage("probe") { MessageId = Guid.NewGuid().ToString("N") };
    var sender = _fixture.Client.CreateSender(HELD_TOPIC);
    await using (sender) {
      await sender.SendMessageAsync(probe);
    }
    var head = await _peekUntilHeadIsAsync(HELD_TOPIC, probe.MessageId);

    var before = DateTimeOffset.UtcNow;
    var samples = await transport.PeekBacklogsAsync(CancellationToken.None);
    var after = DateTimeOffset.UtcNow;

    await Assert.That(samples).HasSingleItem();
    await Assert.That(samples[0].Entity).IsEqualTo($"{HELD_TOPIC}/{SUBSCRIPTION}");
    await Assert.That(samples[0].Depth).IsEqualTo(1);
    // HasValue, not IsNotNull: TUnit's IsNotNull passes on a null Nullable<T>, so the earlier
    // form of this check passed whether or not the peek found the message.
    await Assert.That(samples[0].OldestAge.HasValue).IsTrue()
      .Because("a peek that returned nothing would make every backlog look brand new, "
             + "which is the one thing this sample exists to disprove");
    // Bounded by the broker's own enqueued time for the same message, so the check holds whatever
    // the offset between the emulator's clock and this host's. Two peeks of one message on the
    // emulator have disagreed on its enqueued time by a few milliseconds, hence the slack; an
    // age taken from anything but the head's enqueued time is off by far more.
    var slack = TimeSpan.FromSeconds(1);
    await Assert.That(samples[0].OldestAge!.Value).IsGreaterThanOrEqualTo(before - head.EnqueuedTime - slack)
      .Because("the age is measured from the head message's enqueued time");
    await Assert.That(samples[0].OldestAge!.Value).IsLessThanOrEqualTo(after - head.EnqueuedTime + slack)
      .Because("the age is measured from the head message's enqueued time");
  }

  [Test]
  public async Task PeekBacklogs_OnAnEmptyEntity_ReportsDepthWithNoAgeAsync() {
    // An empty head is a real broker answer, not a failure: the sample keeps the depth the
    // management plane reported and has no age, rather than an age made up from nothing.
    await _drainAsync(EMPTY_TOPIC);
    var transport = _transport(new DepthOnlyAdminClient(depth: 4));
    transport.LivenessWatchdog!.Track(EMPTY_TOPIC, SUBSCRIPTION);
    await Assert.That(await _peekHeadAsync(EMPTY_TOPIC)).IsNull()
      .Because("the broker answers a peek of an empty entity with no message rather than an error");

    var samples = await transport.PeekBacklogsAsync(CancellationToken.None);

    await Assert.That(samples).HasSingleItem();
    await Assert.That(samples[0].Entity).IsEqualTo($"{EMPTY_TOPIC}/{SUBSCRIPTION}");
    await Assert.That(samples[0].Depth).IsEqualTo(4);
    await Assert.That(samples[0].OldestAge).IsNull()
      .Because("an empty entity has no oldest message to measure an age from");
  }

  [Test]
  public async Task PeekBacklogs_WhenTheDepthCallFails_SkipsThatEntityAsync() {
    // An entity that cannot be read right now is skipped, not reported with a wrong depth —
    // the next tick retries it.
    var transport = _transport(new DepthOnlyAdminClient(0, new InvalidOperationException("unreadable")));
    transport.LivenessWatchdog!.Track("topic-00", "sub-00-a");

    var samples = await transport.PeekBacklogsAsync(CancellationToken.None);

    await Assert.That(samples).IsEmpty();
  }

  [Test]
  public async Task PeekBacklogs_WithAProbeOverride_UsesItInsteadOfTheBrokerAsync() {
    // The override exists so a host can supply a cheaper age source than a peek.
    var transport = _transport(new DepthOnlyAdminClient(depth: 7));
    transport.LivenessWatchdog!.Track("topic-00", "sub-00-a");
    var stamped = DateTimeOffset.UtcNow.AddMinutes(-30);
    transport.OldestEnqueuedTimeProbe = (_, _, _) => Task.FromResult<DateTimeOffset?>(stamped);

    var samples = await transport.PeekBacklogsAsync(CancellationToken.None);

    await Assert.That(samples).HasSingleItem();
    await Assert.That(samples[0].Depth).IsEqualTo(7);
    await Assert.That(samples[0].OldestAge).IsNotNull();
    await Assert.That(samples[0].OldestAge!.Value.TotalMinutes).IsGreaterThan(25);
  }

  [Test]
  public async Task PeekBacklogs_WhenTheProbeFails_StillReportsDepthAsync() {
    // Age is the richer signal but depth alone is still worth reporting — losing the whole
    // sample because the peek failed would hide a growing backlog entirely.
    var transport = _transport(new DepthOnlyAdminClient(depth: 3));
    transport.LivenessWatchdog!.Track("topic-00", "sub-00-a");
    transport.OldestEnqueuedTimeProbe =
      (_, _, _) => Task.FromException<DateTimeOffset?>(new InvalidOperationException("probe failed"));

    var samples = await transport.PeekBacklogsAsync(CancellationToken.None);

    await Assert.That(samples).HasSingleItem();
    await Assert.That(samples[0].Depth).IsEqualTo(3);
    await Assert.That(samples[0].OldestAge).IsNull();
  }
}
