using System.Collections.Concurrent;
using System.Threading.Channels;
using Azure.Messaging.ServiceBus;
using Whizbang.Core.Dispatch;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Serialization;
using Whizbang.Core.Transports;
using Whizbang.Core.ValueObjects;
using Whizbang.Transports.AzureServiceBus.Integration.Tests.Containers;

namespace Whizbang.Transports.AzureServiceBus.Integration.Tests;

/// <summary>
/// The transport-tier E2E that was missing when a live fleet's receive side froze: the
/// reconciliation E2E proves the ALGORITHM over an in-test wire, so every transport-layer
/// failure (session locks dying under the consumers, redeliveries burning to the DLQ, a
/// backlog that never drains) was invisible to it by construction. These tests run the REAL
/// transport against the real broker emulator and pin the two outcomes that matter:
///
/// <list type="number">
/// <item>The failure mechanism is reproducible: short locks + starved renewal turn a modest
/// backlog into dead letters instead of progress — the exact signature observed live
/// (locks lost mid-handling, completions failing, MaxDeliveryCount exhausting).</item>
/// <item>The invariant holds under safe settings: a session-distributed backlog drains to
/// ZERO, every message completes exactly once, and nothing dead-letters.</item>
/// </list>
///
/// <para>Honest limits, documented deliberately: namespace throttling (error 50009) and
/// renewal starvation at production concurrency are not emulator-reproducible — those remain
/// the live meters' job (Whizbang.StreamIntegrity + transport metrics).</para>
///
/// <para>Timing note: the slow handler in the failure-mechanism test is NOT a synchronization
/// sleep — the delay IS the scenario (handler work exceeding the lock duration). All
/// synchronization is completion-signal or bounded-receive based.</para>
/// </summary>
/// <docs>transports/azure-service-bus</docs>
[ClassDataSource<ServiceBusEmulatorFixtureSource>(Shared = SharedType.PerAssembly)]
public class SessionBacklogDrainIntegrationTests(ServiceBusEmulatorFixtureSource fixtureSource) {
  private readonly ServiceBusEmulatorFixture _fixture = fixtureSource.Fixture;
  private readonly List<IAsyncDisposable> _disposables = [];

  private const string HAIR_TRIGGER_TOPIC = "topic-drain-hairtrigger";   // LockDuration PT5S in Config.json
  private const string SAFE_TOPIC = "topic-drain-safe";                  // LockDuration PT1M in Config.json
  private const string DRAIN_SUB = "sub-drain-session";                  // MaxDeliveryCount 3, RequiresSession

  [After(Test)]
  public async Task DisposeTrackedTransportsAsync() {
    foreach (var d in _disposables) {
      try { await d.DisposeAsync(); } catch { /* best-effort cleanup */ }
    }
    _disposables.Clear();
  }

