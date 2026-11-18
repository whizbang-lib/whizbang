// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Reflection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Transports;

namespace Whizbang.Core.Tests.Transports;

/// <summary>
/// Branch coverage for <see cref="NamespacePattern.Matches"/>'s empty-name guard. A type reporting an
/// empty (not null) full name, which a reflection-only or delegating <see cref="Type"/> can do, must
/// be refused up front: a catch-all pattern compiles to <c>^.*$</c>, which matches the empty string,
/// so without the guard a nameless type would be swept into every wildcard subscription.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Transports/NamespacePattern.cs</code-under-test>
public class NamespacePatternBranchCoverageTests {
  private sealed class EmptyFullNameType() : TypeDelegator(typeof(NamespacePatternBranchCoverageTests)) {
    public override string FullName => string.Empty;
  }

  [Test]
  public async Task Matches_TypeWithEmptyFullName_ReturnsFalseEvenForACatchAllPatternAsync() {
    var pattern = new NamespacePattern("*");
    var nameless = new EmptyFullNameType();

    await Assert.That(nameless.FullName).IsEmpty()
      .Because("the setup must actually present an empty, non-null full name");

    await Assert.That(pattern.Matches(nameless)).IsFalse()
      .Because("a type with no name is never a routable message type, even under a catch-all pattern");
  }
}
