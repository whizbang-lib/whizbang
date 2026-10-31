// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.Metrics;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Observability;
using Whizbang.Core.Routing;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Component.Tests.Workers;

/// <summary>
/// Starting a host with no message broker (#1876): the transport consumer is not started, and startup logs one
/// line saying cross-service messaging is off because no transport is configured.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Workers/TransportConsumerBuilderExtensions.cs</code-under-test>
public class TransportConsumerWithoutATransportStartupTests {

  [Test]
  public async Task Startup_WithNoTransport_LogsOnceThatCrossServiceMessagingIsOffAsync() {
    var services = new ServiceCollection();
    services.TryAddWhizbangDefaults();
    services.AddLogging(b => {
      b.SetMinimumLevel(LogLevel.Trace);
      b.AddFakeLogging();
    });
    services.AddSingleton<IServiceInstanceProvider>(new ServiceInstanceProvider(
      configuration: new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build()));
    services.AddSingleton(new JsonSerializerOptions());
    services.AddSingleton<ISchemaReadyGate>(SchemaReadyGate.AlreadyReady());
    services.AddSingleton(new WhizbangMetrics(meterFactory: new ServiceCollection().AddMetrics().BuildServiceProvider().GetRequiredService<IMeterFactory>()));
    services.AddSingleton<TransportMetrics>();
    var builder = new WhizbangBuilder(services).WithRouting(routing => routing.OwnDomains("myapp.orders.commands"));
    builder.AddTransportConsumer();
    await using var provider = services.BuildServiceProvider();

    var hosted = provider.GetServices<IHostedService>().ToList();
    foreach (var service in hosted) {
      await service.StartAsync(CancellationToken.None);
    }
    foreach (var service in hosted) {
      await service.StopAsync(CancellationToken.None);
    }

    var lines = provider.GetFakeLogCollector().GetSnapshot()
      .Where(r => r.Message.Contains("Cross-service messaging is off", StringComparison.Ordinal))
      .ToList();
    await Assert.That(lines.Count).IsEqualTo(1)
      .Because("a host without a transport says once, at startup, that it consumes nothing from other services");
    await Assert.That(lines[0].Message).Contains("no transport is configured");
    await Assert.That(lines[0].Level).IsEqualTo(LogLevel.Information);
  }
}
