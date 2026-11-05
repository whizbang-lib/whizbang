// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Notifications.AppSignals;
using Whizbang.Core.Signals;

namespace Whizbang.Core.Tests.Signals;

/// <summary>
/// #2046: application topics are carried by the signal bus. <see cref="IAppSignalChannel"/> is the bus itself; the bus
/// hands each topic to the transports that carry topics, and owns nothing about the wire.
/// </summary>
public class SignalBusAppTopicTests {
  private sealed class TopicTransport : ISignalTransport, ISignalTopicTransport {
    public List<(string Topic, string Payload)> Published { get; } = [];
    public Dictionary<string, List<Func<string, CancellationToken, Task>>> Handlers { get; } = new(StringComparer.Ordinal);
    public int Disposed { get; private set; }

    public Task StartAsync(ISignalSink sink, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public ValueTask PublishAsync<TSignal>(TSignal signal, SignalTarget target, CancellationToken cancellationToken = default)
      where TSignal : ISignal => ValueTask.CompletedTask;

    public Task PublishTopicAsync(string topic, string payload, CancellationToken cancellationToken = default) {
      Published.Add((topic, payload));
      return Task.CompletedTask;
    }

    public IDisposable SubscribeTopic(string topic, Func<string, CancellationToken, Task> handler) {
      if (!Handlers.TryGetValue(topic, out var list)) {
        Handlers[topic] = list = [];
      }
      list.Add(handler);
      return new Handle(this);
    }

    private sealed class Handle(TopicTransport owner) : IDisposable {
      public void Dispose() => owner.Disposed++;
    }
  }

  [Test]
  public async Task Publish_HandsTheTopicToEveryTopicTransportAsync() {
    var first = new TopicTransport();
    var second = new TopicTransport();
    IAppSignalChannel channel = new SignalBus([first, new InMemorySignalTransport(), second], []);

    await channel.PublishAsync("orders", "{\"id\":1}");

    await Assert.That(first.Published).IsEquivalentTo([("orders", "{\"id\":1}")]);
    await Assert.That(second.Published).IsEquivalentTo([("orders", "{\"id\":1}")]);
  }

  [Test]
  public async Task Subscribe_RegistersWithEveryTopicTransport_AndDisposingReleasesEachAsync() {
    var first = new TopicTransport();
    var second = new TopicTransport();
    IAppSignalChannel channel = new SignalBus([first, second], []);
    static Task handlerAsync(string payload, CancellationToken cancellationToken) => Task.CompletedTask;
    Func<string, CancellationToken, Task> handler = handlerAsync;

    var subscription = channel.Subscribe("orders", handler);

    await Assert.That(first.Handlers["orders"]).Contains(handler);
    await Assert.That(second.Handlers["orders"]).Contains(handler);
    subscription.Dispose();
    subscription.Dispose();
    await Assert.That(first.Disposed).IsEqualTo(1).Because("a second dispose releases nothing twice");
    await Assert.That(second.Disposed).IsEqualTo(1);
  }

  [Test]
  public async Task WithNoTopicTransport_PublishSendsNothing_AndSubscribeHoldsNothingAsync() {
    IAppSignalChannel channel = new SignalBus([new InMemorySignalTransport()], []);

    await channel.PublishAsync("orders", "x");
    var subscription = channel.Subscribe("orders", (_, _) => Task.CompletedTask);
    subscription.Dispose();

    await Assert.That(subscription).IsNotNull();
  }

  [Test]
  public async Task Topics_AreValidated_AndAHandlerIsRequiredAsync() {
    IAppSignalChannel channel = new SignalBus([new TopicTransport()], []);

    await Assert.That(async () => await channel.PublishAsync("wh_internal", "x")).Throws<ArgumentException>()
      .Because("the wh_ prefix is reserved for the framework's own channels");
    await Assert.That(() => channel.Subscribe("Bad Topic", (_, _) => Task.CompletedTask)).Throws<ArgumentException>();
    await Assert.That(() => channel.Subscribe("orders", null!)).Throws<ArgumentNullException>();
  }
}
