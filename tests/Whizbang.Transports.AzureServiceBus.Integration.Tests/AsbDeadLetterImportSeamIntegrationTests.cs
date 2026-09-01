using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Perspectives.Sync;
using Whizbang.Core.Transports;
using Whizbang.Transports.AzureServiceBus.Integration.Tests.Containers;

#pragma warning disable CA1707 // Identifiers should not contain underscores (test method names use underscores by convention)

namespace Whizbang.Transports.AzureServiceBus.Integration.Tests;

/// <summary>
/// <para>Covers the custody seam the ASB hosting registration hands to the fleet dead-letter
/// drainer — the <c>importAsync</c> lambda in <c>ServiceCollectionExtensions</c> that resolves
/// <see cref="IWorkCoordinator"/> from a fresh scope per call. Unit tests can prove the drainer
/// is REGISTERED, but the lambda itself only runs when a real dead-lettered message is actually
/// imported, so it takes a live broker to execute either arm.</para>
/// <para>Both arms matter operationally and they are opposites:</para>
/// <list type="bullet">
///   <item>Coordinator present — custody transfers and the broker copy is settled, which is what
///   stops a broker DLQ from growing without bound (issue #514).</item>
///   <item>No coordinator — the seam THROWS rather than reporting a duplicate, so the drainer
///   abandons and the message STAYS on the broker DLQ. Returning <c>false</c> there would read
///   as "already in custody, safe to settle" and delete the only copy of the message.</item>
/// </list>
/// <para>Waits are broker-side <c>ReceiveMessageAsync(maxWaitTime)</c> — no polling loops.</para>
/// </summary>
/// <code-under-test>src/Whizbang.Transports.AzureServiceBus/ServiceCollectionExtensions.cs</code-under-test>
/// <code-under-test>src/Whizbang.Transports.AzureServiceBus/AzureServiceBusDeadLetterDrainer.cs</code-under-test>
[Category("Integration")]
[NotInParallel("ServiceBus")]
[Timeout(240_000)]
[ClassDataSource<ServiceBusEmulatorFixtureSource>(Shared = SharedType.PerAssembly)]
public class AsbDeadLetterImportSeamIntegrationTests(ServiceBusEmulatorFixtureSource fixtureSource) {
  private const string TOPIC = "topic-dlq-import";
  private const string CUSTODY_SUB = "sub-dlq-import-custody";
  private const string NO_CUSTODY_SUB = "sub-dlq-import-nocustody";

  private readonly ServiceBusEmulatorFixture _fixture = fixtureSource.Fixture;

  [Test]
  public async Task DrainDeadLetterQueue_WithACoordinatorRegistered_ImportsThroughTheSeamAndSettlesTheBrokerCopyAsync(
      CancellationToken cancellationToken) {
    // Arrange — a container shaped exactly like a host's: the registration under test plus a
    // coordinator that can take custody. Nothing here constructs the drainer by hand; the whole
    // point is that the lambda DI wires up is the one that runs.
    var coordinator = new RecordingWorkCoordinator();
    await using var provider = _buildProvider(services => services.AddSingleton<IWorkCoordinator>(coordinator));

    var subscription = await _activateSubscriptionAsync(provider, CUSTODY_SUB);
    try {
      await _clearAsync(CUSTODY_SUB, cancellationToken);
      var messageId = Guid.CreateVersion7().ToString();
      await _dispatchWithoutEnvelopeTypeAsync(messageId, cancellationToken);

      // The transport dead-letters a message with no EnvelopeType; wait broker-side for it to
      // land on the DLQ, then put it back untouched so the drainer under test finds it.
      var dead = await _awaitOnDeadLetterQueueAsync(CUSTODY_SUB, messageId, cancellationToken);
      await Assert.That(dead).IsNotNull()
        .Because("the rest of the test has nothing to drain unless the transport dead-lettered it");

      // Act
      var drainer = provider.GetRequiredService<ITransportDeadLetterDrainer>();
      var drained = await drainer.DrainDeadLetterQueueAsync(10, cancellationToken);

      // Assert — the seam ran, and it ran with the broker's own metadata intact
      await Assert.That(drained).IsEqualTo(1);
      await Assert.That(coordinator.Imports.Count).IsEqualTo(1)
        .Because("the registered import seam is what transfers custody — one DLQ message, one import");

      var import = coordinator.Imports[0];
      await Assert.That(import.MessageId).IsEqualTo(Guid.Parse(messageId));
      await Assert.That(import.Destination).IsEqualTo($"{TOPIC}/{CUSTODY_SUB}")
        .Because("custody records where the message was stranded, so an operator can trace it back");
      await Assert.That(import.BrokerReason).IsEqualTo("MissingEnvelopeType")
        .Because("the broker's own dead-letter reason is preserved, not replaced by ours");

      // Assert — the broker copy is gone: this is the half that actually drains the DLQ
      var residual = await _peekAsync(CUSTODY_SUB, messageId, cancellationToken);
      await Assert.That(residual).IsNull()
        .Because("custody succeeded, so the broker copy must be completed — leaving it is how "
               + "issue #514's DLQ grew to five figures");
    } finally {
      subscription.Dispose();
    }
  }

