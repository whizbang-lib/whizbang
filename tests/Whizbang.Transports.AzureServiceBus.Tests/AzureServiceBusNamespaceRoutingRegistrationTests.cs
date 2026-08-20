#pragma warning disable CA1707 // Test method names can contain underscores

using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Transports;

namespace Whizbang.Transports.AzureServiceBus.Tests;

/// <summary>
/// Registration tests for the multi-namespace ASB overload (transport traffic classes, topology
/// arc phase 8 / #424 increment 2): one <see cref="ServiceBusClient"/> per configured
/// TransportNamespace, publish routed by the key the publish strategy stamped on destination
/// metadata, and the LOCKED single-namespace guarantee — the connection-string overload and a
/// <c>default</c>-only map produce byte-identical registrations with zero extra clients.
/// </summary>
public class AzureServiceBusNamespaceRoutingRegistrationTests {
  private const string EMULATOR_CONNECTION_STRING =
    "Endpoint=sb://localhost;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true";
  private const string SECOND_EMULATOR_CONNECTION_STRING =
    "Endpoint=sb://127.0.0.1;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true";

  #region 'default' is required

  [Test]
  public async Task AddAzureServiceBusTransport_MapWithoutDefaultKey_ThrowsNamingTheKeyAsync() {
    // 'default' is the namespace every unrouted message and every unmatched key falls back to.
    // A map without it has no home for the traffic that carries no class — fail at registration
    // with an actionable message rather than route silently into whichever key sorts first.
    var services = new ServiceCollection();
    var map = new Dictionary<string, string> { ["bulk"] = EMULATOR_CONNECTION_STRING };

    var ex = await Assert.ThrowsAsync<ArgumentException>(() => {
      services.AddAzureServiceBusTransport(map);
      return Task.CompletedTask;
    });

    await Assert.That(ex!.Message).Contains(TransportNamespaces.DefaultKey);
    await Assert.That(ex.ParamName).IsEqualTo("namespaceConnectionStrings");
  }

  [Test]
  public async Task AddAzureServiceBusTransport_EmptyMap_ThrowsAsync() {
    var services = new ServiceCollection();

    await Assert.ThrowsAsync<ArgumentException>(() => {
      services.AddAzureServiceBusTransport(new Dictionary<string, string>());
      return Task.CompletedTask;
    });
  }

  [Test]
  public async Task AddAzureServiceBusTransport_NullMap_ThrowsAsync() {
    var services = new ServiceCollection();

    await Assert.ThrowsAsync<ArgumentNullException>(() => {
      services.AddAzureServiceBusTransport((IReadOnlyDictionary<string, string>)null!);
      return Task.CompletedTask;
    });
  }

  [Test]
  public async Task AddAzureServiceBusTransport_BlankConnectionStringForAKey_ThrowsNamingItAsync() {
    var services = new ServiceCollection();
    var map = new Dictionary<string, string> {
      [TransportNamespaces.DefaultKey] = EMULATOR_CONNECTION_STRING,
      ["bulk"] = "   "
    };

    var ex = await Assert.ThrowsAsync<ArgumentException>(() => {
      services.AddAzureServiceBusTransport(map);
      return Task.CompletedTask;
    });

    await Assert.That(ex!.Message).Contains("bulk");
  }

  #endregion

  #region Single-namespace guarantee (LOCKED)

  [Test]
  public async Task AddAzureServiceBusTransport_DefaultOnlyMap_RegistersExactlyTheSingleStringShapeAsync() {
    // The locked no-op guarantee: a 'default'-only map IS the single-connection-string
    // registration — same service types, same lifetimes, same count. No composition, no
    // second client, nothing to review differently in a single-namespace deployment.
    var viaString = new ServiceCollection();
    viaString.AddAzureServiceBusTransport(EMULATOR_CONNECTION_STRING);

    var viaMap = new ServiceCollection();
    viaMap.AddAzureServiceBusTransport(
      new Dictionary<string, string> { [TransportNamespaces.DefaultKey] = EMULATOR_CONNECTION_STRING });

    await Assert.That(viaMap.Count).IsEqualTo(viaString.Count);
    await Assert.That(viaMap.Select(d => $"{d.ServiceType.FullName}|{d.Lifetime}").ToList())
      .IsEquivalentTo(viaString.Select(d => $"{d.ServiceType.FullName}|{d.Lifetime}").ToList());
  }

