#pragma warning disable CA1707 // Test method names can contain underscores

using System.Reflection;
using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Routing;
using Whizbang.Core.Transports;
using Whizbang.Core.Workers;

namespace Whizbang.Transports.AzureServiceBus.Tests;

/// <summary>
/// Unit tests for Azure Service Bus dependency injection extensions.
/// Complements the emulator-backed tests in Whizbang.Transports.AzureServiceBus.Integration.Tests
/// by covering registration branches, lifetimes, and factory lambdas that never touch the network:
/// admin client auto-registration, the SharedTopicOutboxStrategy inbox-topic branch,
/// AddAzureServiceBusProvisioner, and the failed-initialization rethrow path.
/// ServiceBusClient and ServiceBusAdministrationClient validate connection string format in their
/// constructors but connect lazily, so well-formed fake connection strings are safe here.
/// A localhost endpoint makes the transport take the emulator path in InitializeAsync, which
/// skips admin-API connectivity verification entirely.
/// </summary>
public class ServiceCollectionExtensionsTests {
  private const string FAKE_CONNECTION_STRING =
    "Endpoint=sb://fake.servicebus.windows.net/;SharedAccessKeyName=x;SharedAccessKey=Zm9v";
  private const string EMULATOR_CONNECTION_STRING =
    "Endpoint=sb://localhost;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true";

  // --- argument validation ---

  [Test]
  public async Task AddAzureServiceBusTransport_WithWhitespaceConnectionString_ThrowsWithParamNameAsync() {
    // Arrange
    var services = new ServiceCollection();

    // Act
    var ex = await Assert.ThrowsAsync<ArgumentException>(() => {
      services.AddAzureServiceBusTransport("   ");
      return Task.CompletedTask;
    });

    // Assert
    await Assert.That(ex!.ParamName).IsEqualTo("connectionString");
  }

  [Test]
  public async Task AddAzureServiceBusProvisioner_WithWhitespaceConnectionString_ThrowsWithParamNameAsync() {
    // Arrange
    var services = new ServiceCollection();

    // Act
    var ex = await Assert.ThrowsAsync<ArgumentException>(() => {
      services.AddAzureServiceBusProvisioner("   ");
      return Task.CompletedTask;
    });

    // Assert
    await Assert.That(ex!.ParamName).IsEqualTo("connectionString");
  }

  // --- registration shape: lifetimes asserted via ServiceDescriptor, no resolution ---

  [Test]
  public async Task AddAzureServiceBusTransport_RegistersCoreServices_AsSingletonsAsync() {
    // Arrange
    var services = new ServiceCollection();

    // Act
    var result = services.AddAzureServiceBusTransport(FAKE_CONNECTION_STRING);

    // Assert - chaining
    await Assert.That(ReferenceEquals(result, services)).IsTrue();

    // Assert - JsonSerializerOptions registered as a singleton instance from JsonContextRegistry
    var jsonDescriptor = services.Single(sd => sd.ServiceType == typeof(JsonSerializerOptions));
    await Assert.That(jsonDescriptor.Lifetime).IsEqualTo(ServiceLifetime.Singleton);
    await Assert.That(jsonDescriptor.ImplementationInstance).IsNotNull();

    // Assert - client + transport + readiness + publish strategy all singleton factories
    var clientDescriptor = services.Single(sd => sd.ServiceType == typeof(ServiceBusClient));
    await Assert.That(clientDescriptor.Lifetime).IsEqualTo(ServiceLifetime.Singleton);
    await Assert.That(clientDescriptor.ImplementationFactory).IsNotNull();

    var transportDescriptor = services.Single(sd => sd.ServiceType == typeof(ITransport));
    await Assert.That(transportDescriptor.Lifetime).IsEqualTo(ServiceLifetime.Singleton);
    await Assert.That(transportDescriptor.ImplementationFactory).IsNotNull();

    var readinessDescriptor = services.Single(sd => sd.ServiceType == typeof(ITransportReadinessCheck));
    await Assert.That(readinessDescriptor.Lifetime).IsEqualTo(ServiceLifetime.Singleton);
    await Assert.That(readinessDescriptor.ImplementationFactory).IsNotNull();

    var strategyDescriptor = services.Single(sd => sd.ServiceType == typeof(IMessagePublishStrategy));
    await Assert.That(strategyDescriptor.Lifetime).IsEqualTo(ServiceLifetime.Singleton);
    await Assert.That(strategyDescriptor.ImplementationFactory).IsNotNull();
  }

  [Test]
  public async Task AddAzureServiceBusTransport_WithExistingClient_DoesNotRegisterSecondClientAsync() {
    // Arrange
    var services = new ServiceCollection();
    var existingClient = new ServiceBusClient(EMULATOR_CONNECTION_STRING);
    services.AddSingleton(existingClient);

    // Act
    services.AddAzureServiceBusTransport(FAKE_CONNECTION_STRING);

    // Assert - the already-registered branch must skip the factory registration
    await Assert.That(services.Count(sd => sd.ServiceType == typeof(ServiceBusClient))).IsEqualTo(1);

    var provider = services.BuildServiceProvider();
    var resolvedClient = provider.GetRequiredService<ServiceBusClient>();
    await Assert.That(ReferenceEquals(resolvedClient, existingClient)).IsTrue();
  }

