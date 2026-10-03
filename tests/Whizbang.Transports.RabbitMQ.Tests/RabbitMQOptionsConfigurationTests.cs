#pragma warning disable CA1707 // Identifiers should not contain underscores (test method names use underscores by convention)

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Routing;
using Whizbang.Core.Transports;

namespace Whizbang.Transports.RabbitMQ.Tests;

/// <summary>
/// <see cref="RabbitMQOptions"/> bind from <c>Whizbang:Transports:RabbitMQ</c> over the code
/// callback (#1012), and every component the registration builds resolves the bound instance.
/// </summary>
public class RabbitMQOptionsConfigurationTests {
  private const string CONNECTION_STRING = "amqp://guest:guest@localhost:5672/";

  private static IConfiguration _config(params (string Key, string Value)[] values) =>
    new ConfigurationBuilder()
      .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
      .Build();

  private static ServiceProvider _provider(IConfiguration? configuration, Action<RabbitMQOptions>? configure = null) {
    var services = new ServiceCollection();
    if (configuration is not null) {
      services.AddSingleton(configuration);
    }
    services.AddSingleton<IConnection>(new FakeConnection(() => Task.FromResult<IChannel>(new FakeChannel())));
    services.AddRabbitMQTransport(CONNECTION_STRING, configure);
    return services.BuildServiceProvider();
  }

  [Test]
  public async Task AddRabbitMQTransport_BindsEveryKnobFromItsSectionAsync() {
    await using var provider = _provider(_config(
      ("Whizbang:Transports:RabbitMQ:MaxChannels", "4"),
      ("Whizbang:Transports:RabbitMQ:MaxDeliveryAttempts", "3"),
      ("Whizbang:Transports:RabbitMQ:DefaultQueueName", "orders-queue"),
      ("Whizbang:Transports:RabbitMQ:PrefetchCount", "50"),
      ("Whizbang:Transports:RabbitMQ:AutoDeclareDeadLetterExchange", "false"),
      ("Whizbang:Transports:RabbitMQ:EnableSingleActiveConsumer", "true"),
      ("Whizbang:Transports:RabbitMQ:InitialRetryAttempts", "2"),
      ("Whizbang:Transports:RabbitMQ:InitialRetryDelay", "00:00:05"),
      ("Whizbang:Transports:RabbitMQ:MaxRetryDelay", "00:02:00"),
      ("Whizbang:Transports:RabbitMQ:BackoffMultiplier", "1.5"),
      ("Whizbang:Transports:RabbitMQ:RetryIndefinitely", "false")));

    var options = provider.GetRequiredService<RabbitMQOptions>();

    await Assert.That(options.MaxChannels).IsEqualTo(4);
    await Assert.That(options.MaxDeliveryAttempts).IsEqualTo(3);
    await Assert.That(options.DefaultQueueName).IsEqualTo("orders-queue");
    await Assert.That(options.PrefetchCount).IsEqualTo((ushort)50);
    await Assert.That(options.AutoDeclareDeadLetterExchange).IsFalse();
    await Assert.That(options.EnableSingleActiveConsumer).IsTrue();
    await Assert.That(options.InitialRetryAttempts).IsEqualTo(2);
    await Assert.That(options.InitialRetryDelay).IsEqualTo(TimeSpan.FromSeconds(5));
    await Assert.That(options.MaxRetryDelay).IsEqualTo(TimeSpan.FromMinutes(2));
    await Assert.That(options.BackoffMultiplier).IsEqualTo(1.5);
    await Assert.That(options.RetryIndefinitely).IsFalse();
  }

  [Test]
  public async Task CodeCallback_StaysTheDefault_ConfigurationOverridesPerKeyAsync() {
    await using var provider = _provider(
      _config(("Whizbang:Transports:RabbitMQ:PrefetchCount", "25")),
      o => {
        o.PrefetchCount = 10;
        o.MaxChannels = 6;
      });

    var options = provider.GetRequiredService<RabbitMQOptions>();

    await Assert.That(options.PrefetchCount).IsEqualTo((ushort)25);
    await Assert.That(options.MaxChannels).IsEqualTo(6);
  }

  [Test]
  public async Task AnotherTransportsSection_IsIgnoredAsync() {
    await using var provider = _provider(_config(
      ("Whizbang:Transports:AzureServiceBus:MaxDeliveryAttempts", "2"),
      ("Whizbang:Transports:AzureServiceBus:PrefetchCount", "9")));

    var options = provider.GetRequiredService<RabbitMQOptions>();

    await Assert.That(options.MaxDeliveryAttempts).IsEqualTo(10);
    await Assert.That(options.PrefetchCount).IsEqualTo((ushort)200);
  }

  [Test]
  public async Task NoConfiguration_KeepsTheCodeValuesAsync() {
    await using var provider = _provider(null, o => o.MaxDeliveryAttempts = 4);

    await Assert.That(provider.GetRequiredService<RabbitMQOptions>().MaxDeliveryAttempts).IsEqualTo(4);
  }

  [Test]
  public async Task PoisonThreshold_DerivesFromTheBoundDeliveryCapAsync() {
    await using var provider = _provider(_config(("Whizbang:Transports:RabbitMQ:MaxDeliveryAttempts", "3")));
    var poison = new PoisonMessageOptions();

    foreach (var postConfigure in provider.GetServices<IPostConfigureOptions<PoisonMessageOptions>>()) {
      postConfigure.PostConfigure(Options.DefaultName, poison);
    }

    await Assert.That(poison.MaxDeliveryAttempts).IsEqualTo(3);
  }

  [Test]
  public async Task AddRabbitMQTransport_RegistersItsSectionForTheTransportConsumerAsync() {
    await using var provider = _provider(_config());

    var sections = provider.GetServices<TransportConfigurationSection>().Select(s => s.Name).ToList();

    await Assert.That(sections).IsEquivalentTo(["RabbitMQ"]);
  }

  [Test]
  public async Task Apply_RejectsNullOptionsAsync() {
    await Assert.That(() => RabbitMQOptionsConfigurationBinder.Apply(null!, null)).Throws<ArgumentNullException>();
  }
}
