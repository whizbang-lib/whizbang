using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Whizbang.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Serialization;
using Whizbang.Core.Transports;
using Whizbang.Core.ValueObjects;
using Whizbang.Testing.MultiService;

namespace Whizbang.Testing.Tests.MultiService;

/// <summary>Probe contract for the harness tests — a service's own cross-wire event type.</summary>
public sealed record WireProbeEvent {
  public int X { get; init; }
}

/// <summary>JSON context for the probe contract (production parity: a contracts assembly ships its own).</summary>
[JsonSerializable(typeof(WireProbeEvent))]
[JsonSerializable(typeof(MessageEnvelope<WireProbeEvent>))]
public sealed partial class WireProbeJsonContext : JsonSerializerContext;

/// <summary>
/// Build-time and per-service-seam behavior of <see cref="MultiServiceHarness"/>: what it refuses
/// to build, what it cleans up when a service cannot start, and what each service's transport seam
/// (<c>ServiceWireTap</c>) does and does not change about the shared wire.
/// </summary>
/// <code-under-test>src/Whizbang.Testing/MultiService/MultiServiceHarness.cs</code-under-test>
/// <code-under-test>src/Whizbang.Testing/MultiService/WireFaultInjection.cs</code-under-test>
[Category("MultiService")]
[NotInParallel("WhizbangBackgroundServiceTests")]
public class MultiServiceHarnessTests {

  public sealed class ProbeCatalog : IMessageTypeCatalog {
    public IReadOnlyList<MessageTypeCatalogEntry> GetAll() => [
      new(typeof(WireProbeEvent), TypeNameFormatter.FormatClrTypeName(typeof(WireProbeEvent)), "event", null),
    ];
  }

  /// <summary>Records its own disposal so a test can prove the container that owned it was torn down.</summary>
  private sealed class DisposalSpy : IDisposable {
    public bool Disposed { get; private set; }
    public void Dispose() => Disposed = true;
  }

  private static bool _contextRegistered;
  private static readonly Lock _registerLock = new();

  private static void _ensureJsonContext() {
    lock (_registerLock) {
      if (!_contextRegistered) {
        JsonContextRegistry.RegisterContext(WireProbeJsonContext.Default);
        _contextRegistered = true;
      }
    }
  }

  [Test]
  public async Task StartAsync_WithNoServices_RefusesToBuildAnEmptyHarnessAsync() {
    // An empty harness would start, dispose cleanly and pass every assertion a test wrote about
    // it, because there is nothing to contradict them. Failing the build is the only way that
    // mistake surfaces as a test failure instead of a green run that proved nothing.
    var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
      await MultiServiceHarness.Create().StartAsync());

    await Assert.That(ex!.Message).Contains("at least one service");
  }

  [Test]
  public async Task StartAsync_WhenAServiceCannotStart_DisposesThePartiallyBuiltContainerAsync() {
    var spy = new DisposalSpy();

    // A service whose transport cannot be resolved fails after its container is built. That
    // container owns singletons (connections, channels, background state); leaking it would
    // survive the failed test and poison whatever ran next in the same process.
    var builder = MultiServiceHarness.Create()
      .AddService("doomed", svc => svc
        .WithCatalog<ProbeCatalog>()
        .Configure(services => {
          services.AddSingleton(_ => spy);
          services.AddSingleton<ITransport>(sp => {
            _ = sp.GetRequiredService<DisposalSpy>();
            throw new InvalidOperationException("probe transport unavailable");
          });
        }));

    await Assert.ThrowsAsync<InvalidOperationException>(async () => await builder.StartAsync());

    await Assert.That(spy.Disposed).IsTrue()
      .Because("the half-built container must be disposed on the failure path, not leaked");
  }

  [Test]
  public async Task ServiceTransport_DelegatesEveryNonReceiveOperationToTheSharedWireAsync(
      CancellationToken cancellationToken) {
    _ensureJsonContext();
    await using var harness = await MultiServiceHarness.Create()
      .AddService("svc-a", svc => svc.WithCatalog<ProbeCatalog>())
      .StartAsync();

    var tap = harness.GetService("svc-a").Provider.GetRequiredService<ITransport>();

    // The service is wired to a per-service seam, not to the wire itself — that is what makes a
    // per-consumer delivery fault possible at all.
    await Assert.That(tap).IsNotSameReferenceAs(harness.Wire);

    // Everything the seam is not there to change has to read through to the wire. A seam that
    // answered for itself would let a service see capabilities or readiness the wire it is
    // actually on does not have.
    await Assert.That(tap.IsInitialized).IsEqualTo(harness.Wire.IsInitialized);
    await Assert.That(tap.Capabilities).IsEqualTo(harness.Wire.Capabilities);
    await tap.InitializeAsync(cancellationToken);

    var envelope = _probeEnvelope(1);
    var ex = await Assert.ThrowsAsync<NotSupportedException>(async () =>
      await tap.SendAsync<WireProbeEvent, WireProbeEvent>(
        envelope, new TransportDestination(MultiServiceHarnessDefaults.SHARED_TOPIC), cancellationToken));
    await Assert.That(ex!.Message).Contains("Request/response");
  }

  [Test]
  public async Task SuppressDeliveries_DropsTheNamedServicesDeliveries_ButNotItsPublishesAsync(
      CancellationToken cancellationToken) {
    _ensureJsonContext();
    await using var harness = await MultiServiceHarness.Create()
      .AddService("svc-a", svc => svc.WithCatalog<ProbeCatalog>())
      .AddService("svc-b", svc => svc.WithCatalog<ProbeCatalog>())
      .StartAsync();

    var tapA = harness.GetService("svc-a").Provider.GetRequiredService<ITransport>();

    // The outage this simulates is per-consumer: the broker delivered, one service never got it.
    // So the faulted service's own PUBLISH must still reach the wire and every other subscriber.
    using (harness.SuppressDeliveries("svc-a")) {
      await tapA.PublishAsync(
        _probeEnvelope(7),
        new TransportDestination(MultiServiceHarnessDefaults.SHARED_TOPIC),
        _wireEnvelopeType,
        cancellationToken: cancellationToken);
    }

    var storedByB = await harness.GetService("svc-b").Inbox.WaitForInboxAsync(1, TimeSpan.FromSeconds(10));
    await Assert.That(storedByB).Count().IsEqualTo(1)
      .Because("the fault is scoped to svc-a's receive seam; publishing through it must be untouched");
    await Assert.That(harness.GetService("svc-a").Inbox.StoredInboxMessages).IsEmpty()
      .Because("delivery is synchronous with publish here, so the suppressed service has already "
             + "missed this message by the time the publish returns");
  }

  private static readonly string _wireEnvelopeType =
    $"Whizbang.Core.Messaging.MessageEnvelope`1[[{TypeNameFormatter.AssemblyQualifiedName(typeof(WireProbeEvent))}]], Whizbang.Core";

  private static MessageEnvelope<WireProbeEvent> _probeEnvelope(int x) => new() {
    MessageId = new MessageId(TrackedGuid.NewMedo()),
    Payload = new WireProbeEvent { X = x },
    Hops = [
      new MessageHop {
        Type = HopType.Current,
        Timestamp = DateTimeOffset.UtcNow,
        ServiceInstance = ServiceInstanceInfo.Unknown
      }
    ],
    DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
  };
}
