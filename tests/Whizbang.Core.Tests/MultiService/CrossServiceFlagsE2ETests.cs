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
/// Cross-service E2E over the multi-service harness: a flag-bearing event published by one service
/// crosses a REAL wire boundary (JSON bytes, payload arrives as <c>JsonElement</c>) and must keep
/// its catalog-derived flags in the receiving service's inbox. This is the test class that
/// single-service suites structurally cannot express — the receiving service derives everything
/// from the wire type name and its own catalog union, never from the publisher's object graph.
/// </summary>
/// <code-under-test>src/Whizbang.Testing/MultiService/MultiServiceHarness.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Workers/TransportConsumerWorker.cs</code-under-test>
[Category("MultiService")]
[NotInParallel("WhizbangBackgroundServiceTests")]
public class CrossServiceFlagsE2ETests {

  // ---- shared contracts (the cross-service event types) ----

  public sealed record CrossServiceCollectiveMarker {
    public int X { get; init; }
  }

  public sealed class SharedContractsCatalog : IMessageTypeCatalog {
    public IReadOnlyList<MessageTypeCatalogEntry> GetAll() => [
      new(typeof(CrossServiceCollectiveMarker), TypeNameFormatter.FormatClrTypeName(typeof(CrossServiceCollectiveMarker)), "event", null) { IsCollective = true },
    ];
  }

  // ---- per-service host catalogs (each service's own types) ----

  private sealed class PublisherOnlyEvent;
  private sealed class ConsumerOnlyEvent;

  public sealed class PublisherHostCatalog : IMessageTypeCatalog {
    public IReadOnlyList<MessageTypeCatalogEntry> GetAll() => [
      new(typeof(PublisherOnlyEvent), TypeNameFormatter.FormatClrTypeName(typeof(PublisherOnlyEvent)), "event", null),
    ];
  }

  public sealed class ConsumerHostCatalog : IMessageTypeCatalog {
    public IReadOnlyList<MessageTypeCatalogEntry> GetAll() => [
      new(typeof(ConsumerOnlyEvent), TypeNameFormatter.FormatClrTypeName(typeof(ConsumerOnlyEvent)), "event", null),
    ];
  }

  private static bool _contextRegistered;
  private static readonly Lock _registerLock = new();

  private static void _ensureJsonContext() {
    lock (_registerLock) {
      if (!_contextRegistered) {
        // Production parity: a contracts assembly ships + registers its own JsonSerializerContext.
        JsonContextRegistry.RegisterContext(CrossServiceE2EJsonContext.Default);
        _contextRegistered = true;
      }
    }
  }

  [Test]
  public async Task CollectiveEvent_CrossesTheWire_KeepsCollectiveFlagInReceivingInboxAsync() {
    _ensureJsonContext();
    await using var harness = await MultiServiceHarness.Create()
      .AddService("publisher", svc => svc
        .WithCatalog<PublisherHostCatalog>()
        .WithCatalog<SharedContractsCatalog>())
      .AddService("consumer", svc => svc
        .WithCatalog<ConsumerHostCatalog>()
        .WithCatalog<SharedContractsCatalog>())
      .StartAsync();

    await harness.PublishAsync(new CrossServiceCollectiveMarker { X = 1 });

    // Shared topic: both services receive the publication; assert on the CONSUMER.
    var stored = await harness.GetService("consumer").Inbox.WaitForInboxAsync(1, TimeSpan.FromSeconds(10));
    await Assert.That(stored[0].Flags & EventFlags.Collective).IsEqualTo(EventFlags.Collective)
      .Because("a collective event that crosses a real wire boundary must keep EventFlags.Collective " +
               "in the receiving service's inbox — the payload is JsonElement there, so only the " +
               "catalog-union lookup by wire type name can supply it.");
  }

  [Test]
  public async Task CollectiveEvent_ConsumerWithoutContractsCatalog_StoresNoFlagsAsync() {
    // Negative control — the boundary CONTRACT, not a bug: a receiving service whose catalog union
    // does not cover the event type cannot derive its flags (typed checks are blind on JsonElement).
    // If this test ever starts failing with flags present, the derivation found a new source —
    // re-examine the boundary contract before celebrating.
    _ensureJsonContext();
    await using var harness = await MultiServiceHarness.Create()
      .AddService("publisher", svc => svc
        .WithCatalog<PublisherHostCatalog>()
        .WithCatalog<SharedContractsCatalog>())
      .AddService("consumer", svc => svc
        .WithCatalog<ConsumerHostCatalog>())
      .StartAsync();

    await harness.PublishAsync(new CrossServiceCollectiveMarker { X = 2 });

    var stored = await harness.GetService("consumer").Inbox.WaitForInboxAsync(1, TimeSpan.FromSeconds(10));
    await Assert.That(stored[0].Flags).IsEqualTo(EventFlags.None);
  }
}

/// <summary>JSON context for the cross-service E2E contracts (production-parity registration).</summary>
[JsonSerializable(typeof(CrossServiceFlagsE2ETests.CrossServiceCollectiveMarker))]
[JsonSerializable(typeof(MessageEnvelope<CrossServiceFlagsE2ETests.CrossServiceCollectiveMarker>))]
public sealed partial class CrossServiceE2EJsonContext : JsonSerializerContext {
}