  /// <summary>
  /// The live failure, as a test: 5-second locks (the scaled-down analog of the 1-minute
  /// default that froze a fleet), lock renewal disabled (the deterministic analog of renewal
  /// starvation under concurrency), and a handler whose work outlives the lock. Every
  /// completion then races a lock the broker already revoked: messages redeliver, exhaust
  /// MaxDeliveryCount (3), and dead-letter — the backlog burns instead of draining.
  /// </summary>
  [Test]
  [Timeout(180_000)]
  public async Task HairTriggerLocks_WithStarvedRenewal_BurnTheBacklogToDeadLettersAsync() {
    var jsonOptions = JsonContextRegistry.CreateCombinedOptions();
    var options = new AzureServiceBusOptions {
      EnableSessions = true,
      MaxConcurrentSessions = 3,
      AutoProvisionInfrastructure = false,   // entities are predeclared in Config.json
      MaxAutoLockRenewalDuration = TimeSpan.Zero,   // renewal starvation, made deterministic
    };
    var transport = new AzureServiceBusTransport(_fixture.Client, jsonOptions, options);
    _disposables.Add(transport);
    await transport.InitializeAsync();
    await _drainDeadLetterQueueAsync(HAIR_TRIGGER_TOPIC, DRAIN_SUB);

    var completedIds = new ConcurrentDictionary<Guid, int>();
    var subscription = await transport.SubscribeAsync(
      async (envelope, _, _) => {
        // The scenario: handler work exceeding the 5s lock. CancellationToken.None because a
        // lock-lost processor cancels the handler token — the live handlers kept working too.
        await Task.Delay(TimeSpan.FromSeconds(8), CancellationToken.None);
        completedIds.AddOrUpdate(envelope.MessageId.Value, 1, (_, n) => n + 1);
      },
      new TransportDestination(HAIR_TRIGGER_TOPIC, DRAIN_SUB));

    var publishedIds = new List<Guid>();
    try {
      foreach (var session in Enumerable.Range(0, 3)) {
        var streamId = Guid.CreateVersion7();
        for (var m = 0; m < 2; m++) {
          var envelope = _createTestEnvelope($"burn-{session}-{m}");
          publishedIds.Add(envelope.MessageId.Value);
          await transport.PublishAsync(envelope, _destinationWithStream(HAIR_TRIGGER_TOPIC, streamId));
        }
      }

      // The proof of failure is a DLQ receipt: bounded receive until at least one of OUR
      // messages dead-letters (broker-side MaxDeliveryCount exhaustion — no handler throw).
      var deadLettered = await _receiveAnyDeadLetteredAsync(
        HAIR_TRIGGER_TOPIC, DRAIN_SUB, publishedIds, TimeSpan.FromSeconds(150));

      // Emulator-fidelity gate: if every message completed exactly once despite handlers
      // outliving the lock, this emulator build does not ENFORCE session-lock expiry — the
      // completion that would throw SessionLockLost against the real service succeeds here.
      // A lying green is worse than an honest skip: the mechanism stays covered by the
      // SessionOccupancyGovernor unit tests and by live-fleet evidence; the enforced
      // invariant on this tier is the drain-to-zero sibling test.
      var totalDeliveries = completedIds.Values.Sum();
      if (deadLettered is null && totalDeliveries == publishedIds.Count
          && completedIds.Keys.ToHashSet().SetEquals(publishedIds)) {
        Skip.Test("This Service Bus emulator build does not enforce session-lock expiry "
                  + "(completions on revoked locks succeed), so the burn mechanism cannot "
                  + "reproduce here — it is covered by unit tests and live evidence.");
      }

      await Assert.That(deadLettered).IsNotNull()
        .Because("a lock the broker revoked mid-handling makes completion impossible — after "
                 + "MaxDeliveryCount redeliveries the message burns to the DLQ, which is "
                 + "exactly how a live fleet turned a backlog into dead letters");
    } finally {
      subscription.Dispose();
      await _drainDeadLetterQueueAsync(HAIR_TRIGGER_TOPIC, DRAIN_SUB);
    }
  }

