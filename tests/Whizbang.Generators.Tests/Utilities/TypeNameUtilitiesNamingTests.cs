// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

extern alias shared;
using Microsoft.CodeAnalysis;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using TypeNameUtilities = shared::Whizbang.Generators.Shared.Utilities.TypeNameUtilities;

namespace Whizbang.Generators.Tests.Utilities;

/// <summary>
/// The naming routes the generators take through TypeNameUtilities (#2267): every read of a type, namespace or
/// assembly symbol's name goes through these, so WHIZ606 can hold everywhere else.
/// </summary>
/// <code-under-test>src/Whizbang.Generators.Shared/Utilities/TypeNameUtilities.cs</code-under-test>
public class TypeNameUtilitiesNamingTests {
  private const string SOURCE = """
    namespace System { public class Clock { } }
    namespace Outer.System { public class Probe { } }
    namespace App { public class Order<T> { } }
    """;

  [Test]
  public async Task SymbolRoutes_ReadTheSymbolsOwnNamesAsync() {
    var compilation = GeneratorTestHelper.CreateCompilation(SOURCE);
    var order = compilation.GetTypeByMetadataName("App.Order`1")!;

    await Assert.That(TypeNameUtilities.SimpleName(order)).IsEqualTo("Order");
    await Assert.That(TypeNameUtilities.MetadataNameOf(order)).IsEqualTo("Order`1");
    await Assert.That(TypeNameUtilities.SimpleName(TypeNameUtilities.NamespaceOf(order))).IsEqualTo("App");
    await Assert.That(TypeNameUtilities.AssemblyOf(order)).IsEqualTo(compilation.Assembly);
    await Assert.That(TypeNameUtilities.Display(order, SymbolDisplayFormat.MinimallyQualifiedFormat)).IsEqualTo("Order<T>");
    await Assert.That(TypeNameUtilities.AssemblyIdentityVersion(compilation.Assembly)).IsEqualTo(compilation.Assembly.Identity.Version);
    await Assert.That(TypeNameUtilities.AssemblyIdentityName(compilation.Assembly)).IsEqualTo(compilation.Assembly.Identity.Name);
  }

  [Test]
  public async Task IsSystemNamespace_IsTheRootSystemNamespaceOnlyAsync() {
    var compilation = GeneratorTestHelper.CreateCompilation(SOURCE);

    await Assert.That(TypeNameUtilities.IsSystemNamespace(TypeNameUtilities.NamespaceOf(compilation.GetTypeByMetadataName("System.Clock")!))).IsTrue();
    await Assert.That(TypeNameUtilities.IsSystemNamespace(TypeNameUtilities.NamespaceOf(compilation.GetTypeByMetadataName("Outer.System.Probe")!))).IsFalse();
    await Assert.That(TypeNameUtilities.IsSystemNamespace(null)).IsFalse();
  }

  [Test]
  public async Task RuntimeRoutes_ReadTheTypeAndAssemblyNamesAsync() {
    var assembly = typeof(TypeNameUtilitiesNamingTests).Assembly;

    await Assert.That(TypeNameUtilities.SimpleName(typeof(TypeNameUtilitiesNamingTests))).IsEqualTo(nameof(TypeNameUtilitiesNamingTests));
    await Assert.That(TypeNameUtilities.AssemblySimpleName(assembly)).IsEqualTo("Whizbang.Generators.Tests");
    await Assert.That(TypeNameUtilities.AssemblyVersion(assembly)).IsNotNull();
  }

  [Test]
  public async Task EveryRoute_RefusesANullArgumentAsync() {
    await Assert.That(() => TypeNameUtilities.SimpleName((ISymbol)null!)).Throws<ArgumentNullException>();
    await Assert.That(() => TypeNameUtilities.SimpleName((Type)null!)).Throws<ArgumentNullException>();
    await Assert.That(() => TypeNameUtilities.MetadataNameOf(null!)).Throws<ArgumentNullException>();
    await Assert.That(() => TypeNameUtilities.NamespaceOf(null!)).Throws<ArgumentNullException>();
    await Assert.That(() => TypeNameUtilities.AssemblyOf(null!)).Throws<ArgumentNullException>();
    await Assert.That(() => TypeNameUtilities.Display(null!, SymbolDisplayFormat.MinimallyQualifiedFormat)).Throws<ArgumentNullException>();
    await Assert.That(() => TypeNameUtilities.AssemblyIdentityVersion(null!)).Throws<ArgumentNullException>();
    await Assert.That(() => TypeNameUtilities.AssemblyIdentityName(null!)).Throws<ArgumentNullException>();
    await Assert.That(() => TypeNameUtilities.AssemblySimpleName(null!)).Throws<ArgumentNullException>();
    await Assert.That(() => TypeNameUtilities.AssemblyVersion(null!)).Throws<ArgumentNullException>();
  }
}
