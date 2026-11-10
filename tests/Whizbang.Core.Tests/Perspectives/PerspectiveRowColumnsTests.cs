// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Perspectives;

namespace Whizbang.Core.Tests.Perspectives;

/// <summary>
/// The well-known names of a perspective row's system columns, which <see cref="PerspectiveIndexAttribute"/> accepts
/// beside model property names (#2087).
/// </summary>
/// <code-under-test>src/Whizbang.Core/Perspectives/PerspectiveRowColumns.cs</code-under-test>
public class PerspectiveRowColumnsTests {
  /// <summary>
  /// No name can be a C# property name, so a model's own <c>UpdatedAt</c> property and the row's <c>updated_at</c>
  /// column are never confused: <c>nameof</c> names the property, the constant names the column.
  /// </summary>
  [Test]
  public async Task NoName_CanBeAPropertyNameAsync() {
    foreach (var name in new[] { PerspectiveRowColumns.UpdatedAt, PerspectiveRowColumns.CreatedAt }) {
      await Assert.That(name).StartsWith("$")
        .Because("an identifier cannot start with '$', so no model property can carry the name");
    }
  }

  /// <summary>Each name is the column it stands for, behind the marker.</summary>
  [Test]
  public async Task EachName_IsItsColumnAsync() {
    await Assert.That(PerspectiveRowColumns.UpdatedAt).IsEqualTo("$updated_at");
    await Assert.That(PerspectiveRowColumns.CreatedAt).IsEqualTo("$created_at");
  }
}
