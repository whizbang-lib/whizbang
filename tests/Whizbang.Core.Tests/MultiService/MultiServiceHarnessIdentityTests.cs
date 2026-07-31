using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Observability;
using Whizbang.Testing.MultiService;

namespace Whizbang.Core.Tests.MultiService;

/// <summary>
/// Locks the per-service identity contract of the multi-service harness: every simulated service
/// gets its OWN <see cref="IServiceInstanceProvider"/> reporting that service's harness name — the
/// seam directed-message targeting and hop attribution key on. Without it, every simulated service
/// would report this test process's entry-assembly name and be indistinguishable on the wire.
/// </summary>
/// <code-under-test>src/Whizbang.Testing/MultiService/MultiServiceHarness.cs</code-under-test>
[Category("MultiService")]
[NotInParallel("WhizbangBackgroundServiceTests")]
public class MultiServiceHarnessIdentityTests {

  public sealed record IdentityProbeEvent {
    public int X { get; init; }
  }

  public sealed class ProbeCatalog : IMessageTypeCatalog {
    public IReadOnlyList<MessageTypeCatalogEntry> GetAll() => [
      new(typeof(IdentityProbeEvent), TypeNameFormatter.FormatClrTypeName(typeof(IdentityProbeEvent)), "event", null),
    ];
  }

  [Test]
  public async Task EachService_ReportsItsOwnHarnessIdentityAsync() {
    await using var harness = await MultiServiceHarness.Create()
      .AddService("svc-a", svc => svc.WithCatalog<ProbeCatalog>())
      .AddService("svc-b", svc => svc.WithCatalog<ProbeCatalog>())
      .StartAsync();

    var a = harness.GetService("svc-a").Provider.GetRequiredService<IServiceInstanceProvider>();
    var b = harness.GetService("svc-b").Provider.GetRequiredService<IServiceInstanceProvider>();

    await Assert.That(a.ServiceName).IsEqualTo("svc-a");
    await Assert.That(b.ServiceName).IsEqualTo("svc-b")
      .Because("identity-aware seams (directed targeting, hop attribution) must see each simulated " +
               "service's own name — not one shared process identity.");
    await Assert.That(a.InstanceId).IsNotEqualTo(b.InstanceId)
      .Because("each service is its own instance.");
    await Assert.That(a.HostName).IsEqualTo("multi-service-harness");
    await Assert.That(a.ProcessId).IsEqualTo(Environment.ProcessId);

    var info = a.ToInfo();
    await Assert.That(info.ServiceName).IsEqualTo("svc-a");
    await Assert.That(info.InstanceId).IsEqualTo(a.InstanceId);
    await Assert.That(info.HostName).IsEqualTo(a.HostName);
    await Assert.That(info.ProcessId).IsEqualTo(a.ProcessId)
      .Because("ToInfo is the hop-attribution shape — it must mirror the provider's identity exactly.");
  }
}
