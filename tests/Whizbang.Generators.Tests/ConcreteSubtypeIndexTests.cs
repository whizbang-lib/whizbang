// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.CodeAnalysis;

namespace Whizbang.Generators.Tests;

/// <summary>
/// The concrete subtypes of the polymorphic message types receptors handle (#1690). The receptor generator widens a
/// receptor declared on an interface or base type to these, and every non-sealed message class counts as polymorphic,
/// so a large consumer asks for hundreds of them. Each was found by its own walk of every type in the compilation and
/// its references, which made the generator's cost the number of message types times the number of types.
/// </summary>
/// <code-under-test>src/Whizbang.Generators/ConcreteSubtypeIndex.cs</code-under-test>
[Category("SourceGenerators")]
public class ConcreteSubtypeIndexTests {
  private const string SOURCE = """
      namespace TestNamespace;

      public interface IShape { }
      public interface IUnused { }
      public class Shape { }
      public abstract class AbstractShape : Shape, IShape { }
      public class Circle : Shape, IShape { }
      public sealed class Square : AbstractShape { }
      public class Unrelated { }

      public static class Contracts {
        public class NestedShape : Shape { }
      }
      """;

  [RequiresAssemblyFiles]
  private static ConcreteSubtypeIndex _index(params string[] baseTypes) =>
      ConcreteSubtypeIndex.Build(GeneratorTestHelper.CreateCompilation(SOURCE), baseTypes);

  /// <summary>
  /// One walk answers every base type: building the index for three base types examines no more types than building it
  /// for one, so the cost no longer grows with the number of polymorphic message types.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Build_ExaminesEachTypeOnce_WhateverTheNumberOfBaseTypesAsync() {
    var one = _index("global::TestNamespace.Shape");
    var three = _index("global::TestNamespace.Shape", "global::TestNamespace.IShape", "global::TestNamespace.IUnused");

    await Assert.That(one.TypesExamined).IsGreaterThan(0);
    await Assert.That(three.TypesExamined).IsEqualTo(one.TypesExamined)
      .Because("the types are walked once for all the base types, not once per base type");
  }

  /// <summary>A base class's subtypes are the concrete classes that inherit it, directly or through others, nested ones included.</summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task SubtypesOf_ABaseClass_AreTheConcreteClassesThatInheritItInDiscoveryOrderAsync() {
    var index = _index("global::TestNamespace.Shape");

    await Assert.That(index.SubtypesOf("global::TestNamespace.Shape")).IsEquivalentTo(
      ["global::TestNamespace.Circle", "global::TestNamespace.Square", "global::TestNamespace.Contracts.NestedShape"],
      TUnit.Assertions.Enums.CollectionOrdering.Matching);
  }

  /// <summary>An interface's subtypes are the concrete classes that implement it, directly or through a base class.</summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task SubtypesOf_AnInterface_AreTheConcreteClassesThatImplementItAsync() {
    var index = _index("global::TestNamespace.IShape", "global::TestNamespace.IUnused");

    await Assert.That(index.SubtypesOf("global::TestNamespace.IShape")).IsEquivalentTo(
      ["global::TestNamespace.Circle", "global::TestNamespace.Square"],
      TUnit.Assertions.Enums.CollectionOrdering.Matching);
    await Assert.That(index.SubtypesOf("global::TestNamespace.IUnused")).IsEmpty();
  }

  /// <summary>A base type the compilation cannot resolve, or one the index was not built for, has no subtypes.</summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task SubtypesOf_AnUnresolvableOrUnaskedType_IsEmptyAsync() {
    var index = _index("global::TestNamespace.Missing");

    await Assert.That(index.SubtypesOf("global::TestNamespace.Missing")).IsEmpty();
    await Assert.That(index.SubtypesOf("global::TestNamespace.Shape")).IsEmpty();
    await Assert.That(index.TypesExamined).IsEqualTo(0)
      .Because("with nothing to find there is no walk");
  }
}
