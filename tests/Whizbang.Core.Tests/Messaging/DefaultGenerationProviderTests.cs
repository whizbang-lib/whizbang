using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// v0.502 — regression locks for <see cref="DefaultGenerationProvider"/>. The provider
/// captures the Whizbang.Core assembly's informational version once at construction and
/// returns it on every call. The DLQ system relies on this for "we shipped a fix" auto-
/// replay; if the value churns mid-process the replay would re-attempt rows on every call.
/// </summary>
public class DefaultGenerationProviderTests {

  [Test]
  public async Task GetGeneration_ReturnsNonEmptyStringAsync() {
    var provider = new DefaultGenerationProvider();
    var gen = provider.GetGeneration();
    await Assert.That(gen).IsNotNull();
    await Assert.That(gen).IsNotEmpty();
  }

  [Test]
  public async Task GetGeneration_StartsWithWhizbangPrefixAsync() {
    var provider = new DefaultGenerationProvider();
    var gen = provider.GetGeneration();
    // The prefix is the contract operators grep for in logs and DLQ rows; locking it.
    await Assert.That(gen).StartsWith("whizbang/");
  }

  [Test]
  public async Task GetGeneration_StableAcrossMultipleCallsAsync() {
    var provider = new DefaultGenerationProvider();
    var first = provider.GetGeneration();
    var second = provider.GetGeneration();
    var third = provider.GetGeneration();
    await Assert.That(second).IsEqualTo(first);
    await Assert.That(third).IsEqualTo(first);
  }

  [Test]
  public async Task GetGeneration_StableAcrossInstancesAsync() {
    // Two instances against the same assembly should produce the same generation string —
    // both lookups read the same AssemblyInformationalVersionAttribute.
    var first = new DefaultGenerationProvider().GetGeneration();
    var second = new DefaultGenerationProvider().GetGeneration();
    await Assert.That(second).IsEqualTo(first);
  }

  [Test]
  public async Task GetGeneration_DoesNotEndWithUnknownAsync() {
    // The "unknown" fallback only fires when both InformationalVersion AND assembly Version
    // are absent. The build pipeline always provides at least one, so this is a smoke check
    // that prod builds don't silently lose their generation tag.
    var provider = new DefaultGenerationProvider();
    var gen = provider.GetGeneration();
    await Assert.That(gen).IsNotEqualTo("whizbang/unknown");
  }
}
