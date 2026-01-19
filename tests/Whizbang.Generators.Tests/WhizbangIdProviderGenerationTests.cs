using System.Diagnostics.CodeAnalysis;

namespace Whizbang.Generators.Tests;

public class WhizbangIdProviderGenerationTests {
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithWhizbangId_GeneratesProviderClassAsync() {
    // Arrange
    var source = @"
      using Whizbang.Core;

      [WhizbangId]
      public readonly partial struct TestId;
    ";

    // Act
    var result = GeneratorTestHelper.RunGenerator<WhizbangIdGenerator>(source);

    // Assert
    var providerSource = GeneratorTestHelper.GetGeneratedSource(result, "TestIdProvider.g.cs");
    await Assert.That(providerSource).IsNotNull();
    await Assert.That(providerSource!).Contains("class TestIdProvider");
    await Assert.That(providerSource).Contains("IWhizbangIdProvider<TestId>");
    await Assert.That(providerSource).Contains("public TestId NewId()");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithMultipleIds_GeneratesAllProvidersAsync() {
    // Arrange
    var source = @"
      using Whizbang.Core;

      [WhizbangId]
      public readonly partial struct TestId1;

      [WhizbangId]
      public readonly partial struct TestId2;
    ";

    // Act
    var result = GeneratorTestHelper.RunGenerator<WhizbangIdGenerator>(source);

    // Assert
    var provider1Source = GeneratorTestHelper.GetGeneratedSource(result, "TestId1Provider.g.cs");
    var provider2Source = GeneratorTestHelper.GetGeneratedSource(result, "TestId2Provider.g.cs");
    await Assert.That(provider1Source).IsNotNull();
    await Assert.That(provider2Source).IsNotNull();
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_GeneratesProviderWithNullCheckAsync() {
    // Arrange
    var source = @"
      using Whizbang.Core;

      [WhizbangId]
      public readonly partial struct TestId;
    ";

    // Act
    var result = GeneratorTestHelper.RunGenerator<WhizbangIdGenerator>(source);

    // Assert
    var providerSource = GeneratorTestHelper.GetGeneratedSource(result, "TestIdProvider.g.cs");
    await Assert.That(providerSource).IsNotNull();
    await Assert.That(providerSource!).Contains("ArgumentNullException");
  }
}
