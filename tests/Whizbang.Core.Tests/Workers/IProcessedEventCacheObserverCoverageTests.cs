using System;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Tail-of-round coverage for <see cref="NullProcessedEventCacheObserver"/>'s
/// <c>OnEventsRemoved</c> no-op — the default observer <see cref="ProcessedEventCache"/> uses
/// when a host has not registered a custom one. <c>Remove</c>/<c>RemoveRange</c> call this on
/// every rewind-replay eviction, so if the default ever stopped being a true no-op (e.g. picked
/// up an accidental side effect), every host without a custom observer would be affected silently.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Workers/IProcessedEventCacheObserver.cs</code-under-test>
public class IProcessedEventCacheObserverCoverageTests {

  [Test]
  public async Task NullProcessedEventCacheObserver_OnEventsRemoved_IsATrueNoOpAsync() {
    var eventIds = new[] { Guid.NewGuid(), Guid.NewGuid() };

    await Assert.That(() => NullProcessedEventCacheObserver.Instance.OnEventsRemoved(eventIds))
      .ThrowsNothing()
      .Because("the default observer backs every host that has not registered a custom one — a "
             + "throw here would surface as a rewind-replay failure with no relation to the "
             + "eviction it was supposed to silently ignore");
  }
}
