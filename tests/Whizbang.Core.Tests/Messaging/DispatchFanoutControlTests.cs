#pragma warning disable CA1707

using System.Collections.Generic;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Minting;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Locks the ambient fan-out control (<see cref="DispatchFanoutControl"/>): a pre-fanout receptor sets
/// a <see cref="FanoutDirective"/> via the static <c>Set</c>; the dispatch worker reads it after the
/// invocation. Off the pre-fanout window, <c>Set</c> is a no-op (no control open).
/// </summary>
[Category("Messaging")]
public class DispatchFanoutControlTests {

  private sealed record _inner(string Id) : IMessage;

  [Test]
  public async Task Set_OutsideControlWindow_IsNoOp_DoesNotLeakIntoNextWindowAsync() {
    // A stray Set with no composite in flight is harmless and must not leak into a later window.
    DispatchFanoutControl.Set(FanoutDirective.Skip);

    using var scope = DispatchFanoutControl.Begin();
    await Assert.That(scope.Control.Directive).IsNull()
      .Because("A Set fired outside any control window is a no-op — the next window starts clean.");
  }

  [Test]
  public async Task Begin_CapturesDirectiveSetDuringWindowAsync() {
    using var scope = DispatchFanoutControl.Begin();
    await Assert.That(scope.Control.Directive).IsNull()
      .Because("No directive until a receptor sets one.");

    DispatchFanoutControl.Set(FanoutDirective.Skip);

    await Assert.That(scope.Control.Directive).IsNotNull();
    await Assert.That(scope.Control.Directive!.Kind).IsEqualTo(FanoutDirectiveKind.Skip);
  }

  [Test]
  public async Task ReplaceWith_CarriesReplacementChildrenAsync() {
    using var scope = DispatchFanoutControl.Begin();
    var replacement = new List<IMessage> { new _inner("a"), new _inner("b") };

    DispatchFanoutControl.Set(FanoutDirective.ReplaceWith(replacement));

    await Assert.That(scope.Control.Directive!.Kind).IsEqualTo(FanoutDirectiveKind.ReplaceWith);
    await Assert.That(scope.Control.Directive!.Replacement!.Count).IsEqualTo(2);
  }

  [Test]
  public async Task Begin_NestsAndRestoresOuterControlAsync() {
    using var outer = DispatchFanoutControl.Begin();
    using (DispatchFanoutControl.Begin()) {
      DispatchFanoutControl.Set(FanoutDirective.Skip);
    }
    // Disposing the inner scope restores the outer control, which never had a directive set.
    await Assert.That(outer.Control.Directive).IsNull()
      .Because("The inner Set targeted the inner control; restoring the outer leaves it untouched.");
  }
}
