// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.Metrics;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Routing;
using Whizbang.Core.Startup;
using Whizbang.Core.Transports;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// A host with no message broker starts without registering a transport (#1876). The transport consumer is
/// part of the turnkey composition, but with no <see cref="ITransport"/> it is not started and nothing waits
/// for it: cross-service messaging is off and local dispatch keeps working. Registering a transport, including
/// <see cref="InProcessTransport"/> in a test, turns the consumer on as before.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Workers/TransportConsumerBuilderExtensions.cs</code-under-test>
[Category("DependencyInjection")]
public class TransportConsumerWithoutATransportTests {

  [Test]
  public async Task AddTransportConsumer_WithNoTransport_BuildsUnderContainerValidationAsync() {
    var services = _composition(withTransport: false);

    await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });

    await Assert.That(provider.GetService<TransportConsumerWorker>()).IsNull()
      .Because("with no transport there is nothing to consume, so no consumer is built");
  }

  [Test]
  public async Task AddTransportConsumer_WithNoTransport_StartsNoConsumerAsync() {
    await using var provider = _composition(withTransport: false).BuildServiceProvider();

    var hosted = provider.GetServices<IHostedService>().ToList();

    await Assert.That(hosted.OfType<TransportConsumerWorker>()).IsEmpty();
  }

  [Test]
  public async Task AddTransportConsumer_WithNoTransport_ReadinessDoesNotWaitForAConsumerAsync() {
    await using var provider = _composition(withTransport: false).BuildServiceProvider();

    var contributors = provider.GetServices<IStartupReadinessContributor>().ToList();

    foreach (var contributor in contributors) {
      await Assert.That(contributor.WaitForContributorReadyAsync(CancellationToken.None).IsCompleted).IsTrue()
        .Because($"{contributor.ContributorName} has no subscriptions to wait for when no transport is configured");
    }
  }

  [Test]
  public async Task AddTransportConsumer_WithTheInProcessTransport_StartsTheConsumerAsBeforeAsync() {
    await using var provider = _composition(withTransport: true).BuildServiceProvider();

    var worker = provider.GetRequiredService<TransportConsumerWorker>();

    await Assert.That(provider.GetServices<IHostedService>()).Contains(worker);
    await Assert.That(provider.GetServices<IStartupReadinessContributor>()).Contains(worker);
  }

  private static ServiceCollection _composition(bool withTransport) {
    var services = new ServiceCollection();
    services.TryAddWhizbangDefaults();
    services.AddLogging();
    services.AddSingleton<IServiceInstanceProvider>(new TestServiceInstanceProvider());
    services.AddSingleton(new JsonSerializerOptions());
    services.AddSingleton<ISchemaReadyGate>(SchemaReadyGate.AlreadyReady());
    services.AddSingleton(new WhizbangMetrics(meterFactory: new ServiceCollection().AddMetrics().BuildServiceProvider().GetRequiredService<IMeterFactory>()));
    services.AddSingleton<TransportMetrics>();
    if (withTransport) {
      services.AddSingleton<ITransport>(new InProcessTransport(new FakeTimeProvider()));
    }
    new WhizbangBuilder(services)
      .WithRouting(routing => routing.OwnDomains("myapp.orders.commands"))
      .AddTransportConsumer();
    return services;
  }
}
