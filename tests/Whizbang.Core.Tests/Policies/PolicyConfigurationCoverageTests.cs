using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Policies;

namespace Whizbang.Core.Tests.Policies;

/// <summary>
/// Targeted coverage for <see cref="PolicyConfiguration.WithPersistenceSize"/>'s guard clause —
/// the sibling of the zero/negative checks <c>WithPartitions</c> and <c>WithConcurrency</c> already
/// lock in <c>Whizbang.Policies.Tests/PolicyConfigurationExtensionsTests.cs</c>, but never asserted
/// for this method.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Policies/PolicyConfiguration.cs</code-under-test>
public class PolicyConfigurationCoverageTests {

  [Test]
  public async Task WithPersistenceSize_ZeroMaxDataSizeBytes_ThrowsAsync() {
    // MaxDataSizeBytes gates the JsonbSizeValidator threshold check. A silently-accepted zero would
    // mean "every payload, no matter how small, is treated as oversized" — every message would trip
    // the size-warning/throw path, which is not a configuration anyone actually wants; it must be
    // rejected loudly at configuration time instead of misbehaving at persistence time.
    var config = new PolicyConfiguration();

    await Assert.That(() => config.WithPersistenceSize(maxDataSizeBytes: 0))
      .Throws<ArgumentOutOfRangeException>();
  }

  [Test]
  public async Task WithPersistenceSize_NegativeMaxDataSizeBytes_ThrowsAsync() {
    var config = new PolicyConfiguration();

    await Assert.That(() => config.WithPersistenceSize(maxDataSizeBytes: -1))
      .Throws<ArgumentOutOfRangeException>();
  }
}
