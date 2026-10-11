// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Which registered perspectives a drained event feeds. A stream can hold events of a type this service
/// registers no perspective for (another service's perspectives queued them, or the registration was
/// removed); such an event feeds nothing here, rather than failing the stream's drain.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Workers/PerspectiveWorker.cs</code-under-test>
public class PerspectiveWorkerPerspectivesForTests {
  private static readonly Dictionary<string, IReadOnlyList<string>> _registered = new() {
    ["Shop.OrderPlaced, Shop"] = ["Shop.OrderSummary", "Shop.CustomerHistory"],
  };

  [Test]
  public async Task ARegisteredEventType_FeedsEveryPerspectiveRegisteredForItAsync() {
    var perspectives = PerspectiveWorker.PerspectivesFor(_registered, "Shop.OrderPlaced, Shop");

    await Assert.That(perspectives).IsEquivalentTo(["Shop.OrderSummary", "Shop.CustomerHistory"]);
  }

  [Test]
  public async Task AnEventTypeNoPerspectiveTakes_FeedsNoneAsync() {
    var perspectives = PerspectiveWorker.PerspectivesFor(_registered, "Shop.OrderShipped, Shop");

    await Assert.That(perspectives).IsEmpty()
      .Because("an event no registered perspective takes must feed nothing, not fail the drain");
  }
}
