// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Perspectives;

namespace Whizbang.Core.Tests.Perspectives;

/// <summary>
/// The facts an <c>Apply</c> reads about the event it is applying (#1878). The runner sets them around each
/// <c>Apply</c>; a unit test of an <c>Apply</c> sets them the same way.
/// </summary>
/// <docs>fundamentals/perspectives/perspectives-with-actions#no-row-contract</docs>
public class ApplyContextTests {
  [Test]
  public async Task OutsideAnApply_NoEventIsTheFirstInItsStreamAsync() {
    await Assert.That(ApplyContext.Current.IsFirstEventInStream).IsFalse()
      .Because("with no Apply running there is no event, so nothing is the first of a stream");
  }

  [Test]
  public async Task Enter_MakesTheFactCurrentUntilTheScopeIsDisposedAsync() {
    bool during;
    using (ApplyContext.Enter(isFirstEventInStream: true)) {
      during = ApplyContext.Current.IsFirstEventInStream;
    }

    await Assert.That(during).IsTrue();
    await Assert.That(ApplyContext.Current.IsFirstEventInStream).IsFalse()
      .Because("disposing the scope restores the facts that were current before it");
  }

  [Test]
  public async Task ANestedScope_RestoresTheEnclosingFactOnDisposeAsync() {
    // No await inside a scope: the facts belong to the thread the Apply runs on, and an Apply is synchronous.
    bool inner;
    bool afterInner;
    using (ApplyContext.Enter(isFirstEventInStream: true)) {
      using (ApplyContext.Enter(isFirstEventInStream: false)) {
        inner = ApplyContext.Current.IsFirstEventInStream;
      }
      afterInner = ApplyContext.Current.IsFirstEventInStream;
    }

    await Assert.That(inner).IsFalse();
    await Assert.That(afterInner).IsTrue()
      .Because("an inner scope restores the outer one's fact, not a default");
  }

  [Test]
  public async Task ADefaultScope_DisposesToNotFirstAsync() {
    bool afterDefault;
    using (ApplyContext.Enter(isFirstEventInStream: true)) {
      default(ApplyContext.Scope).Dispose();
      afterDefault = ApplyContext.Current.IsFirstEventInStream;
    }

    await Assert.That(afterDefault).IsFalse()
      .Because("a scope that never entered anything restores the empty context");
  }
}