  // --- AutoProvisionInfrastructure admin client branches ---

  [Test]
  public async Task AddAzureServiceBusTransport_DefaultOptions_RegistersAdminClientAsSingletonAsync() {
    // Arrange
    var services = new ServiceCollection();

    // Act - AutoProvisionInfrastructure defaults to true
    services.AddAzureServiceBusTransport(FAKE_CONNECTION_STRING);

    // Assert
    var adminDescriptor = services.Single(sd => sd.ServiceType == typeof(IServiceBusAdminClient));
    await Assert.That(adminDescriptor.Lifetime).IsEqualTo(ServiceLifetime.Singleton);
    await Assert.That(adminDescriptor.ImplementationFactory).IsNotNull();
  }

  [Test]
  public async Task AddAzureServiceBusTransport_AutoProvisionDisabled_DoesNotRegisterAdminClientAsync() {
    // Arrange
    var services = new ServiceCollection();

    // Act
    services.AddAzureServiceBusTransport(
      FAKE_CONNECTION_STRING,
      options => options.AutoProvisionInfrastructure = false);

    // Assert
    await Assert.That(services.Count(sd => sd.ServiceType == typeof(IServiceBusAdminClient))).IsEqualTo(0);
  }

  [Test]
  public async Task AddAzureServiceBusTransport_WithExistingAdminClient_DoesNotRegisterDuplicateAsync() {
    // Arrange
    var services = new ServiceCollection();
    var existingAdminClient = new ServiceBusAdminClientWrapper(
      new ServiceBusAdministrationClient(FAKE_CONNECTION_STRING));
    services.AddSingleton<IServiceBusAdminClient>(existingAdminClient);

    // Act - AutoProvisionInfrastructure=true must respect the existing registration
    services.AddAzureServiceBusTransport(FAKE_CONNECTION_STRING);

    // Assert
    await Assert.That(services.Count(sd => sd.ServiceType == typeof(IServiceBusAdminClient))).IsEqualTo(1);

    var provider = services.BuildServiceProvider();
    var resolvedAdminClient = provider.GetRequiredService<IServiceBusAdminClient>();
    await Assert.That(ReferenceEquals(resolvedAdminClient, existingAdminClient)).IsTrue();
  }

  [Test]
  public async Task AddAzureServiceBusTransport_ResolvingAdminClient_ReturnsWrapperAsync() {
    // Arrange - ServiceBusAdministrationClient validates format in ctor but connects lazily
    var services = new ServiceCollection();
    services.AddAzureServiceBusTransport(FAKE_CONNECTION_STRING);
    var provider = services.BuildServiceProvider();

    // Act - exercises the admin client factory lambda
    var adminClient = provider.GetRequiredService<IServiceBusAdminClient>();

    // Assert
    await Assert.That(adminClient).IsTypeOf<ServiceBusAdminClientWrapper>();
  }

  // --- namespace client factory (IServiceBusNamespaceClientFactory) ---

  [Test]
  public async Task AddAzureServiceBusTransport_ResolvingNamespaceClientFactory_ReturnsTheDefaultSingletonAsync() {
    // Registering this factory is unconditional so single- and multi-namespace hosts share one
    // container shape, but the lambda itself is only PROVEN by resolving it: if the default
    // implementation cannot be constructed, a host adding its first traffic-class namespace
    // fails at that resolution instead of at today's single-client path.
    var services = new ServiceCollection();
    services.AddSingleton(new ServiceBusClient(EMULATOR_CONNECTION_STRING));
    services.AddLogging();
    services.AddAzureServiceBusTransport(EMULATOR_CONNECTION_STRING);
    var provider = services.BuildServiceProvider();

    // Act
    var factory1 = provider.GetRequiredService<IServiceBusNamespaceClientFactory>();
    var factory2 = provider.GetRequiredService<IServiceBusNamespaceClientFactory>();

    // Assert
    await Assert.That(factory1).IsTypeOf<ServiceBusNamespaceClientFactory>();
    await Assert.That(ReferenceEquals(factory1, factory2)).IsTrue()
      .Because("TryAddSingleton must hand every namespace's client-open call the SAME factory instance");
  }

  // --- options configuration callback ---

  [Test]
  public async Task AddAzureServiceBusTransport_OptionsCallback_ReceivesFreshDefaultsAsync() {
    // Arrange
    var services = new ServiceCollection();
    var callbackInvoked = false;
    AzureServiceBusOptions? captured = null;

    // Act
    services.AddAzureServiceBusTransport(FAKE_CONNECTION_STRING, options => {
      callbackInvoked = true;
      captured = options;
    });

    // Assert - callback runs during registration against default option values
    await Assert.That(callbackInvoked).IsTrue();
    await Assert.That(captured).IsNotNull();
    var capturedOptions = captured!;
    await Assert.That(capturedOptions.AutoProvisionInfrastructure).IsTrue();
    await Assert.That(capturedOptions.MaxConcurrentCalls).IsEqualTo(200);
    await Assert.That(capturedOptions.PublishMaxConcurrency).IsEqualTo(200);
  }

  // --- configuration binding: Whizbang:Transports:AzureServiceBus ---

