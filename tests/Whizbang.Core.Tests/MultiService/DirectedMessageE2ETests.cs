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
    var bystanderStored = await harness.GetService("bystander-svc").Inbox.WaitForInboxAsync(1, TimeSpan.FromSeconds(10));

    await Assert.That(targetStored.Count).IsEqualTo(2)
      .Because("the target service stores BOTH the message directed at it and the broadcast.");
    await Assert.That(bystanderStored.Count).IsEqualTo(1)
      .Because("a non-target service discards the directed message at the receive seam — " +
               "only the broadcast reaches its inbox.");
  }
}

/// <summary>JSON context for the directed-message E2E payloads (production-parity registration).</summary>
[JsonSerializable(typeof(DirectedMessageE2ETests.DirectedProbeEvent))]
[JsonSerializable(typeof(MessageEnvelope<DirectedMessageE2ETests.DirectedProbeEvent>))]
public sealed partial class DirectedMessageE2EJsonContext : JsonSerializerContext {
}
