// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Linq.Expressions;
using HotChocolate.Types;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Transports.HotChocolate.Types;

namespace Whizbang.Transports.HotChocolate.Ids.Tests.Unit;

/// <summary>
/// The handler of a strong-id filter operation is set on its own field, so the filter convention never chooses it
/// for another one (#2073).
/// </summary>
[Category("Unit")]
[Category("WhizbangId")]
public class WhizbangIdFilterHandlerTests {
  [Test]
  public async Task CanHandle_AnyField_IsFalseAsync() {
    var handler = new WhizbangIdFilterHandler(
      new InputParser(), canBeNull: true, (property, _) => Expression.Constant(true));

    await Assert.That(handler.CanHandle(null!, null!, null!)).IsFalse();
  }
}
