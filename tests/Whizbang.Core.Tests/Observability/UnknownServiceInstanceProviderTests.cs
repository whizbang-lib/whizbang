using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Tests.Observability;

/// <summary>
/// The provider used when a host has not identified itself.
/// <para>
/// Its job is to be a stable, obviously-anonymous answer rather than a null or a throw. Every
/// surface that stamps an instance onto a message, a claim, or a heartbeat reads this when nothing
/// better is registered, so returning null would move the failure to whichever of those touched it
/// first — arbitrarily far from the missing registration that actually caused it.
/// </para>
/// <para>
/// The identity must also be stable across reads. These values end up in claim rows and message
/// hops, so an identity that varied per call would attribute one pod's work to several instances
/// that never existed, and make orphan takeover chase them.
/// </para>
/// </summary>
/// <code-under-test>src/Whizbang.Core/Observability/UnknownServiceInstanceProvider.cs</code-under-test>
public class UnknownServiceInstanceProviderTests {

  [Test]
  public async Task EveryFacet_MatchesTheSharedUnknownIdentityAsync() {
    var provider = UnknownServiceInstanceProvider.Instance;

    await Assert.That(provider.InstanceId).IsEqualTo(ServiceInstanceInfo.Unknown.InstanceId);
    await Assert.That(provider.ServiceName).IsEqualTo(ServiceInstanceInfo.Unknown.ServiceName);
    await Assert.That(provider.HostName).IsEqualTo(ServiceInstanceInfo.Unknown.HostName);
    await Assert.That(provider.ProcessId).IsEqualTo(ServiceInstanceInfo.Unknown.ProcessId);
    await Assert.That(provider.ToInfo()).IsEqualTo(ServiceInstanceInfo.Unknown)
      .Because("the composed form and the individual facets must agree, or a row written through "
             + "one and read through the other describes two different instances");
  }

  [Test]
  public async Task TheIdentity_IsStableAcrossReadsAsync() {
    var provider = UnknownServiceInstanceProvider.Instance;

    await Assert.That(provider.InstanceId).IsEqualTo(provider.InstanceId)
      .Because("these values land in claim rows and message hops; an identity that varied per read "
             + "would attribute one pod's work to instances that never existed");
    await Assert.That(UnknownServiceInstanceProvider.Instance).IsSameReferenceAs(provider)
      .Because("the shared instance is the point — the value is immutable, so every caller may "
             + "read the same one");
  }
}
