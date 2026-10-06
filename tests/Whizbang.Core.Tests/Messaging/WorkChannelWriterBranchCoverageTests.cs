// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Branch coverage for <see cref="WorkChannelWriter.SignalNewPerspectiveWorkAvailable"/>: the
/// signal reaches a subscribed listener, and with nobody subscribed (a host with no perspective
/// worker) signalling is a silent no-op rather than a null dereference on the producer's path.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Messaging/WorkChannelWriter.cs</code-under-test>
public class WorkChannelWriterBranchCoverageTests {

  [Test]
  public async Task SignalNewPerspectiveWorkAvailable_ReachesSubscribersAndIsSilentWithoutThemAsync() {
    var writer = new WorkChannelWriter();
    var signals = 0;
    void OnSignal() => Interlocked.Increment(ref signals);

    writer.SignalNewPerspectiveWorkAvailable();
    writer.OnNewPerspectiveWorkAvailable += OnSignal;
    writer.SignalNewPerspectiveWorkAvailable();
    writer.OnNewPerspectiveWorkAvailable -= OnSignal;
    writer.SignalNewPerspectiveWorkAvailable();

    await Assert.That(signals).IsEqualTo(1)
      .Because("only the signal raised while the listener was subscribed reaches it; the ones raised with no "
        + "listener go nowhere and do not fail");
  }
}
