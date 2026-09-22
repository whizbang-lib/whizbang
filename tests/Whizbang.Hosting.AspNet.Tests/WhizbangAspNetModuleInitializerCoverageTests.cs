using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;

namespace Whizbang.Hosting.AspNet.Tests;

/// <summary>
/// Coverage for <see cref="WhizbangAspNetModuleInitializer.Register"/> — a
/// <c>[ModuleInitializer]</c> method the CLR invokes once, automatically, the moment this
/// assembly is loaded. Every other test in this project already forces that load by referencing
/// a type from <c>Whizbang.Hosting.AspNet</c>, so by the time any test method runs, the
/// initializer has necessarily already run. This test asserts its actual effect rather than
/// merely observing that the callback is non-null: the consumer-side contract
/// (<c>Whizbang.Core.Tests.WhizbangHostingIntegrationTests</c>) locks that
/// <c>AddWhizbang()</c> invokes <c>ServiceRegistrationCallbacks.HostingIntegration</c> by
/// default, but that suite never references this assembly and so can only fake the callback —
/// it cannot prove the REAL module initializer wired it to the real
/// <see cref="ServiceCollectionExtensions.AddWhizbangAspNet"/>. This is that proof.
/// </summary>
public class WhizbangAspNetModuleInitializerCoverageTests {

  // If the module initializer's wiring ever broke -- pointed at the wrong method, or silently
  // no-op'd -- a host calling AddWhizbang() would never get the startup filters, correlation
  // capture, or hardened security headers this assembly exists to turnkey. No exception, no
  // diagnostic: requests would simply run without them, and nobody would know until an incident
  // asked why a header or a filter that "should always be there" wasn't.
  [Test]
  public async Task HostingIntegrationCallback_IsWiredToAddWhizbangAspNetAsync() {
    var callback = ServiceRegistrationCallbacks.HostingIntegration;

    await Assert.That(callback).IsNotNull()
      .Because("this assembly's module initializer must have already run by the time any of its types are touched, and it sets this callback unconditionally");

    var services = new ServiceCollection();
    callback!(services);

    var descriptor = services.FirstOrDefault(d =>
      d.ServiceType == typeof(IStartupFilter) &&
      d.ImplementationType == typeof(WhizbangFlushStartupFilter));

    await Assert.That(descriptor).IsNotNull()
      .Because("the callback must be the real AddWhizbangAspNet -- its flush startup filter registration is the observable proof, not just a non-null delegate");
  }
}
