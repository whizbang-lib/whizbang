// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json.Serialization;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Serialization;
using Whizbang.Testing.MultiService;

namespace Whizbang.Core.Tests.MultiService;

/// <summary>
/// Directed-message E2E over the multi-service harness: a message published with a
/// <see cref="IMessageEnvelope.Target"/> crosses the REAL wire and is stored ONLY by the
/// service whose identity matches — every other service discards it at the receive seam.
/// An untargeted (broadcast) message is stored by everyone, unchanged.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Workers/TransportConsumerWorker.cs</code-under-test>
/// <code-under-test>src/Whizbang.Testing/MultiService/MultiServiceHarness.cs</code-under-test>
[Category("MultiService")]
[NotInParallel("WhizbangBackgroundServiceTests")]
public class DirectedMessageE2ETests {

  public sealed record DirectedProbeEvent {
    public int X { get; init; }
  }

  public sealed class ProbeCatalog : IMessageTypeCatalog {
    public IReadOnlyList<MessageTypeCatalogEntry> GetAll() => [
      new(typeof(DirectedProbeEvent), TypeNameFormatter.FormatClrTypeName(typeof(DirectedProbeEvent)), "event", null),
    ];
  }

  private static bool _contextRegistered;
  private static readonly Lock _registerLock = new();

  private static void _ensureJsonContext() {
    lock (_registerLock) {
      if (!_contextRegistered) {
        JsonContextRegistry.RegisterContext(DirectedMessageE2EJsonContext.Default);
        _contextRegistered = true;
      }
    }
  }

  [Test]
  public async Task TargetedMessage_IsStoredOnlyByTheTargetServiceAsync() {
    _ensureJsonContext();
    await using var harness = await MultiServiceHarness.Create()
      .AddService("target-svc", svc => svc.WithCatalog<ProbeCatalog>())
      .AddService("bystander-svc", svc => svc.WithCatalog<ProbeCatalog>())
      .StartAsync();

    // Targeted first, broadcast second: per-subscriber delivery is ordered, so once a service
    // has stored the broadcast probe its verdict on the earlier targeted one is final —
    // a deterministic completion signal, no sleeps.
    await harness.PublishAsync(new DirectedProbeEvent { X = 1 }, target: "target-svc");
    await harness.PublishAsync(new DirectedProbeEvent { X = 2 });

    var targetStored = await harness.GetService("target-svc").Inbox.WaitForInboxAsync(2, TimeSpan.FromSeconds(10));
    var bystanderStored = await harness.GetService("bystander-svc").Inbox.WaitForInboxAsync(2, TimeSpan.FromSeconds(10));

    await Assert.That(targetStored.Count).IsEqualTo(2)
      .Because("the target service stores BOTH the message directed at it and the broadcast.");
    await Assert.That(targetStored.All(r => r.DiscardedOnReceive is null)).IsTrue()
      .Because("the target processes both: neither is settled on receive.");
    // #1720: a non-target service no longer drops the directed message unstored. It stores it settled with the
    // foreign-target reason: durable and inspectable, but processed at arrival, so it is never claimed, copied or
    // dispatched. Only the broadcast is work for the bystander.
    await Assert.That(bystanderStored.Count).IsEqualTo(2);
    await Assert.That(bystanderStored.Single(r => _x(r) == 1).DiscardedOnReceive).IsEqualTo(InboxReceiveDiscard.FOREIGN_TARGET)
      .Because("the message directed at another service is stored settled, never processed by the bystander.");
    await Assert.That(bystanderStored.Single(r => _x(r) == 2).DiscardedOnReceive).IsNull()
      .Because("the broadcast is the bystander's only work.");
  }

  private static int _x(InboxMessage row) =>
    row.Envelope.Payload.TryGetProperty("x", out var lower) ? lower.GetInt32() : row.Envelope.Payload.GetProperty("X").GetInt32();
}

/// <summary>JSON context for the directed-message E2E payloads (production-parity registration).</summary>
[JsonSerializable(typeof(DirectedMessageE2ETests.DirectedProbeEvent))]
[JsonSerializable(typeof(MessageEnvelope<DirectedMessageE2ETests.DirectedProbeEvent>))]
public sealed partial class DirectedMessageE2EJsonContext : JsonSerializerContext;
