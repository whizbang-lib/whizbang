// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Reflection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;

namespace Whizbang.Core.Tests;

/// <summary>
/// Branch coverage for the display-name fallbacks in <see cref="TypeNameFormatter"/>: a type with no
/// full name (a generic parameter) displays as its simple name, and an assembly with no full display
/// name falls back to its simple name, then to an empty string. These feed log and exception text,
/// so each fallback must yield readable text rather than null.
/// </summary>
/// <code-under-test>src/Whizbang.Core/TypeNameFormatter.cs</code-under-test>
public class TypeNameFormatterBranchCoverageTests {
  /// <summary>An assembly with no full display name, identified only by the <see cref="AssemblyName"/> given.</summary>
  private sealed class NamelessAssembly(AssemblyName name) : Assembly {
    public override string? FullName => null;
    public override AssemblyName GetName() => name;
    public override AssemblyName GetName(bool copiedName) => name;
  }

  [Test]
  public async Task DisplayName_TypeWithNoFullName_FallsBackToSimpleNameAsync() {
    var genericParameter = typeof(List<>).GetGenericArguments()[0];

    await Assert.That(genericParameter.FullName).IsNull()
      .Because("the setup must present a type with no full name");

    await Assert.That(TypeNameFormatter.DisplayName(genericParameter)).IsEqualTo("T");
  }

  /// <summary>
  /// A registry walk names every type that has a CLR name, in order, and skips one that has none
  /// rather than forwarding a null key to the table lookup.
  /// </summary>
  [Test]
  public async Task ClrTypeNamesOf_SkipsATypeWithNoFullNameAsync() {
    var genericParameter = typeof(List<>).GetGenericArguments()[0];

    var names = TypeNameFormatter.ClrTypeNamesOf([typeof(string), genericParameter, typeof(int)]).ToList();

    await Assert.That(names).IsEquivalentTo(["System.String", "System.Int32"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
  }

  /// <summary>
  /// A type matches a CLR name only when it has exactly that name; a type with no full name matches
  /// nothing, not even a name its simple name would suggest.
  /// </summary>
  [Test]
  public async Task HasClrTypeName_MatchesOnlyTheExactFullNameAsync() {
    var genericParameter = typeof(List<>).GetGenericArguments()[0];

    await Assert.That(TypeNameFormatter.HasClrTypeName(typeof(string), "System.String")).IsTrue();
    await Assert.That(TypeNameFormatter.HasClrTypeName(typeof(string), "System.string")).IsFalse()
      .Because("the comparison is ordinal");
    await Assert.That(TypeNameFormatter.HasClrTypeName(genericParameter, "T")).IsFalse()
      .Because("a type with no full name has no CLR name to match");
  }

  [Test]
  public async Task AssemblyQualifiedNameOrDisplay_ClosedType_IsTheAssemblyQualifiedNameAsync() {
    var name = TypeNameFormatter.AssemblyQualifiedNameOrDisplay(typeof(List<int>));

    await Assert.That(name).IsEqualTo(TypeNameFormatter.AssemblyQualifiedName(typeof(List<int>)));
  }

  [Test]
  public async Task AssemblyQualifiedNameOrDisplay_TypeWithNoAssemblyQualifiedName_IsItsDisplayNameAsync() {
    var genericParameter = typeof(List<>).GetGenericArguments()[0];

    await Assert.That(TypeNameFormatter.AssemblyQualifiedNameOrNull(genericParameter)).IsNull()
      .Because("the setup must present a type with no assembly-qualified name");

    await Assert.That(TypeNameFormatter.AssemblyQualifiedNameOrDisplay(genericParameter)).IsEqualTo("T");
  }

  [Test]
  public async Task AssemblyDisplayName_NoFullName_FallsBackToSimpleNameAsync() {
    var assembly = new NamelessAssembly(new AssemblyName("Probe.Assembly"));

    await Assert.That(TypeNameFormatter.AssemblyDisplayName(assembly)).IsEqualTo("Probe.Assembly");
  }

  [Test]
  public async Task AssemblyDisplayName_NoFullNameAndNoSimpleName_IsEmptyAsync() {
    var assembly = new NamelessAssembly(new AssemblyName());

    await Assert.That(TypeNameFormatter.AssemblyDisplayName(assembly)).IsEqualTo(string.Empty)
      .Because("display text is never null, even for an assembly that has no name at all");
  }

  [Test]
  public async Task AssemblyQualifiedName_OpenParameter_ThrowsAsync() {
    var genericParameter = typeof(List<>).GetGenericArguments()[0];

    await Assert.That(() => TypeNameFormatter.AssemblyQualifiedName(genericParameter))
      .ThrowsExactly<InvalidOperationException>().WithMessageContaining("assembly-qualified");
  }

  [Test]
  public async Task Format_OpenParameter_ThrowsAsync() {
    var genericParameter = typeof(List<>).GetGenericArguments()[0];

    await Assert.That(() => TypeNameFormatter.Format(genericParameter))
      .ThrowsExactly<InvalidOperationException>().WithMessageContaining("FullName");
  }

  [Test]
  public async Task Format_NamelessAssembly_ThrowsAsync() {
    var type = new InAssembly(typeof(string), new NamelessAssembly(new AssemblyName()));

    await Assert.That(() => TypeNameFormatter.Format(type))
      .ThrowsExactly<InvalidOperationException>().WithMessageContaining("Assembly.GetName().Name");
  }

  /// <summary>A real type reported as living in another assembly.</summary>
  private sealed class InAssembly(Type type, Assembly assembly) : TypeDelegator(type) {
    public override Assembly Assembly => assembly;
  }
}