  private static IConfiguration _configWith(params (string Key, string Value)[] pairs) {
    var section = "Whizbang:Transports:AzureServiceBus:";
    var data = pairs.ToDictionary(p => section + p.Key, p => (string?)p.Value);
    return new ConfigurationBuilder().AddInMemoryCollection(data).Build();
  }

  [Test]
  public async Task AddAzureServiceBusTransport_BindsEveryRuntimeKnobFromConfigurationAsync() {
    // Arrange — every configuration-bindable property set to a non-default value
    var services = new ServiceCollection();
    services.AddSingleton(_configWith(
      ("SendTimeout", "00:00:45"),
      ("MaxConcurrentCalls", "64"),
      ("PublishMaxConcurrency", "32"),
      ("MaxAutoLockRenewalDuration", "00:07:00"),
      ("SubscriptionLockDuration", "00:04:00"),
      ("MaxDeliveryAttempts", "7"),
      ("DefaultSubscriptionName", "ops-sub"),
      ("EnableSessions", "false"),
      ("MaxConcurrentSessions", "24"),
      ("SessionIdleTimeout", "00:00:42"),
      ("PrefetchCount", "11"),
      ("EnableReceiveLivenessWatchdog", "false"),
      ("ReceiveLivenessProbeInterval", "00:00:30"),
      ("ReceiveLivenessSilenceThreshold", "00:10:00"),
      ("InitialRetryAttempts", "3"),
      ("InitialRetryDelay", "00:00:02"),
      ("MaxRetryDelay", "00:01:00"),
      ("BackoffMultiplier", "3.5"),
      ("RetryIndefinitely", "false"),
      ("EnableOpsRateSelfCheck", "false"),
      ("OpsRateWarningThresholdPerSecond", "250.5"),
      ("EnableAdaptiveAcceptors", "false"),
      ("AcceptorFloor", "6"),
      ("AcceptorEvaluationInterval", "00:00:10")));

    // Act
    services.AddAzureServiceBusTransport(FAKE_CONNECTION_STRING);
    await using var provider = services.BuildServiceProvider();
    var options = provider.GetRequiredService<IOptions<AzureServiceBusOptions>>().Value;

    // Assert — operators can reach every runtime knob without a code deploy
    await Assert.That(options.SendTimeout).IsEqualTo(TimeSpan.FromSeconds(45));
    await Assert.That(options.MaxConcurrentCalls).IsEqualTo(64);
    await Assert.That(options.PublishMaxConcurrency).IsEqualTo(32);
    await Assert.That(options.MaxAutoLockRenewalDuration).IsEqualTo(TimeSpan.FromMinutes(7));
    await Assert.That(options.SubscriptionLockDuration).IsEqualTo(TimeSpan.FromMinutes(4));
    await Assert.That(options.MaxDeliveryAttempts).IsEqualTo(7);
    await Assert.That(options.DefaultSubscriptionName).IsEqualTo("ops-sub");
    await Assert.That(options.EnableSessions).IsFalse();
    await Assert.That(options.MaxConcurrentSessions).IsEqualTo(24);
    await Assert.That(options.SessionIdleTimeout).IsEqualTo(TimeSpan.FromSeconds(42));
    await Assert.That(options.PrefetchCount).IsEqualTo(11);
    await Assert.That(options.EnableReceiveLivenessWatchdog).IsFalse();
    await Assert.That(options.ReceiveLivenessProbeInterval).IsEqualTo(TimeSpan.FromSeconds(30));
    await Assert.That(options.ReceiveLivenessSilenceThreshold).IsEqualTo(TimeSpan.FromMinutes(10));
    await Assert.That(options.InitialRetryAttempts).IsEqualTo(3);
    await Assert.That(options.InitialRetryDelay).IsEqualTo(TimeSpan.FromSeconds(2));
    await Assert.That(options.MaxRetryDelay).IsEqualTo(TimeSpan.FromMinutes(1));
    await Assert.That(options.BackoffMultiplier).IsEqualTo(3.5);
    await Assert.That(options.RetryIndefinitely).IsFalse();
    await Assert.That(options.EnableOpsRateSelfCheck).IsFalse();
    await Assert.That(options.OpsRateWarningThresholdPerSecond).IsEqualTo(250.5);
    await Assert.That(options.EnableAdaptiveAcceptors).IsFalse();
    await Assert.That(options.AcceptorFloor).IsEqualTo(6);
    await Assert.That(options.AcceptorEvaluationInterval).IsEqualTo(TimeSpan.FromSeconds(10));
  }

  [Test]
  public async Task AddAzureServiceBusTransport_ConfigurationOverridesCodeCallbackAsync() {
    // Arrange — code callback sets one value, configuration sets another for the same knob
    var services = new ServiceCollection();
    services.AddSingleton(_configWith(("MaxConcurrentSessions", "64")));

    // Act
    services.AddAzureServiceBusTransport(FAKE_CONNECTION_STRING, o => {
      o.MaxConcurrentSessions = 24;
      o.SessionIdleTimeout = TimeSpan.FromSeconds(5);
    });
    await using var provider = services.BuildServiceProvider();
    var options = provider.GetRequiredService<IOptions<AzureServiceBusOptions>>().Value;

    // Assert — configuration wins where set (an operator can correct a baked-in value without
    // a redeploy); the code callback survives where configuration is silent
    await Assert.That(options.MaxConcurrentSessions).IsEqualTo(64)
      .Because("a deploy-time configuration override must beat the compiled-in callback value");
    await Assert.That(options.SessionIdleTimeout).IsEqualTo(TimeSpan.FromSeconds(5))
      .Because("callback values stand wherever configuration does not speak");
  }

