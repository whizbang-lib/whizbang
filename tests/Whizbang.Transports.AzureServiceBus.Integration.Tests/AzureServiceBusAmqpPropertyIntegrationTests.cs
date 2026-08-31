using System.Text.Json;
using Azure.Messaging.ServiceBus;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Transports;
using Whizbang.Core.ValueObjects;
using Whizbang.Transports.AzureServiceBus.Integration.Tests.Containers;

namespace Whizbang.Transports.AzureServiceBus.Integration.Tests;

/// <summary>
/// Covers the JsonElement-to-AMQP conversion that copies destination metadata onto a message's
/// ApplicationProperties.
/// </summary>
/// <remarks>
/// Uses topic-spike-plain/sub-spike rather than topic-00: nine classes publish to topic-00 and a
/// reader there takes whatever arrives first, so adding publishers makes unrelated tests receive
/// each other's messages.
/// <para>
/// Carries the assembly's existing "ServiceBus" not-in-parallel key. Thirteen classes already use
/// it — the emulator does not take concurrent load well, so this suite is serialised by
/// convention. A key of its own would not serialise against those thirteen, which is how the
/// first version of this class ended up racing the spike tests for the same subscription.
/// </para>
/// </remarks>
[Category("Integration")]
[Timeout(240_000)]
[NotInParallel("ServiceBus")]
[ClassDataSource<ServiceBusEmulatorFixtureSource>(Shared = SharedType.PerAssembly)]
public class AzureServiceBusAmqpPropertyIntegrationTests(ServiceBusEmulatorFixtureSource fixtureSource) {
  private const string TOPIC = "topic-spike-plain";
  private const string SUBSCRIPTION = "sub-spike";

  private readonly ServiceBusEmulatorFixture _fixture = fixtureSource.Fixture;
  private readonly List<IAsyncDisposable> _disposables = [];

  [After(Test)]
  public async Task DisposeAsync() {
    foreach (var d in _disposables) {
      try { await d.DisposeAsync(); } catch { /* best-effort cleanup */ }
    }
    _disposables.Clear();
  }

  private static MessageEnvelope<TestMessage> _envelope() => new() {
    MessageId = MessageId.New(),
    Payload = new TestMessage("amqp-props"),
    DispatchContext = new MessageDispatchContext {
      Mode = Whizbang.Core.Dispatch.DispatchModes.Outbox,
      Source = MessageSource.Outbox,
    },
    Hops = [],
  };

  /// <summary>Drains anything left behind so a run starts from a known-empty subscription.</summary>
  private async Task _drainAsync() {
    var receiver = _fixture.Client.CreateReceiver(TOPIC, SUBSCRIPTION);
    await using (receiver) {
      while (true) {
        var msg = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(1));
        if (msg is null) {
          return;
        }
        await receiver.CompleteMessageAsync(msg);
      }
    }
  }

  /// <summary>
  /// Receives one message and settles it. Completing matters: an unsettled message has its lock
  /// expire and is redelivered, which inflates the DeliveryCount that the lock-loss spike tests
  /// on this same subscription assert on exactly.
  /// </summary>
  private async Task<ServiceBusReceivedMessage?> _receiveAsync() {
    var receiver = _fixture.Client.CreateReceiver(TOPIC, SUBSCRIPTION);
    await using (receiver) {
      var msg = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(15));
      if (msg is not null) {
        await receiver.CompleteMessageAsync(msg);
      }
      return msg;
    }
  }

  [Test]
  public async Task PublishAsync_ConvertsEveryJsonKindOntoApplicationPropertiesAsync() {
    // Every JSON kind has to survive the trip: a header the broker rejects fails the publish,
    // and one that silently changes shape breaks any consumer filtering on it.
    var transport = new AzureServiceBusTransport(
      _fixture.Client, new JsonSerializerOptions { TypeInfoResolver = TestJsonContext.Default });
    _disposables.Add(transport);
    await transport.InitializeAsync();
    await _drainAsync();

    using var doc = JsonDocument.Parse("""
      {
        "str": "hello",
        "int": 42,
        "float": 1.5,
        "yes": true,
        "no": false,
        "nothing": null,
        "arr": [1, 2],
        "obj": { "k": "v" }
      }
      """);
    var metadata = doc.RootElement.EnumerateObject()
      .ToDictionary(p => p.Name, p => p.Value, StringComparer.Ordinal);

    await transport.PublishAsync(_envelope(), new TransportDestination(TOPIC, Metadata: metadata));

    var received = await _receiveAsync();
    await Assert.That(received).IsNotNull();
    var props = received!.ApplicationProperties;

    await Assert.That(props["str"]).IsEqualTo("hello");

    // Numbers arrive native rather than as text, so a consumer can filter on them numerically.
    await Assert.That(props["int"]).IsEqualTo(42L);
    await Assert.That(props["float"]).IsEqualTo(1.5d);

    // Booleans stay booleans, not "True"/"False".
    await Assert.That(props["yes"]).IsEqualTo(true);
    await Assert.That(props["no"]).IsEqualTo(false);

    // Null stays null rather than becoming the string "null".
    await Assert.That(props["nothing"]).IsNull();

    // AMQP has no array or map primitive here, so both ride as their raw JSON text.
    // GetRawText preserves the source spacing, so this is the JSON as written.
    await Assert.That(props["arr"]?.ToString()).IsEqualTo("[1, 2]");
    await Assert.That(props["obj"]?.ToString()).Contains("\"k\"");
  }

  [Test]
  public async Task PublishAsync_WithAStreamIdInMetadata_SetsASessionKeyAsync() {
    // The session key is what gives a stream its FIFO ordering, and it is read out of metadata
    // through the same conversion — a StreamId that failed to convert would scatter one
    // stream's messages across sessions.
    var transport = new AzureServiceBusTransport(
      _fixture.Client, new JsonSerializerOptions { TypeInfoResolver = TestJsonContext.Default });
    _disposables.Add(transport);
    await transport.InitializeAsync();
    await _drainAsync();

    var streamId = Guid.NewGuid();
    using var doc = JsonDocument.Parse($"{{\"StreamId\":\"{streamId}\"}}");
    var metadata = doc.RootElement.EnumerateObject()
      .ToDictionary(p => p.Name, p => p.Value, StringComparer.Ordinal);

    await transport.PublishAsync(_envelope(), new TransportDestination(TOPIC, Metadata: metadata));

    var received = await _receiveAsync();
    await Assert.That(received).IsNotNull();
    await Assert.That(received!.SessionId).IsNotNull();
  }
}
