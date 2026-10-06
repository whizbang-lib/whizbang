// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Core.Tests;

/// <summary>
/// <see cref="ArgumentGuard"/> is the expression form of
/// <see cref="ArgumentNullException.ThrowIfNull(object?, string?)"/>, for the places a statement
/// cannot go (primary-constructor field and property initializers). It must behave exactly like
/// the <c>x ?? throw new ArgumentNullException(nameof(x))</c> it replaces.
/// </summary>
/// <code-under-test>src/Whizbang.Core/ArgumentGuard.cs</code-under-test>
public class ArgumentGuardTests {
  [Test]
  public async Task NotNull_NonNullValue_ReturnsTheSameInstanceAsync() {
    var value = new object();

    await Assert.That(ArgumentGuard.NotNull(value)).IsSameReferenceAs(value);
  }

  [Test]
  public async Task NotNull_NullValue_ThrowsArgumentNullExceptionNamingTheArgumentAsync() {
    string? connectionName = null;

    var thrown = await Assert.That(() => ArgumentGuard.NotNull(connectionName)).ThrowsExactly<ArgumentNullException>();

    await Assert.That(thrown!.ParamName).IsEqualTo(nameof(connectionName))
      .Because("callers and existing tests read ParamName; it must be the argument's own name, as nameof gave");
    // The old `x ?? throw new ArgumentNullException(nameof(x))` form threw exactly this; ParamName is
    // asserted above to be the argument's name.
    var replacedGuard = new ArgumentNullException(thrown.ParamName);
    await Assert.That(thrown.Message).IsEqualTo(replacedGuard.Message)
      .Because("the message must match the exception the guard replaces");
  }
}