  [Test]
  public async Task AddAzureServiceBusTransport_NoConfigurationRegistered_CallbackAndDefaultsApplyAsync() {
    // Arrange — no IConfiguration in the container at all
    var services = new ServiceCollection();

    // Act
    services.AddAzureServiceBusTransport(FAKE_CONNECTION_STRING, o => o.PrefetchCount = 17);
    await using var provider = services.BuildServiceProvider();
    var options = provider.GetRequiredService<IOptions<AzureServiceBusOptions>>().Value;

    // Assert
    await Assert.That(options.PrefetchCount).IsEqualTo(17);
    await Assert.That(options.MaxConcurrentSessions).IsEqualTo(200);
  }

  [Test]
  public async Task AddAzureServiceBusTransport_RegistersTheOpsRateHealthSourceAsync() {
    // Arrange — a raisable client so resolving the transport never touches the network, and
    // AutoProvisionInfrastructure=false so InitializeAsync skips admin-API verification.
    var services = new ServiceCollection();
    services.AddSingleton<ServiceBusClient>(new RaisableServiceBusClient());

    // Act
    services.AddAzureServiceBusTransport(FAKE_CONNECTION_STRING, o => o.AutoProvisionInfrastructure = false);
    await using var provider = services.BuildServiceProvider();
    var sources = provider.GetServices<Whizbang.Core.Health.IWhizbangHealthSource>().ToList();

    // Assert — the transport package contributes the ops-rate source to the managed-health
    // aggregation so the idle-churn projection can DEGRADE the transport component.
    var opsRateSource = sources.OfType<AsbOpsRateHealthSource>().SingleOrDefault();
    await Assert.That(opsRateSource is not null).IsTrue()
      .Because("registering the transport must also register the health source that closes the log-only Phase-1 delta");
    await Assert.That(opsRateSource!.Component).IsEqualTo("transport");
  }

  [Test]
  public async Task AddAzureServiceBusTransport_AutoProvisionInfrastructure_IsCodeOnlyAsync() {
    // Arrange — AutoProvisionInfrastructure shapes DI at registration time (whether the admin
    // client is registered), so it is deliberately NOT configuration-bindable: a config value
    // could not re-shape a container that is already built.
    var services = new ServiceCollection();
    services.AddSingleton(_configWith(("AutoProvisionInfrastructure", "false")));

    // Act
    services.AddAzureServiceBusTransport(FAKE_CONNECTION_STRING);
    await using var provider = services.BuildServiceProvider();
    var options = provider.GetRequiredService<IOptions<AzureServiceBusOptions>>().Value;

    // Assert — the code default stands and the admin client stays registered
    await Assert.That(options.AutoProvisionInfrastructure).IsTrue()
      .Because("AutoProvisionInfrastructure is a registration-time DI-shape decision, not a runtime knob");
    await Assert.That(services.Count(sd => sd.ServiceType == typeof(IServiceBusAdminClient))).IsEqualTo(1);
  }

  // --- per-namespace observability sources (backlog peek / ops-rate gauge) ---

  [Test]
  public async Task AddAzureServiceBusTransport_RegistersBacklogPeekOverTheResolvedTransportAsSingletonAsync() {
    // If IBacklogPeek fails to resolve, or resolves to a fresh instance on every duty tick, the
    // backlog-age sampler either never starts or silently loses the transport reference it
    // samples through — the exact "invisible while healthy" failure mode this peek exists to
    // close would stay invisible, just one layer further down.
    var services = new ServiceCollection();
    services.AddSingleton(new ServiceBusClient(EMULATOR_CONNECTION_STRING));
    services.AddLogging();
    services.AddAzureServiceBusTransport(EMULATOR_CONNECTION_STRING);
    var provider = services.BuildServiceProvider();

    // Act
    var peek1 = provider.GetRequiredService<IBacklogPeek>();
    var peek2 = provider.GetRequiredService<IBacklogPeek>();

    // Assert
    await Assert.That(peek1).IsTypeOf<AsbBacklogPeek>();
    await Assert.That(ReferenceEquals(peek1, peek2)).IsTrue()
      .Because("a non-singleton peek would silently disconnect from whatever the duty accumulated between ticks");
    await Assert.That(peek1.TransportName).IsEqualTo("asb");

    // Usable end to end, not just constructible: a transport with nothing subscribed yet
    // reports zero samples rather than throwing.
    var samples = await peek1.PeekAsync(CancellationToken.None);
    await Assert.That(samples.Count).IsEqualTo(0);
  }

