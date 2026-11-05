// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Routing;
using Whizbang.Core.Transports;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// The topic integrity traffic addressed to this service travels on: the configured repair topic,
/// else the first topic the service consumes, else none (the callers then skip or refuse the send).
/// </summary>
/// <code-under-test>src/Whizbang.Core/Workers/RepairTopicResolver.cs</code-under-test>
public class RepairTopicResolverTests {
  [Test]
  public async Task Configured_WinsAsync() {
    var services = _services("first");

    await Assert.That(RepairTopicResolver.Resolve("repair", services)).IsEqualTo("repair");
  }

  [Test]
  public async Task Unset_FirstDestinationAsync() {
    var services = _services("first", "second");

    await Assert.That(RepairTopicResolver.Resolve(null, services)).IsEqualTo("first");
  }

  [Test]
  public async Task Unset_NoDestinations_NullAsync() {
    var services = _services();

    await Assert.That(RepairTopicResolver.Resolve(null, services)).IsNull();
  }

  [Test]
  public async Task Unset_NoConsumer_NullAsync() {
    var services = new ServiceCollection().BuildServiceProvider();

    await Assert.That(RepairTopicResolver.Resolve(null, services)).IsNull();
  }

  // ── #2078: integrity traffic addressed to this service goes to its own command inbox ──

  [Test]
  public async Task Unset_OwnCommandInbox_IsPreferredOverASharedFirstDestinationAsync() {
    var services = _destinations(_shared("inbox.whizbang"), _ownInbox("inbox.app.orders"));

    await Assert.That(RepairTopicResolver.Resolve(null, services)).IsEqualTo("inbox.app.orders")
      .Because("only this service subscribes to its own command inbox, so a directed message reaches it alone");
  }

  [Test]
  public async Task Unset_NoOwnCommandInbox_KeepsTheFirstDestination_AndSaysOnceThatItIsSharedAsync() {
    var logs = new CapturingLoggerFactory();
    var services = _destinations(logs, _shared("inbox.whizbang"));

    var first = RepairTopicResolver.Resolve(null, services);
    var second = RepairTopicResolver.Resolve(null, services);

    await Assert.That(first).IsEqualTo("inbox.whizbang");
    await Assert.That(second).IsEqualTo("inbox.whizbang");
    await Assert.That(logs.Messages.Count(m => m.Contains("shared", StringComparison.OrdinalIgnoreCase))).IsEqualTo(1)
      .Because("an operator learns once that this service's directed integrity traffic rides a shared topic");
  }

  [Test]
  public async Task Broadcast_IsTheSharedTopic_NeverTheOwnCommandInboxAsync() {
    var services = _destinations(_ownInbox("inbox.app.orders"), _shared("inbox.whizbang"));

    await Assert.That(RepairTopicResolver.ResolveBroadcast(null, services)).IsEqualTo("inbox.whizbang")
      .Because("a broadcast must reach every service, which only the shared topic does");
  }

  [Test]
  public async Task Broadcast_Configured_WinsAsync() {
    var services = _destinations(_ownInbox("inbox.app.orders"), _shared("inbox.whizbang"));

    await Assert.That(RepairTopicResolver.ResolveBroadcast("repair", services)).IsEqualTo("repair");
  }

  [Test]
  public async Task Broadcast_OnlyAnOwnCommandInbox_IsThatInboxAsync() {
    var services = _destinations(_ownInbox("inbox.app.orders"));

    await Assert.That(RepairTopicResolver.ResolveBroadcast(null, services)).IsEqualTo("inbox.app.orders")
      .Because("with nothing shared to broadcast on, the only topic the service has is the last resort");
  }

  private static TransportDestination _shared(string topic) => new(topic);

  private static TransportDestination _ownInbox(string topic) => new(topic, Metadata: new Dictionary<string, JsonElement> {
    [NamespaceInboxStrategy.OwnedCommandInboxMetadataKey] = JsonElement.Parse("true"),
  });

  private static ServiceProvider _destinations(params TransportDestination[] destinations) =>
    _destinations(null, destinations);

  private static ServiceProvider _destinations(ILoggerFactory? logs, params TransportDestination[] destinations) {
    var options = new TransportConsumerOptions();
    options.Destinations.AddRange(destinations);
    var services = new ServiceCollection().AddSingleton(options);
    if (logs is not null) {
      services.AddSingleton(logs);
    }
    return services.BuildServiceProvider();
  }

  private sealed class CapturingLoggerFactory : ILoggerFactory, ILogger {
    public List<string> Messages { get; } = [];
    public ILogger CreateLogger(string categoryName) => this;
    public void AddProvider(ILoggerProvider provider) { }
    public void Dispose() { }
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
  }

  private static ServiceProvider _services(params string[] topics) {
    var options = new TransportConsumerOptions();
    foreach (var topic in topics) {
      options.Destinations.Add(new TransportDestination(topic));
    }
    return new ServiceCollection().AddSingleton(options).BuildServiceProvider();
  }
}
