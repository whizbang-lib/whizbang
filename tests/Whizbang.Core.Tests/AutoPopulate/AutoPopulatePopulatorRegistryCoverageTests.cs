using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.AutoPopulate;

namespace Whizbang.Core.Tests.AutoPopulate;

/// <summary>
/// Coverage-round-23 targets for <see cref="AutoPopulatePopulatorRegistry"/>: the "no populator
/// recognized this message" fallback for <see cref="AutoPopulatePopulatorRegistry.PopulateQueued"/>
/// and <see cref="AutoPopulatePopulatorRegistry.PopulateDelivered"/> — the same fallback
/// <c>PopulateSent</c> already has a test for, but the other two lifecycle stages did not. If this
/// fallback were wrong (returning null, or throwing, instead of the original message), every message
/// type with no auto-populate populator would either NPE downstream at the queued/delivered lifecycle
/// stage or lose its original instance identity for reference-based logic.
/// </summary>
public class AutoPopulatePopulatorRegistryCoverageTests {
  [Test]
  public async Task PopulateQueued_WithNonMatchingType_ReturnsOriginalMessageAsync() {
    const string message = "not a record any populator recognizes";

    var result = AutoPopulatePopulatorRegistry.PopulateQueued(message, DateTimeOffset.UtcNow);

    await Assert.That(result).IsSameReferenceAs(message)
      .Because("when no registered populator handles the type, the loop must fall through to the "
        + "original message — not null, and not a different instance");
  }

  [Test]
  public async Task PopulateDelivered_WithNonMatchingType_ReturnsOriginalMessageAsync() {
    const string message = "not a record any populator recognizes";

    var result = AutoPopulatePopulatorRegistry.PopulateDelivered(message, DateTimeOffset.UtcNow);

    await Assert.That(result).IsSameReferenceAs(message)
      .Because("when no registered populator handles the type, the loop must fall through to the "
        + "original message — not null, and not a different instance");
  }
}
