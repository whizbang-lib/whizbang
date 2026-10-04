using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Resilience;
using Whizbang.Core.Routing;
using Whizbang.Core.Transports;
using Whizbang.Core.Workers;

#pragma warning disable CA1707 // Test method names use underscores by convention

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// The transport consumer's options bind from the section of the transport it consumes,
/// <c>Whizbang:Transports:&lt;transport&gt;:{MessageProcessing,Batch,SubscriptionResilience,Consumer}</c>,
/// over the values set in code. A transport's section counts only when that transport is
/// registered; with none registered every option keeps its code value.
/// </summary>
public class TransportConsumerOptionsConfigurationTests {
  private const string ASB = "Whizbang:Transports:AzureServiceBus";
  private const string RABBIT = "Whizbang:Transports:RabbitMQ";

  private static IConfiguration _config(params (string Key, string Value)[] values) =>
    new ConfigurationBuilder()
      .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
      .Build();

  private static ServiceCollection _services(IConfiguration configuration, params string[] transports) {
    var services = new ServiceCollection();
    services.AddSingleton(configuration);
    services.TryAddWhizbangDefaults();
    services.AddLogging();
    services.AddSingleton<IServiceInstanceProvider>(new TestServiceInstanceProvider("TestService"));
    foreach (var transport in transports) {
      TransportConfigurationSection.Register(services, transport);
    }
    return services;
  }

  private static WhizbangBuilder _builder(ServiceCollection services) {
    var builder = new WhizbangBuilder(services);
    builder.WithRouting(routing => routing.OwnDomains("myapp.orders.commands").Inbox.UseSharedTopic("inbox"));
    return builder;
  }

  [Test]
  public async Task RegisteredTransport_BindsEachConsumerSection_AndIgnoresOtherTransportsAsync() {
    var services = _services(_config(
      ($"{ASB}:MessageProcessing:MaxConcurrentMessages", "12"),
      ($"{ASB}:MessageProcessing:InboxBatchSize", "250"),
      ($"{ASB}:MessageProcessing:InboxBatchSlideMs", "75"),
      ($"{ASB}:MessageProcessing:InboxBatchMaxWaitMs", "2000"),
      ($"{ASB}:Batch:BatchSize", "64"),
      ($"{ASB}:Batch:SlideMs", "30"),
      ($"{ASB}:Batch:MaxWaitMs", "500"),
      ($"{ASB}:SubscriptionResilience:InitialRetryAttempts", "2"),
      ($"{ASB}:SubscriptionResilience:InitialRetryDelay", "00:00:03"),
      ($"{ASB}:SubscriptionResilience:MaxRetryDelay", "00:05:00"),
      ($"{ASB}:SubscriptionResilience:BackoffMultiplier", "1.5"),
      ($"{ASB}:SubscriptionResilience:RetryIndefinitely", "false"),
      ($"{ASB}:SubscriptionResilience:HealthCheckInterval", "00:00:20"),
      ($"{ASB}:SubscriptionResilience:AllowPartialSubscriptions", "false"),
      ($"{RABBIT}:MessageProcessing:MaxConcurrentMessages", "99"),
      ($"{RABBIT}:Batch:BatchSize", "99")), "AzureServiceBus");
    _builder(services).AddTransportConsumer();

    await using var provider = services.BuildServiceProvider();
    var processing = provider.GetRequiredService<MessageProcessingOptions>();
    var batch = provider.GetRequiredService<TransportBatchOptions>();
    var resilience = provider.GetRequiredService<SubscriptionResilienceOptions>();

    await Assert.That(processing.MaxConcurrentMessages).IsEqualTo(12);
    await Assert.That(processing.InboxBatchSize).IsEqualTo(250);
    await Assert.That(processing.InboxBatchSlideMs).IsEqualTo(75);
    await Assert.That(processing.InboxBatchMaxWaitMs).IsEqualTo(2000);
    await Assert.That(batch.BatchSize).IsEqualTo(64);
    await Assert.That(batch.SlideMs).IsEqualTo(30);
    await Assert.That(batch.MaxWaitMs).IsEqualTo(500);
    await Assert.That(resilience.InitialRetryAttempts).IsEqualTo(2);
    await Assert.That(resilience.InitialRetryDelay).IsEqualTo(TimeSpan.FromSeconds(3));
    await Assert.That(resilience.MaxRetryDelay).IsEqualTo(TimeSpan.FromMinutes(5));
    await Assert.That(resilience.BackoffMultiplier).IsEqualTo(1.5);
    await Assert.That(resilience.RetryIndefinitely).IsFalse();
    await Assert.That(resilience.HealthCheckInterval).IsEqualTo(TimeSpan.FromSeconds(20));
    await Assert.That(resilience.AllowPartialSubscriptions).IsFalse();
  }

