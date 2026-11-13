// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Tests;

/// <summary>
/// One wire form of a type name, <c>Namespace.Type, Assembly</c> with no version at any depth (#2266, #2267): from a type,
/// <see cref="TypeNameFormatter.Format"/>; from any rendering of its name, <see cref="EventTypeMatchingHelper.NormalizeTypeName"/>
/// (which the SQL normalizer matches byte for byte), and <see cref="TypeNameFormatter.Parse"/> delegates to it. The two
/// sides agree for simple, nested and generic types, type arguments from other assemblies included, whichever build
/// rendered the name.
/// </summary>
/// <code-under-test>src/Whizbang.Core/TypeNameFormatter.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Messaging/EventTypeMatchingHelper.cs</code-under-test>
/// <docs>operations/infrastructure/migrations#normalizing-type-names</docs>
public partial class TypeNameWireFormTests {
  /// <summary>A type nested in another, whose CLR name carries a '+'.</summary>
  public sealed record Nested(string Id);

  /// <summary>A generic type whose arguments carry assembly-qualified names of their own.</summary>
  public sealed record Box<T>(T Value);

  private const string OTHER_BUILD = ", Version=9.8.7.6, Culture=neutral, PublicKeyToken=0123456789abcdef";

  private static IEnumerable<Type> _shapes() => [
    typeof(string),
    typeof(TypeNameWireFormTests),
    typeof(Nested),
    typeof(Box<string>),                                    // an argument from the core library
    typeof(Box<Nested>),                                    // an argument from this assembly, nested
    typeof(Box<Box<Nested>>),                               // a nested generic argument
    typeof(Dictionary<Nested, List<Box<int>>>),             // an outer type from the core library, arguments from both
  ];

  [System.Text.RegularExpressions.GeneratedRegex(@", Version=[^,\]]+, Culture=[^,\]]+, PublicKeyToken=[^,\]]+")]
  private static partial System.Text.RegularExpressions.Regex _decoration();

  private static IEnumerable<string> _renderingsOf(Type type) {
    var versioned = TypeNameFormatter.AssemblyQualifiedName(type);
    yield return versioned;
    yield return _decoration().Replace(versioned, OTHER_BUILD);
    yield return TypeNameFormatter.Format(type);
  }

  [Test]
  public async Task Format_CarriesNoVersionAtAnyDepthAsync() {
    foreach (var type in _shapes()) {
      await Assert.That(TypeNameFormatter.Format(type).Contains("Version=", StringComparison.Ordinal)).IsFalse()
        .Because($"{type}: the wire form is version-free, type arguments included ({TypeNameFormatter.Format(type)})");
    }
  }

  [Test]
  public async Task Format_IsWhatTheStringNormalizerMakesOfEveryRenderingAsync() {
    foreach (var type in _shapes()) {
      foreach (var rendering in _renderingsOf(type)) {
        await Assert.That(EventTypeMatchingHelper.NormalizeTypeName(rendering)).IsEqualTo(TypeNameFormatter.Format(type))
          .Because($"{type}: '{rendering}' names the same type");
      }
    }
  }

  [Test]
  public async Task Parse_DelegatesToTheNormalizer_ForEveryRenderingAsync() {
    foreach (var type in _shapes()) {
      foreach (var rendering in _renderingsOf(type)) {
        await Assert.That(TypeNameFormatter.Parse(rendering)).IsEqualTo(TypeNameFormatter.Format(type))
          .Because($"{type}: a generic name's commas inside its brackets are not the assembly separator");
      }
    }
  }

  [Test]
  public async Task Parse_KeepsItsContract_ForTheShortAndPaddedFormsAsync() {
    await Assert.That(TypeNameFormatter.Parse("  Ns.Type  ,  Asm  ")).IsEqualTo("Ns.Type, Asm");
    await Assert.That(TypeNameFormatter.Parse("Ns.Type, Asm, Culture=neutral")).IsEqualTo("Ns.Type, Asm");
    await Assert.That(() => TypeNameFormatter.Parse("Ns.Box`1[[Ns.A, A]]")).Throws<InvalidOperationException>()
      .Because("a comma inside a type argument is not an assembly part");
  }

  [Test]
  public async Task SimpleNames_AreTheTypesOwnAsync() {
    var assembly = typeof(TypeNameWireFormTests).Assembly;

    await Assert.That(TypeNameFormatter.SimpleName(typeof(Box<int>))).IsEqualTo("Box`1");
    await Assert.That(TypeNameFormatter.SimpleName(typeof(Nested))).IsEqualTo("Nested");
    await Assert.That(TypeNameFormatter.ForLog(typeof(Box<int>))).IsEqualTo("Box`1").Because("messages print the simple name, as they always have");
    await Assert.That(TypeNameFormatter.NamespaceOf(typeof(TypeNameWireFormTests))).IsEqualTo("Whizbang.Core.Tests");
    await Assert.That(TypeNameFormatter.AssemblySimpleName(assembly)).IsEqualTo("Whizbang.Core.Tests");
    await Assert.That(TypeNameFormatter.AssemblyVersion(assembly)).IsEqualTo(TypeNameFormatter.AssemblyIdentity(assembly).Version);
  }
}
