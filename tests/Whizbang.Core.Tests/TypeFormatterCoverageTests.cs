// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;

namespace Whizbang.Core.Tests;

/// <summary>
/// Tail-of-round coverage for <see cref="TypeFormatter"/>: the null/empty guard on
/// <see cref="TypeFormatter.ParseAssemblyName"/>, distinct from its "no comma" guard which is
/// already covered.
/// </summary>
/// <code-under-test>src/Whizbang.Core/TypeFormatter.cs</code-under-test>
public class TypeFormatterCoverageTests {

  /// <summary>
  /// Callers pass strings sourced from <c>Type.AssemblyQualifiedName</c> or a database column that
  /// may be empty (never null, given the signature, but empty is a real value a caller can hand
  /// in). Without this guard an empty input falls through to <c>Split(',')</c>, which still
  /// produces a one-element array and returns the same empty string — but only by accident; the
  /// guard makes "nothing to parse" an explicit, intentional outcome rather than a lucky fallthrough.
  /// </summary>
  [Test]
  public async Task ParseAssemblyName_EmptyInput_ReturnsEmptyStringAsync() {
    var result = TypeFormatter.ParseAssemblyName(string.Empty, stripVersion: true);

    await Assert.That(result).IsEqualTo(string.Empty);
  }

  /// <summary>
  /// A strong-named assembly's token is rendered as lowercase hex, the form runtime type names use;
  /// only an unsigned assembly reads "null".
  /// </summary>
  [Test]
  public async Task FormatType_StrongNamedAssembly_RendersThePublicKeyTokenAsHexAsync() {
    var expected = Convert.ToHexStringLower(typeof(string).Assembly.GetName().GetPublicKeyToken()!);

    var result = TypeFormatter.FormatType(typeof(string), TypeQualifications.Assembly | TypeQualifications.PublicKeyToken);

    await Assert.That(expected.Length).IsEqualTo(16).Because("the precondition: the core library is strong-named");
    await Assert.That(result).EndsWith($", PublicKeyToken={expected}");
  }
}