  [Test]
  public async Task NoRegisteredTransport_KeepsEveryDefaultAsync() {
    var services = _services(_config(
      ($"{ASB}:MessageProcessing:MaxConcurrentMessages", "12"),
      ($"{ASB}:Batch:BatchSize", "64"),
      ($"{ASB}:SubscriptionResilience:InitialRetryAttempts", "2"),
      ($"{ASB}:Consumer:AdditionalDestinations:0:Address", "extra")));
    _builder(services).AddTransportConsumer();

    await using var provider = services.BuildServiceProvider();

    await Assert.That(provider.GetRequiredService<MessageProcessingOptions>().MaxConcurrentMessages).IsEqualTo(40);
    await Assert.That(provider.GetRequiredService<TransportBatchOptions>().BatchSize).IsEqualTo(200);
    await Assert.That(provider.GetRequiredService<SubscriptionResilienceOptions>().InitialRetryAttempts).IsEqualTo(5);
    await Assert.That(provider.GetRequiredService<TransportConsumerOptions>().Destinations.Any(d => d.Address == "extra")).IsFalse();
  }

  [Test]
  public async Task EachTransport_ReadsOnlyItsOwnSectionAsync() {
    var services = _services(_config(
      ($"{ASB}:Batch:BatchSize", "64"),
      ($"{RABBIT}:Batch:SlideMs", "45")), "RabbitMQ");
    _builder(services).AddTransportConsumer();

    await using var provider = services.BuildServiceProvider();
    var batch = provider.GetRequiredService<TransportBatchOptions>();

    await Assert.That(batch.SlideMs).IsEqualTo(45);
    await Assert.That(batch.BatchSize).IsEqualTo(200).Because("the Service Bus section belongs to a transport this host does not run");
  }

  [Test]
  public async Task InstanceRegisteredInCode_KeepsItsValues_ConfigurationOverridesPerKeyAsync() {
    var services = _services(_config(($"{ASB}:MessageProcessing:InboxBatchSize", "9")), "AzureServiceBus");
    var codeSet = new MessageProcessingOptions { MaxConcurrentMessages = 7, InboxBatchSize = 5 };
    services.AddSingleton(codeSet);
    _builder(services).AddTransportConsumer();

    await using var provider = services.BuildServiceProvider();
    var processing = provider.GetRequiredService<MessageProcessingOptions>();

    await Assert.That(processing).IsSameReferenceAs(codeSet);
    await Assert.That(processing.MaxConcurrentMessages).IsEqualTo(7);
    await Assert.That(processing.InboxBatchSize).IsEqualTo(9);
  }

  [Test]
  public async Task FactoryRegisteredInCode_IsWrapped_ConfigurationOverridesPerKeyAsync() {
    var services = _services(_config(($"{ASB}:Batch:MaxWaitMs", "300")), "AzureServiceBus");
    services.AddScoped(_ => new TransportBatchOptions { BatchSize = 11 });
    _builder(services).AddTransportConsumer();

    var descriptor = services.Last(d => d.ServiceType == typeof(TransportBatchOptions));
    await using var provider = services.BuildServiceProvider();
    using var scope = provider.CreateScope();
    var batch = scope.ServiceProvider.GetRequiredService<TransportBatchOptions>();

    await Assert.That(descriptor.Lifetime).IsEqualTo(ServiceLifetime.Scoped).Because("the host's lifetime is kept");
    await Assert.That(batch.BatchSize).IsEqualTo(11);
    await Assert.That(batch.MaxWaitMs).IsEqualTo(300);
  }

  [Test]
  public async Task TypeRegisteredInCode_IsLeftAsTheHostRegisteredItAsync() {
    var services = _services(_config(($"{ASB}:Batch:BatchSize", "64")), "AzureServiceBus");
    services.AddSingleton<TransportBatchOptions>();
    _builder(services).AddTransportConsumer();

    await using var provider = services.BuildServiceProvider();

    await Assert.That(provider.GetRequiredService<TransportBatchOptions>().BatchSize).IsEqualTo(200)
      .Because("a type registration is constructed by the container, so there is no instance to bind over");
  }

