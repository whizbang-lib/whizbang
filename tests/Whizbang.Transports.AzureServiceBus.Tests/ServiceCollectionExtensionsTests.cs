#pragma warning disable CA1707 // Test method names can contain underscores

using System.Reflection;
using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
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
      ("RetryIndefinitely", "false")));

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
  public async Task AddAzureServiceBusTransport_WithNonSharedTopicOutboxStrategy_FallsBackToDefaultInboxTopicAsync() {
    // Arrange - a registered strategy that is NOT SharedTopicOutboxStrategy must not match the branch
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
}