  [Test]
  public async Task AddAzureServiceBusTransport_DefaultOnlyMap_RegistersExactlyOneServiceBusClientAsync() {
    var services = new ServiceCollection();

    services.AddAzureServiceBusTransport(
      new Dictionary<string, string> { [TransportNamespaces.DefaultKey] = EMULATOR_CONNECTION_STRING });

    await Assert.That(services.Count(d => d.ServiceType == typeof(ServiceBusClient))).IsEqualTo(1);
  }

  [Test]
  public async Task AddAzureServiceBusTransport_DefaultOnlyMap_TransportIsNotAComposedRouterAsync() {
    var services = new ServiceCollection();
    services.AddAzureServiceBusTransport(
      new Dictionary<string, string> { [TransportNamespaces.DefaultKey] = EMULATOR_CONNECTION_STRING },
      o => o.AutoProvisionInfrastructure = false);

    await using var provider = _offline(services).BuildServiceProvider();
    var transport = provider.GetRequiredService<ITransport>();

    await Assert.That(transport).IsTypeOf<AzureServiceBusTransport>()
      .Because("a single-namespace host must be byte-identical to today — no routing wrapper at all");
  }

  #endregion

  #region Multi-namespace composition

  [Test]
  public async Task AddAzureServiceBusTransport_MultiNamespaceMap_ComposesANamespaceRouterAsync() {
    var services = new ServiceCollection();
    services.AddAzureServiceBusTransport(
      new Dictionary<string, string> {
        [TransportNamespaces.DefaultKey] = EMULATOR_CONNECTION_STRING,
        ["bulk"] = SECOND_EMULATOR_CONNECTION_STRING
      },
      o => o.AutoProvisionInfrastructure = false);

    await using var provider = _offline(services).BuildServiceProvider();
    var transport = provider.GetRequiredService<ITransport>();

    var router = await Assert.That(transport).IsTypeOf<NamespaceRoutingTransport>();
    await Assert.That(router!.NamespaceKeys).IsEquivalentTo(new[] { TransportNamespaces.DefaultKey, "bulk" });
  }

  [Test]
  public async Task AddAzureServiceBusTransport_MultiNamespaceMap_BuildsOneTransportPerNamespaceAsync() {
    // One client per namespace — not one client shared behind a routing table. Each namespace
    // owns its own senders, admin plane, session acceptors and ops-rate projection, which is
    // what keeps the per-namespace governors from counting each other's slots.
    var services = new ServiceCollection();
    services.AddAzureServiceBusTransport(
      new Dictionary<string, string> {
        [TransportNamespaces.DefaultKey] = EMULATOR_CONNECTION_STRING,
        ["bulk"] = SECOND_EMULATOR_CONNECTION_STRING,
        ["control"] = SECOND_EMULATOR_CONNECTION_STRING
      },
      o => o.AutoProvisionInfrastructure = false);

    await using var provider = _offline(services).BuildServiceProvider();
    var router = (NamespaceRoutingTransport)provider.GetRequiredService<ITransport>();

    await Assert.That(router.Transports.Count).IsEqualTo(3);
    await Assert.That(router.Transports.Distinct().Count()).IsEqualTo(3);
    await Assert.That(router.Transports.All(t => t is AzureServiceBusTransport)).IsTrue();
  }

  [Test]
  public async Task AddAzureServiceBusTransport_MultiNamespaceMap_ResolveRoutesByKeyAsync() {
    var services = new ServiceCollection();
    services.AddAzureServiceBusTransport(
      new Dictionary<string, string> {
        [TransportNamespaces.DefaultKey] = EMULATOR_CONNECTION_STRING,
        ["bulk"] = SECOND_EMULATOR_CONNECTION_STRING
      },
      o => o.AutoProvisionInfrastructure = false);

    await using var provider = _offline(services).BuildServiceProvider();
    var router = (NamespaceRoutingTransport)provider.GetRequiredService<ITransport>();

    await Assert.That(router.Resolve("bulk")).IsNotSameReferenceAs(router.Resolve(TransportNamespaces.DefaultKey));
    await Assert.That(router.Resolve("never-configured"))
      .IsSameReferenceAs(router.Resolve(TransportNamespaces.DefaultKey))
      .Because("an unconfigured class degrades to default, never to a drop");
  }