  [Test]
  public async Task ResilienceSetInCode_KeepsItsValues_ConfigurationOverridesPerKeyAsync() {
    var services = _services(_config(($"{ASB}:SubscriptionResilience:MaxRetryDelay", "00:00:30")), "AzureServiceBus");
    _builder(services).AddTransportConsumer(config => config.ResilienceOptions.InitialRetryAttempts = 8);

    await using var provider = services.BuildServiceProvider();
    var resilience = provider.GetRequiredService<SubscriptionResilienceOptions>();

    await Assert.That(resilience.InitialRetryAttempts).IsEqualTo(8);
    await Assert.That(resilience.MaxRetryDelay).IsEqualTo(TimeSpan.FromSeconds(30));
  }

  [Test]
  public async Task ConsumerSection_AddsAdditionalDestinations_SkippingBlankAndDuplicateAsync() {
    var services = _services(_config(
      ($"{ASB}:Consumer:AdditionalDestinations:0:Address", "audit-topic"),
      ($"{ASB}:Consumer:AdditionalDestinations:0:RoutingKey", "audit.#"),
      ($"{ASB}:Consumer:AdditionalDestinations:1:Address", "  "),
      ($"{ASB}:Consumer:AdditionalDestinations:2:Address", "code-topic"),
      ($"{ASB}:Consumer:AdditionalDestinations:3:Address", "bare-topic")), "AzureServiceBus");
    _builder(services).AddTransportConsumer(config =>
      config.AdditionalDestinations.Add(new TransportDestination("code-topic")));

    await using var provider = services.BuildServiceProvider();
    var destinations = provider.GetRequiredService<TransportConsumerOptions>().Destinations;

    await Assert.That(destinations.Count(d => d.Address == "audit-topic" && d.RoutingKey == "audit.#")).IsEqualTo(1);
    await Assert.That(destinations.Count(d => d.Address == "code-topic")).IsEqualTo(1)
      .Because("a destination the code already subscribes to is not subscribed twice");
    await Assert.That(destinations.Count(d => d.Address == "bare-topic" && d.RoutingKey == null)).IsEqualTo(1);
    await Assert.That(destinations.Any(d => string.IsNullOrWhiteSpace(d.Address))).IsFalse();
  }

  [Test]
  public async Task PerspectiveBuilderOverload_BindsTheSameSectionsAsync() {
    var services = _services(_config(
      ($"{ASB}:MessageProcessing:MaxConcurrentMessages", "12"),
      ($"{ASB}:SubscriptionResilience:InitialRetryAttempts", "2"),
      ($"{ASB}:Consumer:AdditionalDestinations:0:Address", "extra")), "AzureServiceBus");
    _builder(services);
    new WhizbangPerspectiveBuilder(services).AddTransportConsumer();

    await using var provider = services.BuildServiceProvider();

    await Assert.That(provider.GetRequiredService<MessageProcessingOptions>().MaxConcurrentMessages).IsEqualTo(12);
    await Assert.That(provider.GetRequiredService<SubscriptionResilienceOptions>().InitialRetryAttempts).IsEqualTo(2);
    await Assert.That(provider.GetRequiredService<TransportConsumerOptions>().Destinations.Any(d => d.Address == "extra")).IsTrue();
  }

  [Test]
  public async Task RegisteringTheSameTransportTwice_AddsOneSectionAsync() {
    var services = new ServiceCollection();

    TransportConfigurationSection.Register(services, "AzureServiceBus");
    TransportConfigurationSection.Register(services, "AzureServiceBus");
    TransportConfigurationSection.Register(services, "RabbitMQ");

    await using var provider = services.BuildServiceProvider();
    var names = provider.GetServices<TransportConfigurationSection>().Select(s => s.Name).ToList();

    await Assert.That(names).IsEquivalentTo(["AzureServiceBus", "RabbitMQ"]);
    await Assert.That(new TransportConfigurationSection("RabbitMQ").Path).IsEqualTo(RABBIT);
  }

  [Test]
  public async Task Register_RejectsMissingArgumentsAsync() {
    await Assert.That(() => TransportConfigurationSection.Register(null!, "AzureServiceBus")).Throws<ArgumentNullException>();
    await Assert.That(() => TransportConfigurationSection.Register(new ServiceCollection(), " ")).Throws<ArgumentException>();
  }

  private sealed class TestServiceInstanceProvider(string serviceName) : IServiceInstanceProvider {
    public string ServiceName { get; } = serviceName;
    public string HostName => "test-host";
    public int ProcessId => 1;
    Guid IServiceInstanceProvider.InstanceId => Guid.Empty;
    public ServiceInstanceInfo ToInfo() => throw new NotSupportedException();
  }
}
