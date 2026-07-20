using System;
using System.Threading.Tasks;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Perspectives;

namespace Whizbang.Core.Tests.Perspectives;

/// <summary>
/// Unit tests for <see cref="FullHistoryPerspectiveRegistry"/> — the generator-populated set of full-history
/// perspective names A1's close guard consults. Uses unique names so the process-wide static set stays
/// independent across tests.
/// </summary>
/// <docs>fundamentals/events/ephemeral-events</docs>
public class FullHistoryPerspectiveRegistryTests {
  [Test]
  public async Task Register_ThenIsFullHistory_TrueForRegistered_FalseOtherwiseAsync() {
    var name = "P_" + Guid.NewGuid().ToString("N");
    await Assert.That(FullHistoryPerspectiveRegistry.IsFullHistory(name)).IsFalse()
      .Because("An unregistered perspective is resumable by default.");

    FullHistoryPerspectiveRegistry.Register(name);

    await Assert.That(FullHistoryPerspectiveRegistry.IsFullHistory(name)).IsTrue();
    await Assert.That(FullHistoryPerspectiveRegistry.IsFullHistory("P_" + Guid.NewGuid().ToString("N"))).IsFalse();
  }

  [Test]
  public async Task AnyFullHistory_TrueWhenSetContainsARegisteredNameAsync() {
    var registered = "P_" + Guid.NewGuid().ToString("N");
    var other = "P_" + Guid.NewGuid().ToString("N");
    FullHistoryPerspectiveRegistry.Register(registered);

    await Assert.That(FullHistoryPerspectiveRegistry.AnyFullHistory([other, registered])).IsTrue();
    await Assert.That(FullHistoryPerspectiveRegistry.AnyFullHistory([other])).IsFalse();
    await Assert.That(FullHistoryPerspectiveRegistry.AnyFullHistory([])).IsFalse();
  }
}