  [Test]
  public async Task AddAzureServiceBusTransport_RegistersTrafficClassOpsRateSourceOverTheResolvedTransportAsSingletonAsync() {
    // If this source fails to resolve, the per-namespace ops-rate gauge goes dark on startup —
    // an operator loses the graph the self-check's degrade decision is based on, with no error
    // to point at because nothing downstream ever calls it.
    var services = new ServiceCollection();
    services.AddSingleton(new ServiceBusClient(EMULATOR_CONNECTION_STRING));
    services.AddLogging();
    services.AddAzureServiceBusTransport(EMULATOR_CONNECTION_STRING);
    var provider = services.BuildServiceProvider();

    // Act
    var source1 = provider.GetRequiredService<ITrafficClassOpsRateSource>();
    var source2 = provider.GetRequiredService<ITrafficClassOpsRateSource>();

    // Assert
    await Assert.That(source1).IsTypeOf<AsbTrafficClassOpsRateSource>();
    await Assert.That(ReferenceEquals(source1, source2)).IsTrue()
      .Because("a non-singleton source would silently disconnect the gauge from the self-check's live projection");
    await Assert.That(source1.TransportName).IsEqualTo("asb");

    // Usable end to end: no self-check tick has run yet, so there is nothing to project.
    var rates = source1.Project();
    await Assert.That(rates.Count).IsEqualTo(0);
  }

  // --- transport factory: offline initialization paths ---

  [Test]
  public async Task AddAzureServiceBusTransport_WithLocalhostClient_InitializesTransportWithoutAdminVerificationAsync() {
    // Arrange - localhost endpoint triggers the emulator path in InitializeAsync (no admin call)
    var services = new ServiceCollection();
    services.AddSingleton(new ServiceBusClient(EMULATOR_CONNECTION_STRING));
    services.AddLogging();
    services.AddAzureServiceBusTransport(EMULATOR_CONNECTION_STRING);
    var provider = services.BuildServiceProvider();

    // Act
    var transport1 = provider.GetRequiredService<ITransport>();
    var transport2 = provider.GetRequiredService<ITransport>();

    // Assert
    await Assert.That(transport1).IsTypeOf<AzureServiceBusTransport>();
    await Assert.That(transport1.IsInitialized).IsTrue();
    await Assert.That(ReferenceEquals(transport1, transport2)).IsTrue();
  }

  [Test]
  public async Task AddAzureServiceBusTransport_NoAdminClient_InitializesTransportWithoutConnectivityCheckAsync() {
    // Arrange - non-localhost endpoint + AutoProvision=false takes the "no admin client" branch
    var services = new ServiceCollection();
    services.AddSingleton(new ServiceBusClient(FAKE_CONNECTION_STRING));
    services.AddLogging();
    services.AddAzureServiceBusTransport(
      FAKE_CONNECTION_STRING,
      options => options.AutoProvisionInfrastructure = false);
    var provider = services.BuildServiceProvider();

    // Act
    var transport = provider.GetRequiredService<ITransport>();

    // Assert
    await Assert.That(transport).IsTypeOf<AzureServiceBusTransport>();
    await Assert.That(transport.IsInitialized).IsTrue();
  }

  [Test]
  public async Task AddAzureServiceBusTransport_WithClosedClient_ResolvingTransportRethrowsInitializationFailureAsync() {
    // Arrange - a disposed client makes InitializeAsync throw, exercising the log-and-rethrow path
    var services = new ServiceCollection();
    var closedClient = new ServiceBusClient(EMULATOR_CONNECTION_STRING);
    await closedClient.DisposeAsync();
    services.AddSingleton(closedClient);
    services.AddLogging();
    services.AddAzureServiceBusTransport(EMULATOR_CONNECTION_STRING);
    var provider = services.BuildServiceProvider();

    // Act
    var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => {
      _ = provider.GetRequiredService<ITransport>();
      return Task.CompletedTask;
    });