  [Test]
  public async Task DrainDeadLetterQueue_WithNoCoordinatorRegistered_LeavesTheMessageOnTheBrokerDlqAsync(
      CancellationToken cancellationToken) {
    // Arrange — the SAME registration, minus the coordinator. This is a real host shape: a
    // transport-only service, or one whose data package failed to register.
    await using var provider = _buildProvider(_ => { });

    var subscription = await _activateSubscriptionAsync(provider, NO_CUSTODY_SUB);
    try {
      await _clearAsync(NO_CUSTODY_SUB, cancellationToken);
      var messageId = Guid.CreateVersion7().ToString();
      await _dispatchWithoutEnvelopeTypeAsync(messageId, cancellationToken);

      var dead = await _awaitOnDeadLetterQueueAsync(NO_CUSTODY_SUB, messageId, cancellationToken);
      await Assert.That(dead).IsNotNull();

      // Act
      var drainer = provider.GetRequiredService<ITransportDeadLetterDrainer>();
      var drained = await drainer.DrainDeadLetterQueueAsync(10, cancellationToken);

      // Assert — nothing drained, and crucially nothing LOST
      await Assert.That(drained).IsEqualTo(0)
        .Because("a message that never reached custody was not drained, and reporting it as "
               + "drained would hide the misconfiguration from the drain worker's metrics");

      var survivor = await _peekAsync(NO_CUSTODY_SUB, messageId, cancellationToken);
      await Assert.That(survivor).IsNotNull()
        .Because("with no coordinator the seam throws, the drainer abandons, and the broker DLQ "
               + "stays the only custody there is — settling it here would destroy the message");
    } finally {
      subscription.Dispose();
      await _clearAsync(NO_CUSTODY_SUB, cancellationToken);
    }
  }

  // ========================================
  // HELPERS
  // ========================================

