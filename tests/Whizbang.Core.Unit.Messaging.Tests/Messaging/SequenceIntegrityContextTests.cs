// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// The ambient sequence integrity scope, the counterpart of the ambient priority: a setting entered here applies to every
/// dispatch inside the scope that names none itself, a nested scope replaces it until disposed, and disposing restores
/// what was there before.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Messaging/SequenceIntegrityContext.cs</code-under-test>
[Category("Unit")]
public class SequenceIntegrityContextTests {
  [Test]
  public async Task OutsideAnyScope_NothingIsSetAsync() {
    await Assert.That(SequenceIntegrityContext.CurrentMode).IsNull();
    await Assert.That(SequenceIntegrityContext.CurrentHoldTime).IsNull();
  }

  [Test]
  public async Task Enter_SetsTheModeAndHoldTime_AndDisposingClearsThemAsync() {
    using (SequenceIntegrityContext.Enter(SequenceIntegrity.Bounded, TimeSpan.FromMilliseconds(250))) {
      await Assert.That(SequenceIntegrityContext.CurrentMode).IsEqualTo(SequenceIntegrity.Bounded);
      await Assert.That(SequenceIntegrityContext.CurrentHoldTime).IsEqualTo(TimeSpan.FromMilliseconds(250));
    }

    await Assert.That(SequenceIntegrityContext.CurrentMode).IsNull();
    await Assert.That(SequenceIntegrityContext.CurrentHoldTime).IsNull();
  }

  [Test]
  public async Task Enter_WithNoHoldTime_NamesNoneAsync() {
    using (SequenceIntegrityContext.Enter(SequenceIntegrity.None)) {
      await Assert.That(SequenceIntegrityContext.CurrentMode).IsEqualTo(SequenceIntegrity.None);
      await Assert.That(SequenceIntegrityContext.CurrentHoldTime).IsNull()
        .Because("a scope that names no hold time leaves it to the type and the configuration");
    }
  }

  [Test]
  public async Task NestedScope_ReplacesTheOuterOne_AndDisposingRestoresItAsync() {
    using (SequenceIntegrityContext.Enter(SequenceIntegrity.UntilStamped)) {
      using (SequenceIntegrityContext.Enter(SequenceIntegrity.None)) {
        await Assert.That(SequenceIntegrityContext.CurrentMode).IsEqualTo(SequenceIntegrity.None);
      }

      await Assert.That(SequenceIntegrityContext.CurrentMode).IsEqualTo(SequenceIntegrity.UntilStamped)
        .Because("disposing the inner scope restores the outer one, as a nested priority scope does");
    }
  }

  [Test]
  public async Task NestedScope_WithNoHoldTime_KeepsTheOuterHoldTimeAsync() {
    using (SequenceIntegrityContext.Enter(SequenceIntegrity.Bounded, TimeSpan.FromSeconds(5))) {
      using (SequenceIntegrityContext.Enter(SequenceIntegrity.Bounded)) {
        await Assert.That(SequenceIntegrityContext.CurrentHoldTime).IsEqualTo(TimeSpan.FromSeconds(5))
          .Because("the hold time comes from the most specific level that names one, and the outer scope is such a level");
      }
      using (SequenceIntegrityContext.Enter(SequenceIntegrity.Bounded, TimeSpan.FromMilliseconds(40))) {
        await Assert.That(SequenceIntegrityContext.CurrentHoldTime).IsEqualTo(TimeSpan.FromMilliseconds(40));
      }
    }
  }

  [Test]
  public async Task Enter_ANegativeHoldTime_IsRefusedAsync() {
    await Assert.That(() => SequenceIntegrityContext.Enter(SequenceIntegrity.Bounded, TimeSpan.FromMilliseconds(-1)))
      .ThrowsExactly<ArgumentOutOfRangeException>();
    await Assert.That(SequenceIntegrityContext.CurrentMode).IsNull()
      .Because("a refused scope sets nothing");
  }

  [Test]
  public async Task TheScope_FlowsIntoWorkItStartsAsync() {
    SequenceIntegrity? seen;
    using (SequenceIntegrityContext.Enter(SequenceIntegrity.None)) {
      seen = await _readAfterAYieldAsync();
    }

    await Assert.That(seen).IsEqualTo(SequenceIntegrity.None)
      .Because("the setting flows with the async context, so an awaited handler and what it dispatches see it");
  }

  private static async Task<SequenceIntegrity?> _readAfterAYieldAsync() {
    await Task.Yield();
    return SequenceIntegrityContext.CurrentMode;
  }
}