  [Test]
  public async Task AddAzureServiceBusTransport_MultiNamespaceMap_DefaultClientIsTheDiRegisteredOneAsync() {
    // The default namespace keeps using the container's ServiceBusClient, so the readiness
    // check, dead-letter drainer and every other client consumer are untouched.
    var services = new ServiceCollection();
    services.AddAzureServiceBusTransport(
      new Dictionary<string, string> {
        [TransportNamespaces.DefaultKey] = EMULATOR_CONNECTION_STRING,
        ["bulk"] = SECOND_EMULATOR_CONNECTION_STRING
      },
      o => o.AutoProvisionInfrastructure = false);

    await Assert.That(services.Count(d => d.ServiceType == typeof(ServiceBusClient))).IsEqualTo(1)
      .Because("non-default clients are owned by the composition, never registered as ambient singletons — "
        + "the container's ServiceBusClient stays the DEFAULT namespace's, so the readiness check and "
        + "dead-letter drainer are untouched by adding traffic classes");

    await using var provider = _offline(services).BuildServiceProvider();
    var router = (NamespaceRoutingTransport)provider.GetRequiredService<ITransport>();
    var ambientClient = provider.GetRequiredService<ServiceBusClient>();

    await Assert.That(ambientClient.FullyQualifiedNamespace).IsEqualTo("localhost");
    await Assert.That(router.Resolve(TransportNamespaces.DefaultKey)).IsNotNull();
  }

  #endregion

  #region Publish strategy wiring