  private ServiceProvider _buildProvider(Action<IServiceCollection> configure) {
    var services = new ServiceCollection();
    services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning));
    // A client of our own rather than the fixture's: the provider disposes the singletons it
    // holds, and the fixture's client is shared by every test in the assembly.
    services.AddSingleton(new ServiceBusClient(_fixture.ConnectionString));
    services.AddAzureServiceBusTransport(
      _fixture.ConnectionString,
      options => {
        options.EnableSessions = false;
        // Entities are predeclared in Config.json; the emulator serves the data plane only, so
        // registering the admin client would fail every subscribe on a management-API call.
        options.AutoProvisionInfrastructure = false;
      });
    configure(services);
    return services.BuildServiceProvider();
  }

  /// <summary>
  /// Subscribes through the DI-resolved transport so the (topic, subscription) pair enters the
  /// transport's active set — the fleet drainer enumerates that set on every pass, so a
  /// subscription nobody established has no DLQ to drain.
  /// </summary>
  private static async Task<ISubscription> _activateSubscriptionAsync(
      IServiceProvider provider, string subscriptionName) {
    // The cast is the same one the registered drainer makes to reach ActiveSubscriptions: with a
    // single namespace the router short-circuits to the transport itself. If that ever stops
    // being true the drainer silently drains nothing, so failing loudly here is the point.
    var transport = provider.GetRequiredService<ITransport>() as AzureServiceBusTransport
      ?? throw new InvalidOperationException(
        "ITransport did not resolve to AzureServiceBusTransport — the fleet drainer's active "
        + "subscription snapshot depends on exactly this cast and would come back empty.");
    return await transport.SubscribeAsync(
      (_, _, _) => Task.CompletedTask,
      new TransportDestination(TOPIC, subscriptionName));
  }

  /// <summary>
  /// Sends a raw message carrying no EnvelopeType application property. The transport has
  /// nothing to route on, so its decision maker dead-letters it with reason
  /// <c>MissingEnvelopeType</c> — a real broker dead-letter, not one this test staged by hand.
  /// </summary>
  private async Task _dispatchWithoutEnvelopeTypeAsync(string messageId, CancellationToken ct) {
    var sender = _fixture.Client.CreateSender(TOPIC);
    try {
      await sender.SendMessageAsync(
        new ServiceBusMessage("{}") { MessageId = messageId, ContentType = "application/json" },
        ct);
    } finally {
      await sender.DisposeAsync();
    }
  }

  /// <summary>
  /// Waits broker-side for <paramref name="expectedMessageId"/> to reach the subscription's
  /// dead-letter sub-queue and then ABANDONS it, so the message is back on the DLQ, unlocked,
  /// for the drainer under test. Stray dead letters from earlier runs are completed and skipped.
  /// </summary>
  private async Task<ServiceBusReceivedMessage?> _awaitOnDeadLetterQueueAsync(
      string subscriptionName, string expectedMessageId, CancellationToken ct) {
    var receiver = _fixture.Client.CreateReceiver(TOPIC, subscriptionName,
      new ServiceBusReceiverOptions { SubQueue = SubQueue.DeadLetter, ReceiveMode = ServiceBusReceiveMode.PeekLock });
    try {
      for (var attempt = 0; attempt < 6; attempt++) {
        var message = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(10), ct);
        if (message is null) {
          continue;
        }
        if (message.MessageId == expectedMessageId) {
          await receiver.AbandonMessageAsync(message, cancellationToken: ct);
          return message;
        }
        await receiver.CompleteMessageAsync(message, ct);
      }
      return null;
    } finally {
      await receiver.DisposeAsync();
    }
  }

  /// <summary>Peeks the dead-letter sub-queue for a message id without settling anything.</summary>
  private async Task<ServiceBusReceivedMessage?> _peekAsync(
      string subscriptionName, string messageId, CancellationToken ct) {
    var receiver = _fixture.Client.CreateReceiver(TOPIC, subscriptionName,
      new ServiceBusReceiverOptions { SubQueue = SubQueue.DeadLetter, ReceiveMode = ServiceBusReceiveMode.PeekLock });
    try {
      var peeked = await receiver.PeekMessagesAsync(maxMessages: 100, cancellationToken: ct);
      return peeked.FirstOrDefault(m => m.MessageId == messageId);
    } finally {
      await receiver.DisposeAsync();
    }
  }

  /// <summary>Empties both the subscription and its dead-letter sub-queue.</summary>
  private async Task _clearAsync(string subscriptionName, CancellationToken ct) {
    foreach (var subQueue in new[] { SubQueue.None, SubQueue.DeadLetter }) {
      var receiver = _fixture.Client.CreateReceiver(TOPIC, subscriptionName,
        new ServiceBusReceiverOptions { SubQueue = subQueue, ReceiveMode = ServiceBusReceiveMode.ReceiveAndDelete });
      try {
        while (true) {
          var batch = await receiver.ReceiveMessagesAsync(100, TimeSpan.FromMilliseconds(200), ct);
          if (batch is null || batch.Count == 0) {
            break;
          }
        }
      } finally {
        await receiver.DisposeAsync();
      }
    }
  }

  /// <summary>
  /// Records every import the seam performs. Every other coordinator operation is the interface
  /// default — this exists to observe custody transfer, not to store anything.
  /// </summary>
  private sealed class RecordingWorkCoordinator : IWorkCoordinator {
    private readonly List<BrokerDeadLetterImport> _imports = [];
    private readonly Lock _lock = new();

    public IReadOnlyList<BrokerDeadLetterImport> Imports {
      get {
        lock (_lock) {
          return [.. _imports];
        }
      }
    }

    public Task<bool> ImportBrokerDeadLetterAsync(
        BrokerDeadLetterImport import, CancellationToken cancellationToken = default) {
      lock (_lock) {
        _imports.Add(import);
      }
      return Task.FromResult(true);
    }

    public Task<WorkBatch> ClaimWorkAsync(ClaimWorkRequest request, CancellationToken cancellationToken = default) =>
      Task.FromResult(new WorkBatch { OutboxWork = [], InboxWork = [], PerspectiveWork = [] });

    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount = 2, CancellationToken cancellationToken = default) =>
      Task.CompletedTask;

    public Task StoreOutboxMessagesAsync(OutboxMessage[] messages, int partitionCount, CancellationToken cancellationToken = default) =>
      Task.CompletedTask;

    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion completion, CancellationToken cancellationToken = default) =>
      Task.CompletedTask;

    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure failure, CancellationToken cancellationToken = default) =>
      Task.CompletedTask;

    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken cancellationToken = default) =>
      Task.FromResult(new WorkCoordinatorStatistics());

    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default) =>
      Task.CompletedTask;

    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default) =>
      Task.FromResult<PerspectiveCursorInfo?>(null);
  }
}
