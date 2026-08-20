using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Routing;

namespace Whizbang.Transports.AzureServiceBus.Tests;

/// <summary>
/// Sessionless provisioning for the control class (topology arc phase 9). Session enablement is a
/// transport-WIDE boolean today; the control class is the first per-entity deviation, and it
/// travels on the manifest subscription rather than on options — the routing strategy names the
/// class, the transport executes it, which is the same division of labour phase 8 used for
/// TransportNamespaces.
/// <para>
/// Two reasons this matters, and the second is the load-bearing one:
/// </para>
/// <list type="bullet">
/// <item>Control consumers need no ordering, so the accept/lock machinery is pure idle cost for
/// this class — the very cost that pinned a namespace at its request ceiling while idle.</item>
/// <item>Phase 8.5 established that a SESSION-enabled entity's delivery counter does not rise
/// under connection-death lock loss, so the broker's <c>MaxDeliveryCount</c> valve can never fire
/// there. A sessionless entity's does. Provisioning the control class sessionless therefore gives
/// it back the broker's own valve, and the age-based detector becomes its backstop rather than its
/// only defence.</item>
/// </list>
/// </summary>
[Timeout(10_000)]
[Category("Transports")]
public class AsbControlClassProvisioningTests {
  [Test]
  public async Task ProvisionManifest_ControlClassSubscription_CreatedWithoutSessionsAsync() {
    var admin = new RecordingProvisioningAdminClient();
    var provisioner = _provisioner(admin, new AzureServiceBusOptions { EnableSessions = true });

    await provisioner.ProvisionManifestAsync(_manifest(_controlSubscription()));

    var created = admin.CreatedSubscriptions.Single(s => s.Topic == CommandInboxNaming.ControlBroadcastTopic);
    // The sessionless overload leaves RequiresSession unset — on Service Bus, "not requested" IS
    // sessionless (it is immutable after creation, which is why the deviation must be made here).
    await Assert.That(created.RequiresSession is true).IsFalse()
      .Because("the control class is provisioned sessionless even though the transport enables "
             + "sessions everywhere else — that per-entity deviation IS the class");
  }

  [Test]
  public async Task ProvisionManifest_DurableBroadcastSubscription_KeepsSessionsAsync() {
    // The other half of the split: durable system commands and composite envelopes keep ordering.
    var admin = new RecordingProvisioningAdminClient();
    var provisioner = _provisioner(admin, new AzureServiceBusOptions { EnableSessions = true });

    await provisioner.ProvisionManifestAsync(_manifest(_durableBroadcastSubscription()));

    var created = admin.CreatedSubscriptions.Single(s => s.Topic == CommandInboxNaming.SystemBroadcastTopic);
    await Assert.That(created.RequiresSession).IsEqualTo(true);
  }

  [Test]
  public async Task ProvisionManifest_SessionsDisabledGlobally_ControlClassStaysSessionlessAsync() {
    // The marker must not accidentally RE-ENABLE sessions on a host that turned them off.
    var admin = new RecordingProvisioningAdminClient();
    var provisioner = _provisioner(admin, new AzureServiceBusOptions { EnableSessions = false });

    await provisioner.ProvisionManifestAsync(_manifest(_controlSubscription(), _durableBroadcastSubscription()));

    foreach (var created in admin.CreatedSubscriptions) {
      await Assert.That(created.RequiresSession is null or false).IsTrue();
    }
  }

  [Test]
  public async Task ProvisionManifest_ControlAndDurableEntities_AreDistinctSubscriptionsAsync() {
    var admin = new RecordingProvisioningAdminClient();
    var provisioner = _provisioner(admin, new AzureServiceBusOptions { EnableSessions = true });

    await provisioner.ProvisionManifestAsync(_manifest(_controlSubscription(), _durableBroadcastSubscription()));

    await Assert.That(admin.CreatedSubscriptions.Count).IsEqualTo(2);
    await Assert.That(admin.CreatedTopics.Order()).IsEquivalentTo([
      CommandInboxNaming.ControlBroadcastTopic,
      CommandInboxNaming.SystemBroadcastTopic,
    ]);
  }

  private static ServiceBusInfrastructureProvisioner _provisioner(
      RecordingProvisioningAdminClient admin, AzureServiceBusOptions options) =>
    new(admin, NullLogger<ServiceBusInfrastructureProvisioner>.Instance, options);

  private static TopologyManifest _manifest(params InboxSubscription[] subscriptions) =>
    new("orders-service", [], subscriptions);

  private static InboxSubscription _controlSubscription() => new(
    Topic: CommandInboxNaming.ControlBroadcastTopic,
    FilterExpression: "whizbang.core.messaging.#",
    Metadata: new Dictionary<string, object> {
      ["RoutingPatterns"] = new List<string> { "whizbang.core.messaging.#" },
      [NamespaceInboxStrategy.ControlClassMetadataKey] = true,
    });

  private static InboxSubscription _durableBroadcastSubscription() => new(
    Topic: CommandInboxNaming.SystemBroadcastTopic,
    FilterExpression: "whizbang.core.commands.system.#",
    Metadata: new Dictionary<string, object> {
      ["RoutingPatterns"] = new List<string> { "whizbang.core.commands.system.#" },
    });
}
