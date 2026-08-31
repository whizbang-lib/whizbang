using Microsoft.AspNetCore.Http;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Hosting.AspNet.Tests;

/// <summary>
/// Unit tests for <see cref="WhizbangAvailabilityExemptions"/> — the prefix list the
/// availability gate consults to let probe traffic through while the app is unavailable.
/// </summary>
public class WhizbangAvailabilityExemptionsTests {

  [Test]
  public async Task Add_ThenIsExempt_MatchesTheRegisteredPrefixAsync() {
    var exemptions = new WhizbangAvailabilityExemptions();
    exemptions.Add("/health");

    await Assert.That(exemptions.IsExempt(new PathString("/health"))).IsTrue();
  }

  [Test]
  public async Task IsExempt_MatchesNestedSegmentsUnderThePrefixAsync() {
    var exemptions = new WhizbangAvailabilityExemptions();
    exemptions.Add("/health");

    await Assert.That(exemptions.IsExempt(new PathString("/health/ready"))).IsTrue();
  }

  [Test]
  public async Task IsExempt_IsCaseInsensitiveAsync() {
    var exemptions = new WhizbangAvailabilityExemptions();
    exemptions.Add("/health");

    await Assert.That(exemptions.IsExempt(new PathString("/HEALTH"))).IsTrue();
  }

  [Test]
  public async Task IsExempt_UnregisteredPath_IsNotExemptAsync() {
    var exemptions = new WhizbangAvailabilityExemptions();
    exemptions.Add("/health");

    await Assert.That(exemptions.IsExempt(new PathString("/orders"))).IsFalse();
  }

  [Test]
  public async Task IsExempt_WithNothingRegistered_IsAlwaysFalseAsync() {
    var exemptions = new WhizbangAvailabilityExemptions();

    await Assert.That(exemptions.IsExempt(new PathString("/health"))).IsFalse();
  }

  [Test]
  public async Task Add_SamePrefixTwice_IsIdempotentAsync() {
    // Registration is additive and callers may wire the same probe path from more than
    // one place, so a repeat has to be dropped rather than grow the array unbounded.
    var exemptions = new WhizbangAvailabilityExemptions();

    exemptions.Add("/health");
    exemptions.Add("/health");

    await Assert.That(exemptions.IsExempt(new PathString("/health"))).IsTrue();
  }

  [Test]
  public async Task Add_DistinctPrefixes_AreAllHonouredAsync() {
    var exemptions = new WhizbangAvailabilityExemptions();

    exemptions.Add("/health");
    exemptions.Add("/metrics");
    exemptions.Add("/health");

    await Assert.That(exemptions.IsExempt(new PathString("/health"))).IsTrue();
    await Assert.That(exemptions.IsExempt(new PathString("/metrics"))).IsTrue();
    await Assert.That(exemptions.IsExempt(new PathString("/other"))).IsFalse();
  }

  [Test]
  public async Task Add_EmptyPrefix_ThrowsArgumentExceptionAsync() {
    var exemptions = new WhizbangAvailabilityExemptions();

    await Assert.That(() => exemptions.Add(string.Empty)).ThrowsExactly<ArgumentException>();
  }

  [Test]
  public async Task Add_NullPrefix_ThrowsArgumentNullExceptionAsync() {
    var exemptions = new WhizbangAvailabilityExemptions();

    await Assert.That(() => exemptions.Add(null!)).ThrowsExactly<ArgumentNullException>();
  }
}
