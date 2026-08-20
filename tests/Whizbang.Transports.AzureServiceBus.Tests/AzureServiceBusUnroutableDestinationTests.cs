using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Routing;
using Whizbang.Core.Transports;

#pragma warning disable CA1707 // Identifiers should not contain underscores (test method names use underscores by convention)

namespace Whizbang.Transports.AzureServiceBus.Tests;

/// <summary>
/// The unroutable-command lock (topology arc phase 6): a destination marked
/// RequireProvisionedEntity names a CONSUMER-provisioned entity (per-namespace command inbox
/// / system broadcast inbox). The transport must NEVER auto-create it — entity existence is
/// the proof a subscriber dark-provisioned it — and a missing entity must fail the publish
/// LOUDLY with <see cref="UnroutableDestinationException"/> carrying the entity name, never
/// silently drop at the broker.
/// </summary>
public class AzureServiceBusUnroutableDestinationTests {
  private const string FLIPPED_ENTITY = "inbox.myapp.orders.commands";

  private static TransportDestination _markedDestination(string address = FLIPPED_ENTITY) =>
    new(address,
        "myapp.orders.commands.placeordercommand",
        new Dictionary<string, JsonElement> {
          [CommandInboxNaming.RequireProvisionedEntityMetadataKey] = AsbTransportTestData.Json("true")
        });

  private static TransportDestination _unmarkedDestination(string address = FLIPPED_ENTITY) =>
    new(address, "myapp.orders.commands.placeordercommand");

  private static AzureServiceBusTransport _transport(
      RaisableServiceBusClient client, RecordingProvisioningAdminClient? adminClient) =>
    new(client,
        AsbTransportTestData.CombinedOptions,
        new AzureServiceBusOptions { EnableSessions = false },
        NullLogger<AzureServiceBusTransport>.Instance,
        adminClient: adminClient);

  [Test]
  public async Task PublishAsync_Marked_MissingEntity_ThrowsUnroutableWithEntityNameAsync() {
    var client = new RaisableServiceBusClient();
    var adminClient = new RecordingProvisioningAdminClient(); // no topics exist
    var transport = _transport(client, adminClient);

    var exception = await Assert.ThrowsAsync<UnroutableDestinationException>(() =>
      transport.PublishAsync(AsbTransportTestData.CreateEnvelope(), _markedDestination()));

    await Assert.That(exception!.EntityName).IsEqualTo(FLIPPED_ENTITY);
  }

  [Test]
  public async Task PublishAsync_Marked_MissingEntity_NeverAutoCreatesTheTopicAsync() {
    var client = new RaisableServiceBusClient();
    var adminClient = new RecordingProvisioningAdminClient();
    var transport = _transport(client, adminClient);

    await Assert.ThrowsAsync<UnroutableDestinationException>(() =>
      transport.PublishAsync(AsbTransportTestData.CreateEnvelope(), _markedDestination()));

    await Assert.That(adminClient.CreatedTopics).IsEmpty()
      .Because("publishers never create consumer-provisioned inbox entities — auto-creating one would turn 'no subscriber' into a silent drop");
  }

  [Test]
  public async Task PublishAsync_Marked_EntityExists_PublishesWithoutCreatingAsync() {
    var client = new RaisableServiceBusClient();
    var adminClient = new RecordingProvisioningAdminClient {
      ExistingTopics = { FLIPPED_ENTITY }
    };
    var transport = _transport(client, adminClient);

    await transport.PublishAsync(AsbTransportTestData.CreateEnvelope(), _markedDestination());

    await Assert.That(client.LastSender!.Sent.Count).IsEqualTo(1);
    await Assert.That(adminClient.CreatedTopics).IsEmpty();
  }

  [Test]
  public async Task PublishAsync_Marked_ProvisionedAfterFailure_SucceedsOnRetryAsync() {
    // The negative answer must NOT be cached: the operator provisions the handling service,
    // then the outbox retry succeeds — no publisher restart required.
    var client = new RaisableServiceBusClient();
    var adminClient = new RecordingProvisioningAdminClient();
    var transport = _transport(client, adminClient);

    await Assert.ThrowsAsync<UnroutableDestinationException>(() =>
      transport.PublishAsync(AsbTransportTestData.CreateEnvelope(), _markedDestination()));

    adminClient.ExistingTopics.Add(FLIPPED_ENTITY); // the handling service provisions
    await transport.PublishAsync(AsbTransportTestData.CreateEnvelope(), _markedDestination());

    await Assert.That(client.LastSender!.Sent.Count).IsEqualTo(1);
  }

  [Test]
  public async Task PublishAsync_Unmarked_AutoCreatesTopicAsTodayAsync() {
    // DEFAULT LOCK: destinations without the marker keep today's on-demand publish-side
    // provisioning byte-identically.
    var client = new RaisableServiceBusClient();
    var adminClient = new RecordingProvisioningAdminClient();
    var transport = _transport(client, adminClient);

    await transport.PublishAsync(AsbTransportTestData.CreateEnvelope(), _unmarkedDestination("myapp.orders.events"));

    await Assert.That(adminClient.CreatedTopics).Contains("myapp.orders.events");
    await Assert.That(client.LastSender!.Sent.Count).IsEqualTo(1);
  }

  [Test]
  public async Task PublishAsync_Marked_NoAdminClient_EntityNotFoundSendFailure_WrapsInUnroutableAsync() {
    // Emulator/no-admin deployments cannot pre-check existence; the SDK's entity-not-found
    // failure at send time is translated into the same LOUD typed failure.
    var client = new RaisableServiceBusClient {
      SendException = new ServiceBusException(
        "entity could not be found", ServiceBusFailureReason.MessagingEntityNotFound)
    };
    var transport = _transport(client, adminClient: null);

    var exception = await Assert.ThrowsAsync<UnroutableDestinationException>(() =>
      transport.PublishAsync(AsbTransportTestData.CreateEnvelope(), _markedDestination()));

    await Assert.That(exception!.EntityName).IsEqualTo(FLIPPED_ENTITY);
    await Assert.That(exception.InnerException).IsTypeOf<ServiceBusException>();
  }

  [Test]
  public async Task PublishAsync_Unmarked_NoAdminClient_EntityNotFoundSendFailure_PropagatesOriginalAsync() {
    // DEFAULT LOCK: unmarked destinations keep today's failure shape (raw SDK exception).
    var client = new RaisableServiceBusClient {
      SendException = new ServiceBusException(
        "entity could not be found", ServiceBusFailureReason.MessagingEntityNotFound)
    };
    var transport = _transport(client, adminClient: null);

    var exception = await Assert.ThrowsAsync<ServiceBusException>(() =>
      transport.PublishAsync(AsbTransportTestData.CreateEnvelope(), _unmarkedDestination("myapp.orders.events")));

    await Assert.That(exception!.Reason).IsEqualTo(ServiceBusFailureReason.MessagingEntityNotFound);
  }
}
