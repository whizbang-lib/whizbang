using System.Diagnostics.CodeAnalysis;

namespace Whizbang.Generators.Tests;

/// <summary>
/// Coverage for <see cref="ScopedLensFactoryGenerator"/> paths the existing
/// <c>ScopedLensFactoryGeneratorTests</c> never exercise: a non-public lens type, a type
/// implementing the multi-generic <c>ILensQuery&lt;T1, T2&gt;</c> contract instead of the
/// single-generic one this registry targets, and an open (unbound) generic lens declaration.
/// </summary>
/// <tests>Whizbang.Generators/ScopedLensFactoryGenerator.cs</tests>
[Category("Generators")]
[Category("Lenses")]
public class ScopedLensFactoryGeneratorCoverageTests {
  /// <summary>
  /// Verifies that a non-public lens type is excluded from the generated registry. Without this
  /// guard, internal test doubles or scaffolding types implementing <c>ILensQuery&lt;TModel&gt;</c>
  /// would be registered alongside real, consumer-facing lenses and generate <c>typeof()</c>
  /// references to types the registry's own consumers may not even be able to see.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task NonPublicLensType_ExcludedFromRegistryAsync() {
    // Arrange
    const string source = """
            using System;
            using Whizbang.Core.Lenses;

            namespace TestApp;

            public record Order(Guid Id, string Status);

            public interface IOrderLens : ILensQuery<Order> { }
            internal interface IInternalOrderLens : ILensQuery<Order> { }
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<ScopedLensFactoryGenerator>(source);

    // Assert
    var code = GeneratorTestHelper.GetGeneratedSource(result, "LensRegistry.g.cs");
    await Assert.That(code).IsNotNull();
    await Assert.That(code).Contains("IOrderLens");
    await Assert.That(code).DoesNotContain("IInternalOrderLens")
      .Because("only public types may be discovered, so an internal lens must never reach the generated registry");
  }

  /// <summary>
  /// Verifies that a type implementing the multi-generic <c>ILensQuery&lt;T1, T2&gt;</c> contract
  /// is excluded from this single-model registry. Without this guard, the generator would try to
  /// treat a two-parameter lens as if it had exactly one model type, silently registering it under
  /// whichever type argument happened to be inspected first.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task MultiGenericLensQuery_ExcludedFromSingleModelRegistryAsync() {
    // Arrange
    const string source = """
            using System;
            using Whizbang.Core.Lenses;

            namespace TestApp;

            public record Order(Guid Id, string Status);
            public record Customer(Guid Id, string Name);

            public interface IOrderLens : ILensQuery<Order> { }
            public interface IMultiModelLens : ILensQuery<Order, Customer> { }
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<ScopedLensFactoryGenerator>(source);

    // Assert
    var code = GeneratorTestHelper.GetGeneratedSource(result, "LensRegistry.g.cs");
    await Assert.That(code).IsNotNull();
    await Assert.That(code).Contains("IOrderLens");
    await Assert.That(code).DoesNotContain("IMultiModelLens")
      .Because("ILensQuery<T1, T2> is a different, multi-model contract - it has no single model type this registry can record");
  }

  /// <summary>
  /// Verifies that an open (unbound) generic lens declaration - one whose own type parameter is
  /// passed straight through as the model type - is excluded from the registry. Without this
  /// guard, the generator would emit <c>typeof(TestApp.IGenericLens&lt;TModel&gt;)</c> and
  /// <c>typeof(TModel)</c> for a type parameter that means nothing outside the open declaration,
  /// producing generated code that fails to compile.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task OpenGenericLensDeclaration_ExcludedFromRegistryAsync() {
    // Arrange
    const string source = """
            using System;
            using Whizbang.Core.Lenses;

            namespace TestApp;

            public record Order(Guid Id, string Status);

            public interface IOrderLens : ILensQuery<Order> { }

            public interface IGenericLens<TModel> : ILensQuery<TModel> where TModel : class { }
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<ScopedLensFactoryGenerator>(source);

    // Assert
    var code = GeneratorTestHelper.GetGeneratedSource(result, "LensRegistry.g.cs");
    await Assert.That(code).IsNotNull();
    await Assert.That(code).Contains("IOrderLens");
    await Assert.That(code).DoesNotContain("IGenericLens")
      .Because("TModel is the open declaration's own type parameter, not a concrete model type - there is nothing to register");
  }
}