  [Test]
  public async Task AddAzureServiceBusTransport_WiresTheTransportNamespaceSeamOnThePublishStrategyAsync() {
    // Without the seam the strategy never stamps a key and every class silently rides default.
    var services = new ServiceCollection();
    services.AddWhizbang(o => o.Tags.RouteNamespace("bulk-import", "bulk"));
    services.AddAzureServiceBusTransport(EMULATOR_CONNECTION_STRING, o => o.AutoProvisionInfrastructure = false);

    await using var provider = _offline(services).BuildServiceProvider();
    var strategy = provider.GetRequiredService<Whizbang.Core.Workers.IMessagePublishStrategy>();

    var field = typeof(Whizbang.Core.Workers.TransportPublishStrategy)
      .GetField("_transportNamespaces", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
    await Assert.That(field!.GetValue(strategy)).IsNotNull();
  }

  #endregion

  #region Per-namespace manifest provisioning

  [Test]
  public async Task AddAzureServiceBusProvisioner_DefaultOnlyMap_RegistersTheSingleProvisionerAsync() {
    var services = new ServiceCollection();
    services.AddAzureServiceBusProvisioner(
      new Dictionary<string, string> { [TransportNamespaces.DefaultKey] = EMULATOR_CONNECTION_STRING });

    await using var provider = _offline(services).BuildServiceProvider();

    await Assert.That(provider.GetRequiredService<IInfrastructureProvisioner>())
      .IsTypeOf<ServiceBusInfrastructureProvisioner>()
      .Because("a single-namespace host provisions exactly as it does today — no composition");
  }

  [Test]
  public async Task AddAzureServiceBusProvisioner_MultiNamespaceMap_ProvisionsEveryNamespaceAsync() {
    // The consume-side mirror subscribes the SAME entity set in each class namespace, so the
    // manifest pass has to have created those entities there.
    var services = new ServiceCollection();
    services.AddAzureServiceBusProvisioner(
      new Dictionary<string, string> {
        [TransportNamespaces.DefaultKey] = EMULATOR_CONNECTION_STRING,
        ["bulk"] = SECOND_EMULATOR_CONNECTION_STRING
      });

    await using var provider = _offline(services).BuildServiceProvider();

    await Assert.That(provider.GetRequiredService<IInfrastructureProvisioner>())
      .IsTypeOf<CompositeInfrastructureProvisioner>();
  }

  [Test]
  public async Task AddAzureServiceBusProvisioner_MapWithoutDefaultKey_ThrowsAsync() {
    var services = new ServiceCollection();

    var ex = await Assert.ThrowsAsync<ArgumentException>(() => {
      services.AddAzureServiceBusProvisioner(new Dictionary<string, string> { ["bulk"] = EMULATOR_CONNECTION_STRING });
      return Task.CompletedTask;
    });

    await Assert.That(ex!.ParamName).IsEqualTo("namespaceConnectionStrings");
  }

  #endregion

  #region Configuration binding

  [Test]
  public async Task NamespaceConnectionStrings_BindsFromConfigurationViaConnectionStringsConventionAsync() {
    // Shape: Whizbang:Transports:AzureServiceBus:Namespaces:<key> names a ConnectionStrings
    // entry; the connection VALUE never lives in the Whizbang section (it is a secret).
    var configuration = new ConfigurationBuilder()
      .AddInMemoryCollection(new Dictionary<string, string?> {
        ["ConnectionStrings:servicebus"] = EMULATOR_CONNECTION_STRING,
        ["ConnectionStrings:servicebus-bulk"] = SECOND_EMULATOR_CONNECTION_STRING,
        ["Whizbang:Transports:AzureServiceBus:Namespaces:default"] = "servicebus",
        ["Whizbang:Transports:AzureServiceBus:Namespaces:bulk"] = "servicebus-bulk"
      })
      .Build();

    var map = TransportNamespaceConnectionStrings.Read(
      configuration, AzureServiceBusOptionsPostConfigure.CONFIGURATION_SECTION);

    await Assert.That(map.Count).IsEqualTo(2);
    await Assert.That(map[TransportNamespaces.DefaultKey]).IsEqualTo(EMULATOR_CONNECTION_STRING);
    await Assert.That(map["bulk"]).IsEqualTo(SECOND_EMULATOR_CONNECTION_STRING);
  }

  [Test]
  public async Task NamespaceConnectionStrings_InlineValueIsUsedWhenNoConnectionStringsEntryMatchesAsync() {
    // Escape hatch for hosts that inject the whole value (env var, secret store projection).
    var configuration = new ConfigurationBuilder()
      .AddInMemoryCollection(new Dictionary<string, string?> {
        ["Whizbang:Transports:AzureServiceBus:Namespaces:default"] = EMULATOR_CONNECTION_STRING
      })
      .Build();

    var map = TransportNamespaceConnectionStrings.Read(
      configuration, AzureServiceBusOptionsPostConfigure.CONFIGURATION_SECTION);

    await Assert.That(map[TransportNamespaces.DefaultKey]).IsEqualTo(EMULATOR_CONNECTION_STRING);
  }

  [Test]
  public async Task NamespaceConnectionStrings_AbsentSection_IsEmptyAsync() {
    var configuration = new ConfigurationBuilder().Build();

    var map = TransportNamespaceConnectionStrings.Read(
      configuration, AzureServiceBusOptionsPostConfigure.CONFIGURATION_SECTION);

    await Assert.That(map.Count).IsEqualTo(0);
  }

  [Test]
  public async Task AddAzureServiceBusTransport_ConfigurationNamespacesOverrideTheCodeMapAsync() {
    // Configuration wins over the code callback — the operator seam, same as every other
    // transport knob (AzureServiceBusOptionsPostConfigure).
    var configuration = new ConfigurationBuilder()
      .AddInMemoryCollection(new Dictionary<string, string?> {
        ["ConnectionStrings:sb-bulk"] = SECOND_EMULATOR_CONNECTION_STRING,
        ["Whizbang:Transports:AzureServiceBus:Namespaces:bulk"] = "sb-bulk"
      })
      .Build();
    var services = new ServiceCollection();
    services.AddSingleton<IConfiguration>(configuration);

    services.AddAzureServiceBusTransport(
      new Dictionary<string, string> { [TransportNamespaces.DefaultKey] = EMULATOR_CONNECTION_STRING },
      o => o.AutoProvisionInfrastructure = false);

    await using var provider = _offline(services).BuildServiceProvider();
    var router = (NamespaceRoutingTransport)provider.GetRequiredService<ITransport>();

    await Assert.That(router.NamespaceKeys).IsEquivalentTo(new[] { TransportNamespaces.DefaultKey, "bulk" })
      .Because("an operator can add a traffic-class namespace without a code change");
  }

  #endregion

  #region Helpers

  /// <summary>
  /// Offline registration idiom for this suite: pre-register the ambient ServiceBusClient (the
  /// retry factory would block on a live handshake) and a namespace client factory that mints
  /// clients without connecting. Registered BEFORE resolution, so the TryAdd seams take these.
  /// </summary>
  private static IServiceCollection _offline(IServiceCollection services) {
    services.AddLogging();
    services.AddSingleton(new ServiceBusClient(EMULATOR_CONNECTION_STRING));
    services.AddSingleton<IServiceBusNamespaceClientFactory>(new OfflineNamespaceClientFactory());
    return services;
  }

  private sealed class OfflineNamespaceClientFactory : IServiceBusNamespaceClientFactory {
    public ServiceBusClient CreateClient(string namespaceKey, string connectionString, AzureServiceBusOptions options) =>
      new(connectionString);

    public IServiceBusAdminClient? CreateAdminClient(
      string namespaceKey, string connectionString, AzureServiceBusOptions options) => null;
  }

  #endregion
}