    // Assert - the factory wraps initialization failures in its own diagnostic message
    await Assert.That(ex!.Message).Contains("Failed to initialize Azure Service Bus transport");
  }

  // --- IMessagePublishStrategy inbox-topic branch selection ---

  [Test]
  public async Task AddAzureServiceBusTransport_WithSharedTopicOutboxStrategy_UsesConfiguredInboxTopicAsync() {
    // Arrange
    var services = new ServiceCollection();
    services.AddSingleton(new ServiceBusClient(EMULATOR_CONNECTION_STRING));
    services.AddLogging();
    services.AddSingleton<IOutboxRoutingStrategy>(
      new SharedTopicOutboxStrategy("custom-inbox", PassthroughRoutingStrategy.Instance));
    services.AddAzureServiceBusTransport(EMULATOR_CONNECTION_STRING);
    var provider = services.BuildServiceProvider();

    // Act
    var strategy = provider.GetRequiredService<IMessagePublishStrategy>();

    // Assert - the SharedTopicOutboxStrategy branch must propagate the configured inbox topic
    await Assert.That(strategy).IsTypeOf<TransportPublishStrategy>();
    await Assert.That(_getInboxTopic(strategy)).IsEqualTo("custom-inbox");
  }

  [Test]
  public async Task AddAzureServiceBusTransport_WithNamespaceOutboxStrategy_WiresPublishTimeFlipSeamAsync() {
    // Phase 6: the DI factory must recognize NamespaceOutboxStrategy, propagate its shared
    // inbox topic, AND hand the strategy itself to TransportPublishStrategy so the
    // publish-time command resolution consults the flip set.
    var services = new ServiceCollection();
    services.AddSingleton(new ServiceBusClient(EMULATOR_CONNECTION_STRING));
    services.AddLogging();
    var namespaceStrategy = new NamespaceOutboxStrategy(
      new Whizbang.Core.Routing.RoutingOptions(), "custom-inbox");
    services.AddSingleton<IOutboxRoutingStrategy>(namespaceStrategy);
    services.AddAzureServiceBusTransport(EMULATOR_CONNECTION_STRING);
    var provider = services.BuildServiceProvider();

    var strategy = provider.GetRequiredService<IMessagePublishStrategy>();

    await Assert.That(strategy).IsTypeOf<TransportPublishStrategy>();
    await Assert.That(_getInboxTopic(strategy)).IsEqualTo("custom-inbox");
    await Assert.That(_getNamespaceRouting(strategy)).IsSameReferenceAs(namespaceStrategy)
      .Because("without the seam, flips would silently never reach the wire");
  }

  [Test]
  public async Task AddAzureServiceBusTransport_WithSharedTopicOutboxStrategy_ResolverSeamWiredAndNeverFlipsAsync() {
    // Phase 7 seam unification: the DI factory consumes ICommandInboxAddressResolver — no
    // concrete-strategy type tests. The shared-topic strategy rides the SAME wiring as the
    // namespace strategy; byte-identical behavior holds because its resolver never flips.
    var services = new ServiceCollection();
    services.AddSingleton(new ServiceBusClient(EMULATOR_CONNECTION_STRING));
    services.AddLogging();
    var sharedStrategy = new SharedTopicOutboxStrategy("custom-inbox", PassthroughRoutingStrategy.Instance);
    services.AddSingleton<IOutboxRoutingStrategy>(sharedStrategy);
    services.AddAzureServiceBusTransport(EMULATOR_CONNECTION_STRING);
    var provider = services.BuildServiceProvider();

    var strategy = provider.GetRequiredService<IMessagePublishStrategy>();

    var seam = _getNamespaceRouting(strategy);
    await Assert.That(seam).IsSameReferenceAs(sharedStrategy)
      .Because("one interface seam serves every command-routing strategy — no type tests in the factory");
    await Assert.That(seam!.ResolveFlippedCommandInboxAddress("myapp.orders.commands")).IsNull()
      .Because("the shared strategy never flips, so the wiring stays byte-identical to phase 6");
  }

  [Test]
  public async Task AddAzureServiceBusTransport_WithNonSharedTopicOutboxStrategy_FallsBackToDefaultInboxTopicAsync() {
    // Arrange - a registered strategy OUTSIDE the command-inbox seam falls back to defaults
    var services = new ServiceCollection();
    services.AddSingleton(new ServiceBusClient(EMULATOR_CONNECTION_STRING));
    services.AddLogging();
    services.AddSingleton<IOutboxRoutingStrategy>(
      new DomainTopicOutboxStrategy(PassthroughRoutingStrategy.Instance));
    services.AddAzureServiceBusTransport(EMULATOR_CONNECTION_STRING);
    var provider = services.BuildServiceProvider();

    // Act
    var strategy = provider.GetRequiredService<IMessagePublishStrategy>();

    // Assert
    await Assert.That(strategy).IsTypeOf<TransportPublishStrategy>();
    await Assert.That(_getInboxTopic(strategy)).IsEqualTo(SharedTopicOutboxStrategy.DefaultInboxTopic);
    await Assert.That(_getNamespaceRouting(strategy)).IsNull()
      .Because("a strategy outside the seam wires no flip hook — commands ride the default inbox topic");
  }

  // --- multi-namespace composition: peer-namespace init logging + active-consume projection ---

  [Test]
  public async Task AddAzureServiceBusTransport_MultiNamespaceMap_LogsEachPeerNamespaceInitializationAsync() {
    // If this log line regressed or silently stopped firing, an operator watching a
    // multi-namespace rollout come up would have no boot-time confirmation that a traffic-class
    // namespace's client actually initialized — the first sign of trouble would be a stuck
    // consumer on that namespace, not a log line at startup.
    var services = new ServiceCollection();
    var defaultClient = new RaisableServiceBusClient();
    services.AddSingleton<ServiceBusClient>(defaultClient);
    var recordingLogger = new RecordingTransportLogger();
    services.AddSingleton<ILogger<AzureServiceBusTransport>>(recordingLogger);
    services.AddSingleton<IServiceBusNamespaceClientFactory>(new RaisableNamespaceClientFactory());

    services.AddAzureServiceBusTransport(
      new Dictionary<string, string> {
        [TransportNamespaces.DefaultKey] = FAKE_CONNECTION_STRING,
        ["bulk"] = FAKE_CONNECTION_STRING
      },
      o => o.AutoProvisionInfrastructure = false);
    var provider = services.BuildServiceProvider();

    // Act
    var transport = provider.GetRequiredService<ITransport>();

    // Assert
    await Assert.That(transport).IsTypeOf<NamespaceRoutingTransport>();
    await Assert.That(recordingLogger.Contains(LogLevel.Information, "Transport initialized for TransportNamespace 'bulk'"))
      .IsTrue()
      .Because("this is the only boot-time signal an operator has that the 'bulk' traffic-class connection came up healthy");
  }

  [Test]
  public async Task AddAzureServiceBusTransport_MultiNamespaceMap_SubscribeProjectsZeroMirrorsWithNothingHandledAsync() {
    // The consume-side mirror is supposed to subscribe a namespace ONLY when this service
    // actively handles a type routed there — a namespace it merely publishes to must cost zero
    // broker entities. If the active-namespace projection broke (threw, or mirrored
    // unconditionally), a class this service never consumes would either crash every subscribe
    // call or pick up a subscription — and an idle acceptor slot — it should never have opened.
    var services = new ServiceCollection();
    var defaultClient = new RaisableServiceBusClient();
    services.AddSingleton<ServiceBusClient>(defaultClient);
    var clientFactory = new RaisableNamespaceClientFactory();
    services.AddSingleton<IServiceBusNamespaceClientFactory>(clientFactory);

    // A routing binding exists (HasBindings = true) but nothing is reported as handled — the
    // projection must degrade to "nothing active" rather than mirror unconditionally.
    var tagOptions = new Whizbang.Core.Tags.TagOptions();
    tagOptions.RouteNamespace("stub-tag", "bulk");
    services.AddSingleton(new Whizbang.Core.Tags.TransportNamespaceResolver(tagOptions));

    services.AddAzureServiceBusTransport(
      new Dictionary<string, string> {
        [TransportNamespaces.DefaultKey] = FAKE_CONNECTION_STRING,
        ["bulk"] = FAKE_CONNECTION_STRING
      },
      o => o.AutoProvisionInfrastructure = false);
    var provider = services.BuildServiceProvider();
    var transport = provider.GetRequiredService<ITransport>();
    var peerClient = clientFactory.CreatedClients.Single();

    // Act - subscribing on the composed router must invoke the active-namespace projection
    var subscription = await transport.SubscribeBatchAsync(
      (_, _) => Task.CompletedTask,
      new TransportDestination("stub-topic"),
      new TransportBatchOptions());

    // Assert - the default subscription is opened, but the 'bulk' peer never sees one
    await Assert.That(subscription).IsNotNull();
    await Assert.That(peerClient.CreatedProcessors.Count).IsEqualTo(0)
      .Because("nothing resolves to 'bulk' as actively handled, so the mirror must stay off");
    await Assert.That(peerClient.LastSessionProcessor).IsNull()
      .Because("neither the session nor the non-session subscribe path may reach an inactive namespace");
  }

  [Test]
  public async Task AddAzureServiceBusTransport_MultiNamespaceMap_NoRoutingBindings_SkipsTheProjectionEntirelyAsync() {
    // The sibling test above configures a binding and finds nothing handled. This is the case
    // before any routing is configured at all -- the overwhelmingly common one, since a service
    // that names a second namespace for publishing only never routes a tag to it. The projection
    // has to short-circuit on "no bindings" rather than ask the receptor registry what it handles:
    // that query runs on every subscribe call, and answering it to reach a conclusion that the
    // absent bindings already determined is work every such service would pay for forever.
    var services = new ServiceCollection();
    services.AddSingleton<ServiceBusClient>(new RaisableServiceBusClient());
    var clientFactory = new RaisableNamespaceClientFactory();
    services.AddSingleton<IServiceBusNamespaceClientFactory>(clientFactory);

    // A resolver with no RouteNamespace call at all -- HasBindings is false.
    services.AddSingleton(new Whizbang.Core.Tags.TransportNamespaceResolver(
      new Whizbang.Core.Tags.TagOptions()));

    services.AddAzureServiceBusTransport(
      new Dictionary<string, string> {
        [TransportNamespaces.DefaultKey] = FAKE_CONNECTION_STRING,
        ["bulk"] = FAKE_CONNECTION_STRING
      },
      o => o.AutoProvisionInfrastructure = false);
    var provider = services.BuildServiceProvider();
    var transport = provider.GetRequiredService<ITransport>();
    var peerClient = clientFactory.CreatedClients.Single();

    var subscription = await transport.SubscribeBatchAsync(
      (_, _) => Task.CompletedTask,
      new TransportDestination("stub-topic"),
      new TransportBatchOptions());

    await Assert.That(subscription).IsNotNull()
      .Because("an unrouted second namespace must not stop the default subscription from opening");
    await Assert.That(peerClient.CreatedProcessors.Count).IsEqualTo(0)
      .Because("with nothing routed to 'bulk', mirroring it would open a subscription and hold an "
             + "acceptor slot for traffic that can never arrive");
  }

  // --- AddAzureServiceBusProvisioner ---

  [Test]
  public async Task AddAzureServiceBusProvisioner_RegistersAdminClientAndProvisioner_AsSingletonsAsync() {
    // Arrange
    var services = new ServiceCollection();
    services.AddLogging();

    // Act
    var result = services.AddAzureServiceBusProvisioner(FAKE_CONNECTION_STRING);

    // Assert - chaining
    await Assert.That(ReferenceEquals(result, services)).IsTrue();

    // Assert - descriptor lifetimes
    var adminDescriptor = services.Single(sd => sd.ServiceType == typeof(IServiceBusAdminClient));
    await Assert.That(adminDescriptor.Lifetime).IsEqualTo(ServiceLifetime.Singleton);

    var provisionerDescriptor = services.Single(sd => sd.ServiceType == typeof(IInfrastructureProvisioner));
    await Assert.That(provisionerDescriptor.Lifetime).IsEqualTo(ServiceLifetime.Singleton);

    // Assert - both factory lambdas produce the expected implementations without connecting
    var provider = services.BuildServiceProvider();
    var adminClient = provider.GetRequiredService<IServiceBusAdminClient>();
    await Assert.That(adminClient).IsTypeOf<ServiceBusAdminClientWrapper>();

    var provisioner = provider.GetRequiredService<IInfrastructureProvisioner>();
    await Assert.That(provisioner).IsTypeOf<ServiceBusInfrastructureProvisioner>();
  }

  [Test]
  public async Task AddAzureServiceBusProvisioner_ConfigurationOnlyNamespace_ComposesAnExtraProvisionerAsync() {
    // An operator can add a traffic-class namespace purely through configuration (no code
    // redeploy) — the same seam AddAzureServiceBusTransport honors. If the provisioner factory
    // stopped merging configuration OVER the code map, that namespace's entities would never
    // get provisioned even though the transport happily opens a client for it.
    var configuration = new ConfigurationBuilder()
      .AddInMemoryCollection(new Dictionary<string, string?> {
        ["Whizbang:Transports:AzureServiceBus:Namespaces:bulk"] = FAKE_CONNECTION_STRING
      })
      .Build();
    var services = new ServiceCollection();
    services.AddSingleton<IConfiguration>(configuration);
    services.AddLogging();

    // Act - single-connection-string overload: the code map carries only 'default'
    services.AddAzureServiceBusProvisioner(FAKE_CONNECTION_STRING);
    var provider = services.BuildServiceProvider();
    var provisioner = provider.GetRequiredService<IInfrastructureProvisioner>();

    // Assert - the configuration-only 'bulk' entry must still compose into the provisioner
    await Assert.That(provisioner).IsTypeOf<CompositeInfrastructureProvisioner>()
      .Because("a namespace added purely via configuration must get its own provisioner composed in, not dropped");
  }

  // --- AddAzureServiceBusHealthChecks ---

  [Test]
  public async Task AddAzureServiceBusHealthChecks_RegistersNamedCheck_AndFactoryCreatesHealthCheckAsync() {
    // Arrange
    var services = new ServiceCollection();
    services.AddSingleton(new ServiceBusClient(EMULATOR_CONNECTION_STRING));
    services.AddLogging();
    services.AddAzureServiceBusTransport(EMULATOR_CONNECTION_STRING);

    // Act
    var result = services.AddAzureServiceBusHealthChecks();
    var provider = services.BuildServiceProvider();
    var healthOptions = provider.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value;

    // Assert - chaining
    await Assert.That(ReferenceEquals(result, services)).IsTrue();

    // Assert - the named registration exists and its factory builds the ASB health check
    var registration = healthOptions.Registrations.Single(r => r.Name == "azure_servicebus");
    var healthCheck = registration.Factory(provider);
    await Assert.That(healthCheck).IsTypeOf<AzureServiceBusHealthCheck>();
  }

  // --- helpers ---

  /// <summary>
  /// Reads the private inbox topic captured by TransportPublishStrategy so tests can assert
  /// which branch of the IMessagePublishStrategy factory selected the topic.
  /// </summary>
  private static string? _getInboxTopic(IMessagePublishStrategy strategy) {
    var field = typeof(TransportPublishStrategy).GetField(
      "_inboxTopic",
      BindingFlags.NonPublic | BindingFlags.Instance)
      ?? throw new InvalidOperationException(
        "_inboxTopic field not found on TransportPublishStrategy - was it renamed?");

    return (string?)field.GetValue(strategy);
  }

  /// <summary>
  /// Reads the private publish-time flip seam captured by TransportPublishStrategy so tests
  /// can assert the DI factory handed the ICommandInboxAddressResolver seam through.
  /// </summary>
  private static Whizbang.Core.Routing.ICommandInboxAddressResolver? _getNamespaceRouting(
      IMessagePublishStrategy strategy) {
    var field = typeof(TransportPublishStrategy).GetField(
      "_namespaceRouting",
      BindingFlags.NonPublic | BindingFlags.Instance)
      ?? throw new InvalidOperationException(
        "_namespaceRouting field not found on TransportPublishStrategy - was it renamed?");

    return (Whizbang.Core.Routing.ICommandInboxAddressResolver?)field.GetValue(strategy);
  }

  /// <summary>
  /// IServiceBusNamespaceClientFactory stand-in that mints a RaisableServiceBusClient (never a
  /// real connection) per TransportNamespace it is asked to open — the offline registration
  /// idiom this file's multi-namespace tests use to compose a router without a broker.
  /// </summary>
  private sealed class RaisableNamespaceClientFactory : IServiceBusNamespaceClientFactory {
    public List<RaisableServiceBusClient> CreatedClients { get; } = [];

    public ServiceBusClient CreateClient(string namespaceKey, string connectionString, AzureServiceBusOptions options) {
      var client = new RaisableServiceBusClient($"{namespaceKey}.unit-test.servicebus.windows.net");
      CreatedClients.Add(client);
      return client;
    }

    public IServiceBusAdminClient? CreateAdminClient(
      string namespaceKey, string connectionString, AzureServiceBusOptions options) => null;
  }
}