  /// <summary>
  /// The invariant the whole receive side exists to uphold, on the real wire: a
  /// session-distributed backlog drains to ZERO — every message completes exactly once,
  /// nothing dead-letters, and the subscription is empty afterwards. Any future
  /// receive-side freeze, whatever its mechanism, fails THIS test as "the backlog did not
  /// reach zero".
  /// </summary>
  [Test]
  [Timeout(180_000)]
  public async Task WideLocks_DrainTheBacklogToZero_EveryMessageExactlyOnce_NothingDeadLettersAsync() {
    const int SESSIONS = 6;
    const int PER_SESSION = 6;
    const int TOTAL = SESSIONS * PER_SESSION;

    var jsonOptions = JsonContextRegistry.CreateCombinedOptions();
    var options = new AzureServiceBusOptions {
      EnableSessions = true,
      MaxConcurrentSessions = SESSIONS,
      AutoProvisionInfrastructure = false,
    };
    var transport = new AzureServiceBusTransport(_fixture.Client, jsonOptions, options);
    _disposables.Add(transport);
    await transport.InitializeAsync();
    await _drainDeadLetterQueueAsync(SAFE_TOPIC, DRAIN_SUB);

    var handled = Channel.CreateUnbounded<Guid>();
    var subscription = await transport.SubscribeAsync(
      async (envelope, _, ct) => {
        await handled.Writer.WriteAsync(envelope.MessageId.Value, ct);
      },
      new TransportDestination(SAFE_TOPIC, DRAIN_SUB));

    var publishedIds = new HashSet<Guid>();
    try {
      foreach (var session in Enumerable.Range(0, SESSIONS)) {
        var streamId = Guid.CreateVersion7();
        for (var m = 0; m < PER_SESSION; m++) {
          var envelope = _createTestEnvelope($"drain-{session}-{m}");
          publishedIds.Add(envelope.MessageId.Value);
          await transport.PublishAsync(envelope, _destinationWithStream(SAFE_TOPIC, streamId));
        }
      }

      // Completion-signal drain: read exactly TOTAL handled ids.
      var received = new List<Guid>(TOTAL);
      using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
      for (var i = 0; i < TOTAL; i++) {
        received.Add(await handled.Reader.ReadAsync(cts.Token));
      }

      await Assert.That(received.Distinct().Count()).IsEqualTo(TOTAL)
        .Because("exactly-once at the transport tier: a duplicate here means a completion "
                 + "failed and the broker redelivered — the burn loop in miniature");
      await Assert.That(received.ToHashSet().SetEquals(publishedIds)).IsTrue()
        .Because("every published message must be the one that drained — no losses, no strays");

      // Nothing dead-lettered: a bounded receive on the DLQ comes back empty.
      var straggler = await _receiveAnyDeadLetteredAsync(
        SAFE_TOPIC, DRAIN_SUB, [.. publishedIds], TimeSpan.FromSeconds(3));
      await Assert.That(straggler).IsNull()
        .Because("a drained backlog with dead letters is not a drained backlog");
    } finally {
      subscription.Dispose();
    }
  }

  // ===== Helpers =====

  private static TransportDestination _destinationWithStream(string topic, Guid streamId) =>
    new(topic, null, new Dictionary<string, System.Text.Json.JsonElement> {
      ["StreamId"] = System.Text.Json.JsonDocument.Parse($"\"{streamId}\"").RootElement
    });

  private static MessageEnvelope<TestMessage> _createTestEnvelope(string content) =>
    new() {
      MessageId = MessageId.New(),
      Payload = new TestMessage(content),
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Outbox },
      Hops = [
        new MessageHop {
          Type = HopType.Current,
          Timestamp = DateTimeOffset.UtcNow,
          Topic = "drain-test",
          ServiceInstance = ServiceInstanceInfo.Unknown
        }
      ]
    };

  /// <summary>Bounded DLQ receive: first message whose id is in <paramref name="candidateIds"/>, or null.</summary>
  private async Task<ServiceBusReceivedMessage?> _receiveAnyDeadLetteredAsync(
    string topicName, string subscriptionName, IReadOnlyCollection<Guid> candidateIds, TimeSpan overallBudget) {
    var candidates = candidateIds.Select(g => g.ToString()).ToHashSet();
    var receiver = _fixture.Client.CreateReceiver(topicName, subscriptionName,
      new ServiceBusReceiverOptions { SubQueue = SubQueue.DeadLetter, ReceiveMode = ServiceBusReceiveMode.PeekLock });
    try {
      var deadline = DateTimeOffset.UtcNow + overallBudget;
      while (DateTimeOffset.UtcNow < deadline) {
        var message = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(3));
        if (message is null) {
          continue;
        }
        await receiver.CompleteMessageAsync(message);
        if (candidates.Contains(message.MessageId)) {
          return message;
        }
      }
      return null;
    } finally {
      await receiver.DisposeAsync();
    }
  }

  private async Task _drainDeadLetterQueueAsync(string topicName, string subscriptionName) {
    var receiver = _fixture.Client.CreateReceiver(topicName, subscriptionName,
      new ServiceBusReceiverOptions { SubQueue = SubQueue.DeadLetter, ReceiveMode = ServiceBusReceiveMode.PeekLock });
    try {
      for (var i = 0; i < 100; i++) {
        var message = await receiver.ReceiveMessageAsync(TimeSpan.FromMilliseconds(200));
        if (message is null) {
          break;
        }
        await receiver.CompleteMessageAsync(message);
      }
    } finally {
      await receiver.DisposeAsync();
    }
  }
}
